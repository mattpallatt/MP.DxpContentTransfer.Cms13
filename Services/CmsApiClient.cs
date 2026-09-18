using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;

namespace DxpContentTransfer.Cms13.Services;

// Thin transport wrapper over the Optimizely CMS 13 REST API (/_cms/v1). Centralises URL
// building, bearer auth, the send, and request/response logging so callers only deal with
// status + body. Replaces the old CmaClient (Content Management API v3), which has no CMS 13
// release at all — see CLAUDE.md for the full CMA → REST API mapping.
//
// Key design fact this whole client leans on: content identity is preserved across
// environments by using the SOURCE GUID (32-char lowercase hex, no dashes) as the caller-
// supplied `key` on every create. Since property references are self-contained
// "cms://content/{key}" URIs (no separate integer id layer like CMA had), copying a source
// item's properties onto a target item created with the same key just works — no id
// injection/remapping subsystem is needed at all.
public sealed class CmsApiClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<CmsApiClient> _logger;

    public CmsApiClient(IHttpClientFactory httpClientFactory, ILogger<CmsApiClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    // For callers that need to drive the request themselves (binary download).
    public HttpClient CreateClient() => _httpClientFactory.CreateClient();

    private static string Base(string baseUrl) => baseUrl.TrimEnd('/') + "/_cms/v1";
    private static string ContentUrl(string baseUrl, string key) => $"{Base(baseUrl)}/content/{key}";

    // GET the node (structural metadata: key/container/owner/contentType/locales). 404 if absent.
    public Task<CmsApiResponse> GetNodeAsync(string baseUrl, string token, string key, string purpose) =>
        SendAsync(HttpMethod.Get, ContentUrl(baseUrl, key), token, purpose, accept: "application/json");

    // GET the version list for a node, optionally filtered to specific locales
    // (comma-separated). Each item is a full ContentVersion including `properties`.
    public Task<CmsApiResponse> ListVersionsAsync(string baseUrl, string token, string key, string purpose, string locales = null)
    {
        var url = $"{ContentUrl(baseUrl, key)}/versions";
        if (!string.IsNullOrEmpty(locales)) url += $"?locales={Uri.EscapeDataString(locales)}";
        return SendAsync(HttpMethod.Get, url, token, purpose, accept: "application/json");
    }

    // GET the children of a container (paged). Only carries key/container/owner/contentType —
    // NOT name/routeSegment (those live on the version), so path-walking needs a follow-up
    // ListVersionsAsync per candidate child. See ContentTransferService.ResolvePathAsync.
    public Task<CmsApiResponse> ListItemsAsync(string baseUrl, string token, string key, string purpose) =>
        SendAsync(HttpMethod.Get, $"{ContentUrl(baseUrl, key)}/items", token, purpose, accept: "application/json");

    // GET the raw binary for a media version. Confirmed live: 200 + the file bytes.
    public async Task<(HttpStatusCode Status, byte[] Bytes, string ContentType)> GetMediaBinaryAsync(
        string baseUrl, string token, string key, string version, string purpose)
    {
        var client = _httpClientFactory.CreateClient();
        var url = $"{ContentUrl(baseUrl, key)}/versions/{version}/media";
        LogRequest(HttpMethod.Get, url, purpose, null);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await client.SendAsync(request);
        var contentType = response.Content.Headers.ContentType?.MediaType;
        var bytes = response.IsSuccessStatusCode ? await response.Content.ReadAsByteArrayAsync() : null;
        _logger.LogDebug("<<< {Status} GET (media binary) ({Purpose}) {Url} — {Bytes} bytes", (int)response.StatusCode, purpose, url, bytes?.Length ?? 0);
        return (response.StatusCode, bytes, contentType);
    }

    // GET the content owned by `key` (page-local media AND blocks alike) — replaces the entire
    // CMA-era "For This Page" asset-folder probe/discover dance. No folder object exists in this
    // model; "owned" content just carries owner == key.
    public Task<CmsApiResponse> ListAssetsAsync(string baseUrl, string token, string key, string purpose) =>
        SendAsync(HttpMethod.Get, $"{ContentUrl(baseUrl, key)}/assets", token, purpose, accept: "application/json");

    // POST create a new content node + its initial version in one call. No upsert — 409s if
    // `key` already exists (call GetNodeAsync first to decide create vs. new-version).
    public Task<CmsApiResponse> CreateContentAsync(string baseUrl, string token, string json, string purpose) =>
        SendAsync(HttpMethod.Post, $"{Base(baseUrl)}/content", token, purpose,
            content: new StringContent(json, Encoding.UTF8, "application/json"), requestBody: json, accept: "application/json");

    // Multipart create: a "content" JSON part (same shape as CreateContentAsync's body) plus a
    // "file" binary part. Confirmed live against /_cms/v1/content — same field names as the old
    // CMA multipart convention.
    public async Task<CmsApiResponse> CreateContentWithBinaryAsync(
        string baseUrl, string token, string json, byte[] bytes, string fileName, string mimeType, string purpose)
    {
        var client = _httpClientFactory.CreateClient();
        var url = $"{Base(baseUrl)}/content";
        LogRequest(HttpMethod.Post, url, purpose, json);

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var multipart = new MultipartFormDataContent();
        multipart.Add(new StringContent(json, Encoding.UTF8, "application/json"), "content");
        var filePart = new ByteArrayContent(bytes);
        filePart.Headers.ContentType = new MediaTypeHeaderValue(mimeType);
        multipart.Add(filePart, "file", fileName);
        request.Content = multipart;

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        _logger.LogDebug("<<< {Status} POST (multipart) ({Purpose}) {Url}\n{Body}", (int)response.StatusCode, purpose, url, body);
        return new CmsApiResponse(response.StatusCode, body);
    }

    // POST a new version (new locale, or a new draft revision of an existing locale).
    public Task<CmsApiResponse> CreateVersionAsync(string baseUrl, string token, string key, string json, string purpose) =>
        SendAsync(HttpMethod.Post, $"{ContentUrl(baseUrl, key)}/versions", token, purpose,
            content: new StringContent(json, Encoding.UTF8, "application/json"), requestBody: json, accept: "application/json");

    // PATCH an existing version (merge-patch+json) — edits displayName/published/routeSegment/
    // properties/etc. in place.
    public Task<CmsApiResponse> PatchVersionAsync(string baseUrl, string token, string key, string version, string mergePatchJson, string purpose) =>
        SendAsync(HttpMethod.Patch, $"{ContentUrl(baseUrl, key)}/versions/{version}", token, purpose,
            content: new StringContent(mergePatchJson, Encoding.UTF8, "application/merge-patch+json"), requestBody: mergePatchJson, accept: "application/json");

    // PATCH the node itself (merge-patch+json) — ONLY container/owner can be changed this way
    // (re-parenting); nothing else.
    public Task<CmsApiResponse> PatchNodeAsync(string baseUrl, string token, string key, string mergePatchJson, string purpose) =>
        SendAsync(HttpMethod.Patch, ContentUrl(baseUrl, key), token, purpose,
            content: new StringContent(mergePatchJson, Encoding.UTF8, "application/merge-patch+json"), requestBody: mergePatchJson, accept: "application/json");

    // BUG FIX: setting `published` on the initialVersion/version write body alone does NOT
    // transition status to Published — confirmed live (a transferred page still showed an active
    // "Publish" button, i.e. still Draft, despite a `published` timestamp having been set). The
    // explicit :publish action is required to actually make a version live.
    public Task<CmsApiResponse> PublishVersionAsync(string baseUrl, string token, string key, string version, string purpose) =>
        SendAsync(HttpMethod.Post, $"{ContentUrl(baseUrl, key)}/versions/{version}:publish", token, purpose,
            content: new StringContent("{}", Encoding.UTF8, "application/json"), accept: "application/json");

    // DELETE a content node. Soft by default; pass permanent:true for a hard delete
    // (cms-permanent-delete header).
    public Task<CmsApiResponse> DeleteAsync(string baseUrl, string token, string key, string purpose, bool permanent = false)
    {
        var url = ContentUrl(baseUrl, key);
        if (!permanent) return SendAsync(HttpMethod.Delete, url, token, purpose);
        return SendWithHeaderAsync(HttpMethod.Delete, url, token, purpose, "cms-permanent-delete", "true");
    }

    public async Task<CmsApiResponse> SendAsync(
        HttpMethod method, string url, string token, string purpose,
        HttpContent content = null, string accept = null, string requestBody = null)
    {
        var client = _httpClientFactory.CreateClient();
        LogRequest(method, url, purpose, requestBody);

        using var request = new HttpRequestMessage(method, url) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (accept != null) request.Headers.Add("Accept", accept);

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        _logger.LogDebug("<<< {Status} {Method} ({Purpose}) {Url}\n{Body}",
            (int)response.StatusCode, method.Method, purpose, url, body);
        return new CmsApiResponse(response.StatusCode, body);
    }

    private async Task<CmsApiResponse> SendWithHeaderAsync(
        HttpMethod method, string url, string token, string purpose, string headerName, string headerValue)
    {
        var client = _httpClientFactory.CreateClient();
        LogRequest(method, url, purpose, null);

        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add(headerName, headerValue);

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        _logger.LogDebug("<<< {Status} {Method} ({Purpose}) {Url}\n{Body}",
            (int)response.StatusCode, method.Method, purpose, url, body);
        return new CmsApiResponse(response.StatusCode, body);
    }

    private void LogRequest(HttpMethod method, string url, string purpose, string requestBody)
    {
        if (string.IsNullOrEmpty(requestBody))
        {
            _logger.LogDebug(">>> {Method} ({Purpose}) {Url}", method.Method, purpose, url);
            return;
        }
        _logger.LogDebug(">>> {Method} ({Purpose}) {Url}\n{Body}", method.Method, purpose, url, requestBody);
    }
}

public readonly record struct CmsApiResponse(HttpStatusCode Status, string Body)
{
    public bool IsSuccess => (int)Status is >= 200 and < 300;
    public bool NotFound => Status == HttpStatusCode.NotFound;
}
