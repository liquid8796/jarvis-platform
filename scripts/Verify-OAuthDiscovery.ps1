[CmdletBinding()]
param([Parameter(Mandatory)][uri]$Origin, [string]$ExpectedVersion)
$ErrorActionPreference = 'Stop'
if ($Origin.Scheme -ne 'https' -or $Origin.AbsolutePath -ne '/' -or $Origin.Query -or $Origin.Fragment -or $Origin.UserInfo) {
    throw 'Origin must be an HTTPS origin without credentials, path, query or fragment.'
}
$base = $Origin.AbsoluteUri.TrimEnd('/')
$health = Invoke-RestMethod "$base/health" -TimeoutSec 20
if ($health.status -ne 'ok') { throw 'Health check failed.' }
if ($ExpectedVersion -and $health.version -ne $ExpectedVersion) { throw 'Unexpected deployed version.' }
$checks = @()
foreach ($route in @('/.well-known/oauth-protected-resource','/.well-known/oauth-protected-resource/mcp')) {
    $metadata = Invoke-RestMethod "$base$route" -TimeoutSec 20
    if ($metadata.resource -cne "$base/mcp" -or $metadata.authorization_servers -cnotcontains $base) {
        throw "Invalid protected-resource metadata: $route"
    }
    if ($metadata.scopes_supported -cnotcontains 'mcp:tools') { throw 'MCP scope missing.' }
    $checks += [pscustomobject]@{Check=$route;Result='PASS'}
}
foreach ($route in @('/.well-known/oauth-authorization-server','/.well-known/openid-configuration')) {
    $metadata = Invoke-RestMethod "$base$route" -TimeoutSec 20
    if ($metadata.issuer -cne "$base/" -or $metadata.registration_endpoint -cne "$base/connect/register" -or
        $metadata.authorization_endpoint -cne "$base/connect/authorize" -or $metadata.token_endpoint -cne "$base/connect/token") {
        throw "Incorrect issuer or OAuth endpoints: $route"
    }
    if ($metadata.token_endpoint_auth_methods_supported -cnotcontains 'none' -or
        $metadata.code_challenge_methods_supported -cnotcontains 'S256') { throw 'Public PKCE flow not advertised.' }
    if ($metadata.scopes_supported -cnotcontains 'mcp:tools' -or $metadata.scopes_supported -cnotcontains 'offline_access' -or
        $metadata.scopes_supported -ccontains 'openid') { throw 'Discovery scopes do not match the MCP consent flow.' }
    $checks += [pscustomobject]@{Check=$route;Result='PASS'}
}
[pscustomobject]@{
    Origin=$base;Version=$health.version;Checks=$checks;Result='PASS'
    Scope='Public GET metadata and TLS only; no client registration, login, token exchange or MCP requests.'
}
