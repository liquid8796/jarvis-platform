#requires -Version 7.0
<#
.SYNOPSIS
Build and verify selected suites in isolated artifacts, without approving reference updates.
.EXAMPLE
pwsh -File scripts/verify-parity.ps1 -Suites Core -TestFilter FullyQualifiedName~PermissionRulePrecedenceTests
.EXAMPLE
pwsh -File scripts/verify-parity.ps1 -Suites Parity -Configuration Release -IncludeNativeUi
.EXAMPLE
pwsh -File scripts/verify-parity.ps1 -Suites Parity -IncludeNativeUi -IncludeLiveProviderTests -LiveSettingsPath C:\fixtures\settings.json
.NOTES
No suite runs implicitly. Select only the suites and test filter relevant to the patch.
Native UI and real provider turns require the explicit switches above.
Requires Windows, PowerShell 7 and the repository's .NET SDK. Exit 0 means selected checks passed;
1 means failed/incomplete checks; 2 means invalid configuration or a preflight/build failure.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [string]$ArtifactsPath,
    [string]$ReferenceCli = $env:JARVIS_REFERENCE_CLI,
    [string]$ReferenceApp = $env:JARVIS_REFERENCE_APP,
    [switch]$StrictInstalledReference,
    [string[]]$Suites = @(),
    [string]$TestFilter,
    [switch]$IncludeNativeUi,
    [switch]$IncludeLiveProviderTests,
    [string]$LiveSettingsPath,
    [ValidateRange(30, 14400)][int]$SuiteTimeoutSeconds = 1200,
    [ValidateRange(30, 7200)][int]$BuildTimeoutSeconds = 900,
    [switch]$NoBuild,
    [switch]$PlanOnly
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$runId = (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
if (-not $ArtifactsPath) { $ArtifactsPath = Join-Path $repoRoot 'artifacts/parity-verification' }
$artifactRoot = [IO.Path]::GetFullPath($ArtifactsPath, $repoRoot)
$runRoot = Join-Path $artifactRoot "runs/$runId"
$buildRoot = Join-Path $artifactRoot 'build'
$resultsRoot = Join-Path $runRoot 'results'
$logsRoot = Join-Path $runRoot 'logs'
$nativeRoot = Join-Path $runRoot 'native'
$null = [IO.Directory]::CreateDirectory($resultsRoot)
$null = [IO.Directory]::CreateDirectory($logsRoot)
$suiteResults = [Collections.Generic.List[object]]::new()
$preflight = [Collections.Generic.List[object]]::new()
$exitCode = 0
$summary = [ordered]@{
    schemaVersion = 1; runId = $runId; startedUtc = [datetime]::UtcNow.ToString('o'); finishedUtc = $null
    repository = $repoRoot; gitHead = $null; gitWorkingTreeStatus = @(); gitDirty = $null; configuration = $Configuration
    artifacts = $runRoot; buildArtifacts = $buildRoot; mode = $(if ($IncludeNativeUi) { 'native-ui' } else { 'static' })
    includesLiveProviderTests = [bool]$IncludeLiveProviderTests; liveSettingsFixture = $null; strictInstalledReference = [bool]$StrictInstalledReference
    plannedOnly = [bool]$PlanOnly; reference = @{}; buildArtifactsMetadata = @(); preflight = @(); suites = @(); exitCode = $null
    reusedBuildArtifacts = [bool]$NoBuild; preservation = @{}; excludedGroups = @(); requestedTestFilter = $TestFilter
    exclusions = @('PortedTextApproval', 'UiStringManifestApproval', 'ReferenceSurfaceParityTests.Rewrite_reference_surface')
    evidenceScope = 'Mixed unit/component tests, recorded fixtures, installed-reference corpus/CLI checks, and explicitly selected native/live checks. A pass is not proof of complete product equivalence.'
    environmentPolicy = 'Overrides apply only to child processes; caller environment and account/profile files are not changed.'
}

function Invoke-RecordedProcess {
    param([string]$Executable, [string[]]$Arguments, [string]$Label, [int]$TimeoutSeconds, [hashtable]$ExtraEnvironment = @{})
    $info = [Diagnostics.ProcessStartInfo]::new($Executable)
    $info.WorkingDirectory = $repoRoot
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardInput = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
    $info.StandardErrorEncoding = [Text.UTF8Encoding]::new($false)
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    foreach ($name in @($info.Environment.Keys)) {
        if ($name -match '^(JARVIS_APPROVE_|ANTHROPIC_|CLAUDE_|OPENAI_|AWS_|AZURE_|GOOGLE_|GCLOUD_|VERTEX_|BEDROCK_)' -or $name -in @('JARVIS_E2E', 'JARVIS_PARITY_LIVE_SETTINGS', 'JARVIS_PARITY_NATIVE_UI', 'JARVISCODE_PROFILE')) {
            $null = $info.Environment.Remove($name)
        }
    }
    foreach ($entry in $ExtraEnvironment.GetEnumerator()) {
        if ($null -eq $entry.Value) { $null = $info.Environment.Remove($entry.Key) }
        else { $info.Environment[$entry.Key] = [string]$entry.Value }
    }
    $stdoutPath = Join-Path $logsRoot "$Label.stdout.log"
    $stderrPath = Join-Path $logsRoot "$Label.stderr.log"
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $info
    $started = [datetime]::UtcNow
    $timedOut = $false
    try {
        if (-not $process.Start()) { throw "Could not start $Executable" }
        $process.StandardInput.Close()
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $nextUpdate = [datetime]::UtcNow.AddSeconds(45)
        while (-not $process.WaitForExit(1000)) {
            if (([datetime]::UtcNow - $started).TotalSeconds -ge $TimeoutSeconds) {
                $timedOut = $true
                try { $process.Kill($true) } catch [InvalidOperationException] { }
                $null = $process.WaitForExit(10000)
                break
            }
            if ([datetime]::UtcNow -ge $nextUpdate) {
                Write-Host "$Label is still running; logs will be saved to $logsRoot"
                $nextUpdate = [datetime]::UtcNow.AddSeconds(45)
            }
        }
        # A child that inherited a pipe must not keep the verifier waiting indefinitely.
        $stdoutText = if ($stdout.Wait(10000)) { $stdout.Result } else { '[stdout stream did not close after process exit]' }
        $stderrText = if ($stderr.Wait(10000)) { $stderr.Result } else { '[stderr stream did not close after process exit]' }
        [IO.File]::WriteAllText($stdoutPath, $stdoutText, [Text.UTF8Encoding]::new($false))
        [IO.File]::WriteAllText($stderrPath, $stderrText, [Text.UTF8Encoding]::new($false))
        return [pscustomobject]@{
            exitCode = $(if ($timedOut) { -1 } else { $process.ExitCode }); timedOut = $timedOut
            durationSeconds = [math]::Round(([datetime]::UtcNow - $started).TotalSeconds, 3)
            stdout = $stdoutPath; stderr = $stderrPath; output = $stdoutText
        }
    }
    finally { $process.Dispose() }
}

function File-Metadata([string]$Path) {
    if (-not $Path -or -not [IO.File]::Exists($Path)) { return $null }
    $file = Get-Item -LiteralPath $Path
    return [ordered]@{ path = $file.FullName; bytes = $file.Length; sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash }
}

function Preservation-Snapshot {
    $registry = [ordered]@{}
    foreach ($browser in @('Google\Chrome', 'Microsoft\Edge', 'CocCoc\Browser')) {
        $subkey = "Software\$browser\NativeMessagingHosts\com.jarvis.browser"
        $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($subkey)
        try { $registry[$subkey] = if ($key) { [ordered]@{ exists = $true; value = $key.GetValue('') } } else { [ordered]@{ exists = $false; value = $null } } }
        finally { if ($key) { $key.Dispose() } }
    }
    $profile = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'JarvisCode'
    $files = [ordered]@{}
    foreach ($name in @('settings.json', 'ui-settings.json', 'mcp.json', 'hooks.json')) { $files[$name] = File-Metadata (Join-Path $profile $name) }
    return [ordered]@{ browserRegistration = $registry; defaultProfileConfiguration = $files }
}

function Resolve-ReferenceCli([string]$ExplicitPath) {
    if ($ExplicitPath) { return [IO.Path]::GetFullPath($ExplicitPath, $repoRoot) }
    $standalone = Join-Path ([Environment]::GetFolderPath('UserProfile')) '.local/bin/claude.exe'
    if ([IO.File]::Exists($standalone)) { return $standalone }
    $roots = [Collections.Generic.List[string]]::new()
    $roots.Add((Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'Claude/claude-code'))
    $packages = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Packages'
    if ([IO.Directory]::Exists($packages)) {
        foreach ($package in [IO.Directory]::EnumerateDirectories($packages, 'Claude_*')) { $roots.Add((Join-Path $package 'LocalCache/Roaming/Claude/claude-code')) }
    }
    $candidates = foreach ($root in $roots) {
        if (-not [IO.Directory]::Exists($root)) { continue }
        foreach ($directory in [IO.Directory]::EnumerateDirectories($root)) {
            $binary = Join-Path $directory 'claude.exe'
            $version = [version]'0.0'
            $null = [version]::TryParse([IO.Path]::GetFileName($directory), [ref]$version)
            if ([IO.File]::Exists($binary)) { [pscustomobject]@{ path = $binary; version = $version } }
        }
    }
    $selected = $candidates | Sort-Object version -Descending | Select-Object -First 1
    return $(if ($selected) { $selected.path } else { $null })
}

function Resolve-ReferenceApp([string]$ExplicitPath) {
    if ($ExplicitPath) { $selected = [IO.Path]::GetFullPath($ExplicitPath, $repoRoot) }
    else {
        $run = Invoke-RecordedProcess 'powershell.exe' @('-NoProfile', '-NonInteractive', '-Command', '(Get-AppxPackage -Name Claude).InstallLocation') 'reference-app-discovery' 90
        $selected = $run.output.Trim()
        if ($run.exitCode -ne 0 -or -not $selected) { return $null }
    }
    if ([IO.Directory]::Exists((Join-Path $selected 'resources'))) { return $selected }
    return Join-Path $selected 'app'
}

function Suite-Filter([string]$Suite) {
    $parts = [Collections.Generic.List[string]]::new()
    if ($TestFilter) { $parts.Add("($TestFilter)") }
    if ($Suite -ne 'Parity') { return $(if ($parts.Count) { $parts.ToArray() -join '&' } else { $null }) }
    $parts.Add('FullyQualifiedName!~PortedTextApproval')
    $parts.Add('FullyQualifiedName!~UiStringManifestApproval')
    $parts.Add('FullyQualifiedName!~ReferenceSurfaceParityTests.Rewrite_reference_surface')
    if (-not $IncludeNativeUi) {
        foreach ($name in @('JarvisCode.Parity.Tests.Ui', 'SmokeSelfTestTests')) { $parts.Add("FullyQualifiedName!~$name") }
    }
    elseif (-not $IncludeLiveProviderTests) {
        $parts.Add('FullyQualifiedName!~SmokeSelfTestTests.One_real_turn_completes')
        $parts.Add('FullyQualifiedName!~SmokeSelfTestTests.A_turn_survives_a_session_switch')
    }
    return $parts.ToArray() -join '&'
}

function Read-Trx([string]$Path) {
    if (-not [IO.File]::Exists($Path)) { return $null }
    [xml]$document = [IO.File]::ReadAllText($Path)
    $namespace = [Xml.XmlNamespaceManager]::new($document.NameTable)
    $namespace.AddNamespace('t', 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010')
    $cases = [Collections.Generic.List[object]]::new()
    foreach ($test in $document.SelectNodes('//t:UnitTestResult', $namespace)) {
        $messageNode = $test.SelectSingleNode('t:Output/t:ErrorInfo/t:Message', $namespace)
        $reason = if ($messageNode) { $messageNode.InnerText } else { '' }
        $category = $null
        if ($test.outcome -in @('NotExecuted', 'Skipped', 'Inconclusive')) {
            $category = switch -Regex ($reason) {
                'reference.*not installed|en-US.json|reference.*absent' { 'missing-reference'; break }
                'JarvisCode.App.exe.*not found|built app.*not found' { 'missing-native-artifact'; break }
                'JARVIS_E2E|configured provider|spend tokens|JARVIS_PARITY_LIVE_SETTINGS' { 'live-opt-in'; break }
                'JARVIS_PARITY_NATIVE_UI' { 'native-ui-opt-in'; break }
                'JARVIS_APPROVE_' { 'approval-only'; break }
                default { 'other' }
            }
        }
        $cases.Add([pscustomobject]@{ name = [string]$test.testName; outcome = [string]$test.outcome; reason = $reason; skipCategory = $category })
    }
    $all = $cases.ToArray()
    return [pscustomobject]@{
        total = $all.Count; passed = @($all | Where-Object outcome -EQ 'Passed').Count
        failed = @($all | Where-Object { $_.outcome -notin @('Passed', 'NotExecuted', 'Skipped', 'Inconclusive') }).Count
        skipped = @($all | Where-Object { $_.outcome -in @('NotExecuted', 'Skipped', 'Inconclusive') }).Count
        details = @($all | Where-Object outcome -NE 'Passed')
    }
}

try {
    if (-not $IsWindows) { throw 'The parity suite targets Windows and requires a Windows host.' }
    $Suites = @($Suites | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() })
    if ($Suites.Count -eq 0 -or @($Suites | Where-Object { $_ -notin @('Core', 'Providers', 'App', 'Cli', 'Parity') }).Count -gt 0) {
        throw 'No tests run by default. Explicitly select -Suites Core, Providers, App, Cli or Parity and use -TestFilter for the changed behavior.'
    }
    if ('App' -in $Suites -and -not $TestFilter -and -not $IncludeNativeUi) {
        throw 'The full App suite includes native UI. Select a focused -TestFilter, or explicitly pass -IncludeNativeUi for a relevant UI check.'
    }
    if ($IncludeLiveProviderTests -and (-not $IncludeNativeUi -or -not $LiveSettingsPath)) {
        throw '-IncludeLiveProviderTests requires -IncludeNativeUi and an explicit -LiveSettingsPath.'
    }
    if ($IncludeLiveProviderTests -and 'Parity' -notin $Suites) { throw '-IncludeLiveProviderTests requires Parity in -Suites.' }
    if ($LiveSettingsPath) {
        if (-not $IncludeLiveProviderTests) { throw '-LiveSettingsPath requires -IncludeLiveProviderTests.' }
        $LiveSettingsPath = [IO.Path]::GetFullPath($LiveSettingsPath, $repoRoot)
        if (-not [IO.File]::Exists($LiveSettingsPath)) { throw 'The explicit live settings fixture does not exist.' }
        $settingsCheck = [IO.File]::ReadAllText($LiveSettingsPath) | ConvertFrom-Json -AsHashtable
        if ($settingsCheck -isnot [Collections.IDictionary]) { throw 'The live settings fixture must be a settings JSON object.' }
        $summary.liveSettingsFixture = File-Metadata $LiveSettingsPath
    }
    $dotnet = (Get-Command dotnet -CommandType Application -ErrorAction Stop).Source
    $preservationBefore = Preservation-Snapshot
    $summary.preservation = [ordered]@{ before = $preservationBefore; after = $null; unchanged = $null }
    $git = Invoke-RecordedProcess 'git' @('rev-parse', 'HEAD') 'git-head' 30
    if ($git.exitCode -eq 0) { $summary.gitHead = $git.output.Trim() }
    $gitStatus = Invoke-RecordedProcess 'git' @('status', '--porcelain=v1') 'git-status' 30
    if ($gitStatus.exitCode -eq 0) {
        $summary.gitWorkingTreeStatus = @($gitStatus.output -split '\r?\n' | Where-Object { $_.Length -gt 0 })
        $summary.gitDirty = $summary.gitWorkingTreeStatus.Count -gt 0
    }
    if ('Parity' -in $Suites -or $StrictInstalledReference) {
        $ReferenceCli = Resolve-ReferenceCli $ReferenceCli
        $ReferenceApp = Resolve-ReferenceApp $ReferenceApp
        $catalogPath = if ($ReferenceApp) { Join-Path $ReferenceApp 'resources/ion-dist/i18n/en-US.json' } else { $null }
        $asarPath = if ($ReferenceApp) { Join-Path $ReferenceApp 'resources/app.asar' } else { $null }
        $cliMetadata = File-Metadata $ReferenceCli
        $catalogMetadata = File-Metadata $catalogPath
        $asarMetadata = File-Metadata $asarPath
        $summary.reference = [ordered]@{ cli = $cliMetadata; appDirectory = $ReferenceApp; catalogue = $catalogMetadata; appAsar = $asarMetadata }
        $referenceReady = $cliMetadata -and $cliMetadata.bytes -gt 2 -and $catalogMetadata -and $asarMetadata -and $asarMetadata.bytes -gt 0
        if ($cliMetadata) {
            $binary = [IO.File]::OpenRead($ReferenceCli)
            try { if ($binary.ReadByte() -ne 77 -or $binary.ReadByte() -ne 90) { $referenceReady = $false } }
            finally { $binary.Dispose() }
            $cliMetadata['version'] = [Diagnostics.FileVersionInfo]::GetVersionInfo($ReferenceCli).FileVersion
        }
        if ($catalogMetadata) {
            try { $catalog = [IO.File]::ReadAllText($catalogPath) | ConvertFrom-Json -AsHashtable; if ($catalog -isnot [Collections.IDictionary] -or $catalog.Count -eq 0) { $referenceReady = $false } }
            catch { $referenceReady = $false }
        }
        $preflight.Add([pscustomobject]@{ check = 'installed-reference'; status = $(if ($referenceReady) { 'available' } else { 'missing-or-invalid' }); strict = [bool]$StrictInstalledReference })
        if ($StrictInstalledReference -and -not $referenceReady) { throw 'Strict installed-reference preflight needs the selected CLI binary, desktop app.asar, and a nonempty valid en-US catalogue. Explicit paths never fall back.' }
    }
    else {
        $ReferenceCli = $null
        $ReferenceApp = $null
        $preflight.Add([pscustomobject]@{ check = 'installed-reference'; status = 'not-requested'; strict = $false })
    }
    $environment = @{
        JARVIS_REFERENCE_CLI = $ReferenceCli; JARVIS_REFERENCE_APP = $ReferenceApp
        JARVIS_PARITY_EVIDENCE_DIR = $nativeRoot
        JARVIS_PARITY_ISOLATED = '1'
        JARVIS_PARITY_NATIVE_UI = $(if ($IncludeNativeUi) { '1' } else { $null })
        CLAUDE_CONFIG_DIR = Join-Path $runRoot 'claude-config'
        JARVIS_IDE_REGISTRY = Join-Path $runRoot 'ide'
        JARVIS_E2E = $(if ($IncludeLiveProviderTests) { '1' } else { $null })
        JARVIS_PARITY_LIVE_SETTINGS = $(if ($IncludeLiveProviderTests) { $LiveSettingsPath } else { $null })
    }
    foreach ($suite in ($Suites | Select-Object -Unique)) {
        $projectName = "JarvisCode.$suite.Tests"
        $project = Join-Path $repoRoot "tests/$projectName/$projectName.csproj"
        $filter = Suite-Filter $suite
        Write-Host "[$suite] $Configuration; $(if ($filter) { $filter } else { 'all component tests' })"
        if ($PlanOnly) { $suiteResults.Add([pscustomobject]@{ suite = $suite; status = 'planned'; filter = $filter }); continue }
        if (-not $NoBuild) {
            $build = Invoke-RecordedProcess $dotnet @('build', $project, '--configuration', $Configuration, '--artifacts-path', $buildRoot, '--verbosity', 'minimal') "$suite-build" $BuildTimeoutSeconds
            $build.PSObject.Properties.Remove('output')
            if ($build.exitCode -ne 0) {
                $suiteResults.Add([pscustomobject]@{ suite = $suite; status = 'build-failed'; filter = $filter; process = $build })
                $exitCode = 2; continue
            }
        }
        $dll = Join-Path $buildRoot "bin/$projectName/$($Configuration.ToLowerInvariant())/$projectName.dll"
        if (-not [IO.File]::Exists($dll)) {
            $suiteResults.Add([pscustomobject]@{ suite = $suite; status = 'missing-build-artifact'; filter = $filter; expected = $dll })
            $exitCode = 2; continue
        }
        $testArtifact = File-Metadata $dll
        $testArtifact['suite'] = $suite
        $testArtifact['role'] = 'test-assembly'
        $summary.buildArtifactsMetadata += $testArtifact
        foreach ($name in @('JarvisCode.App.dll', 'JarvisCode.App.exe', 'jarvis.dll', 'jarvis.exe', 'JarvisCode.Cli.dll', 'JarvisCode.Cli.exe', 'JarvisCode.Core.dll', 'JarvisCode.Providers.dll', 'JarvisCode.Host.dll')) {
            $productArtifact = File-Metadata (Join-Path ([IO.Path]::GetDirectoryName($dll)) $name)
            if ($productArtifact) {
                $productArtifact['suite'] = $suite
                $productArtifact['role'] = 'product-beside-test-assembly'
                $summary.buildArtifactsMetadata += $productArtifact
            }
        }
        $trxPath = Join-Path $resultsRoot "$suite.trx"
        $arguments = [Collections.Generic.List[string]]::new()
        foreach ($value in @('test', $project, '--configuration', $Configuration, '--artifacts-path', $buildRoot, '--no-build', '--no-restore', '--verbosity', 'minimal', '--logger', "trx;LogFileName=$suite.trx", '--results-directory', $resultsRoot)) { $arguments.Add($value) }
        if ($filter) { $arguments.Add('--filter'); $arguments.Add($filter) }
        $run = Invoke-RecordedProcess $dotnet $arguments.ToArray() "$suite-test" $SuiteTimeoutSeconds $environment
        $run.PSObject.Properties.Remove('output')
        $counts = Read-Trx $trxPath
        $status = if ($run.timedOut) { 'timed-out' } elseif (-not $counts -or $counts.total -eq 0) { 'incomplete' } elseif ($run.exitCode -ne 0 -or $counts.failed -gt 0) { 'failed' } else { 'passed' }
        if ($StrictInstalledReference -and $counts -and @($counts.details | Where-Object skipCategory -EQ 'missing-reference').Count -gt 0) { $status = 'missing-reference' }
        if ($IncludeNativeUi -and $counts -and @($counts.details | Where-Object skipCategory -EQ 'missing-native-artifact').Count -gt 0) { $status = 'missing-native-artifact' }
        if ($IncludeLiveProviderTests -and $counts -and @($counts.details | Where-Object skipCategory -EQ 'live-opt-in').Count -gt 0) { $status = 'live-fixture-unavailable' }
        if ($status -ne 'passed') { $exitCode = [math]::Max($exitCode, 1) }
        $suiteResults.Add([pscustomobject]@{ suite = $suite; status = $status; filter = $filter; trx = $trxPath; counts = $counts; process = $run })
        Write-Host "[$suite] $status$(if ($counts) { ': ' + $counts.passed + ' passed, ' + $counts.failed + ' failed, ' + $counts.skipped + ' skipped' })"
    }
}
catch {
    $preflight.Add([pscustomobject]@{ check = 'runner'; status = 'failed'; error = $_.Exception.Message })
    Write-Host "Verification could not finish: $($_.Exception.Message)" -ForegroundColor Red
    $exitCode = 2
}
finally {
    if ($summary.preservation.Contains('before')) {
        $preservationAfter = Preservation-Snapshot
        $unchanged = ($summary.preservation.before | ConvertTo-Json -Depth 8 -Compress) -ceq ($preservationAfter | ConvertTo-Json -Depth 8 -Compress)
        $summary.preservation.after = $preservationAfter
        $summary.preservation.unchanged = $unchanged
        if (-not $unchanged) {
            $preflight.Add([pscustomobject]@{ check = 'configuration-preservation'; status = 'changed-during-verification'; error = 'Default profile configuration or browser registration changed during verification; see before/after hashes. The runner did not restore or overwrite these user-owned values.' })
            $exitCode = [math]::Max($exitCode, 1)
        }
    }
    $summary.finishedUtc = [datetime]::UtcNow.ToString('o')
    $summary.preflight = $preflight.ToArray()
    $summary.suites = $suiteResults.ToArray()
    $summary.exitCode = $exitCode
    if (-not $IncludeNativeUi) { $summary.exclusions += 'UI parity tests; native App fixtures (explicit UI opt-in)' }
    if (-not $IncludeLiveProviderTests) { $summary.exclusions += 'One_real_turn_completes; A_turn_survives_a_session_switch (live-provider opt-in)' }
    $summary.excludedGroups = @([ordered]@{ category = 'approval-only'; reason = 'Reference and golden approvals are never enabled by this verifier.' })
    if (-not $IncludeNativeUi) { $summary.excludedGroups += [ordered]@{ category = 'native-ui-opt-in'; reason = 'UI parity and native App fixtures are not automatic patch checks. Explicitly pass -IncludeNativeUi for relevant UI work.' } }
    if (-not $IncludeLiveProviderTests) { $summary.excludedGroups += [ordered]@{ category = 'live-opt-in'; reason = 'Requires -IncludeLiveProviderTests, -IncludeNativeUi and an explicit -LiveSettingsPath.' } }
    $jsonPath = Join-Path $runRoot 'summary.json'
    [IO.File]::WriteAllText($jsonPath, ($summary | ConvertTo-Json -Depth 14), [Text.UTF8Encoding]::new($false))
    $markdown = [Collections.Generic.List[string]]::new()
    $markdown.Add('# Parity verification')
    $markdown.Add('')
    $markdown.Add("Run: $runId; mode: $($summary.mode); configuration: $Configuration; exit: $exitCode.")
    $markdown.Add('')
    $markdown.Add($summary.evidenceScope)
    $markdown.Add('')
    $markdown.Add('| Suite | Status | Passed | Failed | Skipped |')
    $markdown.Add('| --- | --- | ---: | ---: | ---: |')
    foreach ($suite in $suiteResults) {
        $counts = if ($suite.PSObject.Properties['counts']) { $suite.counts } else { $null }
        $markdown.Add("| $($suite.suite) | $($suite.status) | $(if ($counts) { $counts.passed } else { '-' }) | $(if ($counts) { $counts.failed } else { '-' }) | $(if ($counts) { $counts.skipped } else { '-' }) |")
    }
    $markdown.Add('')
    $markdown.Add('Excluded: ' + ($summary.exclusions -join '; ') + '.')
    foreach ($check in $preflight) { if ($check.PSObject.Properties['error']) { $markdown.Add(''); $markdown.Add('Runner error: ' + $check.error) } }
    $markdown.Add('')
    $markdown.Add('Reference paths/hashes, tested product and test assembly hashes, git HEAD/working-tree status, individual failures and skip reasons are recorded in [summary.json](summary.json). Native evidence is kept under native/; stdout/stderr under logs/. No reference approvals are enabled. Environment overrides apply only to child processes.')
    if ($summary.preservation.Contains('unchanged')) { $markdown.Add("Default profile configuration/browser registration unchanged during this run: $($summary.preservation.unchanged).") }
    [IO.File]::WriteAllText((Join-Path $runRoot 'summary.md'), ($markdown.ToArray() -join "`n") + "`n", [Text.UTF8Encoding]::new($false))
    Write-Host "Evidence: $runRoot"
}
exit $exitCode
