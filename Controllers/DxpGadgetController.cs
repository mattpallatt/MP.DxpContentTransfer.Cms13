using DxpContentTransfer.Cms13.Models;
using DxpContentTransfer.Cms13.Services;
using EPiServer;
using EPiServer.Core;
using EPiServer.Shell.ViewComposition;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace DxpContentTransfer.Cms13.Controllers;

[Authorize]
[IFrameComponent(
    Url = "/dxp-content-transfer/gadget",
    Title = "DXP Content Transfer",
    Description = "Transfer content to another DXP environment",
    Categories = "content",
    PlugInAreas = "/episerver/cms/assets",
    ReloadOnContextChange = true)]
public class DxpGadgetController : Controller
{
    private readonly IDxpSettingsService _settingsService;
    private readonly IContentLoader _contentLoader;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public DxpGadgetController(
        IDxpSettingsService settingsService,
        IContentLoader contentLoader,
        IHttpContextAccessor httpContextAccessor)
    {
        _settingsService = settingsService;
        _contentLoader = contentLoader;
        _httpContextAccessor = httpContextAccessor;
    }

    [HttpGet]
    [Route("~/dxp-content-transfer/gadget")]
    public IActionResult Index()
    {
        var contentId = Request.Query["id"].ToString();
        var settings = _settingsService.Get();
        var currentHost = _httpContextAccessor.HttpContext?.Request.Host.Host ?? string.Empty;
        var currentEnv = DetectCurrentEnvironment(settings, currentHost);

        var (contentName, isPage, isPublished) = ResolveContentInfo(contentId);

        var availableTargets = settings.AllEnvironments
            .Where(e => e.IsConfigured && !string.Equals(e.Name, currentEnv, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var model = new GadgetViewModel
        {
            ContentId = contentId,
            ContentName = contentName,
            CurrentEnvironmentName = currentEnv ?? "Unknown",
            AvailableTargets = availableTargets,
            IsSettingsConfigured = settings.AllEnvironments.Any(e => e.IsConfigured),
            IsPageContent = isPage,
            IsPublished = isPublished
        };

        return View("~/Views/DxpGadget/Index.cshtml", model);
    }

    private (string name, bool isPage, bool isPublished) ResolveContentInfo(string contentId)
    {
        if (string.IsNullOrWhiteSpace(contentId))
            return ("(no page selected)", false, false);

        var parts = contentId.Split('_', ':');
        if (!int.TryParse(parts[0], out var id))
            return (contentId, false, false);

        try
        {
            var content = _contentLoader.Get<IContent>(new ContentReference(id));
            var isPage = content is PageData;
            var isPublished = !(content is IVersionable v) || v.Status == VersionStatus.Published;
            return (content?.Name ?? contentId, isPage, isPublished);
        }
        catch
        {
            return (contentId, false, false);
        }
    }

    private static string DetectCurrentEnvironment(DxpTransferSettings settings, string currentHost)
    {
        if (string.IsNullOrWhiteSpace(currentHost))
            return null;

        foreach (var env in settings.AllEnvironments)
        {
            if (string.IsNullOrWhiteSpace(env.BaseUrl))
                continue;

            if (Uri.TryCreate(env.BaseUrl, UriKind.Absolute, out var uri) &&
                string.Equals(uri.Host, currentHost, StringComparison.OrdinalIgnoreCase))
            {
                return env.Name;
            }
        }

        return null;
    }
}
