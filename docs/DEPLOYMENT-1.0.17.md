# Jarvis Platform 1.0.17 Deployment Notes

## MCP Server
- Runtime target: Linux ARM64
- Service: jarvis-mcp-server
- Internal listener: 127.0.0.1:18765
- HTTPS reverse proxy: Caddy

## Agent
- Build locally using Visual Studio 2026
- Target: .NET 10 Windows

Do not commit certificates or production credentials.
