[CmdletBinding()]
param(
    [ValidateSet('Debug','Release')][string]$Configuration = 'Debug',
    [string]$MSBuildPath
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$expectedVersion = (Get-Content -LiteralPath (Join-Path $root 'VERSION') -Raw).Trim() + '.0'
$rows = @()
foreach ($solutionPath in @('Jarvis.slnx', 'jarvis-agent/Jarvis Agent.slnx')) {
    $fullSolution = Join-Path $root $solutionPath
    [xml]$solution = Get-Content -LiteralPath $fullSolution -Raw
    $listed = @($solution.SelectNodes('//Project') | ForEach-Object {
        [IO.Path]::GetFullPath((Join-Path (Split-Path $fullSolution -Parent) $_.Path))
    })
    foreach ($projectPath in $listed) {
        [xml]$project = Get-Content -LiteralPath $projectPath -Raw
        $assetsPath = Join-Path (Split-Path $projectPath -Parent) 'obj/project.assets.json'
        if (-not (Test-Path -LiteralPath $assetsPath)) { throw "Restore outputs missing: $projectPath" }
        $assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json
        if (@($assets.logs | Where-Object { $_.level -eq 'Error' }).Count -gt 0) {
            throw "Restore errors remain in $assetsPath"
        }
        # NuGet can key assets by targetAlias and put the canonical TFM in .framework.
        # Older assets use the canonical TFM directly as the dictionary key.
        foreach ($framework in $assets.project.frameworks.PSObject.Properties) {
            $canonical = if ($framework.Value.framework) { $framework.Value.framework } else { $framework.Name }
            if ($canonical -match '^net[0-9.]+-windows$') {
                throw "Unnormalized Windows platform in $assetsPath. Restore the complete solution."
            }
        }
        foreach ($reference in $project.SelectNodes('//ProjectReference')) {
            $dependency = [IO.Path]::GetFullPath((Join-Path (Split-Path $projectPath -Parent) $reference.Include))
            if ($listed -notcontains $dependency) {
                throw "Solution dependency missing: $solutionPath -> $dependency"
            }
        }
    }
    $rows += [pscustomobject]@{Check='Solution graph'; Path=$solutionPath; Count=$listed.Count; Result='PASS'}
}
foreach ($name in @('Jarvis.Agent.Windows','Jarvis.Agent.Cli','Jarvis.Agent.Desktop')) {
    $projectPath = Join-Path $root "jarvis-agent/src/$name/$name.csproj"
    $arguments = @($projectPath, '-nologo', "-p:Configuration=$Configuration",
        '-getProperty:TargetPath,TargetRefPath,ProduceReferenceAssembly,TargetFramework,AssemblyVersion,ProjectAssetsFile')
    if ($MSBuildPath) { $raw = & $MSBuildPath @arguments }
    else { $raw = & dotnet msbuild @arguments }
    if ($LASTEXITCODE -ne 0) { throw "MSBuild property evaluation failed: $name" }
    $properties = ($raw -join "`n" | ConvertFrom-Json).Properties
    if ($properties.ProduceReferenceAssembly -ne 'true') { throw "Reference assemblies disabled: $name" }
    foreach ($path in @($properties.TargetPath, $properties.TargetRefPath)) {
        if (-not $path -or -not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Missing $name build output: $path. Build the complete solution first."
        }
        $version = [Reflection.AssemblyName]::GetAssemblyName($path).Version.ToString()
        if ($version -ne $expectedVersion) { throw "Stale assembly $path : $version, expected $expectedVersion" }
    }
    $assets = Get-Content -LiteralPath $properties.ProjectAssetsFile -Raw | ConvertFrom-Json
    if (@($assets.logs | Where-Object { $_.level -eq 'Error' }).Count -gt 0) {
        throw "Restore errors remain in $($properties.ProjectAssetsFile)"
    }
    $rows += [pscustomobject]@{Check='Reference assembly'; Path=$properties.TargetRefPath; Count=1; Result='PASS'}
}
$rows
