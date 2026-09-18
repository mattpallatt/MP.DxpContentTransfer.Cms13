using DxpContentTransfer.Cms13.Models;
using DxpContentTransfer.Cms13.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DxpContentTransfer.Cms13.Controllers;

[Authorize(Roles = "CmsAdmins,Administrators,WebAdmins")]
public class DxpSettingsController : Controller
{
    private readonly IDxpSettingsService _settingsService;
    private readonly IEnvironmentHealthService _healthService;

    public DxpSettingsController(IDxpSettingsService settingsService, IEnvironmentHealthService healthService)
    {
        _settingsService = settingsService;
        _healthService = healthService;
    }

    [HttpGet]
    [Route("~/EPiServer/DxpContentTransfer/Admin/Settings")]
    public IActionResult Index()
    {
        Response.Headers["X-Frame-Options"] = "SAMEORIGIN";
        var settings = _settingsService.Get();
        var model = MapToViewModel(settings);
        // CMS 13: shell-aware view that includes the navigation bundle so the sidebar remains visible.
        // CMS 12: standalone HTML displayed inside the AdminInit.js iframe overlay — no platform nav
        //         markup so the admin chrome doesn't render twice inside the iframe.
        return View("~/Views/DxpSettings/Index.cshtml", model);
    }

    [HttpPost]
    [Route("~/EPiServer/DxpContentTransfer/Admin/Settings")]
    [ValidateAntiForgeryToken]
    public IActionResult Save(SettingsViewModel model)
    {
        try
        {
            var settings = new DxpTransferSettings
            {
                IntegrationBaseUrl = model.IntegrationBaseUrl?.Trim(),
                IntegrationClientKey = model.IntegrationClientKey?.Trim(),
                IntegrationClientSecret = model.IntegrationClientSecret?.Trim(),

                PreproductionBaseUrl = model.PreproductionBaseUrl?.Trim(),
                PreproductionClientKey = model.PreproductionClientKey?.Trim(),
                PreproductionClientSecret = model.PreproductionClientSecret?.Trim(),

                ProductionBaseUrl = model.ProductionBaseUrl?.Trim(),
                ProductionClientKey = model.ProductionClientKey?.Trim(),
                ProductionClientSecret = model.ProductionClientSecret?.Trim()
            };

            _settingsService.Save(settings);
            model.Saved = true;
        }
        catch (Exception ex)
        {
            model.ErrorMessage = $"Failed to save settings: {ex.Message}";
        }

        return View("~/Views/DxpSettings/Index.cshtml", model);
    }

    // Diagnostic probe for the "Test connection" button. Tests the credentials currently in the
    // form (not the saved ones), so editors can verify before saving. Returns JSON for the page's AJAX.
    [HttpPost]
    [Route("~/EPiServer/DxpContentTransfer/Admin/TestConnection")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> TestConnection(string environment, string baseUrl, string clientKey, string clientSecret)
    {
        var config = new DxpEnvironmentConfig
        {
            Name = environment,
            BaseUrl = baseUrl?.Trim(),
            ClientKey = clientKey?.Trim(),
            ClientSecret = clientSecret?.Trim()
        };
        var result = await _healthService.CheckAsync(config);
        return Json(new { ok = result.Ok, message = result.Message });
    }

    private static SettingsViewModel MapToViewModel(DxpTransferSettings settings) => new()
    {
        IntegrationBaseUrl = settings.Integration?.BaseUrl,
        IntegrationClientKey = settings.Integration?.ClientKey,
        IntegrationClientSecret = settings.Integration?.ClientSecret,

        PreproductionBaseUrl = settings.Preproduction?.BaseUrl,
        PreproductionClientKey = settings.Preproduction?.ClientKey,
        PreproductionClientSecret = settings.Preproduction?.ClientSecret,

        ProductionBaseUrl = settings.Production?.BaseUrl,
        ProductionClientKey = settings.Production?.ClientKey,
        ProductionClientSecret = settings.Production?.ClientSecret
    };

}
