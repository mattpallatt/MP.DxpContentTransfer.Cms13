using DxpContentTransfer.Cms13.Services;
using EPiServer.Shell.Modules;
using Microsoft.Extensions.DependencyInjection;

namespace DxpContentTransfer.Cms13.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddDxpContentTransfer(this IServiceCollection services)
    {
        services.AddHttpClient();
        services.AddMemoryCache();
        services.AddHttpContextAccessor();

        services.AddSingleton<IDxpSettingsService, DxpSettingsService>();
        services.AddSingleton<IEnvironmentTokenService, EnvironmentTokenService>();
        services.AddScoped<IContentTransferService, ContentTransferService>();

        services.Configure<ProtectedModuleOptions>(opts =>
        {
            if (!opts.Items.Any(x => string.Equals(x.Name, "DxpContentTransfer.Cms13", StringComparison.OrdinalIgnoreCase)))
            {
                opts.Items.Add(new ModuleDetails { Name = "DxpContentTransfer.Cms13" });
            }
        });

        return services;
    }
}
