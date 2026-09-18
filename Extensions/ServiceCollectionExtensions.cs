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
        //
        // This registers the module by NAME only — EPiServer.Shell.Modules.ModuleFinder still has
        // to separately find an actual module.config manifest on disk (or via a file provider) at
        // "{ProtectedModuleOptions.RootPath}DxpContentTransfer.Cms13" before this registration does
        // anything at all; if it can't, the gadget's [IFrameComponent] crashes the Edit UI dashboard
        // the moment a signed-in editor opens it ("Unable to find a module by assembly..."), even
        // though this line ran without error. This package ships that manifest as a static web
        // asset (see the csproj's StaticWebAssetBasePath) so it resolves automatically at the
        // DEFAULT RootPath ("~/ui") with zero manual setup — confirmed the hard way once already
        // (hours lost installing the NuGet package into a fresh site before this fix existed).
        // ONE CASE THIS DOESN'T COVER: a host that reconfigures RootPath away from the default
        // (e.g. Jhoose.Security, which remaps every module's route base to "~/Optimizely" for
        // admin-URL obscurity) — there's no single physical location that satisfies every possible
        // override, so that case needs a hand-placed module.config at
        // "{their RootPath}/DxpContentTransfer.Cms13/module.config" instead. This repo's own CMS13
        // test host does exactly that (see its Optimizely/DxpContentTransfer.Cms13/module.config)
        // because it uses Jhoose.Security. If the gadget still crashes the dashboard after
        // referencing this package, check what (if anything) the consuming app has configured for
        // ProtectedModuleOptions.RootPath before assuming this registration is broken.
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
