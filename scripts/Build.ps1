[CmdletBinding()]
param([ValidateSet('All','Server','Agent')][string]$Component='All',
      [ValidateSet('linux-x64','linux-arm64')][string]$ServerRuntime='linux-arm64',
      [switch]$NoRestore, [switch]$SkipTests)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$version=(Get-Content -LiteralPath (Join-Path $root 'VERSION') -Raw).Trim()
if($version -notmatch '^\d+\.\d+\.\d+$'){throw 'VERSION must contain major.minor.patch.'}
Push-Location $root
try {
  $sdk=& dotnet --version
  if ($LASTEXITCODE -ne 0 -or $sdk -notmatch '^10\.') { throw '.NET 10 SDK is required.' }
  function Run-Dotnet([string[]]$Arguments) { & dotnet @Arguments; if($LASTEXITCODE -ne 0){throw "dotnet failed: $($Arguments -join ' ')"} }
  $restore=@(); if($NoRestore){$restore=@('--no-restore')}
  if(-not $SkipTests){
    Run-Dotnet (@('test','tests/Jarvis.Core.Tests','-c','Release','--logger','trx','--results-directory','artifacts/test-results')+$restore)
    if ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)) {
      Run-Dotnet (@('test','tests/Jarvis.Agent.Windows.Tests','-c','Release','--logger','trx','--results-directory','artifacts/test-results')+$restore)
    }
    Run-Dotnet (@('test','tests/Jarvis.Server.Tests','-c','Release','--logger','trx','--results-directory','artifacts/test-results')+$restore)
  }
  if($Component -in 'All','Server'){
    $out="artifacts/server/$version-$ServerRuntime"
    Run-Dotnet (@('publish','jarvis-mcp-server/src/Jarvis.McpServer','-c','Release','-r',$ServerRuntime,'--self-contained','true','-p:PublishSingleFile=false','-o',$out)+$restore)
    & tar -czf "artifacts/jarvis-mcp-server-$version-$ServerRuntime.tar.gz" -C $out .
    if($LASTEXITCODE -ne 0){throw 'Server archive creation failed.'}
  }
  if($Component -in 'All','Agent'){
    if([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT){throw 'Build the Windows desktop package on Windows with .NET desktop targeting packs.'}
    $agentOutput=Join-Path $root "artifacts/agent/$version"
    foreach($target in @(@('Jarvis.Agent.Desktop','desktop'),@('Jarvis.Agent.Cli','cli'))){
      Run-Dotnet (@('publish',"jarvis-agent/src/$($target[0])",'-c','Release','-r','win-x64','--self-contained','true','-p:PublishSingleFile=false','-o',"$agentOutput/$($target[1])")+$restore)
    }
        # Ship both real entry points, not standalone baseline host artifacts or secrets.
    # Required framework assemblies are not private configuration files.
    $frameworkPrivateAssemblies = @('System.Private.CoreLib.dll','System.Private.DataContractSerialization.dll',
      'System.Private.Uri.dll','System.Private.Windows.Core.dll','System.Private.Windows.GdiPlus.dll',
      'System.Private.Xml.dll','System.Private.Xml.Linq.dll')
    $prohibited = Get-ChildItem $agentOutput -Recurse -File | Where-Object {
      $_.Extension -in '.pfx','.p12','.key','.pem','.ttf','.otf','.woff','.woff2','.eot','.ttc' -or
      $_.Name -in 'agent.local.json','tool-permissions.json','computer-settings.json' -or ($_.Name -like '*.private.*' -and $_.Name -notin $frameworkPrivateAssemblies)
    }
    if($prohibited){throw ('Refusing to package private configuration or font binaries: ' + ($prohibited.FullName -join ', '))}
    Compress-Archive -Path "$agentOutput/*" -DestinationPath "artifacts/Jarvis-Agent-$version-win-x64.zip" -Force
  }
} finally { Pop-Location }
