using System.Text.Json;
using DxpContentTransfer.Cms13.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace DxpContentTransfer.Cms13.Services;

public class EnvironmentTokenService : IEnvironmentTokenService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _cache;
    private readonly ILogger<EnvironmentTokenService> _logger;

    public EnvironmentTokenService(IHttpClientFactory httpClientFactory, IMemoryCache cache, ILogger<EnvironmentTokenService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _logger = logger;
    }

    public async Task<string> GetTokenAsync(DxpEnvironmentConfig config)
    {
        var cacheKey = $"dxp_token_{config.BaseUrl}";

        if (_cache.TryGetValue(cacheKey, out string cachedToken))
        {
            _logger.LogDebug("Token for {Env} served from cache", config.Name);
            return cachedToken;
        }

        var client = _httpClientFactory.CreateClient();
        var tokenUrl = $"{config.BaseUrl.TrimEnd('/')}/api/episerver/connect/token";

        var formBody = $"grant_type=client_credentials&client_id={Uri.EscapeDataString(config.ClientKey)}&client_secret=[redacted]&scope=epi_content_management";
        _logger.LogDebug(">>> POST {TokenUrl}\n    Purpose: Acquiring OAuth2 client_credentials token for {Env}\n    Content-Type: application/x-www-form-urlencoded\n{FormBody}", tokenUrl, config.Name, formBody);

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = config.ClientKey,
            ["client_secret"] = config.ClientSecret,
            ["scope"] = "epi_content_management"
        };

        var response = await client.PostAsync(tokenUrl, new FormUrlEncodedContent(form));
        var json = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogDebug("<<< {Status} POST {TokenUrl}\n{ResponseBody}", (int)response.StatusCode, tokenUrl, json);
            throw new HttpRequestException($"Token endpoint returned {(int)response.StatusCode} from {tokenUrl} using client_id='{config.ClientKey}'. Response: {json}");
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var token = root.GetProperty("access_token").GetString();
        var expiresIn = root.TryGetProperty("expires_in", out var expProp) ? expProp.GetInt32() : 3600;
        var grantedScope = root.TryGetProperty("scope", out var scopeProp) ? scopeProp.GetString() : "(not returned)";
        _logger.LogDebug("Token acquired for {Env}: expires_in={ExpiresIn}s scope='{Scope}'",
            config.Name, expiresIn, grantedScope);

        _ = LogUserInfoAsync(client, config, token);

        _cache.Set(cacheKey, token, TimeSpan.FromSeconds(expiresIn - 30));

        return token;
    }

    private async Task LogUserInfoAsync(HttpClient client, DxpEnvironmentConfig config, string token)
    {
        try
        {
            var userinfoUrl = $"{config.BaseUrl.TrimEnd('/')}/api/episerver/connect/userinfo";
            _logger.LogDebug(">>> GET {UserinfoUrl}\n    Purpose: Resolving identity for {Env} via userinfo endpoint", userinfoUrl, config.Name);
            var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, userinfoUrl);
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            var resp = await client.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();
            _logger.LogDebug("<<< {Status} GET {UserinfoUrl}\n{Body}", (int)resp.StatusCode, userinfoUrl, body);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Userinfo call failed for {Env}: {Error}", config.Name, ex.Message);
        }
    }
}
