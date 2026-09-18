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
        var currentConfig = settings.DetectByHost(currentHost);
        var currentEnv = currentConfig?.Name;

        var (contentName, isPage, isPublished, languages) = ResolveContentInfo(contentId);

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
            IsPublished = isPublished,
            AvailableLanguages = languages
        };

        return View("~/Views/DxpGadget/Index.cshtml", model);
    }

    private (string name, bool isPage, bool isPublished, List<LanguageOption> languages) ResolveContentInfo(string contentId)
    {
        if (string.IsNullOrWhiteSpace(contentId))
            return ("(no page selected)", false, false, new());

        var reference = ContentReferenceParser.Parse(contentId);
        if (ContentReference.IsNullOrEmpty(reference))
            return (contentId, false, false, new());

        try
        {
            var content = _contentLoader.Get<IContent>(reference);
            var isPage = content is PageData;
            var isPublished = !(content is IVersionable v) || v.Status == VersionStatus.Published;
            var languages = new List<LanguageOption>();
            if (content is ILocalizable loc && loc.ExistingLanguages != null)
            {
                var masterCode = loc.MasterLanguage?.Name;
                foreach (var culture in loc.ExistingLanguages)
                    languages.Add(new LanguageOption
                    {
                        Code = culture.Name,
                        DisplayName = culture.EnglishName,
                        IsMaster = string.Equals(culture.Name, masterCode, StringComparison.OrdinalIgnoreCase)
                    });
                // Pin the master language first so the picker lists it at the top.
                languages = languages.OrderByDescending(l => l.IsMaster).ToList();
            }
            return (content?.Name ?? contentId, isPage, isPublished, languages);
        }
        catch
        {
            return (contentId, false, false, new());
        }
    }
}
