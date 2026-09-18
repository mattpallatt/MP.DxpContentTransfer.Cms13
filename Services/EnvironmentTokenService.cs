using System.Net.Http.Headers;
using System.Text;
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
        // Key on BaseUrl + ClientKey so rotating the client credentials doesn't serve a stale token.
        var cacheKey = $"dxp_token_{config.BaseUrl}_{config.ClientKey}";

        if (_cache.TryGetValue(cacheKey, out string cachedToken))
        {
            _logger.LogDebug("Token for {Env} served from cache", config.Name);
            return cachedToken;
        }

        var client = _httpClientFactory.CreateClient();
        // New CMS 13 REST API's own OAuth endpoint — NOT the old EPiServer OpenIDConnect
        // /api/episerver/connect/token. The two auth systems are entirely separate and do not
        // interoperate (confirmed live: a token from one is flatly 401'd by the other's API).
        var tokenUrl = $"{config.BaseUrl.TrimEnd('/')}/_cms/v1/oauth/token";

        // Confirmed live: this endpoint wants the client credentials as HTTP Basic auth, NOT as
        // client_id/client_secret form fields (the old OpenIDConnect convention) — sending them
        // as form fields gets "The authorization field is required." Scope is `api:admin`
        // ("full administrative access to the API" per Optimizely's docs); there is no narrower
        // documented scope for content read/write specifically.
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["scope"] = "api:admin"
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, tokenUrl) { Content = new FormUrlEncodedContent(form) };
        var basicAuth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{config.ClientKey}:{config.ClientSecret}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basicAuth);

        _logger.LogDebug(">>> POST {TokenUrl}\n    Purpose: Acquiring OAuth2 client_credentials token for {Env}\n    Authorization: Basic [redacted] (client_id={ClientId})\n    Content-Type: application/x-www-form-urlencoded\n    grant_type=client_credentials&scope=api:admin",
            tokenUrl, config.Name, config.ClientKey);

        var response = await client.SendAsync(request);
        var json = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogDebug("<<< {Status} POST {TokenUrl}\n{ResponseBody}", (int)response.StatusCode, tokenUrl, json);
            throw new HttpRequestException($"Token endpoint returned {(int)response.StatusCode} from {tokenUrl} using client_id='{config.ClientKey}'. Response: {json}");
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var token = root.GetProperty("access_token").GetString();
        var expiresIn = root.TryGetProperty("expires_in", out var expProp) ? expProp.GetInt32() : 300;
        _logger.LogDebug("Token acquired for {Env}: expires_in={ExpiresIn}s", config.Name, expiresIn);

        // The new API's tokens are short-lived (confirmed live: 300s / 5 minutes, vs. the old
        // OpenIDConnect token's 3600s) — cache with a tighter safety margin accordingly.
        _cache.Set(cacheKey, token, TimeSpan.FromSeconds(Math.Max(expiresIn - 15, 15)));

        return token;
    }
}
