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

    // Computed getters only — no setter, so DDS skips them
    public DxpEnvironmentConfig Integration => new() { Name = "Integration", BaseUrl = IntegrationBaseUrl, ClientKey = IntegrationClientKey, ClientSecret = IntegrationClientSecret };
    public DxpEnvironmentConfig Preproduction => new() { Name = "Preproduction", BaseUrl = PreproductionBaseUrl, ClientKey = PreproductionClientKey, ClientSecret = PreproductionClientSecret };
    public DxpEnvironmentConfig Production => new() { Name = "Production", BaseUrl = ProductionBaseUrl, ClientKey = ProductionClientKey, ClientSecret = ProductionClientSecret };

    public IEnumerable<DxpEnvironmentConfig> AllEnvironments =>
        new[] { Integration, Preproduction, Production };
}
