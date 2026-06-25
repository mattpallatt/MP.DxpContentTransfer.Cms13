namespace DxpContentTransfer.Cms13.Models;

public class DxpEnvironmentConfig
{
    public string Name { get; set; }
    public string BaseUrl { get; set; }
    public string ClientKey { get; set; }
    public string ClientSecret { get; set; }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(BaseUrl) &&
        !string.IsNullOrWhiteSpace(ClientKey) &&
        !string.IsNullOrWhiteSpace(ClientSecret);
}
