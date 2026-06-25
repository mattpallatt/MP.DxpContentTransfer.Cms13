namespace DxpContentTransfer.Cms13.Models;

public class SettingsViewModel
{
    public string IntegrationBaseUrl { get; set; }
    public string IntegrationClientKey { get; set; }
    public string IntegrationClientSecret { get; set; }

    public string PreproductionBaseUrl { get; set; }
    public string PreproductionClientKey { get; set; }
    public string PreproductionClientSecret { get; set; }

    public string ProductionBaseUrl { get; set; }
    public string ProductionClientKey { get; set; }
    public string ProductionClientSecret { get; set; }

    public bool Saved { get; set; }
    public string ErrorMessage { get; set; }
}
