using EPiServer.Data;
using EPiServer.Data.Dynamic;

namespace DxpContentTransfer.Cms13.Models;

[EPiServerDataStore(AutomaticallyRemapStore = true)]
public class DxpTransferSettings : IDynamicData
{
    public Identity Id { get; set; }

    // Flat string properties — DDS stores these reliably
    public string IntegrationBaseUrl { get; set; }
    public string IntegrationClientKey { get; set; }
    public string IntegrationClientSecret { get; set; }

    public string PreproductionBaseUrl { get; set; }
    public string PreproductionClientKey { get; set; }
    public string PreproductionClientSecret { get; set; }

    public string ProductionBaseUrl { get; set; }
    public string ProductionClientKey { get; set; }
    public string ProductionClientSecret { get; set; }

    // Computed getters only (no setter, so DDS skips them). Each config is built once on first
    // access and cached — these objects used to be re-allocated on every read, which also meant
    // settings.Integration != settings.Integration by reference. The flat strings are fully
    // populated before any consumer reads a config, so lazy caching is safe.
    private DxpEnvironmentConfig _integration;
    private DxpEnvironmentConfig _preproduction;
    private DxpEnvironmentConfig _production;

    public DxpEnvironmentConfig Integration => _integration ??=
        new() { Name = "Integration", BaseUrl = IntegrationBaseUrl, ClientKey = IntegrationClientKey, ClientSecret = IntegrationClientSecret };
    public DxpEnvironmentConfig Preproduction => _preproduction ??=
        new() { Name = "Preproduction", BaseUrl = PreproductionBaseUrl, ClientKey = PreproductionClientKey, ClientSecret = PreproductionClientSecret };
    public DxpEnvironmentConfig Production => _production ??=
        new() { Name = "Production", BaseUrl = ProductionBaseUrl, ClientKey = ProductionClientKey, ClientSecret = ProductionClientSecret };

    public IEnumerable<DxpEnvironmentConfig> AllEnvironments =>
        new[] { Integration, Preproduction, Production };

    // Returns the environment whose BaseUrl host matches the given request host, or null.
    // Shared by the gadget and API controllers, which both used to inline this loop.
    public DxpEnvironmentConfig DetectByHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return null;
        foreach (var env in AllEnvironments)
        {
            if (string.IsNullOrWhiteSpace(env.BaseUrl)) continue;
            if (Uri.TryCreate(env.BaseUrl, UriKind.Absolute, out var uri) &&
                string.Equals(uri.Host, host, StringComparison.OrdinalIgnoreCase))
                return env;
        }
        return null;
    }
}
