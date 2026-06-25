using System.Collections.Concurrent;
using DxpContentTransfer.Cms13.Models;
using DxpContentTransfer.Cms13.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace DxpContentTransfer.Cms13.Controllers;

[Authorize]
[ApiController]
[Route("dxp-content-transfer/api")]
public class DxpTransferApiController : ControllerBase
{
    private static readonly ConcurrentDictionary<string, TransferJob> _jobs = new();

    private sealed class TransferJob
    {
        public int Completed;
        public int Total;
        public bool Done;
        public TransferResult Result;
        public readonly DateTimeOffset CreatedAt = DateTimeOffset.UtcNow;
        public DateTimeOffset? CompletedAt;

        public bool IsExpired =>
            CompletedAt.HasValue
                ? DateTimeOffset.UtcNow - CompletedAt.Value > TimeSpan.FromMinutes(30)
                : DateTimeOffset.UtcNow - CreatedAt > TimeSpan.FromHours(1);
    }

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDxpSettingsService _settingsService;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public DxpTransferApiController(
        IServiceScopeFactory scopeFactory,
        IDxpSettingsService settingsService,
        IHttpContextAccessor httpContextAccessor)
    {
        _scopeFactory = scopeFactory;
        _settingsService = settingsService;
        _httpContextAccessor = httpContextAccessor;
    }

    [HttpPost("pre-check")]
    public async Task<IActionResult> PreCheck([FromBody] PreCheckRequestModel request)
    {
        if (string.IsNullOrWhiteSpace(request?.ContentId))
            return BadRequest(new PreCheckResult { Success = false, ErrorMessage = "ContentId is required." });

        if (string.IsNullOrWhiteSpace(request.TargetEnvironment))
            return BadRequest(new PreCheckResult { Success = false, ErrorMessage = "TargetEnvironment is required." });

        using var scope = _scopeFactory.CreateScope();
        var transferService = scope.ServiceProvider.GetRequiredService<IContentTransferService>();
        var result = await transferService.PreCheckAsync(
            request.ContentId,
            request.TargetEnvironment,
            request.IncludeChildren,
            request.OverwriteMatchingIds);

        return Ok(result);
    }

    [HttpPost("transfer")]
    public IActionResult Transfer([FromBody] TransferRequestModel request)
    {
        if (string.IsNullOrWhiteSpace(request?.ContentId))
            return BadRequest(new TransferResult { Success = false, ErrorMessage = "ContentId is required." });

        if (string.IsNullOrWhiteSpace(request.TargetEnvironment))
            return BadRequest(new TransferResult { Success = false, ErrorMessage = "TargetEnvironment is required." });

        var jobId = request.JobId ?? Guid.NewGuid().ToString("N");
        var total = CountTotal(request.Plan);
        var job = new TransferJob { Total = total };
        _jobs[jobId] = job;

        var sourceEnvironmentName = DetectSourceEnvironment();

        _ = Task.Run(async () =>
        {
            using var scope = _scopeFactory.CreateScope();
            var transferService = scope.ServiceProvider.GetRequiredService<IContentTransferService>();
            try
            {
                var result = await transferService.TransferAsync(
                    request.ContentId,
                    request.TargetEnvironment,
                    request.IncludeChildren,
                    sourceEnvironmentName,
                    request.TransferStatus ?? "Published",
                    request.Plan,
                    onItemComplete: () => Interlocked.Increment(ref job.Completed));
                job.Result = result;
            }
            catch (Exception ex)
            {
                job.Result = new TransferResult { Success = false, ErrorMessage = ex.Message };
            }
            finally
            {
                job.Done = true;
                job.CompletedAt = DateTimeOffset.UtcNow;
            }
        });

        return Accepted(new { jobId });
    }

    [HttpGet("progress/{jobId}")]
    public IActionResult Progress(string jobId)
    {
        foreach (var key in _jobs.Keys.ToList())
            if (_jobs.TryGetValue(key, out var j) && j.IsExpired)
                _jobs.TryRemove(key, out _);

        if (!_jobs.TryGetValue(jobId, out var job))
            return NotFound();
        return Ok(new { job.Completed, job.Total, job.Done });
    }

    [HttpGet("result/{jobId}")]
    public IActionResult Result(string jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var job))
            return NotFound();
        if (!job.Done)
            return Conflict(new { message = "Job not yet complete." });
        return Ok(job.Result);
    }

    private static int CountTotal(List<PreCheckItemResult> plan)
    {
        if (plan == null) return 1;
        return plan.Sum(p => 1 + CountNodes(p.Dependencies));
    }

    private static int CountNodes(List<DependencyNode> nodes)
    {
        if (nodes == null) return 0;
        return nodes.Sum(n => 1 + CountNodes(n.Children));
    }

    private string DetectSourceEnvironment()
    {
        var host = _httpContextAccessor.HttpContext?.Request.Host.Host ?? string.Empty;
        var settings = _settingsService.Get();
        var envs = new[] {
            ("integration", settings.Integration),
            ("preproduction", settings.Preproduction),
            ("production", settings.Production)
        };
        foreach (var (name, config) in envs)
        {
            if (config?.IsConfigured != true) continue;
            if (Uri.TryCreate(config.BaseUrl, UriKind.Absolute, out var uri) &&
                string.Equals(uri.Host, host, StringComparison.OrdinalIgnoreCase))
                return name;
        }
        return null;
    }
}
