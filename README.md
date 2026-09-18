# MP.DxpContentTransfer.Cms13

A CMS Edit UI sidebar gadget for Optimizely CMS 13 that lets an editor push content (pages,
blocks, media, inline images, references) from one DXP environment to another — Integration,
Preproduction, Production — directly from the editor sidebar, with no Kudu/file-system access and
no developer involvement required.

It talks to each environment's own [CMS 13 REST API](https://docs.developers.optimizely.com/content-management-system/v13.0.0-CMS/reference/introduction-cms-rest-api)
(`/_cms/v1/*`) — that API ships as part of `EPiServer.CMS` itself, not a separate package.

## Install

```bash
dotnet add package MP.DxpContentTransfer.Cms13
```

```csharp
// Startup.cs / Program.cs
services.AddDxpContentTransfer();
```

That's it for the module itself — the gadget's `module.config` ships as a static web asset and
resolves automatically for the default `ProtectedModuleOptions.RootPath` (`~/ui`). If your host
remaps `RootPath` (e.g. via `Jhoose.Security`), see [CLAUDE.md](CLAUDE.md#cms-13-shell-integration---still-current-notes-from-before-the-port-unaffected-by-it)
for the one manual step that needs.

## Configure

Each environment's CMS 13 REST API needs its own OAuth2 `client_credentials` client:

```csharp
services.AddOptimizelyCmsServiceV1().AddOauthEndpoints();
services.Configure<CmsServiceOauthOptions>(options =>
{
    options.AddDevelopmentSigningCredentials(); // swap for a real X.509 cert in Production
    options.Clients.Add(new OauthClient { ClientId = "...", ClientSecret = "..." });
});
```

Then, in **each** environment's own CMS Edit UI, go to **Admin > DXP Content Transfer** and enter
that environment's base URL / `ClientId` / `ClientSecret` as a transfer target. These are the
gadget's own settings — separate, per-environment, live CMS content, not app configuration — so
Integration's settings point at Preproduction/Production and vice versa.

**Access rights**: a freshly-registered OAuth client has no role membership at all (its token
carries no role claim), so it only gets whatever access is explicitly granted to it as a named
account via **Settings > Set Access Rights**. If your app replaces a content subtree's ACL
entirely (turning off inheritance, e.g. a per-editor scoped ACL), remember to include this account
there too, or content creation under that subtree will 403 with `Required access is 'create'`
even though the account has full access at the root.

## Known limitations

See [CLAUDE.md](CLAUDE.md#known-gaps--deliberately-not-ported-or-not-yet-re-verified) for the full
list (numeric-id remaps in rich text, URL-based ancestor-matching fallback, forward-reference
deferred patching). The short version: a genuine circular reference between two blocks, or a page
moved to a different URL on target by something other than this tool, aren't handled — everything
else transfers correctly, verified against real DXP environments.

## Contributing / issues

This is a CMS 13-only rewrite of the CMA/CDAPI-based `MP.DxpContentTransfer` — see
[CLAUDE.md](CLAUDE.md) for the full architecture writeup, the REST API's wire format, and why the
port turned out simpler than the original CMA-based engine. Open an issue or PR on
[this repo](https://github.com/mattpallatt/MP.DxpContentTransfer.Cms13).
