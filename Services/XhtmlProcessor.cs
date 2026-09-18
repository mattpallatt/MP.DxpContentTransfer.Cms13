using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using static DxpContentTransfer.Cms13.Services.JsonVisitors;

namespace DxpContentTransfer.Cms13.Services;

// Pure XHTML (PropertyXhtmlString) processing — extraction and remapping of the inline assets and
// content-block fragments the editor bakes into rich-text markup. No I/O, no engine state, so it is
// unit-tested directly (see tests/DxpContentTransfer.Tests/XhtmlHelperTests). The recursive transfer
// engine in ContentTransferService drives these; the resolution/upload of what they surface stays there.
internal static class XhtmlProcessor
{
    // Extracts all inline-asset URLs (img src + a href) from PropertyXhtmlString fields in a document.
    internal static List<string> ExtractXhtmlImageUrls(string json)
    {
        var urls = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            CollectXhtmlUrls(doc.RootElement, urls);
        }
        catch { }
        return urls;
    }

    // Rich text ("RichText") properties in the new CMS 13 REST API are shaped
    // {"value": {"html": "..."}} — no "propertyDataType" wrapper like CMA had. Detect by shape:
    // an object value carrying an "html" string field.
    private static bool TryGetRichTextHtml(JsonElement obj, out string html)
    {
        html = null;
        if (!obj.TryGetProperty("value", out var val) || val.ValueKind != JsonValueKind.Object) return false;
        if (!val.TryGetProperty("html", out var h) || h.ValueKind != JsonValueKind.String) return false;
        html = h.GetString() ?? "";
        return true;
    }

    private static void CollectXhtmlUrls(JsonElement element, List<string> urls) =>
        WalkJsonElements(element, obj =>
        {
            if (TryGetRichTextHtml(obj, out var html))
                CollectAssetUrls(html, urls);
        });

    // Both <img src> and <a href> can point at internal assets (images, video, documents). Collect
    // both in document order; the transfer loop filters to actual media before upload. Parsed via
    // HtmlAgilityPack rather than regex so attribute quoting/order/whitespace can't slip an asset past.
    private static void CollectAssetUrls(string html, List<string> urls)
    {
        if (string.IsNullOrEmpty(html)) return;
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var nodes = doc.DocumentNode.SelectNodes("//*[@src] | //*[@href]");
        if (nodes == null) return;
        foreach (var node in nodes)
        {
            var src = node.GetAttributeValue("src", null);
            if (!string.IsNullOrEmpty(src)) urls.Add(src);
            var href = node.GetAttributeValue("href", null);
            if (!string.IsNullOrEmpty(href)) urls.Add(href);
        }
    }

    // Extracts inline content-block references from every PropertyXhtmlString in a document. The
    // editor stores them as <div class="epi-contentfragment" data-contentguid data-contentlink
    // data-contentname …> nodes — the guid lives only in the HTML string, so the generic guidValue
    // walker never sees them.
    internal static List<(Guid Guid, int ContentLink, string Name)> ExtractXhtmlContentFragments(string json)
    {
        var list = new List<(Guid, int, string)>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            WalkJsonElements(doc.RootElement, obj =>
            {
                if (TryGetRichTextHtml(obj, out var html))
                    list.AddRange(ParseContentFragments(html));
            });
        }
        catch { }
        return list;
    }

    // Pulls (guid, integer id, name) out of each epi-contentfragment div. Parsed via HtmlAgilityPack
    // so attribute order, quoting and whitespace are handled by the DOM rather than a brittle regex.
    internal static IEnumerable<(Guid Guid, int ContentLink, string Name)> ParseContentFragments(string html)
    {
        var results = new List<(Guid, int, string)>();
        if (string.IsNullOrEmpty(html)) return results;

        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var divs = doc.DocumentNode.SelectNodes("//div");
        if (divs == null) return results;

        foreach (var div in divs)
        {
            var cls = div.GetAttributeValue("class", "");
            if (!cls.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("epi-contentfragment"))
                continue;
            // The guid is the only required attribute; without a valid one there's nothing to transfer.
            if (!Guid.TryParse(div.GetAttributeValue("data-contentguid", null), out var guid)) continue;
            var contentLink = int.TryParse(div.GetAttributeValue("data-contentlink", null), out var id) ? id : 0;
            var name = div.GetAttributeValue("data-contentname", null);
            results.Add((guid, contentLink, name));
        }
        return results;
    }

    // Rewrites inline asset src/href values inside RichText properties per xhtmlUrlMap (source URL
    // → replacement URL, applied as plain substring replacement — matches must be exact strings as
    // extracted by ExtractXhtmlImageUrls).
    //
    // NOTE on what this deliberately no longer does: CMA-era code also remapped two environment-
    // specific NUMERIC ids baked into the markup by the classic editor — the ",,{id}" suffix on
    // inline image src attributes, and the integer data-contentlink on epi-contentfragment divs.
    // Both remaps depended on CMA's GET-by-guid response exposing contentLink.id for the target
    // environment. The new REST API has no numeric content-id concept at all (content identity is
    // purely the GUID-based `key`), so there is no way to compute a replacement integer — this is a
    // known, permanent gap, not an oversight. In practice this only matters if the CMS's own
    // rendering treats a stale/foreign numeric hint as authoritative instead of falling back to
    // data-contentguid / re-resolving the src by its GUID; if inline images or content fragments
    // render incorrectly after a transfer, this is the first thing to check.
    internal static string RewriteXhtmlUrls(string json, string sourceBaseUrl, Dictionary<string, string> xhtmlUrlMap = null)
    {
        var node = JsonNode.Parse(json)?.AsObject();
        if (node == null) return json;
        try
        {
            var origin = new Uri(sourceBaseUrl).GetLeftPart(UriPartial.Authority);
            RewriteXhtmlNodes(node, origin, xhtmlUrlMap);
        }
        catch { }
        return node.ToJsonString();
    }

    private static void RewriteXhtmlNodes(JsonNode node, string origin, Dictionary<string, string> xhtmlUrlMap = null) =>
        WalkJsonObjects(node, obj =>
        {
            if (obj["value"] is not JsonObject val || val["html"] is not JsonValue htmlVal)
                return;
            try
            {
                var html = htmlVal.GetValue<string>() ?? "";
                if (html.Contains(origin, StringComparison.OrdinalIgnoreCase))
                    html = html.Replace(origin, "", StringComparison.OrdinalIgnoreCase);
                if (xhtmlUrlMap != null)
                    foreach (var (src, tgt) in xhtmlUrlMap)
                        if (!string.IsNullOrEmpty(src) && !string.IsNullOrEmpty(tgt))
                            html = html.Replace(src, tgt, StringComparison.OrdinalIgnoreCase);
                val["html"] = html;
            }
            catch { }
        });

    // Converts an absolute URL to its path component, or returns the input unchanged if
    // it is already relative.
    internal static string ToRelativePath(string url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return uri.PathAndQuery;
        return url.StartsWith('/') ? url : null;
    }

    private const string EditModeContentPrefix = "/EPiServer/CMS/Content";

    // Turns an XHTML <img src> into a path we can resolve back to a content GUID. Editor markup
    // commonly stores internal edit-mode URLs such as
    //   /EPiServer/CMS/Content/globalassets/en/foo/bar.jpg,,108%3Fepieditmode=false
    // so we: URL-decode (the query is often %3F-encoded), drop the query/fragment, strip the
    // ",,<version>" suffix, and rewrite the edit-mode prefix to its friendly form (/globalassets/…).
    internal static string NormalizeInlineImagePath(string relPath)
    {
        if (string.IsNullOrEmpty(relPath)) return relPath;
        var p = Uri.UnescapeDataString(relPath);
        var q = p.IndexOfAny(['?', '#']);
        if (q >= 0) p = p[..q];
        var v = p.IndexOf(",,", StringComparison.Ordinal);
        if (v >= 0) p = p[..v];
        if (p.StartsWith(EditModeContentPrefix, StringComparison.OrdinalIgnoreCase))
            p = p[EditModeContentPrefix.Length..];
        return p;
    }
}
