# Third-party source and dependencies

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
