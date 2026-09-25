# OAuth and MCP integration - 1.0.21

## ChatGPT plugin setup

MCP URL: `https://jarvis-mcp.158.180.59.36.sslip.io/mcp` for the current OCI deployment.
Keep Authentication set to **OAuth**. In **Advanced OAuth settings**, use **Dynamic Client Registration (DCR)**.
The existing DCR endpoint is `/connect/register`; discovery now advertises it explicitly.
Use token endpoint authentication method **none** for this public client; this does not disable OAuth.
PKCE **S256** and the user's Jarvis sign-in/consent are still required. Request `mcp:tools offline_access`.
Do not select CIMD: Jarvis does not implement or advertise that client registration method.

The message "Enter a client ID to use a user-defined OAuth client" is a ChatGPT setup-form validation error.
A user-defined client needs a real registered OAuth client ID. DCR obtains that ID automatically.
An agent device ID, a `jra_` enrollment token, a username or a PFX password is not an OAuth client ID.
No client secret is generated for the public DCR clients implemented here.

## Exact callback approval

Copy the **Callback URL** shown in ChatGPT Advanced OAuth settings for this plugin instance.
An administrator must add that exact value to `Jarvis:OAuthRedirectUris` in the external server configuration.
Do not substitute a generic callback from old instructions, guess its suffix, or allow a domain wildcard.
The server continues to reject every non-allowlisted callback, including an unapproved chatgpt.com callback.
A successful discovery check does not prove that your new callback is already approved.

After the server metadata update, refresh/reopen the plugin form so ChatGPT can discover DCR again.
With the callback approved, select DCR, create/connect the plugin, sign in to Jarvis and consent to one owned device.
For manual/user-defined setup, register the same approved callback at `/connect/register` with
`token_endpoint_auth_method: "none"`, then use the returned `client_id` and leave the secret empty.

## Discovery and authorization

`/.well-known/oauth-protected-resource` and `/.well-known/oauth-protected-resource/mcp` identify `/mcp`
and the configured authorization server. Anonymous POST /mcp returns 401 with a resource_metadata challenge.
Both `/.well-known/oauth-authorization-server` and `/.well-known/openid-configuration` include
registration_endpoint, authorization_endpoint, token_endpoint, PKCE S256 and token auth method none.
The OpenIddict 7.7 `HandleConfigurationRequestContext` handler adds the missing public-client metadata.
URLs are derived from the configured issuer, not request Host values. No CIMD support is claimed.
Discovery does not advertise openid: this flow returns MCP access/refresh grants, not an OIDC identity token.

DCR allows only exact configured redirect URIs and code/refresh grants. Registered IDs remain stable for
an exact callback set; changing a display name cannot create unlimited client IDs.
Consent requires the web cookie, CSRF validation and an owned enabled device. PKCE S256, mcp:tools and
the exact resource `https://<origin>/mcp` remain mandatory. Tokens are bound to the device/security stamp.
Access lifetime is 10 minutes; refresh lifetime is seven days subject to rolling tokens and revocation.
Neither query-string access tokens nor agent enrollment tokens authenticate MCP requests.

Before scanning tools, connect the Agent and complete OAuth consent for the intended device. That selected device's live manifest is the discovery source: newly advertised capabilities default to **Auto** and do not require an Import/Enable pass. **Hidden** is the explicit deny state. Empty `tools/list` is not an OAuth client registration failure.

Initialize-based clients advertise `tools.listChanged=true` and can refresh direct definitions when `notifications/tools/list_changed` arrives. Regardless of refresh support, the permanent `jarvis__tool_search` and `jarvis__tool_call` tools remain available so a session can discover and invoke capabilities added after it started. Server 1.0.90 also removes policy rows for capabilities no enrolled device advertises and blocks centrally retired IDs, so stale database metadata cannot keep an old direct or generic tool callable.

## Verification

```powershell
.\scripts\Verify-OAuthDiscovery.ps1 -Origin 'https://jarvis-mcp.158.180.59.36.sslip.io' -ExpectedVersion '1.0.21'
```

This uses only public GET requests to check metadata/TLS. It creates no clients/tokens and does not call /mcp.
Tests exercise discovery -> DCR -> consent -> PKCE token -> authenticated MCP initialize/tools/list
against an isolated server fixture. Actual ChatGPT linking still requires the user's callback and consent.

## References

- OpenAI plugin authentication and client registration: https://developers.openai.com/plugins/build/auth
- OpenIddict ASP.NET Core integration: https://documentation.openiddict.com/integrations/aspnet-core.html
- Version-specific API: installed OpenIddict.Server 7.7.0 XML docs and integration tests in this repository.
- See `docs/BUILD-STATUS.md` and `artifacts/verification/oauth-discovery/` for the executed checks.

## Production callback configuration - 2026-09-15

The user supplied the exact callback `https://chatgpt.com/connector_platform_oauth_redirect`.
It was added to external `Jarvis:OAuthRedirectUris` on jarvis01; the prior allowlist was empty.
A private backup was retained on the VM. File ownership/mode and all other configuration were preserved.
Only jarvis-mcp-server was restarted. No application binary was changed; deployed version remains 1.0.20.

Public HTTPS verification completed:
- DCR `/connect/register`: 201 for the supplied callback; public client has no client secret.
- Authorization with that client, callback, resource and S256: 302 to Jarvis login; callback preserved.
- An unapproved callback path: 400 invalid_redirect_uri.
- Anonymous MCP initialize: 401 with resource_metadata challenge.
- Health: 200, status ok, version 1.0.20.

Registered public client ID: `jarvis_4eeeeff9eaf59af2bc113a937cbf8c58f0a0217c6e5fc1b000617bc955bfa06c`.
DCR may register/reuse the client automatically; manual setup may use this ID with token auth `none`.
The user must still complete Jarvis sign-in and device consent inside ChatGPT.
No production user login, access/refresh token issuance or computer tool execution was performed by this check.
Evidence: `artifacts/verification/oauth-callback/verification.json` and `public-client.json` (public metadata only).

## Consent button navigation repair - 1.0.21

Symptom: repeatedly selecting Authorize appears to stay on the same consent page.
Production logs showed authorization responses without a following token exchange.
The consent document inherited `form-action 'self'`. Chromium applies this directive
not only to the same-origin form POST, but also to the subsequent cross-origin redirect.
An HTTP client can receive a valid 302 while the browser refuses that redirect.

BrowserContentSecurityPolicy keeps the original default policy on ordinary routes.
Only an authenticated consent document gets the selected, registered and configured
callback origin appended to form-action. For the configured ChatGPT callback this is
`form-action 'self' https://chatgpt.com`. OpenIddict still checks the exact callback path;
the CSP origin is not a replacement for OAuth redirect URI validation.
No wildcard, global policy removal, inline-script exception, CSRF bypass or client change
is needed. PKCE, device ownership, resource binding and callback registration are unchanged.

After deploying this fix, close the old consent tab and start Connect again in ChatGPT.
An already loaded document retains its old CSP; clicking its button repeatedly will not
fetch the new policy. Use a fresh flow to avoid old or expired OAuth state/code values.
Do not rotate client ID, device token, account password or signing keys for this issue.

### Repeat the browser regression locally

From the repository root, with packages already restored and an installed Chrome or Edge:

```powershell
$folder = Join-Path (Get-Location) 'artifacts/verification/oauth-consent-navigation'
New-Item -ItemType Directory -Force $folder | Out-Null
$env:JARVIS_CONSENT_BROWSER_FIXTURE = Join-Path $folder 'consent-browser-fixture.json'
dotnet test tests/Jarvis.Server.Tests -c Release --no-restore
Remove-Item Env:\JARVIS_CONSENT_BROWSER_FIXTURE
python scripts/Verify-ConsentNavigation.py --fixture "$folder/consent-browser-fixture.json" --browser 'C:\Program Files\Google\Chrome\Application\chrome.exe' --output "$folder/chrome-navigation.json"
```

The optional fixture contains rendered ASP.NET consent HTML with hidden values redacted.
The Python harness needs `websockets` and launches a new isolated headless browser profile.
It uses synthetic loopback redirects to verify browser enforcement, not production accounts.
It verifies the old policy blocks navigation, the patched policy allows Authorize and Cancel,
and a different unapproved origin is still blocked. No CSP/browser sandbox check is disabled.
The separate integration tests exercise actual code/PKCE/token/MCP behavior in the server fixture.

CSP reference: https://www.w3.org/TR/CSP3/#directive-form-action

## Tool publication policy - 1.0.88

In Tool catalog, Select all selects the current search results (or all tools with an empty search). Checkboxes also support individual selection. **Use automatic** follows the selected device's live descriptor, **Publish selected** keeps explicit public metadata while the capability exists, and **Hide selected** blocks direct listing, generic gateway calls and task use. A changed/missing policy row rejects the whole batch; refresh the admin page before retrying. Read-only users never see these controls. `Import installed` is now only an optional metadata mirror for the admin UI, not a prerequisite for runtime discovery.

Arm control on Agent 1.0.22 has no 30-minute expiry. The in-memory choice lasts until manual Pause/Disconnect/Exit, survives transient reconnects without replaying jobs, and is never persisted across application restarts. OAuth token lifetimes, device tokens and per-call deadlines are independent and unchanged.
