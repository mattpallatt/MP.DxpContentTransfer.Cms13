using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DxpContentTransfer.Cms13.Models;
using Microsoft.Extensions.Logging;

namespace DxpContentTransfer.Cms13.Services;

public sealed class EnvironmentHealthService : IEnvironmentHealthService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<EnvironmentHealthService> _logger;

    public EnvironmentHealthService(IHttpClientFactory httpClientFactory, ILogger<EnvironmentHealthService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<EnvironmentHealthResult> CheckAsync(DxpEnvironmentConfig config)
    {
        if (config == null || !config.IsConfigured)
            return EnvironmentHealthResult.Fail("Enter Base URL, Client Key and Client Secret first.");
        if (!Uri.TryCreate(config.BaseUrl, UriKind.Absolute, out _))
            return EnvironmentHealthResult.Fail("Base URL must be an absolute URL, e.g. https://example.com.");

        var client = _httpClientFactory.CreateClient();

        // 1. OAuth2 client_credentials token against the new CMS 13 REST API's own token endpoint
        //    (/_cms/v1/oauth/token — NOT the old EPiServer OpenIDConnect /api/episerver/connect/token;
        //    the two auth systems are entirely separate). Confirmed live: this endpoint wants the
        //    client credentials as HTTP Basic auth, not form fields.
        string token;
        try
        {
            var tokenUrl = $"{config.BaseUrl.TrimEnd('/')}/_cms/v1/oauth/token";
            var form = new Dictionary<string, string> { ["grant_type"] = "client_credentials", ["scope"] = "api:admin" };
            using var req = new HttpRequestMessage(HttpMethod.Post, tokenUrl) { Content = new FormUrlEncodedContent(form) };
            var basicAuth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{config.ClientKey}:{config.ClientSecret}"));
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic", basicAuth);

            using var resp = await client.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
                return EnvironmentHealthResult.Fail($"Token request failed ({(int)resp.StatusCode}). {SummariseError(body)}");

            using var doc = JsonDocument.Parse(body);
            token = doc.RootElement.TryGetProperty("access_token", out var at) ? at.GetString() : null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Health check token step failed for {Env}: {Error}", config.Name, ex.Message);
            return EnvironmentHealthResult.Fail($"Could not reach the token endpoint at {config.BaseUrl.TrimEnd('/')}/_cms/v1/oauth/token — {ex.Message}");
        }

        if (string.IsNullOrEmpty(token))
            return EnvironmentHealthResult.Fail("The token endpoint responded but returned no access_token.");

        // 2. Confirm the content API responds to an authenticated call. GET a throwaway key, which a
        //    healthy API answers with 404 (content-not-found) — exactly as it does during a real
        //    transfer's existence checks. So a 404 here is SUCCESS, not failure.
        int status;
        string contentType, probeBody;
        try
        {
            var probeKey = Guid.NewGuid().ToString("N");
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{config.BaseUrl.TrimEnd('/')}/_cms/v1/content/{probeKey}");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.Accept.ParseAdd("application/json");
            using var resp = await client.SendAsync(req);
            status = (int)resp.StatusCode;
            contentType = resp.Content.Headers.ContentType?.MediaType ?? "";
            probeBody = await resp.Content.ReadAsStringAsync();
        }
        catch (Exception ex)
        {
            return EnvironmentHealthResult.Fail($"Token acquired, but the content API call failed — {ex.Message}");
        }

        _logger.LogDebug("Health check probe for {Env}: {Status} content-type='{ContentType}'", config.Name, status, contentType);

        if (status == 401)
            return EnvironmentHealthResult.Fail("Token acquired, but the content API rejected it (401).");
        if (status == 403)
            return EnvironmentHealthResult.Fail("Token acquired, but it is not authorized for the content API (403). This environment's api:admin OAuth client may not have been granted CMS content access rights — see CLAUDE.md.");
        if (status is >= 200 and < 300)
            return EnvironmentHealthResult.Pass("Connection OK — token granted, and the CMS REST API is reachable.");
        if (status == 404)
        {
            var apiAnswered = contentType.Contains("json", StringComparison.OrdinalIgnoreCase) || LooksLikeJson(probeBody);
            return apiAnswered
                ? EnvironmentHealthResult.Pass("Connection OK — token granted, and the CMS REST API is reachable.")
                : EnvironmentHealthResult.Pass("Token OK. The probe returned a bare 404 — almost certainly fine, but if transfers fail with 404, confirm the /_cms/v1 REST API is enabled on this environment.");
        }
        return EnvironmentHealthResult.Fail($"Token OK, but the content API returned {status}.");
    }

    private static bool LooksLikeJson(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return false;
        var c = body.TrimStart()[0];
        return c is '{' or '[';
    }

    private static string SummariseError(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "No response body.";
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var error = root.TryGetProperty("error", out var e) ? e.GetString() : null;
            var desc = root.TryGetProperty("error_description", out var d) ? d.GetString() : null;
            var detail = root.TryGetProperty("detail", out var det) ? det.GetString() : null;
            var parts = new[] { error, desc, detail }.Where(s => !string.IsNullOrEmpty(s));
            if (parts.Any()) return string.Join(": ", parts);
        }
        catch { /* not JSON — fall through to the trimmed raw body */ }
        return body.Length > 200 ? body[..200] + "…" : body;
    }
}
