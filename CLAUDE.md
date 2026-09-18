# CLAUDE.md

Guidance for working in this project. Keep it current when architecture or conventions change.

## What this is

A CMS 13–only build of the DXP Content Transfer editor gadget — pushes content (pages, blocks,
media, inline images, references) between DXP environments (Integration / Preproduction /
Production) over Optimizely's REST APIs. It builds to a class library with no host application
of its own in this repo.

It is consumed as a **`ProjectReference`** (not a NuGet package) by the external CMS 13 test host
at `C:\Users\MattPallatt\source\repos\CMS13` (`CMS13\CMS13.csproj` → `..\..\GadgetWorkbench\
DxpContentTransfer.Cms13\DxpContentTransfer.Cms13.csproj`). That host also carries a module stub
at `CMS13\Optimizely\DxpContentTransfer.Cms13\module.config` just to satisfy Optimizely's
ModuleFinder — the real code lives here.

**This repo's own host, `GadgetWorkbench`, does NOT reference this project** — it references the
sibling `../DxpContentTransfer` instead (see below). That means there is no way to `dotnet run`
this code from inside this repo; to manually exercise a change you must build/run the external
CMS13 host.

## THIS PROJECT NO LONGER TARGETS THE SAME API AS `../DxpContentTransfer` — read this first

Until this rewrite, this project was a byte-for-byte copy-fork of the sibling `../DxpContentTransfer`
(`MP.DxpContentTransfer`) package, which reads/writes content over the **Content Management API
(CMA v3)** and resolves URLs over the **Content Delivery API (CDV)**. That model is no longer
usable on real CMS 13:

- **CMA has no CMS 13 release at all.** `EPiServer.ContentManagementApi` tops out at `3.12.7` on
  the Optimizely NuGet feed — there is no 13.x build, confirmed by querying the feed directly, not
  an assumption. The sibling project's CMA-based writes cannot work against a real CMS 13 target;
  they only ever worked here because this project hadn't been touched since the CMS12/13 fork and
  still targeted the CMA v3 endpoints structurally, untested against real CMS 13 packages.
- **CDAPI (the CMS 13-era rename of CDV) exists, but can't be referenced alongside the alternative
  write API.** See the CMS13 host's own `CLAUDE.md`/support-ticket history for the full
  investigation: `EPiServer.ContentDeliveryApi.*` and the new CMS REST API (`Optimizely.Cms.Service.V1.*`,
  auto-wired into any app referencing `EPiServer.CMS`) cannot coexist in the same host — every
  `/_cms/v1/*` route 500s with an unresolvable `contentIdentifier` route constraint whenever CDAPI
  is also referenced, confirmed reproducible on the latest package versions with no application-
  level fix (Optimizely's own support response confirmed the two are mutually exclusive). CDAPI
  was removed from the CMS13 host entirely to unblock the REST API.

This project has therefore been ported to the **new CMS 13 REST API** (`/_cms/v1`, package family
`Optimizely.Cms.Service.V1.*`) for both reads and writes, replacing CMA and CDAPI both.
`CmaClient.cs` is gone, replaced by `CmsApiClient.cs`. **The engine is no longer shared with
`../DxpContentTransfer` in any form — do not assume parity with that project's `CLAUDE.md` for
anything wire-format-related.** Read that file for background on the *pre-check phase* (which is
still local/`IContentLoader`-based and structurally unchanged) and for the general shape of the
"stub → dependencies → full write" transfer strategy, but not for anything about the write payload
shape, multilingual handling, or asset placement — all of that is different now.

## Why the port turned out simpler than expected — the key-preservation insight

The single design fact this whole engine leans on: **content identity is preserved across
environments by creating every item on the target using its SOURCE GUID (32-char lowercase hex,
no dashes) as the caller-supplied `key`.** Confirmed live: `POST /_cms/v1/content` accepts a
caller-chosen `key`, and a `GET` by that same key immediately returns it. Since the new API's
references are self-contained `"cms://content/{key}"` URIs (there is no separate integer-id layer
the way CMA needed for binding), a property value copied verbatim from the source response is
*already* a valid reference on the target, with zero rewriting, provided the referenced item exists
there under that same key — which the depth-first dependency walk guarantees. This one fact deletes
CMA's entire id-injection/remapping subsystem, and — via the same trick applied to `container` and
`owner` — most of CMA's folder/path-resolution machinery too. See the large comment block at the
top of `Services/ContentTransferService.cs` for the full list of what this eliminated and why.

## Build & restore

EPiServer packages are not on nuget.org — use the Optimizely feed:

```bash
dotnet restore --source https://api.nuget.org/v3/index.json --source https://nuget.optimizely.com/feed/packages.svc/
dotnet build --no-restore
```

`Nullable` is disabled, `ImplicitUsings` enabled. There's no host in this repo to run it against;
build/manual-test via the external CMS13 host (`C:\Users\MattPallatt\source\repos\CMS13`, also
`net10.0`). That host's own OAuth client for local testing — `dxp-transfer-spike` /
`local-dev-only-secret`, scope `api:admin` — has real content-write permissions granted (this
required a manual fix in the CMS's own access-rights configuration; a fresh `/_cms/v1` OAuth
client does NOT get write access by default — `api:admin` scope alone was confirmed insufficient,
returning `403 Forbidden` on every create attempt until that separate fix was applied).

**No test project here**, same as before the port. The old sibling-shared unit tests (`XhtmlProcessor`/
`JsonVisitors`) don't apply to the new property-reference extraction logic
(`ExtractPropertyReferenceGuids`, `ExtractOffendingPropertyName`, etc. in `ContentTransferService.cs`)
— these were verified during the port via a throwaway standalone harness run against real captured
API response bodies (not committed to the repo), not via an in-repo test project. Worth adding one.

## Architecture

- `Controllers/DxpGadgetController` — `[IFrameComponent]`-registered editor gadget
  (`Views/DxpGadget/Index.cshtml`). Unchanged by the port.
- `Controllers/DxpTransferApiController` — `pre-check` / `transfer` (fire-and-forget, in-memory
  `ConcurrentDictionary` job state, does not survive an app-pool recycle) / `progress/{jobId}` /
  `result/{jobId}`. Unchanged by the port.
- `Controllers/DxpSettingsController` + `Views/DxpSettings/Index.cshtml` — admin settings page at
  `~/EPiServer/DxpContentTransfer/Admin/Settings` (admin roles only), plus AJAX
  `Admin/TestConnection` backed by `EnvironmentHealthService`. Unchanged by the port — same
  BaseUrl/ClientKey/ClientSecret fields per environment; only what happens with those credentials
  underneath changed (see `EnvironmentTokenService` below).
- `Services/CmsApiClient` — the sole HTTP transport, replacing `CmaClient`. Every `/_cms/v1/*` call
  the engine makes goes through here: `GetNodeAsync`, `ListVersionsAsync`, `ListItemsAsync`,
  `ListAssetsAsync`, `CreateContentAsync`/`CreateContentWithBinaryAsync` (multipart, same
  `content`+`file` field-name convention as the old CMA multipart — confirmed still works
  unchanged against the new endpoint), `CreateVersionAsync`, `PatchVersionAsync`, `PatchNodeAsync`,
  `DeleteAsync`, `GetMediaBinaryAsync`. If you need a new API call, add a method here.
- `Services/EnvironmentTokenService` — OAuth2 client-credentials tokens against the new API's own
  token endpoint. **Two confirmed-live gotchas that differ from the old OpenIDConnect flow:**
  1. `POST /_cms/v1/oauth/token` wants the client credentials as **HTTP Basic auth**, not
     `client_id`/`client_secret` form fields — sending them as form fields gets "The authorization
     field is required."
  2. Scope is `api:admin` (there is no narrower documented scope for content read/write
     specifically), and tokens are short-lived (`expires_in: 300`, vs. the old flow's 3600s) —
     cached with a tighter safety margin accordingly.
  This is a **completely separate auth system** from EPiServer's classic OpenIDConnect
  (`/api/episerver/connect/token`) — confirmed live, a token from one is flatly `401`'d by the
  other's API. Don't conflate them; an environment could plausibly have OpenIDConnect configured
  for something else entirely and still need a *different* `/_cms/v1` OAuth client set up here.
  **`GetTokenAsync` is meant to be called again and again, right before each `/_cms/v1` request —
  never once and threaded through as a fixed string.** `ContentTransferService` used to fetch a
  single `targetToken`/`sourceToken` at the top of `PreCheckAsync`/`TransferAsync` and pass that
  same string all the way down the recursive transfer walk. Since the new API's tokens only live
  300s, any transfer that ran longer than that (a large/deep content tree easily can) would 401 on
  every request for the rest of the job the moment the token expired, with nothing to recover it —
  a real bug, confirmed reported. `GetTokenAsync` already caches per environment with a tight
  safety margin, so calling it again is a cache hit in the overwhelming common case and a
  transparent silent refresh right when it matters; the fix was to stop threading token *strings*
  and thread the `DxpEnvironmentConfig` instead, fetching the actual token from
  `IEnvironmentTokenService` immediately before every `CmsApiClient` call. If you add a new call
  site that needs a token, follow that pattern — do not accept a pre-fetched token string as a
  parameter and hold onto it across an `await` that could take a while (dependency transfers,
  retry loops, tree walks).
- `Services/EnvironmentHealthService` — the "Test connection" probe, ported to the new token
  endpoint/scope and a `GET /_cms/v1/content/{random-key}` probe (still "404 is success" — a
  healthy API 404s a non-existent key exactly like the old CMA probe did).
- `Services/ContentTransferService` — the engine. See the large comment block at the top of the
  file for the full CMA → REST API mapping and what got simpler (owner-based placement, key
  preservation, locale writes) vs. what's a genuine unclosed gap (see below). Read it before
  touching anything — it's the canonical source now, not the sibling project.
- `Services/XhtmlProcessor` — pure rich-text processing, **updated for the new property shape**:
  CMA wrapped rich text as `{"propertyDataType": "PropertyXhtmlString", "value": "<html>"}`; the
  new API wraps it as `{"value": {"html": "<html>"}}` — no `propertyDataType` field at all.
  Detection is now shape-based (`TryGetRichTextHtml`: does `value` have an `html` string key) —
  confirmed against a real captured `MainBody` property. The actual HTML content is unchanged
  (still classic `epi-contentfragment` divs, still `/link/{guid}.aspx` permalinks) — only the JSON
  envelope around it changed. `RewriteXhtmlUrls` lost its `contentIdMap`/`blockIdMap` parameters —
  see known gaps below for why.
- `Services/JsonVisitors` — generic walkers, unchanged; still transport-agnostic.
- `Services/ContentReferenceParser` — parses UI-supplied local content IDs via the local EPiServer
  API; unrelated to the remote wire format, unchanged.
- `Menu/DxpTransferMenuProvider` — CMS 13 admin menu entry, unchanged by the port.
- `Extensions/ServiceCollectionExtensions.AddDxpContentTransfer()` — DI registration updated to
  register `CmsApiClient` instead of `CmaClient`.
- `Models/TransferResult.TransferItemResult.TargetContentId` — **changed type from `int?` to
  `string`.** The new API has no numeric content-id concept; this is now the target's GUID-based
  key. The gadget's admin deep-link
  (`{targetBaseUrl}/EPiServer/CMS/#context=epi.cms.contentdata:///{targetContentId}`, built in
  `Views/DxpGadget/Index.cshtml`) is unverified against the new API's admin shell with a GUID
  instead of an integer — test this after a real transfer; it used to carry a classic integer id.

## Known gaps — deliberately not ported, or not yet re-verified

These are real, documented simplifications made under time pressure during the port, not
oversights discovered later. Each is also called out with a `KNOWN GAP` comment at the point in
`ContentTransferService.cs` where it bites:

- **Numeric-id remaps in rich text are gone and can't be brought back.** CMA-era code remapped two
  environment-specific integers baked into markup by the classic editor: the `,,{id}` suffix on
  inline image `src` (edit-mode URLs), and the integer `data-contentlink` on
  `epi-contentfragment` divs. Both remaps depended on CMA exposing `contentLink.id` for a GUID on
  the target. **The new API has no numeric content-id concept at all** — content identity is
  purely the GUID-based `key`. There is no way to compute a replacement integer, so these are left
  pointing at the SOURCE's numbers on the target. In practice this only matters if the CMS's own
  rendering treats the stale numeric hint as authoritative instead of falling back to
  `data-contentguid` / re-resolving by GUID — if inline images or content fragments render wrong
  after a transfer, check this first. Inline images are otherwise rewritten to the
  environment-agnostic `/link/{guid}.aspx` permalink form on write (see
  `ContentTransferService.RewriteInlineImages`), which needs no remap at all since the guid is
  preserved — only a genuinely un-resolvable friendly-path image (no `,,{id}` hint, not already a
  permalink) is left completely untouched.
- **PreCheck's URL-based ancestor-matching fallback is dropped, not reimplemented.** The old engine
  had two ways to find a target parent: match by GUID up the ancestor chain, then (if that failed
  entirely) match by URL path via CDV — for a target page that pre-exists under a *different* GUID
  at the same conceptual URL (created by something other than this tool). CDAPI is gone and the new
  REST API has no URL-to-key resolver at all (confirmed against the full documented endpoint
  family, ~40 endpoints, not just the ones this file touches). `ResolveTargetParentAsync` now only
  does the GUID-based phase; falling through means the existing site-root safety net kicks in
  instead (content still gets created, under site root, unpublished — not silently lost, just not
  under the "right" pre-existing parent).
- **Placeholder fabrication and forward-reference deferred patching are gone.** CMA's engine had a
  whole second pass (`GetFallbackReferenceGuidAsync`/`CreatePlaceholderAsync`/the
  `deferredPatches` list) for a required reference whose target didn't exist yet (typically a
  circular dependency): substitute a fallback value, or strip-and-retry-later once the real
  reference existed. The new engine's `WriteContentVersionAsync` retry loop strips a rejected
  property and moves on — no fallback substitution, no second pass. In practice this should rarely
  fire: `properties` is copied verbatim from a source version that already passed the *same*
  required-property validation when it was originally saved, and the depth-first dependency walk
  transfers referenced blocks/media before the parent references them. It's a real gap for a
  genuine circular reference between two blocks, though — not handled.
- **Media MIME type comes from the download response header, not a `mimeType` property.** Never
  found/needed a `mimeType` JSON field in the new schema — `CmsApiClient.GetMediaBinaryAsync`
  returns the actual `Content-Type` response header from the source, which is simpler and was
  confirmed reliable (a real PNG downloaded and re-uploaded correctly this way).
- **Optional JSON fields must be OMITTED, not sent as explicit `null` — confirmed live, a real bug
  caught during the port.** `POST /_cms/v1/content` rejects `{"routeSegment": null}` outright:
  *"The 'routeSegment' field does not allow 'null' values."* `System.Text.Json.Nodes.JsonObject`'s
  indexer sets a literal JSON null when assigned a null string, which is NOT the same as omitting
  the key — `ContentTransferService.SetIfNotNull` exists specifically to avoid this trap. If you
  add a new optional field to a create/version payload, use it rather than a plain indexer assignment.
- **The content-type classification helpers (`IsMediaContentType`/`IsPageContentType`/
  `IsBlockContentType`) reuse the LOCAL `IContentTypeRepository`** (already an injected dependency)
  to map a `contentType` name string to its .NET model type, rather than any new API surface —
  this was a deliberate simplification (no need to invent a REST-based "what kind of content is
  this" heuristic when the exact same classification `ScanContentDependencies` already does
  locally is one line away), but it does mean these checks only work correctly when the content
  type is actually deployed/registered on whichever environment is running this code (always true
  for the source, since content types are deployed via code) — untested against a content type
  that's deployed on target but named differently, though that would be unusual.
- **End-to-end verification was NOT run through the actual gadget UI or `DxpTransferApiController`
  HTTP endpoints** — `DxpSettingsController` requires real ASP.NET Identity admin login, which
  wasn't available in the environment this port was built in. What WAS verified live against the
  running CMS13 host: every individual `/_cms/v1` call `CmsApiClient` wraps (create with owner,
  create with a caller-chosen key round-tripped by GET, multipart media upload+download,
  `listassets` confirming owner-based placement for both a block and pre-existing media, the exact
  400 error shape for both create and version-write paths), plus a full **read-a-real-block's-
  version → copy its properties verbatim → create under a new key with `owner` set → verify via
  `listassets` → delete** round trip using a real, already-existing `JumbotronBlock`'s live data
  (not hand-crafted). The JSON extraction/parsing logic (`ExtractPropertyReferenceGuids`,
  `ExtractOffendingPropertyName`, the rich-text shape detection) was verified via a standalone
  harness against real captured response bodies. **Not verified**: the actual `PreCheckAsync`/
  `TransferAsync` code paths running inside the real DI-wired service, multi-language branch
  writes, the `EnsureContainerExistsAsync` container-chain-creation path (no real host in this
  sandbox had a missing global-asset folder to trigger it), and the gadget's admin deep-link with a
  GUID `TargetContentId`. Before relying on this in anything beyond further local testing, run a
  real transfer through the actual gadget UI end to end.

## CMS 13 shell integration — still-current notes from before the port (unaffected by it)

- **`ModuleDetails` registration (`Extensions/ServiceCollectionExtensions.AddDxpContentTransfer`)
  needs a physical `module.config` manifest to actually resolve — registering the name alone is
  not enough.** `EPiServer.Shell.Modules.ModuleFinder` looks for a manifest on disk (or via a file
  provider) at `{ProtectedModuleOptions.RootPath}DxpContentTransfer.Cms13` before the DI
  registration does anything; if it can't find one, `DxpGadgetController`'s `[IFrameComponent]`
  crashes the Edit UI dashboard for any signed-in editor with `ArgumentException: Unable to find a
  module by assembly 'DxpContentTransfer.Cms13'` — confirmed live, cost hours to diagnose the first
  time a plain `dotnet add package` install of this project hit it, because an unauthenticated
  request never reaches the code path that throws. Fixed by shipping
  `wwwroot/DxpContentTransfer.Cms13/module.config` as an ASP.NET Core **static web asset**, with
  `<StaticWebAssetBasePath>ui</StaticWebAssetBasePath>` in the csproj overriding the Razor Class
  Library default of namespacing under `_content/{library}` — that override is what makes the file
  resolve at `/ui/DxpContentTransfer.Cms13/module.config`, matching
  `ProtectedModuleOptions.RootPath`'s *default* value (`~/ui`) with zero manual setup, for both a
  `ProjectReference` (this repo's own CMS13 test host) and a plain `PackageReference` into any
  other ASP.NET Core CMS 13 app. **Known limitation, not fixed:** a host that reconfigures
  `RootPath` away from the default (e.g. `Jhoose.Security`, which remaps every module's route base
  to `~/Optimizely` for admin-URL obscurity — exactly what this repo's own CMS13 test host uses)
  needs its own hand-placed `module.config` at `{their RootPath}/DxpContentTransfer.Cms13/
  module.config` instead; there's no single physical location that satisfies every possible
  override. See the CMS13 host's own `CMS13/Optimizely/DxpContentTransfer.Cms13/module.config` for
  exactly that case, and the comment on `AddDxpContentTransfer()` for the full explanation. Also
  note `loadFromBin="false"` in the shipped file matters — `"true"` makes `ModuleFinder` look for
  the DLL under a nested `bin/` subfolder that doesn't exist in a normal consuming app.
- **`Middleware/DxpAdminScriptMiddleware` was deleted** (this predates the REST API port). The
  sibling still uses this pattern for CMS 12 (inject an `AdminInit.js` `<script>` tag into admin
  HTML responses so the settings page can overlay the SPA via a hash route). CMS 13 doesn't need
  it: `DxpTransferMenuProvider` points `MenuPaths.Global + "/cms/admin/tools/dxp.transfer"` straight
  at `/EPiServer/DxpContentTransfer/Admin/Settings`, so the shell can load/iframe it directly
  without any client-side script injection.
- **`[MenuProvider]` attribute vs DI registration — still unresolved, still worth checking before
  relying on it.** `DxpTransferMenuProvider` carries both the `[MenuProvider]` attribute AND a DI
  registration (`services.AddTransient<IMenuProvider, DxpTransferMenuProvider>()`), with a comment
  claiming DI alone is sufficient. The sibling project's CMS 13 branch explicitly *skips*
  `ProtectedModuleOptions` registration ("creates an unwanted Add-ons sidebar entry"), while this
  project registers it unconditionally ("required to avoid a shell startup exception"). These two
  claims describe contradictory CMS 13 behaviour for the same API and neither has been re-verified
  since the REST API port (unrelated area of the code, but still open). If you touch menu/module
  registration, verify against a running CMS 13 shell rather than trusting either comment.
