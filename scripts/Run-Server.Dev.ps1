[CmdletBinding()]param([Parameter(Mandatory)][string]$AdminEmail)
$ErrorActionPreference='Stop'
$secret=Read-Host 'Initial admin password (14+ chars, upper/lower/digit/symbol)' -AsSecureString
$credential=[System.Net.NetworkCredential]::new('', $secret)
$env:ASPNETCORE_ENVIRONMENT='Development'
$env:Bootstrap__AdminEmail=$AdminEmail
$env:Bootstrap__AdminPassword=$credential.Password
try{Push-Location (Join-Path $PSScriptRoot '..');dotnet run --project jarvis-mcp-server/src/Jarvis.McpServer; if($LASTEXITCODE -ne 0){throw 'Server exited unsuccessfully.'}}
finally{Pop-Location; Remove-Item Env:Bootstrap__AdminPassword -ErrorAction SilentlyContinue}
