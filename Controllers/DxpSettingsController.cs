using DxpContentTransfer.Cms13.Models;
using DxpContentTransfer.Cms13.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DxpContentTransfer.Cms13.Controllers;

[Authorize(Roles = "CmsAdmins,Administrators,WebAdmins")]
public class DxpSettingsController : Controller
{
    private readonly IDxpSettingsService _settingsService;

    public DxpSettingsController(IDxpSettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    [HttpGet]
    [Route("~/EPiServer/DxpContentTransfer/Admin/Settings")]
    public IActionResult Index()
    {
        Response.Headers["X-Frame-Options"] = "SAMEORIGIN";
        var settings = _settingsService.Get();
        var model = MapToViewModel(settings);
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
