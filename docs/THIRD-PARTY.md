# Third-party source and dependencies

## 1.0.65 dependency status

No third-party dependency, browser-extension or vendored-bridge version changed in 1.0.65. Shipping Jarvis package/assembly/file versions are 1.0.65 / 1.0.65.0; the BrowserBridge remains 1.0.25 / 1.0.25.0 and the browser extension remains 1.2.0. The dependency/license notes from 1.0.64 therefore remain applicable.

## 1.0.64 dependency and bridge changes

The existing vendored BrowserBridge was intentionally modified to propagate application-session envelopes and isolate selected browsers while preserving legacy bridge behavior. Its package/assembly/file versions are now 1.0.25 / 1.0.25.0; the shipping Jarvis products are 1.0.64 / 1.0.64.0. Older baseline parity reports below are historical, not evidence that this bridge remains byte-identical.

Microsoft.Windows.Console.ConPTY **1.24.260710001** is pinned through NuGet for native ConPTY lifecycle support. Its MIT package license and Microsoft's original notices apply. The matching conpty.dll and OpenConsole.exe host are shipped together for process architecture/native OS compatibility; no font binaries or private profiles are redistributed. Primary implementation references: https://github.com/microsoft/terminal and https://www.nuget.org/packages/Microsoft.Windows.Console.ConPTY/1.24.260710001 . The original license is retained in `docs/licenses/Microsoft.Windows.Console.ConPTY-LICENSE.txt` and copied into the Windows publish outputs; see that notice for redistribution terms.

UI UX Pro Max was used as local development guidance for WPF validation, semantic state, keyboard/focus and system-font layout. Reference downloads and Claude analysis artifacts are not included in the source commit or product package.

The baseline is the source archive supplied with the request. Original source comments and existing notices are retained; no new license claim is made over it. It contains reference-derived compatibility assets and bundled third-party code. Review original rights before publicly redistributing. Optional font binaries are not in this release.

New code uses official Model Context Protocol C# SDK, Microsoft .NET/ASP.NET/Identity/EF/Windows APIs, OpenIddict, JsonSchema.Net and WebView2. Their package licenses govern those dependencies; restore obtains them separately. The derived Chrome extension retains original source notices with a distinct manifest identity/native host name.

Primary engineering references:
- https://github.com/modelcontextprotocol/csharp-sdk
- https://github.com/modelcontextprotocol/servers
- https://github.com/openiddict/openiddict-core
- https://github.com/openiddict/openiddict-samples
- https://docs.json-everything.net/schema/basics/
- https://learn.microsoft.com/dotnet/core/whats-new/dotnet-10/overview
- https://learn.microsoft.com/microsoft-edge/webview2/

Version pins are in csproj files. This source delivery has not generated an SBOM or completed a vulnerability scan. Do not mistake preservation of baseline notices for a completed licensing audit.
