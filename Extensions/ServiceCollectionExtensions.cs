using DxpContentTransfer.Cms13.Menu;
using DxpContentTransfer.Cms13.Services;
using EPiServer.Shell.Modules;
using EPiServer.Shell.Navigation;
using Microsoft.Extensions.DependencyInjection;

namespace DxpContentTransfer.Cms13.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddDxpContentTransfer(this IServiceCollection services)
    {
        // The shell calls ToResource(assembly, ...) when building IFrameComponent definitions.
        // Without this, startup throws "Unable to find a module by assembly 'DxpContentTransfer.Cms13'".
        services.Configure<ProtectedModuleOptions>(opts =>
        {
            if (!opts.Items.Any(x => string.Equals(x.Name, "DxpContentTransfer.Cms13", StringComparison.OrdinalIgnoreCase)))
                opts.Items.Add(new ModuleDetails { Name = "DxpContentTransfer.Cms13" });
        });

        services.AddHttpClient();
        services.AddMemoryCache();
        services.AddHttpContextAccessor();

        services.AddSingleton<IDxpSettingsService, DxpSettingsService>();
        services.AddSingleton<IEnvironmentTokenService, EnvironmentTokenService>();
        services.AddSingleton<CmsApiClient>();
        services.AddSingleton<IEnvironmentHealthService, EnvironmentHealthService>();
        services.AddScoped<IContentTransferService, ContentTransferService>();

        // CMS 13: DI registration alone is sufficient for menu discovery; attribute scanning is not used.
        services.AddTransient<IMenuProvider, DxpTransferMenuProvider>();

        return services;
    }
}
