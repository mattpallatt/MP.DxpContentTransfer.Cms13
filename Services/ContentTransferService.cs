using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using DxpContentTransfer.Cms13.Models;
using EPiServer;
using EPiServer.Core;
using EPiServer.Core.Html.StringParsing;
using EPiServer.DataAbstraction;
using EPiServer.SpecializedProperties;
using EPiServer.Web;
using EPiServer.Web.Routing;
using Microsoft.Extensions.Logging;
using static DxpContentTransfer.Cms13.Services.JsonVisitors;

namespace DxpContentTransfer.Cms13.Services;

// ── CMA → new CMS 13 REST API (/_cms/v1) port ────────────────────────────────────────────────
//
// The old engine (CMA v3) is gone from history but its design intent survives here almost
// entirely, because the new API's content model made most of its hardest problems disappear
// rather than requiring a translation:
//
//   • KEY PRESERVATION replaces id injection. CMA needed the target's environment-specific
//     integer id injected into every reference object (guidValue alone wasn't enough to bind a
//     reference). The new API's references are self-contained "cms://content/{key}" URIs, and
//     `key` is caller-suppliable on create (confirmed live: POST /_cms/v1/content with a chosen
//     key, then GET it back by that same key — 201/200). So every item is created on the target
//     using ITS SOURCE GUID (32-char lowercase hex, no dashes) as its key. A ContentArea or
//     single-reference property copied verbatim from the source response is then ALREADY a
//     valid reference on the target, with zero rewriting, PROVIDED the referenced item exists
//     there under that same key — which the depth-first dependency walk below guarantees.
//     This one fact deletes CMA's entire InjectTargetContentIds/GetTargetContentIdAsync/idMap
//     machinery.
//   • OWNER replaces the "For This Page/Block" folder probe. CMA exposed no way to discover a
//     content's local-asset-folder GUID (it's generated per environment); the workaround was to
//     PUT a throwaway 1×1 image to force Optimizely to create+route it, read the folder GUID off
//     the probe's parentLink, delete the probe, and defer local BLOCKS (which Optimizely doesn't
//     auto-route the way it does media) until a sibling media upload revealed the folder. The new
//     API's `owner` field does this directly and uniformly for media AND blocks — confirmed live:
//     a TeaserBlock created with owner=<pageKey> immediately appeared in that page's
//     GET .../assets, exactly like pre-existing media. No probing, no deferral, no folder-mapping
//     cache. This deletes ResolveAssetFolderGuidAsync/CaptureFolderMappingAsync/
//     TransferDeferredLocalBlocksAsync/the ProbePng constant entirely.
//   • CONTAINER-KEY PRESERVATION replaces URL-based folder-path resolution. CMA had no way to
//     look up a folder by GUID chain, so global-asset folders (identified in markup only by
//     path, e.g. /globalassets/events/) were found by walking the path segment-by-segment via
//     the Content Delivery API's ?contentURL= lookup. CDAPI is gone entirely now (a confirmed,
//     unfixable packaging conflict with /_cms/v1 — see CLAUDE.md), and the new REST API has no
//     URL-to-key resolver at all (confirmed against the full documented endpoint family, not
//     just the ones this file touches). But it turns out not to matter: a content's `container`
//     is already a GUID, so the fix is the same key-preservation trick applied recursively —
//     EnsureContainerExistsAsync walks the SOURCE container chain by key and creates any missing
//     link on the target under that same key, no URL involved. This deletes
//     EnsureGlobalAssetFolderPathAsync/FindByUrlAsync/FindByUrlOnTargetAsync/
//     FindByUrlOnSourceAsync/GetTargetContentUrlViaCdvAsync/GetSourceContentUrlAsync.
//   • Locale/invariant properties got SIMPLER, not harder. CMA rejected a branch write that
//     included culture-invariant properties (409), so every non-master language had to be
//     filtered down to only PropertyDefinition.LanguageSpecific properties before writing
//     (LanguagePlan.CultureSpecificByType). Confirmed live: the new API has no such rule — a
//     version write for ANY locale must include the type's required properties (invariant ones
//     too), and resending the same invariant value every time succeeds cleanly (201, no
//     conflict). So every locale write here just sends the SAME full property set read for that
//     locale. This deletes the whole CultureSpecificByType pre-load and the filtering step.
//   • Non-versionable content needed NO special-casing verification. CMA's ContentNotVersionable
//     retry (strip status/startPublish/stopPublish and re-PUT) doesn't have an analogue here: a
//     non-versionable/container-type node simply has `locales: []` and no version to write in
//     the first place (confirmed live) — the version-scoped write model makes the whole class of
//     error structurally impossible rather than something to catch and retry.
//
// What did NOT get simpler, and is a real, permanent gap documented at the point it bites (grep
// "KNOWN GAP" in this file): the new API exposes no numeric content-id concept at all, so the two
// CMA-era numeric remaps baked into rich-text markup by the classic editor — the ",,{id}" suffix
// on inline image src, and the integer data-contentlink on epi-contentfragment divs — cannot be
// computed for the target and are left as-is (pointing at the SOURCE's numbers). And the
// PreCheck-phase URL-based ancestor-matching fallback (for a target page that pre-exists under a
// different GUID at the same conceptual URL, created by something other than this tool) is
// dropped rather than reimplemented — it now falls through to the existing site-root safety net
// instead. See CLAUDE.md for the full list.
public class ContentTransferService : IContentTransferService
{
    private readonly IDxpSettingsService _settingsService;
    private readonly IEnvironmentTokenService _tokenService;
    private readonly CmsApiClient _api;
    private readonly IContentLoader _contentLoader;
    private readonly IUrlResolver _urlResolver;
    private readonly IContentTypeRepository _contentTypeRepository;
    private readonly ILogger<ContentTransferService> _logger;

    // A single write can require several retries as we strip properties the target rejects
    // (unknown, or required-but-unsatisfiable) one at a time off the body; this bounds that loop.
    private const int MaxWriteAttempts = 25;

    private const string BuildMarker = "1.0.0-rest-api (transfer-only)";

    public ContentTransferService(
        IDxpSettingsService settingsService,
        IEnvironmentTokenService tokenService,
        CmsApiClient api,
        IContentLoader contentLoader,
        IUrlResolver urlResolver,
        IContentTypeRepository contentTypeRepository,
        ILogger<ContentTransferService> logger)
    {
        _settingsService = settingsService;
        _tokenService = tokenService;
        _api = api;
        _contentLoader = contentLoader;
        _urlResolver = urlResolver;
        _contentTypeRepository = contentTypeRepository;
        _logger = logger;
    }

    // ── Pre-check ─────────────────────────────────────────────────────────────
    // This whole phase reads only LOCAL content (IContentLoader/IUrlResolver, DB-backed, thread-
    // affine) to build the plan shown to the editor. It is unchanged in spirit from the CMA-era
    // engine — the only wire-format-dependent pieces are ExistsOnTargetAsync and
    // ResolveTargetParentAsync's phase 1, both updated below to call the new API.

    public async Task<PreCheckResult> PreCheckAsync(
        string contentId,
        string targetEnvironmentName,
        bool includeChildren,
        bool overwriteMatchingIds,
        string destinationParentId = null,
        string destinationParentName = null)
    {
        var destinationOverrideGuid = !string.IsNullOrEmpty(destinationParentId) && Guid.TryParseExact(destinationParentId, "N", out var dpg) ? dpg : (Guid?)null;
        _logger.LogInformation("DXP Content Transfer pre-check — build {Build}", BuildMarker);
        var settings = _settingsService.Get();
        var target = ResolveEnvironment(settings, targetEnvironmentName);

        if (target == null || !target.IsConfigured)
            return new PreCheckResult { Success = false, ErrorMessage = $"Target environment '{targetEnvironmentName}' is not configured." };

        var contentRef = ParseContentReference(contentId);
        if (contentRef == ContentReference.EmptyReference)
            return new PreCheckResult { Success = false, ErrorMessage = $"Invalid content reference: {contentId}" };

        // Pre-load ALL IContentLoader/IUrlResolver data synchronously BEFORE the first await.
        // IDatabaseExecutor is not thread-safe; any await can resume on a different thread pool thread.
        var batchGuidMap = new Dictionary<Guid, Guid>();
        var batchForcedNew = new HashSet<Guid>();
        var depSeen = new HashSet<Guid>();
        var (siteRootGuid, siteRootPath) = GetSiteRootFallback();

        var itemContexts = CollectItems(contentRef, includeChildren)
            .Select(itemRef =>
            {
                IContent content = null;
                try { if (!ContentReference.IsNullOrEmpty(itemRef)) content = _contentLoader.Get<IContent>(itemRef, LanguageSelector.AutoDetect(true)); }
                catch (Exception ex) { _logger.LogDebug("Pre-check: could not load content {Ref}: {Error}", itemRef, ex.Message); }

                Guid? directParentSourceGuid = null;
                string directParentName = null;
                if (content != null && !ContentReference.IsNullOrEmpty(content.ParentLink))
                {
                    try
                    {
                        var parent = _contentLoader.Get<IContent>(content.ParentLink, LanguageSelector.AutoDetect(true));
                        directParentSourceGuid = parent.ContentGuid;
                        directParentName = parent.Name;
                    }
                    catch (Exception ex) { _logger.LogDebug("Pre-check: could not load parent of {Ref}: {Error}", itemRef, ex.Message); }
                }

                var ancestorsWithUrls = new List<(IContent ancestor, string url)>();
                if (content != null)
                    foreach (var a in BuildAncestorChain(content.ParentLink))
                    {
                        string url = null;
                        try { url = _urlResolver.GetUrl(a.ContentLink); }
                        catch { }
                        ancestorsWithUrls.Add((a, url));
                    }

                var deps = new List<DependencyNode>();
                if (content is PageData)
                {
                    depSeen.Add(content.ContentGuid);
                    deps = ScanContentDependencies(content, depSeen);
                }

                return (itemRef, content, directParentSourceGuid, directParentName, ancestorsWithUrls, deps);
            })
            .ToList();

        var availableLanguages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ctx in itemContexts)
            if (ctx.content is ILocalizable localizable && localizable.ExistingLanguages != null)
                foreach (var culture in localizable.ExistingLanguages)
                    if (culture != null && !string.IsNullOrEmpty(culture.Name))
                        availableLanguages[culture.Name] = culture.EnglishName;

        // All IContentLoader work done — now safe to await. This is just an up-front auth
        // sanity check (fail fast with a friendly error on bad credentials) — the token itself
        // is NOT threaded through the loop below. See BuildPreCheckItemAsync/ExistsOnTargetAsync/
        // ResolveTargetParentAsync: each fetches its own token from IEnvironmentTokenService right
        // before use instead. GetTokenAsync caches per environment, so this costs nothing extra in
        // the common case, but it means a token that goes stale partway through a long pre-check
        // (large trees; the new API's tokens are short-lived, 300s) gets silently refreshed on the
        // next call instead of every subsequent request 401ing for the rest of the run.
        try { await _tokenService.GetTokenAsync(target); }
        catch (Exception ex) { return new PreCheckResult { Success = false, ErrorMessage = $"Failed to authenticate with target environment: {ex.Message}" }; }

        var result = new PreCheckResult
        {
            Success = true,
            AvailableLanguages = availableLanguages
                .OrderBy(kv => kv.Value, StringComparer.OrdinalIgnoreCase)
                .Select(kv => new LanguageOption { Code = kv.Key, DisplayName = kv.Value })
                .ToList()
        };

        for (var idx = 0; idx < itemContexts.Count; idx++)
        {
            var (itemRef, content, directParentSourceGuid, directParentName, ancestorsWithUrls, deps) = itemContexts[idx];
            // The "Place Under" override from the destination-tree picker applies only to the
            // top-level item being transferred (index 0 — CollectItems always returns the
            // requested root first, then its descendants). Descendants keep resolving their
            // placement via batchGuidMap against their own direct parent, which already preserves
            // the source structure underneath wherever the root ends up — no override needed there.
            var item = await BuildPreCheckItemAsync(
                itemRef, content,
                directParentSourceGuid, directParentName, ancestorsWithUrls,
                siteRootGuid, siteRootPath,
                target, overwriteMatchingIds, batchGuidMap, batchForcedNew,
                idx == 0 ? destinationOverrideGuid : null, idx == 0 ? destinationParentName : null);

            item.Dependencies = deps;
            result.Items.Add(item);

            if (item.ContentGuid != Guid.Empty)
            {
                var targetGuid = item.Action == PreCheckAction.CreateNew ? (item.NewGuid ?? item.ContentGuid) : item.ContentGuid;
                batchGuidMap[item.ContentGuid] = targetGuid;
                if (item.Action != PreCheckAction.Overwrite)
                    batchForcedNew.Add(item.ContentGuid);
            }
        }

        return result;
    }

    // Scans a page, block, or inline PropertyBlock's properties to build the nested dependency
    // tree shown in the plan. Unchanged from the CMA-era engine — entirely local (IContentLoader),
    // no wire format involved.
    private List<DependencyNode> ScanContentDependencies(IContentData contentData, HashSet<Guid> seen)
    {
        var nodes = new List<DependencyNode>();
        if (contentData == null) return nodes;

        foreach (var prop in contentData.Property)
        {
            if (prop is PropertyContentArea area && area.Value is ContentArea contentArea)
            {
                foreach (var areaItem in contentArea.Items)
                {
                    try
                    {
                        if (IsSystemContentReference(areaItem.ContentLink)) continue;
                        var child = _contentLoader.Get<IContent>(areaItem.ContentLink, LanguageSelector.AutoDetect(true));
                        if (!seen.Add(child.ContentGuid)) continue;
                        if (child is IContentMedia)
                            nodes.Add(new DependencyNode { Name = child.Name, NodeType = GetMediaNodeType(child.Name), ContentGuid = child.ContentGuid.ToString("D") });
                        else if (child is PageData)
                            nodes.Add(new DependencyNode { Name = child.Name, NodeType = "Page", ContentGuid = child.ContentGuid.ToString("D") });
                        else
                        {
                            var blockNode = new DependencyNode { Name = child.Name, NodeType = "Block", ContentGuid = child.ContentGuid.ToString("D") };
                            blockNode.Children = ScanContentDependencies(child, seen);
                            nodes.Add(blockNode);
                        }
                    }
                    catch { }
                }
            }
            else if (prop is PropertyContentReference cref && !ContentReference.IsNullOrEmpty(cref.ContentLink))
            {
                try
                {
                    if (IsSystemContentReference(cref.ContentLink)) continue;
                    if (IsSystemPropertyName(prop.Name)) continue;
                    var child = _contentLoader.Get<IContent>(cref.ContentLink, LanguageSelector.AutoDetect(true));
                    if (!seen.Add(child.ContentGuid)) continue;
                    if (child is IContentMedia)
                        nodes.Add(new DependencyNode { Name = child.Name, NodeType = GetMediaNodeType(child.Name), ContentGuid = child.ContentGuid.ToString("D") });
                    else if (child is PageData)
                        nodes.Add(new DependencyNode { Name = child.Name, NodeType = "Page", ContentGuid = child.ContentGuid.ToString("D") });
                }
                catch { }
            }
            else if (prop is PropertyContentReferenceList crefList && crefList.Value is IList<ContentReference> crefListItems)
            {
                foreach (var refVal in crefListItems)
                {
                    try
                    {
                        if (IsSystemContentReference(refVal)) continue;
                        var child = _contentLoader.Get<IContent>(refVal, LanguageSelector.AutoDetect(true));
                        if (!seen.Add(child.ContentGuid)) continue;
                        if (child is IContentMedia)
                            nodes.Add(new DependencyNode { Name = child.Name, NodeType = GetMediaNodeType(child.Name), ContentGuid = child.ContentGuid.ToString("D") });
                        else if (child is PageData)
                            nodes.Add(new DependencyNode { Name = child.Name, NodeType = "Page", ContentGuid = child.ContentGuid.ToString("D") });
                    }
                    catch { }
                }
            }
            else if (prop.Value is BlockData inlineBlock)
            {
                try { nodes.AddRange(ScanContentDependencies(inlineBlock, seen)); }
                catch { }
            }
        }

        var seenInlineUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var contentName = (contentData as IContent)?.Name ?? "inline block";
        foreach (var prop in contentData.Property)
        {
            var typeName = prop.GetType().Name;
            if (!typeName.Contains("Xhtml", StringComparison.OrdinalIgnoreCase)) continue;

            var xhtml = prop.Value as XhtmlString;
            string html = null;
            try
            {
                if (xhtml != null) html = xhtml.ToString();
                if (string.IsNullOrEmpty(html)) html = prop.Value?.ToString();
                if (string.IsNullOrEmpty(html))
                {
                    var field = prop.GetType().GetField("_longString", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    html = field?.GetValue(prop) as string;
                }
            }
            catch (Exception ex) { _logger.LogDebug("Pre-check XHTML read failed for '{Content}'.{Prop}({Type}): {Error}", contentName, prop.Name, typeName, ex.Message); }

            if (xhtml != null)
                foreach (var fragment in xhtml.Fragments)
                {
                    if (fragment is not ContentFragment cf || cf.ContentGuid == Guid.Empty) continue;
                    if (!seenInlineUrls.Add("frag:" + cf.ContentGuid.ToString("D"))) continue;
                    string fragName = null;
                    try { fragName = cf.GetContent()?.Name; } catch { }
                    nodes.Add(new DependencyNode { Name = fragName ?? cf.ContentGuid.ToString("D"), NodeType = "InlineBlock", ContentGuid = cf.ContentGuid.ToString("D") });
                }

            if (string.IsNullOrEmpty(html)) continue;

            foreach (Match m in Regex.Matches(html, @"(?:src|href)=""([^""]+)""", RegexOptions.IgnoreCase))
            {
                var src = m.Groups[1].Value;
                var srcPath = src.Split('?')[0];

                if (srcPath.StartsWith("/link/", StringComparison.OrdinalIgnoreCase) && srcPath.EndsWith(".aspx", StringComparison.OrdinalIgnoreCase))
                {
                    if (!seenInlineUrls.Add(srcPath)) continue;
                    try
                    {
                        var linked = _urlResolver.Route(new UrlBuilder(srcPath));
                        if (linked is IContentMedia media)
                            nodes.Add(new DependencyNode { Name = media.Name ?? srcPath, NodeType = "InlineImage" });
                    }
                    catch { }
                    continue;
                }

                if (!src.Contains("/contentassets/", StringComparison.OrdinalIgnoreCase) &&
                    !src.Contains("/globalassets/", StringComparison.OrdinalIgnoreCase) &&
                    !src.Contains("/EPiServer/", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!seenInlineUrls.Add(srcPath)) continue;
                var filename = Path.GetFileName(srcPath.TrimEnd('/'));
                var versionIdx = filename.LastIndexOf(",,", StringComparison.Ordinal);
                if (versionIdx > 0) filename = filename[..versionIdx];
                if (!string.IsNullOrEmpty(filename))
                    nodes.Add(new DependencyNode { Name = filename, NodeType = "InlineImage" });
            }
        }

        return nodes;
    }

    private async Task<PreCheckItemResult> BuildPreCheckItemAsync(
        ContentReference contentRef,
        IContent content,
        Guid? directParentSourceGuid,
        string directParentName,
        List<(IContent ancestor, string url)> ancestorsWithUrls,
        Guid? siteRootGuid,
        string siteRootPath,
        DxpEnvironmentConfig target,
        bool overwriteMatchingIds,
        Dictionary<Guid, Guid> batchGuidMap,
        HashSet<Guid> batchForcedNew,
        Guid? destinationOverrideGuid = null,
        string destinationOverrideName = null)
    {
        if (content == null)
            return new PreCheckItemResult { ContentId = contentRef.ToString(), ContentName = "?", Action = PreCheckAction.Unresolvable, Notes = "Could not load source content." };

        var guid = content.ContentGuid;
        var name = content.Name;

        var existsOnTarget = await ExistsOnTargetAsync(guid, target);

        if (existsOnTarget && directParentSourceGuid.HasValue && batchForcedNew.Contains(directParentSourceGuid.Value))
            existsOnTarget = false;

        if (existsOnTarget && overwriteMatchingIds)
            return new PreCheckItemResult
            {
                ContentId = contentRef.ToString(), ContentGuid = guid, ContentName = name,
                Action = PreCheckAction.Overwrite, Notes = "Exists on target — will be overwritten in place."
            };

        Guid? parentGuid;
        string parentPath;
        if (destinationOverrideGuid.HasValue)
        {
            // Editor picked an explicit location in the destination-tree picker — takes priority
            // over both automatic ancestor-matching and batch-sibling resolution. Only ever set for
            // the top-level item (see PreCheckAsync); descendants never reach this branch.
            parentGuid = destinationOverrideGuid;
            parentPath = destinationOverrideName ?? "(selected location)";
        }
        else if (directParentSourceGuid.HasValue && batchGuidMap.TryGetValue(directParentSourceGuid.Value, out var batchParentTargetGuid))
        {
            parentGuid = batchParentTargetGuid;
            parentPath = (directParentName ?? "(parent being transferred)") + " (being transferred)";
        }
        else
        {
            (parentGuid, parentPath) = await ResolveTargetParentAsync(ancestorsWithUrls, target);
        }

        var isRootFallback = false;
        if (!parentGuid.HasValue)
        {
            parentGuid = siteRootGuid;
            parentPath = siteRootPath;
            isRootFallback = true;
        }

        if (existsOnTarget)
            return new PreCheckItemResult
            {
                ContentId = contentRef.ToString(), ContentGuid = guid, ContentName = name,
                Action = parentGuid.HasValue ? PreCheckAction.CreateNew : PreCheckAction.Unresolvable,
                NewGuid = parentGuid.HasValue ? Guid.NewGuid() : null,
                TargetParentGuid = parentGuid, TargetParentPath = parentPath, IsRootFallback = isRootFallback,
                Notes = parentGuid.HasValue
                    ? isRootFallback ? "Exists on target — overwrite off. Parent not found, will create as new copy under site root (unpublished)."
                                     : $"Exists on target — overwrite off, will create as new copy under '{parentPath}'."
                    : "Exists on target — overwrite off, but parent could not be resolved on target."
            };

        return new PreCheckItemResult
        {
            ContentId = contentRef.ToString(), ContentGuid = guid, ContentName = name,
            Action = parentGuid.HasValue ? PreCheckAction.Create : PreCheckAction.Unresolvable,
            TargetParentGuid = parentGuid, TargetParentPath = parentPath, IsRootFallback = isRootFallback,
            Notes = parentGuid.HasValue
                ? isRootFallback ? "Does not exist on target and parent not found — will be created under site root (unpublished)."
                                 : $"Does not exist on target — will be created under '{parentPath}'."
                : "Does not exist on target and site root could not be resolved."
        };
    }

    // Fetches its own token rather than taking one as a parameter — GetTokenAsync caches per
    // environment (near-free on the hot path) and transparently returns a freshly-reissued token
    // once the cached one is near/past its (short, 300s) expiry. This is what makes a long-running
    // transfer self-heal instead of every call 401ing for the rest of the job once the token
    // acquired at the start of PreCheck/Transfer goes stale — see CLAUDE.md / EnvironmentTokenService.
    private async Task<bool> ExistsOnTargetAsync(Guid guid, DxpEnvironmentConfig target)
    {
        var targetToken = await _tokenService.GetTokenAsync(target);
        var r = await _api.GetNodeAsync(target.BaseUrl, targetToken, ToKey(guid), "exists check");
        // 401/403 means the content EXISTS but our identity can't read it — treat as present.
        if (r.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return true;
        return r.IsSuccess;
    }

    // Phase 1 (GUID-based): walk up source ancestry, return the first that exists on target under
    // the same key — unchanged from the CMA-era engine's intent.
    //
    // KNOWN GAP: the CMA-era phase 2 (URL-based ancestor matching, for a target page that
    // pre-exists under a DIFFERENT guid at the same conceptual URL — e.g. content created
    // directly on the target rather than by a prior transfer) is not reimplemented. CDAPI (the
    // only thing that could resolve a URL to a key) is gone, and the new REST API has no URL
    // resolver at all. Falling through to null here means the caller's site-root fallback kicks
    // in instead of a URL match — content still gets created (under site root, unpublished)
    // rather than silently lost, just not under the "right" pre-existing parent.
    private async Task<(Guid? guid, string path)> ResolveTargetParentAsync(
        List<(IContent ancestor, string url)> ancestorsWithUrls, DxpEnvironmentConfig target)
    {
        foreach (var (ancestor, _) in ancestorsWithUrls)
        {
            if (ancestor.ContentGuid == Guid.Empty) continue;
            if (await ExistsOnTargetAsync(ancestor.ContentGuid, target))
                return (ancestor.ContentGuid, ancestor.Name);
        }
        return (null, null);
    }

    private List<IContent> BuildAncestorChain(ContentReference startRef)
    {
        var chain = new List<IContent>();
        var current = startRef;
        var visited = new HashSet<int>();
        while (!ContentReference.IsNullOrEmpty(current) && current.ID != ContentReference.RootPage.ID && visited.Add(current.ID))
        {
            try
            {
                var ancestor = _contentLoader.Get<IContent>(current, LanguageSelector.AutoDetect(true));
                chain.Add(ancestor);
                current = ancestor.ParentLink;
            }
            catch { break; }
        }
        return chain;
    }

    // ── Destination tree (manual "Place Under" picker) ──────────────────────────
    // Ported from the nOCP SaaS content-transfer tool's target-directory picker. The new REST API
    // has no URL resolver and ListItemsAsync's children carry no display name (see CmsApiClient's
    // own doc comment), so both methods below lean on the same two primitives PreCheck already
    // uses: ListItemsAsync for structural children, ListVersionsAsync per candidate for its name —
    // the same N+1-per-expand cost nOCP's own implementation accepts for the identical reason.

    // Root of the browsable tree is the true CMS root (see GetTreeRootFallback) — deliberately NOT
    // the same node PreCheck's own automatic-placement fallback uses (that's the Start page, one
    // level below this), since the tree needs to be able to show Start itself as a browsable/
    // selectable node, not double as it.
    public async Task<DestinationTreeRootResult> GetDestinationTreeRootAsync(string contentId, string targetEnvironmentName)
    {
        var settings = _settingsService.Get();
        var target = ResolveEnvironment(settings, targetEnvironmentName);
        if (target == null || !target.IsConfigured)
            return new DestinationTreeRootResult { Success = false, ErrorMessage = $"Target environment '{targetEnvironmentName}' is not configured." };

        var (siteRootGuid, siteRootPath) = GetTreeRootFallback();
        if (!siteRootGuid.HasValue)
            return new DestinationTreeRootResult { Success = false, ErrorMessage = "Could not resolve a site root to browse." };

        // Local IContentLoader/IUrlResolver work first — same thread-affinity constraint as
        // PreCheckAsync (IDatabaseExecutor is not thread-safe across an await).
        var ancestorsWithUrls = new List<(IContent ancestor, string url)>();
        var contentRef = ParseContentReference(contentId);
        if (contentRef != ContentReference.EmptyReference)
        {
            try
            {
                var content = _contentLoader.Get<IContent>(contentRef, LanguageSelector.AutoDetect(true));
                foreach (var a in BuildAncestorChain(content.ParentLink))
                {
                    string url = null;
                    try { url = _urlResolver.GetUrl(a.ContentLink); } catch { }
                    ancestorsWithUrls.Add((a, url));
                }
            }
            catch (Exception ex) { _logger.LogDebug("Destination tree root: could not load content {Ref}: {Error}", contentRef, ex.Message); }
        }

        string targetToken;
        try { targetToken = await _tokenService.GetTokenAsync(target); }
        catch (Exception ex) { return new DestinationTreeRootResult { Success = false, ErrorMessage = $"Failed to authenticate with target environment: {ex.Message}" }; }

        var rootKey = ToKey(siteRootGuid.Value);
        var rootNodeResp = await _api.GetNodeAsync(target.BaseUrl, targetToken, rootKey, "resolve destination tree root");
        // Below this point `target` (not the token fetched above) is threaded through the whole
        // tree preload — each helper re-fetches its own (cached) token right before use, same
        // reasoning as ExistsOnTargetAsync, so a token that goes stale partway through a big/deep
        // site's tree preload gets silently refreshed instead of the rest of the walk 401ing.
        if (!rootNodeResp.IsSuccess)
            return new DestinationTreeRootResult { Success = false, ErrorMessage = $"Site root does not exist on target yet: HTTP {(int)rootNodeResp.Status}" };

        // BUG FIX: ResolveChildDisplayNameAsync used to just take versions[0].DisplayName — whichever
        // locale the API happened to list first for that node, which is NOT necessarily the site's
        // default language. Confirmed live: a tree mixing Russian/German/Swedish names on an English
        // site. The first attempted fix (take the ROOT node's own `locales[0]`) regressed once the
        // tree root moved from Start to the true CMS root (see GetTreeRootFallback) — the system
        // "Root" node's own `locales` list isn't a real, master-first language list the way an actual
        // page's is, so `[0]` came out arbitrary again (confirmed live: Russian). The right source was
        // never any CONTENT's locale list at all — it's the language the current CMS user/editor is
        // actually working in, which is what the editor actually expects the tree to show. Every name
        // lookup in this tree — root and all children — resolves in this ONE locale.
        var defaultLocale = EPiServer.Globalization.ContentLanguage.PreferredCulture?.Name
            ?? System.Globalization.CultureInfo.CurrentUICulture.Name;

        var result = new DestinationTreeRootResult
        {
            Success = true,
            RootKey = rootKey,
            RootName = await ResolveChildDisplayNameAsync(target, rootKey, defaultLocale) ?? siteRootPath,
            DefaultLocale = defaultLocale
        };

        var (defaultParentGuid, defaultParentPath) = await ResolveTargetParentAsync(ancestorsWithUrls, target);
        if (defaultParentGuid.HasValue)
        {
            // BUG FIX: ResolveTargetParentAsync returns the SOURCE ancestor's own name (it's built
            // for PreCheck's plan notes, "will be created under 'X'", describing the match in terms
            // of the source content) — not whatever that node is actually called on the target under
            // its preserved key. Confirmed live: source had since renamed the page to "Alloy TrackINGS"
            // while the target's copy (created by an earlier transfer, before the rename) was still
            // "Alloy Track" — the label said one thing, the tree highlighted a differently-named row
            // for the exact same node, which read as if the picker had matched the wrong page
            // entirely. Re-resolve the label from the TARGET's own version data so it always matches
            // whatever the tree itself displays for that key.
            var defaultParentKey = ToKey(defaultParentGuid.Value);
            result.DefaultParentKey = defaultParentKey;
            result.DefaultParentName = await ResolveChildDisplayNameAsync(target, defaultParentKey, defaultLocale) ?? defaultParentPath;
            result.ExpandPath = await BuildDestinationExpandPathAsync(target, defaultParentGuid.Value, rootKey);
        }
        else
        {
            // No source ancestor exists on target under the same key — same case PreCheck's own
            // site-root fallback covers. The predicted location is the root itself.
            result.DefaultParentKey = rootKey;
            result.DefaultParentName = result.RootName;
            result.ExpandPath = new List<string> { rootKey };
        }

        // PERF: originally lazy (fetch one level per expand click) — confirmed live to feel slow
        // even on a small site, since every expand costs a ListItemsAsync plus one ListVersionsAsync
        // per child just for its name. Preloading the whole tree once here means expand/collapse in
        // the client becomes a pure local state toggle with zero further round-trips. Bounded by
        // MaxDestinationTreeNodes so a large/deep site can't turn one gadget load into thousands of
        // requests; ListDestinationChildrenAsync (the old per-level endpoint) is left in place for a
        // future hybrid fallback if that cap ever needs to matter in practice.
        var budget = new TreeBudget { Remaining = MaxDestinationTreeNodes };
        var children = await BuildDestinationSubtreeAsync(target, rootKey, defaultLocale, 1, budget);
        result.Tree = new DestinationTreeNode { Key = rootKey, Name = result.RootName, Children = children };
        result.Truncated = budget.Remaining <= 0;

        return result;
    }

    private const int MaxDestinationTreeNodes = 300;

    private sealed class TreeBudget { public int Remaining; }

    // Recursively fetches every descendant of containerKey (name + further children), fanning out
    // in parallel at each level via Task.WhenAll — both the per-child name lookups and the recursive
    // calls into grandchildren. `budget` is shared and decremented with Interlocked across the whole
    // walk (concurrent branches decrement it concurrently) so the total node count across the ENTIRE
    // tree stays bounded, not just per-level.
    private async Task<List<DestinationTreeNode>> BuildDestinationSubtreeAsync(
        DxpEnvironmentConfig target, string containerKey, string locale, int depth, TreeBudget budget)
    {
        if (depth > 20 || budget.Remaining <= 0) return new List<DestinationTreeNode>();

        var targetToken = await _tokenService.GetTokenAsync(target);
        var itemsResp = await _api.ListItemsAsync(target.BaseUrl, targetToken, containerKey, "preload destination tree");
        if (!itemsResp.IsSuccess) return new List<DestinationTreeNode>();

        var nodes = new List<DestinationTreeNode>();
        foreach (var key in ExtractItemKeys(itemsResp.Body))
        {
            if (Interlocked.Decrement(ref budget.Remaining) < 0) break;
            nodes.Add(new DestinationTreeNode { Key = key });
        }
        if (nodes.Count == 0) return nodes;

        var names = await Task.WhenAll(nodes.Select(n => ResolveChildDisplayNameAsync(target, n.Key, locale)));
        for (var i = 0; i < nodes.Count; i++)
            nodes[i].Name = names[i] ?? nodes[i].Key;

        var childSubtrees = await Task.WhenAll(nodes.Select(n => BuildDestinationSubtreeAsync(target, n.Key, locale, depth + 1, budget)));
        for (var i = 0; i < nodes.Count; i++)
            nodes[i].Children = childSubtrees[i];

        return nodes;
    }

    // Root-first chain of keys from the tree root down to (and including) targetGuid, by walking
    // `container` back from targetGuid on the TARGET side. Lets the client auto-expand every
    // ancestor of the predicted location in one shot instead of the editor clicking through each.
    private async Task<List<string>> BuildDestinationExpandPathAsync(DxpEnvironmentConfig target, Guid targetGuid, string rootKey)
    {
        var chain = new List<string>();
        var currentKey = ToKey(targetGuid);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (seen.Add(currentKey))
        {
            chain.Add(currentKey);
            if (string.Equals(currentKey, rootKey, StringComparison.OrdinalIgnoreCase)) break;
            var targetToken = await _tokenService.GetTokenAsync(target);
            var nodeResp = await _api.GetNodeAsync(target.BaseUrl, targetToken, currentKey, "walk destination ancestor chain");
            if (!nodeResp.IsSuccess || !TryExtractStringField(nodeResp.Body, "container", out var containerKey)) break;
            currentKey = containerKey;
        }
        chain.Reverse();
        return chain;
    }

    public async Task<DestinationTreeChildrenResult> ListDestinationChildrenAsync(string targetEnvironmentName, string containerKey, string locale = null)
    {
        var settings = _settingsService.Get();
        var target = ResolveEnvironment(settings, targetEnvironmentName);
        if (target == null || !target.IsConfigured)
            return new DestinationTreeChildrenResult { Success = false, ErrorMessage = $"Target environment '{targetEnvironmentName}' is not configured." };

        string targetToken;
        try { targetToken = await _tokenService.GetTokenAsync(target); }
        catch (Exception ex) { return new DestinationTreeChildrenResult { Success = false, ErrorMessage = $"Failed to authenticate with target environment: {ex.Message}" }; }

        var itemsResp = await _api.ListItemsAsync(target.BaseUrl, targetToken, containerKey, "list destination tree children");
        if (!itemsResp.IsSuccess)
            return new DestinationTreeChildrenResult { Success = false, ErrorMessage = $"Could not list children: HTTP {(int)itemsResp.Status}: {itemsResp.Body}" };

        var childKeys = ExtractItemKeys(itemsResp.Body);
        var names = await Task.WhenAll(childKeys.Select(k => ResolveChildDisplayNameAsync(target, k, locale)));

        var children = new List<DestinationTreeNode>();
        for (var i = 0; i < childKeys.Count; i++)
            children.Add(new DestinationTreeNode { Key = childKeys[i], Name = names[i] ?? childKeys[i] });

        return new DestinationTreeChildrenResult { Success = true, Children = children };
    }

    // ListItemsAsync's children carry only key/container/owner/contentType — no displayName — so
    // the friendly name needs a follow-up ListVersionsAsync per candidate (same limitation noted on
    // CmsApiClient.ListItemsAsync itself). `locale` (the tree's resolved default, from
    // GetDestinationTreeRootAsync) is requested directly so every node resolves its name in the
    // SAME language rather than whichever locale the API lists first for that particular node. Falls
    // back to the unfiltered first version if the node has no version in that locale at all (e.g. a
    // page that was only ever translated into other languages), so a name still shows rather than
    // nothing.
    private async Task<string> ResolveChildDisplayNameAsync(DxpEnvironmentConfig target, string key, string locale)
    {
        var token = await _tokenService.GetTokenAsync(target);
        if (!string.IsNullOrEmpty(locale))
        {
            var filtered = await _api.ListVersionsAsync(target.BaseUrl, token, key, "resolve destination tree node name", locale);
            if (filtered.IsSuccess)
            {
                var filteredVersions = ParseVersionList(filtered.Body);
                if (filteredVersions.Count > 0) return filteredVersions[0].DisplayName;
            }
        }

        var resp = await _api.ListVersionsAsync(target.BaseUrl, token, key, "resolve destination tree node name (fallback — no version in default locale)");
        if (!resp.IsSuccess) return null;
        var versions = ParseVersionList(resp.Body);
        return versions.Count > 0 ? versions[0].DisplayName : null;
    }

    private static List<string> ExtractItemKeys(string json)
    {
        var keys = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                foreach (var item in items.EnumerateArray())
                    if (item.TryGetProperty("key", out var k) && k.ValueKind == JsonValueKind.String)
                        keys.Add(k.GetString());
        }
        catch { }
        return keys;
    }

    // ── Transfer ──────────────────────────────────────────────────────────────

    public async Task<TransferResult> TransferAsync(
        string contentId,
        string targetEnvironmentName,
        bool includeChildren,
        string sourceEnvironmentName,
        string transferStatus = "Published",
        List<PreCheckItemResult> plan = null,
        Action onItemComplete = null,
        IReadOnlyCollection<string> selectedLanguages = null)
    {
        _logger.LogInformation("DXP Content Transfer starting — build {Build}", BuildMarker);
        var settings = _settingsService.Get();
        var target = ResolveEnvironment(settings, targetEnvironmentName);
        var source = ResolveEnvironment(settings, sourceEnvironmentName);

        if (target == null || !target.IsConfigured)
            return new TransferResult { Success = false, ErrorMessage = $"Target environment '{targetEnvironmentName}' is not configured." };
        if (source == null || !source.IsConfigured)
            return new TransferResult { Success = false, ErrorMessage = $"Source environment '{sourceEnvironmentName}' is not configured. Ensure credentials are saved in settings." };

        var contentRef = ParseContentReference(contentId);
        if (contentRef == ContentReference.EmptyReference)
            return new TransferResult { Success = false, ErrorMessage = $"Invalid content reference: {contentId}" };

        // Pre-load ALL IContentLoader/IUrlResolver data synchronously BEFORE the first await.
        var itemContexts = CollectItems(contentRef, includeChildren)
            .Select(itemRef =>
            {
                IContent content = null;
                try { content = _contentLoader.Get<IContent>(itemRef, LanguageSelector.AutoDetect(true)); }
                catch (Exception ex) { _logger.LogDebug("Transfer: could not load content {Ref}: {Error}", itemRef, ex.Message); }

                string contentName = null;
                Guid sourceGuid = Guid.Empty;
                var ancestorsWithUrls = new List<(IContent ancestor, string url)>();
                if (content != null)
                {
                    contentName = content.Name;
                    sourceGuid = content.ContentGuid;
                    foreach (var a in BuildAncestorChain(content.ParentLink))
                    {
                        string url = null;
                        try { url = _urlResolver.GetUrl(a.ContentLink); }
                        catch { }
                        ancestorsWithUrls.Add((a, url));
                    }
                }
                return (itemRef, content, contentName, sourceGuid, ancestorsWithUrls);
            })
            .ToList();

        // All IContentLoader work done — now safe to await. As with PreCheckAsync, these are just
        // up-front auth sanity checks — a transfer can take far longer than the new API's 300s
        // token lifetime for a large tree, so the tokens fetched here are deliberately NOT threaded
        // through the transfer below. Every call site down in TransferSingleItemAsync/
        // TransferItemCoreAsync/etc. re-fetches its own (cached) token from IEnvironmentTokenService
        // right before use instead, so a token going stale mid-transfer is silently reissued rather
        // than 401ing every subsequent request for the rest of the job.
        try { await _tokenService.GetTokenAsync(target); }
        catch (Exception ex) { return new TransferResult { Success = false, ErrorMessage = $"Failed to authenticate with target environment: {ex.Message}" }; }

        try { await _tokenService.GetTokenAsync(source); }
        catch (Exception ex) { return new TransferResult { Success = false, ErrorMessage = $"Failed to authenticate with source environment: {ex.Message}" }; }

        var planLookup = plan?.ToDictionary(p => p.ContentId, StringComparer.OrdinalIgnoreCase) ?? new Dictionary<string, PreCheckItemResult>();
        var result = new TransferResult { Success = true };
        var languageFilter = selectedLanguages == null ? null : new HashSet<string>(selectedLanguages, StringComparer.OrdinalIgnoreCase);

        foreach (var ctx in itemContexts)
        {
            planLookup.TryGetValue(ctx.itemRef.ToString(), out var planItem);
            var itemResult = await TransferSingleItemAsync(
                ctx.itemRef, ctx.content, ctx.contentName, ctx.sourceGuid, ctx.ancestorsWithUrls,
                source, target,
                planItem, transferStatus, onItemComplete, languageFilter);
            result.Items.Add(itemResult);
            if (!itemResult.Success) result.Success = false;
        }

        result.TransferredCount = result.Items.Count(i => i.Success);
        return result;
    }

    private async Task<TransferItemResult> TransferSingleItemAsync(
        ContentReference contentRef, IContent content, string contentName, Guid sourceGuid,
        List<(IContent ancestor, string url)> ancestorsWithUrls,
        DxpEnvironmentConfig source, DxpEnvironmentConfig target,
        PreCheckItemResult planItem, string transferStatus, Action onItemComplete, HashSet<string> languageFilter)
    {
        if (content == null)
            return new TransferItemResult { ContentId = contentRef.ToString(), Success = false, ErrorMessage = "Could not load content." };

        var targetGuid = (planItem?.Action == PreCheckAction.CreateNew) ? (planItem.NewGuid ??= Guid.NewGuid()) : sourceGuid;

        Guid targetParentGuid;
        if (planItem?.TargetParentGuid.HasValue == true)
            targetParentGuid = planItem.TargetParentGuid.Value;
        else if (planItem?.Action == PreCheckAction.Overwrite)
        {
            // Overwrite: item already exists — keep its current container/owner on target rather
            // than re-deriving one, exactly as the CMA-era engine did.
            var targetToken = await _tokenService.GetTokenAsync(target);
            var existingNode = await _api.GetNodeAsync(target.BaseUrl, targetToken, ToKey(sourceGuid), "read existing target parent");
            targetParentGuid = existingNode.IsSuccess ? (ExtractGuidField(existingNode.Body, "container") ?? ExtractGuidField(existingNode.Body, "owner") ?? Guid.Empty) : Guid.Empty;
        }
        else
        {
            var (parentGuid, _) = await ResolveTargetParentAsync(ancestorsWithUrls, target);
            targetParentGuid = parentGuid ?? Guid.Empty;
        }

        var visited = new HashSet<Guid> { sourceGuid };
        var failedDependencyGuids = new List<string>();

        try
        {
            var effectiveStatus = transferStatus == "CheckedOut" ? "CheckedOut" : "Published";
            await TransferItemCoreAsync(sourceGuid, source, target,
                targetGuid, targetParentGuid, effectiveStatus, visited, onItemComplete, failedDependencyGuids, languageFilter);

            return new TransferItemResult
            {
                ContentId = contentRef.ToString(), ContentName = contentName, Success = true,
                TargetContentId = ToKey(targetGuid), TargetBaseUrl = target.BaseUrl,
                FailedDependencyGuids = failedDependencyGuids
            };
        }
        catch (Exception ex)
        {
            onItemComplete?.Invoke();
            return new TransferItemResult { ContentId = contentRef.ToString(), ContentName = contentName, Success = false, ErrorMessage = $"Transfer failed: {ex.Message}", FailedDependencyGuids = failedDependencyGuids };
        }
    }

    // Core recursive transfer. Order of operations per item:
    //   1. Read the source node + its default-locale version.
    //   2. Recursively transfer every block/media this version's properties reference
    //      (ContentArea items + single references + inline epi-contentfragment blocks),
    //      depth-first, so they exist on the target before the parent references them.
    //   3. Ensure the target container/owner chain exists (key-preserving; see
    //      EnsureContainerExistsAsync).
    //   4. Create (or add a new version to) the target item under that same key, with the
    //      default-locale properties copied over — rewriting only inline-image src values that
    //      need it (see RewriteInlineImagesAsync) — and a bounded strip-and-retry loop for
    //      properties the target rejects.
    //   5. Write every OTHER language this item has (subject to languageFilter) as an additional
    //      version, same properties-copy logic, no shared/invariant filtering needed.
    private async Task TransferItemCoreAsync(
        Guid sourceGuid, DxpEnvironmentConfig source, DxpEnvironmentConfig target,
        Guid targetGuid, Guid targetParentGuid, string effectiveStatus,
        HashSet<Guid> visited, Action onItemComplete, List<string> failedDependencyGuids, HashSet<string> languageFilter,
        string fallbackLocale = null)
    {
        // Tokens are fetched fresh (from IEnvironmentTokenService's cache) at each point of use
        // throughout this method and everything it calls, rather than once up front — a deep/wide
        // transfer can run well past the new API's 300s token lifetime, and a token fetched once at
        // the start of TransferAsync would otherwise go stale partway through and 401 every
        // subsequent call for the rest of the job. See CLAUDE.md / EnvironmentTokenService.
        var sourceKey = ToKey(sourceGuid);
        var sourceToken = await _tokenService.GetTokenAsync(source);
        var nodeResp = await _api.GetNodeAsync(source.BaseUrl, sourceToken, sourceKey, "read source node");
        if (!nodeResp.IsSuccess)
            throw new HttpRequestException($"Could not read source node {sourceKey}: HTTP {(int)nodeResp.Status}: {nodeResp.Body}");

        var isOwned = TryExtractStringField(nodeResp.Body, "owner", out var sourceOwnerKey);
        TryExtractStringField(nodeResp.Body, "container", out var sourceContainerKey);
        var isContained = !isOwned && !string.IsNullOrEmpty(sourceContainerKey);
        var contentType = ExtractStringField(nodeResp.Body, "contentType");

        sourceToken = await _tokenService.GetTokenAsync(source);
        var versionsResp = await _api.ListVersionsAsync(source.BaseUrl, sourceToken, sourceKey, "read source versions");
        if (!versionsResp.IsSuccess)
            throw new HttpRequestException($"Could not read source versions for {sourceKey}: HTTP {(int)versionsResp.Status}: {versionsResp.Body}");

        var versions = ParseVersionList(versionsResp.Body);
        if (versions.Count == 0)
        {
            // No versions at all — a non-versionable container/folder. Just ensure it (and its
            // container chain) exists on target under the same key; nothing to write.
            await EnsureContainerExistsAsync(targetGuid, source, target, new HashSet<Guid>(), fallbackLocale);
            onItemComplete?.Invoke();
            return;
        }

        var locales = ExtractStringArrayField(nodeResp.Body, "locales");
        var defaultLocale = locales.Count > 0 ? locales[0] : versions[0].Locale;
        // BUG FIX: a genuinely invariant/non-localized item (confirmed live: a shared image and a
        // shared block referenced from a ContentArea) can come back with an empty `locales` array
        // AND a null `locale` on its own version — there's simply no locale concept for it. Forwarding
        // that null straight through to the target 400s ("The 'locale' field does not allow 'null'
        // values.") and the whole item silently fails to transfer, which is why a ContentArea item
        // (and anything it in turn referenced, like an inline image) never showed up on target even
        // though nothing about ITS OWN properties was wrong. Falling back to the locale of whatever
        // is transferring THIS item (ultimately the top-level page/item's own locale) gives the
        // target write a value it will accept, and is the least-surprising choice — an invariant
        // block ends up written under the same locale as the content that references it.
        if (string.IsNullOrEmpty(defaultLocale)) defaultLocale = fallbackLocale;
        foreach (var v in versions)
            if (string.IsNullOrEmpty(v.Locale)) v.Locale = defaultLocale;
        var masterVersion = versions.FirstOrDefault(v => string.Equals(v.Locale, defaultLocale, StringComparison.OrdinalIgnoreCase)) ?? versions[0];

        // ── Step 1: transfer referenced dependencies depth-first ──────────────
        var isMedia = IsMediaContentType(contentType);
        if (!isMedia)
            await ProcessReferencedDependenciesAsync(masterVersion.PropertiesJson, source, target, visited, onItemComplete, failedDependencyGuids, languageFilter, defaultLocale);

        // BUG FIX: an existing media item has nothing meaningful left to do — key preservation
        // already guarantees its binary is correct (it was uploaded once, under this same key, the
        // first time it was transferred), and CmsApiClient has no "add a version WITH new binary"
        // call (CreateVersionAsync is JSON-only, for non-media property updates). Falling through to
        // the normal write path for an existing media item means attempting a binary-less version
        // add, which the target rejects outright — confirmed live: a page's image that had already
        // been transferred in an earlier run threw a genuine HTTP 400 on every subsequent transfer
        // of the page that referenced it, even though the asset was already present and fine on
        // target, and got reported to the editor as a hard failure for something that wasn't broken.
        if (isMedia && await ExistsOnTargetAsync(targetGuid, target))
        {
            onItemComplete?.Invoke();
            return;
        }

        // ── Step 2: ensure the target owner/container chain exists ────────────
        Guid effectiveParent;
        bool useOwner;
        if (isOwned && Guid.TryParseExact(sourceOwnerKey, "N", out var ownerGuid))
        {
            // Page-local content: owner is always something already in this transfer's ancestry
            // (the page itself), so it will already exist by the time we get here.
            effectiveParent = ownerGuid;
            useOwner = true;
        }
        else if (targetParentGuid != Guid.Empty)
        {
            // BUG FIX: this used to be the LAST branch (source-container-mirroring took priority
            // whenever isContained was true, which is true for nearly every normal page). That
            // silently discarded whatever parent TransferSingleItemAsync actually resolved — the
            // batch/plan's TargetParentGuid, which is where an editor's manual "Place Under" override
            // (or even automatic ancestor-matching, whenever it differs from the source's literal
            // parent) lives. Confirmed live: picking "About us" in the destination-tree picker still
            // put the page under Start, because the source's own `container` field (Start) won every
            // time. A non-empty targetParentGuid ONLY ever reaches here from TransferSingleItemAsync
            // (top-level items in the transfer batch, each with an explicitly resolved parent) —
            // ProcessReferencedDependenciesAsync always passes Guid.Empty for its recursive
            // block/media calls specifically so THEY keep mirroring the source structure below. So
            // this branch can never wrongly fire for a dependency; only for the item(s) the editor
            // actually picked a destination for.
            effectiveParent = targetParentGuid;
            useOwner = false;
        }
        else if (isContained && Guid.TryParseExact(sourceContainerKey, "N", out var containerGuid) && containerGuid != Guid.Empty)
        {
            // A dependency (block/media, or a top-level item with no resolvable parent at all) —
            // mirror its source container/folder chain, creating any missing link under the same key.
            await EnsureContainerExistsAsync(containerGuid, source, target, new HashSet<Guid> { sourceGuid }, fallbackLocale);
            effectiveParent = containerGuid;
            useOwner = false;
        }
        else
        {
            effectiveParent = targetParentGuid;
            useOwner = false;
        }

        // ── Step 3: create or add-version on target for the default locale ────
        var exists = await ExistsOnTargetAsync(targetGuid, target);
        await WriteContentVersionAsync(
            targetGuid, exists, contentType, useOwner, effectiveParent, masterVersion,
            source, target, effectiveStatus, isMedia, failedDependencyGuids);

        // ── Step 4: every other language ───────────────────────────────────────
        foreach (var version in versions)
        {
            if (string.Equals(version.Locale, masterVersion.Locale, StringComparison.OrdinalIgnoreCase)) continue;
            if (languageFilter != null && languageFilter.Count > 0 && !languageFilter.Contains(version.Locale)) continue;

            // A branch can reference dependencies the master pass never saw (e.g. a block only
            // used in a culture-specific ContentArea) — walk it too.
            if (!isMedia)
                await ProcessReferencedDependenciesAsync(version.PropertiesJson, source, target, visited, onItemComplete, failedDependencyGuids, languageFilter, defaultLocale);

            try
            {
                await WriteContentVersionAsync(
                    targetGuid, true, contentType, useOwner, effectiveParent, version,
                    source, target, effectiveStatus, isMedia, failedDependencyGuids);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Could not write language branch '{Locale}' for {Guid}: {Error}", version.Locale, targetGuid, ex.Message);
            }
        }

        onItemComplete?.Invoke();
    }

    // Walks a version's properties for content references (ContentArea items + single references
    // + inline epi-contentfragment blocks) and transfers each one depth-first, skipping anything
    // that isn't a block or media (page references are tracked, not auto-transferred — matches
    // the CMA-era engine's behaviour). `visited` dedups across the whole item transfer, including
    // branches, so a dependency shared by several languages is only actually written once.
    private async Task ProcessReferencedDependenciesAsync(
        string propertiesJson, DxpEnvironmentConfig source, DxpEnvironmentConfig target,
        HashSet<Guid> visited, Action onItemComplete, List<string> failedDependencyGuids, HashSet<string> languageFilter,
        string fallbackLocale)
    {
        var refGuids = ExtractPropertyReferenceGuids(propertiesJson);
        foreach (var frag in XhtmlProcessor.ExtractXhtmlContentFragments(propertiesJson))
            if (frag.Guid != Guid.Empty && !refGuids.Contains(frag.Guid))
                refGuids.Add(frag.Guid);

        // BUG FIX: inline <img>/<a href> assets embedded in rich text (permalinks, e.g.
        // /link/{guid}.aspx) were never scanned at all — ExtractXhtmlImageUrls exists (carried
        // over unchanged from the CMA-era XhtmlProcessor) but nothing called it, so every inline
        // image/linked asset in rich text silently failed to transfer: RewriteInlineImages still
        // ran and wrote out a permalink pointing at the SOURCE guid, but that guid was never
        // created on the target, leaving a broken image. Confirmed live via an actual gadget
        // transfer before this fix.
        foreach (var url in XhtmlProcessor.ExtractXhtmlImageUrls(propertiesJson))
        {
            if (!TryParsePermalinkGuid(url, out var inlineGuid)) continue;
            if (!refGuids.Contains(inlineGuid)) refGuids.Add(inlineGuid);
        }

        foreach (var refGuid in refGuids)
        {
            if (!visited.Add(refGuid)) continue;

            var refKey = ToKey(refGuid);
            var sourceToken = await _tokenService.GetTokenAsync(source);
            var refNodeResp = await _api.GetNodeAsync(source.BaseUrl, sourceToken, refKey, "read referenced source node");
            if (!refNodeResp.IsSuccess)
            {
                // Previously silent — a required reference that couldn't even be READ from source
                // (as opposed to failing to WRITE on target, already logged at the TransferItemCoreAsync
                // catch below) left no trace at all of why it never got attempted, only the eventual
                // "Property 'X' is required" from the referencing item's own stripped write.
                _logger.LogWarning("Could not read referenced dependency {Guid} from source: HTTP {Status}", refGuid, (int)refNodeResp.Status);
                failedDependencyGuids?.Add($"{refKey} (could not read from source: HTTP {(int)refNodeResp.Status})");
                visited.Remove(refGuid);
                continue;
            }

            var refContentType = ExtractStringField(refNodeResp.Body, "contentType");
            if (IsPageContentType(refContentType)) continue; // tracked, not transferred

            if (!IsMediaContentType(refContentType) && !IsBlockContentType(refContentType)) continue;

            try
            {
                await TransferItemCoreAsync(refGuid, source, target,
                    refGuid, Guid.Empty, "Published", visited, onItemComplete, failedDependencyGuids, languageFilter, fallbackLocale);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Could not transfer dependency {Guid}: {Error}", refGuid, ex.Message);
                failedDependencyGuids?.Add(refGuid.ToString("D"));
                onItemComplete?.Invoke();
            }
        }
    }

    // Ensures a container chain exists on the target, creating any missing link (and its own
    // container first, recursively) using the SAME key as the source. Replaces the CMA-era
    // engine's URL-based EnsureGlobalAssetFolderPathAsync/EnsureContentParentAsync entirely — no
    // URL resolution needed at all when the target key is always known in advance.
    private async Task EnsureContainerExistsAsync(
        Guid containerGuid, DxpEnvironmentConfig source, DxpEnvironmentConfig target, HashSet<Guid> seen,
        string fallbackLocale = null)
    {
        if (!seen.Add(containerGuid)) return;
        if (await ExistsOnTargetAsync(containerGuid, target)) return;

        var key = ToKey(containerGuid);
        var sourceToken = await _tokenService.GetTokenAsync(source);
        var nodeResp = await _api.GetNodeAsync(source.BaseUrl, sourceToken, key, "read missing container");
        if (!nodeResp.IsSuccess)
        {
            _logger.LogWarning("Could not read missing container {Guid} from source: HTTP {Status}", containerGuid, (int)nodeResp.Status);
            return;
        }

        var contentType = ExtractStringField(nodeResp.Body, "contentType");
        Guid? grandparentGuid = TryExtractStringField(nodeResp.Body, "container", out var gp) && Guid.TryParseExact(gp, "N", out var gpg) ? gpg : null;
        if (grandparentGuid.HasValue && grandparentGuid.Value != Guid.Empty)
            await EnsureContainerExistsAsync(grandparentGuid.Value, source, target, seen, fallbackLocale);

        // KNOWN GAP: if this container has no container/owner of its own (a true root-level
        // container, e.g. the site root itself), it can't be created via this path at all —
        // content_create requires exactly one of container/owner, and any CMS installation
        // capable of running this transfer already has its own root containers, so this should
        // never actually fire in practice. Skip rather than send an invalid request.
        if (!grandparentGuid.HasValue || grandparentGuid.Value == Guid.Empty)
        {
            _logger.LogWarning("Missing container {Guid} has no container/owner of its own on source — cannot create it on target (likely a root-level container that should already exist)", containerGuid);
            return;
        }

        sourceToken = await _tokenService.GetTokenAsync(source);
        var versionsResp = await _api.ListVersionsAsync(source.BaseUrl, sourceToken, key, "read missing container versions");
        var versions = versionsResp.IsSuccess ? ParseVersionList(versionsResp.Body) : new List<SourceVersion>();

        var createJson = new JsonObject
        {
            ["contentType"] = contentType,
            ["key"] = key,
            ["container"] = ToKey(grandparentGuid.Value)
        };
        if (versions.Count > 0)
        {
            var v = versions[0];
            // A container/folder node's own version can come back with a null locale (there's
            // simply no locale concept for it, same gap noted in ProcessReferencedDependenciesAsync)
            // -- forwarding that straight through 400s the create ("The 'locale' field does not
            // allow 'null' values"), which then cascades into every child that needs this container
            // as its parent failing with "Unable to find a content item with the key" since it was
            // never actually created. Fall back to the locale of whatever's transferring THIS
            // container, same as the main content-write path does.
            var locale = string.IsNullOrEmpty(v.Locale) ? fallbackLocale : v.Locale;
            createJson["initialVersion"] = new JsonObject
            {
                ["displayName"] = v.DisplayName,
                ["locale"] = locale,
                ["properties"] = JsonNode.Parse(v.PropertiesJson)
            };
        }

        try
        {
            var targetToken = await _tokenService.GetTokenAsync(target);
            var resp = await _api.CreateContentAsync(target.BaseUrl, targetToken, createJson.ToJsonString(), "create missing container");
            if (!resp.IsSuccess && resp.Status != HttpStatusCode.Conflict)
                _logger.LogWarning("Could not create missing container {Guid} on target: HTTP {Status}: {Body}", containerGuid, (int)resp.Status, resp.Body);
            else
                _logger.LogDebug("Created missing container {Guid} on target", containerGuid);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not create missing container {Guid} on target: {Error}", containerGuid, ex.Message);
        }
    }

    // Creates the item on target (if it doesn't exist yet) or adds a new version to it (if it
    // does), for one locale. Handles media (binary upload) vs. regular content, inline-image
    // rewriting, and the bounded strip-and-retry loop for properties the target rejects.
    private async Task WriteContentVersionAsync(
        Guid targetGuid, bool targetExists, string contentType, bool useOwner, Guid parentGuid, SourceVersion version,
        DxpEnvironmentConfig source, DxpEnvironmentConfig target,
        string effectiveStatus, bool isMedia, List<string> failedDependencyGuids)
    {
        var key = ToKey(targetGuid);
        var propertiesNode = (JsonNode.Parse(version.PropertiesJson) as JsonObject) ?? new JsonObject();
        RewriteInlineImages(propertiesNode);

        var published = effectiveStatus == "Published" ? DateTimeOffset.UtcNow : (DateTimeOffset?)null;

        byte[] mediaBytes = null;
        string mediaFileName = null, mediaMimeType = null;
        if (isMedia && !targetExists)
        {
            (mediaBytes, mediaFileName, mediaMimeType) = await DownloadMediaAsync(source, ToKey(ToGuid(key)), version);
        }

        for (var attempt = 0; attempt < MaxWriteAttempts; attempt++)
        {
            // Fetched fresh each attempt (cached, near-free) rather than once before the loop — a
            // bounded strip-and-retry loop is normally fast, but this keeps the write self-healing
            // even in the rare case a token expires mid-retry.
            var targetToken = await _tokenService.GetTokenAsync(target);
            CmsApiResponse resp;
            if (!targetExists)
            {
                // KNOWN GOTCHA (confirmed live): optional fields like routeSegment/published must
                // be OMITTED, not sent as explicit JSON null — "The 'routeSegment' field does not
                // allow 'null' values." SetIfNotNull below handles this consistently.
                var initialVersion = new JsonObject { ["displayName"] = version.DisplayName, ["locale"] = version.Locale };
                SetIfNotNull(initialVersion, "routeSegment", version.RouteSegment);
                SetIfNotNull(initialVersion, "published", published?.ToString("O"));
                initialVersion["properties"] = propertiesNode.DeepClone();

                var createJson = new JsonObject { ["contentType"] = contentType, ["key"] = key, ["initialVersion"] = initialVersion };
                if (useOwner) createJson["owner"] = ToKey(parentGuid);
                else createJson["container"] = ToKey(parentGuid);

                resp = mediaBytes != null
                    ? await _api.CreateContentWithBinaryAsync(target.BaseUrl, targetToken, createJson.ToJsonString(), mediaBytes, mediaFileName, mediaMimeType, "create content (media)")
                    : await _api.CreateContentAsync(target.BaseUrl, targetToken, createJson.ToJsonString(), "create content");

                if (resp.Status == HttpStatusCode.Conflict)
                {
                    // Already exists (race, or a previous partial run) — fall through to a version write.
                    targetExists = true;
                    continue;
                }
            }
            else
            {
                var versionJson = new JsonObject { ["locale"] = version.Locale, ["displayName"] = version.DisplayName };
                SetIfNotNull(versionJson, "routeSegment", version.RouteSegment);
                SetIfNotNull(versionJson, "published", published?.ToString("O"));
                versionJson["properties"] = propertiesNode.DeepClone();
                resp = await _api.CreateVersionAsync(target.BaseUrl, targetToken, key, versionJson.ToJsonString(), "create version");
            }

            if (resp.IsSuccess)
            {
                if (effectiveStatus == "Published")
                    await PublishLatestVersionAsync(target, key, version.Locale);
                return;
            }

            if (resp.Status == HttpStatusCode.BadRequest)
            {
                // BUG FIX: routeSegment (the page's "Name in URL" slug) must be unique among its
                // target siblings, but the source's slug can collide with something that already
                // exists on the target under a COMPLETELY DIFFERENT key — e.g. a page created
                // directly on target, or a same-named page transferred earlier under a different
                // guid. Confirmed live: "\"Name in URL\" with value \"alloy-track\" is already in
                // use by Alloy Track (8)." routeSegment is optional — SetIfNotNull already omits it
                // when empty — so dropping it here and letting the target auto-generate a unique
                // slug (exactly what the CMS UI does when you save a page with a colliding name) is
                // the right recovery, rather than failing the whole item outright. This has to be
                // checked before ExtractOffendingField below because "routeSegment" isn't a
                // "properties.X" field at all, so that path never even looks at it.
                if (HasRouteSegmentConflict(resp.Body) && !string.IsNullOrEmpty(version.RouteSegment))
                {
                    _logger.LogDebug("routeSegment '{Slug}' conflicts on target for {Key} — omitting it and retrying ({Attempt}/{Max})", version.RouteSegment, key, attempt + 1, MaxWriteAttempts);
                    version.RouteSegment = null;
                    failedDependencyGuids?.Add($"{key}:routeSegment (value omitted — conflicted with existing target content)");
                    continue;
                }

                // The new API reports both "unknown property" (target's content type doesn't
                // recognise it — a schema drift between environments, CMA's old PropertyNotFound
                // case) and "required property is missing" (the SAME error shape). Stripping is
                // the right fix for the first case. For the second, it is only a best-effort
                // degradation, not a real fix — a required property should almost never actually
                // be empty here, since propertiesNode is copied verbatim from a source version
                // that already passed this same validation when it was saved; if this DOES fire in
                // practice it is most likely a required reference whose target failed to transfer
                // (e.g. a circular dependency). CMA's engine handled that case with fallback-value
                // substitution and a deferred second-pass patch once the reference existed —
                // deliberately NOT reimplemented here (see CLAUDE.md known gaps). Stripping still
                // lets the rest of the item transfer rather than failing it outright; the omission
                // is tracked in failedDependencyGuids so the UI surfaces it.
                //
                // BUG FIX: when the bad value is ONE item inside a ContentArea (e.g. a reference to
                // a dependency that itself couldn't be resolved/transferred — confirmed live via a
                // ContentArea item, "New Teaser", that was unresolvable on the source and silently
                // dropped by the dependency walk, leaving a dangling reference in the parent's
                // ContentArea array), stripping the WHOLE top-level property throws away every OTHER
                // item in that array too — confirmed live: a 3-item ContentArea with only one bad
                // item came through on target completely empty. ExtractOffendingField now also
                // reports the array index when the field path is "Prop.value[N]..."; when present
                // and that property is actually an array, remove just that one element instead of
                // the whole property, preserving the good items.
                var (badField, badIndex) = ExtractOffendingField(resp.Body);
                if (badField != null && propertiesNode.TryGetPropertyValue(badField, out var badPropNode) && badPropNode is JsonObject badPropObj)
                {
                    if (badIndex.HasValue && badPropObj["value"] is JsonArray badArray && badIndex.Value >= 0 && badIndex.Value < badArray.Count)
                    {
                        // LogWarning, not LogDebug, and includes the target's actual rejection body:
                        // this is the ONLY place the real reason a property got stripped is ever
                        // recorded. Confirmed live: a required ContentReference (Venue) stripped here
                        // for a genuinely broken dependency surfaced downstream only as "Property
                        // 'Venue' is required" — the FINAL retry's error, not the original rejection
                        // reason — because nothing logged resp.Body at the point of stripping.
                        _logger.LogWarning("Stripping array item [{Index}] from '{Prop}' on {Key} and retrying ({Attempt}/{Max}) — target rejected it: {Body}", badIndex.Value, badField, key, attempt + 1, MaxWriteAttempts, resp.Body);
                        badArray.RemoveAt(badIndex.Value);
                        failedDependencyGuids?.Add($"{key}:{badField}[{badIndex.Value}] (array item omitted — target rejected it: {resp.Body})");
                        continue;
                    }

                    _logger.LogWarning("Stripping property '{Prop}' from {Key} and retrying ({Attempt}/{Max}) — target rejected it: {Body}", badField, key, attempt + 1, MaxWriteAttempts, resp.Body);
                    propertiesNode.Remove(badField);
                    failedDependencyGuids?.Add($"{key}:{badField} (property omitted — target rejected it: {resp.Body})");
                    continue;
                }
            }

            throw new HttpRequestException($"HTTP {(int)resp.Status} writing {key}: {resp.Body}");
        }

        throw new HttpRequestException($"Write failed for {key} after {MaxWriteAttempts} attempts resolving property errors");
    }

    // Re-lists versions rather than parsing the write response directly, since content_create and
    // content_createversion return differently-shaped bodies (NewContentNode vs. bare
    // ContentVersion) — listing is uniform for both and avoids guessing at which shape applies.
    // The highest version number for the locale is the one just written (versions only increase).
    private async Task PublishLatestVersionAsync(DxpEnvironmentConfig target, string key, string locale)
    {
        var token = await _tokenService.GetTokenAsync(target);
        var resp = await _api.ListVersionsAsync(target.BaseUrl, token, key, "find version to publish", locale);
        if (!resp.IsSuccess) return;
        var candidates = ParseVersionList(resp.Body)
            .Where(v => string.Equals(v.Locale, locale, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (candidates.Count == 0) return;
        var latest = candidates.OrderByDescending(v => int.TryParse(v.VersionNumber, out var n) ? n : 0).First();

        token = await _tokenService.GetTokenAsync(target);
        var pubResp = await _api.PublishVersionAsync(target.BaseUrl, token, key, latest.VersionNumber, "publish version");
        if (!pubResp.IsSuccess)
            _logger.LogWarning("Could not publish version {Version} of {Key}: HTTP {Status}: {Body}", latest.VersionNumber, key, (int)pubResp.Status, pubResp.Body);
    }

    private async Task<(byte[] bytes, string fileName, string mimeType)> DownloadMediaAsync(DxpEnvironmentConfig source, string key, SourceVersion version)
    {
        try
        {
            var sourceToken = await _tokenService.GetTokenAsync(source);
            var (status, bytes, contentType) = await _api.GetMediaBinaryAsync(source.BaseUrl, sourceToken, key, version.VersionNumber, "download media");
            if (status != HttpStatusCode.OK || bytes == null) return (null, null, null);
            var fileName = ResolveAssetFileName(version.DisplayName, version.RouteSegment, contentType);
            return (bytes, fileName, contentType ?? "application/octet-stream");
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not download media binary for {Key}: {Error}", key, ex.Message);
            return (null, null, null);
        }
    }

    // The new API rejects an explicit JSON null for several optional fields ("The 'X' field does
    // not allow 'null' values.", confirmed live for routeSegment) — the key must be OMITTED
    // entirely, not present-with-null. JsonObject's indexer sets a literal null, so every
    // optional field goes through this instead.
    private static void SetIfNotNull(JsonObject obj, string key, string value)
    {
        if (!string.IsNullOrEmpty(value)) obj[key] = value;
    }

    private static string ResolveAssetFileName(string displayName, string routeSegment, string mimeType)
    {
        var name = !string.IsNullOrEmpty(displayName) ? displayName : "asset";
        if (Path.HasExtension(name)) return name;
        var ext = !string.IsNullOrEmpty(routeSegment) ? Path.GetExtension(routeSegment) : null;
        if (string.IsNullOrEmpty(ext)) ext = ExtensionForMimeType(mimeType);
        return string.IsNullOrEmpty(ext) ? name : name + ext;
    }

    private static string ExtensionForMimeType(string mimeType) => mimeType?.ToLowerInvariant() switch
    {
        "image/jpeg" or "image/jpg" => ".jpg",
        "image/png" => ".png",
        "image/gif" => ".gif",
        "image/webp" => ".webp",
        "image/svg+xml" => ".svg",
        "image/bmp" => ".bmp",
        "image/tiff" => ".tiff",
        "image/x-icon" or "image/vnd.microsoft.icon" => ".ico",
        "image/avif" => ".avif",
        "image/heic" => ".heic",
        "application/pdf" => ".pdf",
        _ => null
    };

    // Rewrites inline image src/href values in RichText properties to the environment-agnostic
    // "/link/{guid}.aspx" permalink form. This is a deliberate simplification over the CMA-era
    // engine's xhtmlUrlMap/xhtmlContentIdMap rewriting: since content keys are preserved across
    // environments, a permalink is ALREADY correct on the target with zero lookups — no CDV, no
    // numeric-id remap (which isn't computable in the new API anyway; see the KNOWN GAP note at
    // the top of this file). Only genuinely GUID-resolvable sources are rewritten (existing
    // permalinks are left as-is; a ",,{id}" edit-mode URL is resolved locally since the source is
    // always the environment this code runs on). A src that can't be resolved to a GUID at all
    // (a hand-typed friendly URL with no id hint) is left completely untouched — best effort.
    private void RewriteInlineImages(JsonObject propertiesNode)
    {
        WalkJsonObjects(propertiesNode, obj =>
        {
            if (obj["value"] is not JsonObject val || val["html"] is not JsonValue htmlVal) return;
            string html;
            try { html = htmlVal.GetValue<string>(); } catch { return; }
            if (string.IsNullOrEmpty(html)) return;

            var rewritten = Regex.Replace(html, @"(?:src|href)=""([^""]+)""", m =>
            {
                var attr = m.Value[..(m.Value.IndexOf('=') + 1)];
                var src = m.Groups[1].Value;
                var srcPath = src.Split('?')[0];

                if (srcPath.StartsWith("/link/", StringComparison.OrdinalIgnoreCase) && srcPath.EndsWith(".aspx", StringComparison.OrdinalIgnoreCase))
                    return m.Value; // already a permalink — nothing to do

                var idMatch = Regex.Match(srcPath, @",,(\d+)");
                if (!idMatch.Success) return m.Value; // no id hint — best-effort leave as-is

                Guid guid;
                try
                {
                    var content = _contentLoader.Get<IContent>(new ContentReference(int.Parse(idMatch.Groups[1].Value)), LanguageSelector.AutoDetect(true));
                    guid = content?.ContentGuid ?? Guid.Empty;
                }
                catch { return m.Value; }
                if (guid == Guid.Empty) return m.Value;

                return $"{attr}\"/link/{guid:D}.aspx\"";
            });

            if (!string.Equals(rewritten, html, StringComparison.Ordinal))
                val["html"] = rewritten;
        });
    }

    // ── JSON extraction helpers for the new REST API's shapes ──────────────────

    private sealed class SourceVersion
    {
        public string Locale;
        public string VersionNumber;
        public string DisplayName;
        public string RouteSegment;
        public string PropertiesJson;
    }

    private static List<SourceVersion> ParseVersionList(string json)
    {
        var list = new List<SourceVersion>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return list;
            foreach (var item in items.EnumerateArray())
            {
                list.Add(new SourceVersion
                {
                    Locale = item.TryGetProperty("locale", out var l) ? l.GetString() : null,
                    VersionNumber = item.TryGetProperty("version", out var v) ? v.ToString() : null,
                    DisplayName = item.TryGetProperty("displayName", out var d) ? d.GetString() : null,
                    RouteSegment = item.TryGetProperty("routeSegment", out var r) ? r.GetString() : null,
                    PropertiesJson = item.TryGetProperty("properties", out var p) ? p.GetRawText() : "{}"
                });
            }
        }
        catch { }
        return list;
    }

    // Walks a version's `properties` object for content references: a single reference
    // ({"value": "cms://content/{key}"}) or a ContentArea ({"value": [{"reference": "cms://
    // content/{key}"}, ...]}). Embedded/inline components ({"value": {"properties": {...}}}, no
    // "reference" key) are deliberately NOT walked here — they're inline data, not separate
    // transferable content (their own nested references, if any, would need the containing
    // property's value walked recursively; not currently needed by any content type in this
    // deployment's schema, so not implemented — see CLAUDE.md known gaps).
    internal static List<Guid> ExtractPropertyReferenceGuids(string propertiesJson)
    {
        var guids = new List<Guid>();
        var seen = new HashSet<Guid>();
        try
        {
            using var doc = JsonDocument.Parse(propertiesJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return guids;
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (!prop.Value.TryGetProperty("value", out var val)) continue;
                CollectReferenceGuids(val, guids, seen);
            }
        }
        catch { }
        return guids;
    }

    private static void CollectReferenceGuids(JsonElement value, List<Guid> guids, HashSet<Guid> seen)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                if (TryParseContentUri(value.GetString(), out var g) && seen.Add(g)) guids.Add(g);
                break;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("reference", out var r) && r.ValueKind == JsonValueKind.String)
                    {
                        if (TryParseContentUri(r.GetString(), out var g2) && seen.Add(g2)) guids.Add(g2);
                    }
                    else if (item.ValueKind == JsonValueKind.String)
                    {
                        if (TryParseContentUri(item.GetString(), out var g3) && seen.Add(g3)) guids.Add(g3);
                    }
                }
                break;
        }
    }

    private const string ContentUriPrefix = "cms://content/";

    private static bool TryParseContentUri(string value, out Guid guid)
    {
        guid = Guid.Empty;
        if (string.IsNullOrEmpty(value) || !value.StartsWith(ContentUriPrefix, StringComparison.OrdinalIgnoreCase)) return false;
        return Guid.TryParseExact(value[ContentUriPrefix.Length..], "N", out guid);
    }

    // Matches the permalink format RewriteInlineImages writes ("/link/{guid:D}.aspx") and that
    // source rich-text content already uses natively (confirmed live). Query string is stripped
    // by the caller (ExtractXhtmlImageUrls returns the raw src/href, including any "?" suffix).
    private static readonly Regex PermalinkGuidRegex = new(
        @"/link/([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\.aspx",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static bool TryParsePermalinkGuid(string url, out Guid guid)
    {
        guid = Guid.Empty;
        if (string.IsNullOrEmpty(url)) return false;
        var path = url.Split('?')[0];
        var m = PermalinkGuidRegex.Match(path);
        return m.Success && Guid.TryParse(m.Groups[1].Value, out guid);
    }

    // 32-char lowercase-hex key (no dashes) ↔ Guid. This IS the identity-preservation strategy:
    // every item is created on the target with key == ToKey(sourceGuid).
    internal static string ToKey(Guid guid) => guid.ToString("N");
    private static Guid ToGuid(string key) => Guid.TryParseExact(key, "N", out var g) ? g : Guid.Empty;

    private static string ExtractStringField(string json, string field)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        }
        catch { return null; }
    }

    private static bool TryExtractStringField(string json, string field, out string value)
    {
        value = ExtractStringField(json, field);
        return !string.IsNullOrEmpty(value);
    }

    private static Guid? ExtractGuidField(string json, string field) =>
        TryExtractStringField(json, field, out var s) && Guid.TryParseExact(s, "N", out var g) ? g : null;

    private static List<string> ExtractStringArrayField(string json, string field)
    {
        var list = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty(field, out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var item in arr.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String) list.Add(item.GetString());
        }
        catch { }
        return list;
    }

    // Detects a routeSegment uniqueness conflict regardless of casing/prefix — confirmed live in
    // two shapes: "initialVersion.RouteSegment" (content_create) and presumably bare
    // "routeSegment" (content_createversion, matching the lowercase field name PatchVersionAsync/
    // CreateVersionAsync send). Matching on the field's last segment name rather than the error
    // detail text keeps this robust to wording changes in the API's message.
    private static bool HasRouteSegmentConflict(string errorBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(errorBody);
            if (!doc.RootElement.TryGetProperty("errors", out var errors) || errors.ValueKind != JsonValueKind.Array) return false;
            foreach (var err in errors.EnumerateArray())
            {
                if (!err.TryGetProperty("field", out var f) || f.ValueKind != JsonValueKind.String) continue;
                var field = f.GetString() ?? "";
                var lastDot = field.LastIndexOf('.');
                var shortName = lastDot >= 0 ? field[(lastDot + 1)..] : field;
                if (string.Equals(shortName, "routeSegment", StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        catch { }
        return false;
    }

    private static readonly Regex TopLevelArrayIndexRegex = new(@"^value\[(\d+)\]", RegexOptions.Compiled);

    // Pulls the offending property's short name (and, when the bad value is one element of a
    // ContentArea, that element's array index) out of a 400 response's structured `errors` array
    // — e.g. {"errors":[{"field":"initialVersion.properties.Heading","detail":"..."}]} or
    // {"field":"properties.Image", ...} for a version write. Much simpler than CMA's message-
    // string regex matching: the new API always names the exact field.
    private static (string PropertyName, int? ArrayIndex) ExtractOffendingField(string errorBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(errorBody);
            if (!doc.RootElement.TryGetProperty("errors", out var errors) || errors.ValueKind != JsonValueKind.Array) return (null, null);
            foreach (var err in errors.EnumerateArray())
            {
                if (!err.TryGetProperty("field", out var f) || f.ValueKind != JsonValueKind.String) continue;
                var field = f.GetString() ?? "";
                const string marker = "properties.";
                // FIRST occurrence, not last: a field path can contain "properties." more than
                // once when the bad value sits inside an embedded component nested inside a
                // ContentArea item — e.g. confirmed live:
                // "initialVersion.properties.RelatedContentArea.value[2].properties.Image.value".
                // LastIndexOf found the INNER "properties." (right before "Image"), extracting
                // "Image" — no top-level property is named that, so ContainsKey missed and the
                // whole strip-and-retry loop was defeated again, just one level deeper than the
                // first time this bug was found (that fix only handled the single-"properties."
                // case, e.g. "initialVersion.properties.PageImage.value"). Anchoring on the FIRST
                // "properties." and taking only the next segment correctly yields the top-level
                // key ("RelatedContentArea") regardless of nesting depth.
                var idx = field.IndexOf(marker, StringComparison.Ordinal);
                if (idx < 0) continue;
                var afterProperties = field[(idx + marker.Length)..];
                var dotIndex = afterProperties.IndexOf('.');
                var propName = dotIndex >= 0 ? afterProperties[..dotIndex] : afterProperties;

                // If the remainder immediately after "PropName." is "value[N]", the error is
                // scoped to one ContentArea element rather than the whole property — capture N so
                // the caller can drop just that element (see the array-item strip fix at the call
                // site: stripping the whole property here was destroying sibling items that were
                // perfectly fine — confirmed live with a 3-item ContentArea reduced to 0 items
                // because only 1 of the 3 was actually bad).
                int? arrayIndex = null;
                if (dotIndex >= 0)
                {
                    var rest = afterProperties[(dotIndex + 1)..];
                    var m = TopLevelArrayIndexRegex.Match(rest);
                    if (m.Success) arrayIndex = int.Parse(m.Groups[1].Value);
                }

                return (propName, arrayIndex);
            }
        }
        catch { }
        return (null, null);
    }

    // Content-kind classification via the LOCAL content-type repository (already an injected
    // dependency) rather than any new API surface — we already know the type NAME from the
    // node's `contentType` field, and IContentTypeRepository maps that to the real .NET model
    // type, exactly the same distinction ScanContentDependencies already relies on locally.
    private bool IsMediaContentType(string contentTypeName) => ModelTypeImplements(contentTypeName, typeof(IContentMedia));
    private bool IsPageContentType(string contentTypeName) => ModelTypeImplements(contentTypeName, typeof(PageData));
    private bool IsBlockContentType(string contentTypeName) => !IsMediaContentType(contentTypeName) && !IsPageContentType(contentTypeName) && ModelTypeImplements(contentTypeName, typeof(BlockData));

    private bool ModelTypeImplements(string contentTypeName, Type expected)
    {
        if (string.IsNullOrEmpty(contentTypeName)) return false;
        try
        {
            var ct = _contentTypeRepository.Load(contentTypeName);
            return ct?.ModelType != null && expected.IsAssignableFrom(ct.ModelType);
        }
        catch { return false; }
    }

    // Filters out CMS system pages (Root=1, Waste Basket=2).
    private static bool IsSystemContentReference(ContentReference contentRef) =>
        !ContentReference.IsNullOrEmpty(contentRef) && (contentRef.ID == ContentReference.RootPage.ID || contentRef.ID == ContentReference.WasteBasket.ID);

    private static readonly HashSet<string> SystemPropertyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "PageParentLink", "PageShortcutLink", "PageArchiveLink", "PageDeletedLink"
    };

    private static bool IsSystemPropertyName(string propName) => !string.IsNullOrEmpty(propName) && SystemPropertyNames.Contains(propName);

    private static string GetMediaNodeType(string name)
    {
        var ext = Path.GetExtension(name ?? "").TrimStart('.').ToLowerInvariant();
        if (ext is "jpg" or "jpeg" or "png" or "gif" or "bmp" or "webp" or "svg" or "ico" or "tiff" or "tif" or "heic" or "heif" or "avif") return "Image";
        if (ext is "mp4" or "mov" or "avi" or "mkv" or "wmv" or "flv" or "webm" or "m4v" or "mpg" or "mpeg" or "m2v" or "3gp" or "3g2" or "ogv" or "mts" or "m2ts") return "Video";
        if (ext is "mp3" or "wav" or "ogg" or "flac" or "aac" or "m4a" or "wma" or "opus" or "aiff" or "mid" or "midi") return "Audio";
        if (ext is "pdf" or "doc" or "docx" or "xls" or "xlsx" or "ppt" or "pptx" or "odt" or "ods" or "odp" or "rtf" or "txt" or "csv" or "pages" or "numbers" or "keynote" or "epub") return "Document";
        return "UnknownMedia";
    }

    private (Guid? guid, string path) GetSiteRootFallback()
    {
        try
        {
            var startRef = SiteDefinition.Current?.StartPage ?? ContentReference.StartPage;
            if (ContentReference.IsNullOrEmpty(startRef)) return (null, null);
            var startPage = _contentLoader.Get<IContent>(startRef, LanguageSelector.AutoDetect(true));
            return (startPage.ContentGuid, $"{startPage.Name} (site root)");
        }
        catch { return (null, null); }
    }

    // BUG FIX: the destination-tree picker used to root itself at GetSiteRootFallback()'s node (the
    // site's Start page) — reasonable as PreCheck's own "nothing else resolved" fallback PARENT for
    // ordinary content, but wrong as the tree's browsing root: confirmed live, transferring the Start
    // page itself offered "Place Under Start" as the default, i.e. placing Start under itself, when
    // the actually-correct parent is the true CMS root ("Root" in the Pages tree, one level above
    // Start). ContentReference.RootPage (id 1) is a system node EPiServer seeds identically across
    // every environment of the same site, so it key-preserves exactly like Start/WasteBasket already
    // do elsewhere in this file — safe to use as the tree's top without any new configuration.
    private (Guid? guid, string path) GetTreeRootFallback()
    {
        try
        {
            var rootRef = ContentReference.RootPage;
            if (ContentReference.IsNullOrEmpty(rootRef)) return (null, null);
            var root = _contentLoader.Get<IContent>(rootRef, LanguageSelector.AutoDetect(true));
            return (root.ContentGuid, root.Name);
        }
        catch { return (null, null); }
    }

    private List<ContentReference> CollectItems(ContentReference root, bool includeChildren)
    {
        var items = new List<ContentReference> { root };
        if (!includeChildren) return items;
        try
        {
            void AddChildren(ContentReference parent)
            {
                foreach (var child in _contentLoader.GetChildren<IContent>(parent))
                {
                    items.Add(child.ContentLink);
                    if (child is PageData) AddChildren(child.ContentLink);
                }
            }
            AddChildren(root);
        }
        catch (Exception ex) { _logger.LogDebug("Could not enumerate children of {Root}: {Error}", root, ex.Message); }
        return items;
    }

    private static DxpEnvironmentConfig ResolveEnvironment(DxpTransferSettings settings, string name) =>
        name?.ToLowerInvariant() switch
        {
            "integration" => settings.Integration,
            "preproduction" => settings.Preproduction,
            "production" => settings.Production,
            _ => null
        };

    private static ContentReference ParseContentReference(string id) => ContentReferenceParser.Parse(id);
}
