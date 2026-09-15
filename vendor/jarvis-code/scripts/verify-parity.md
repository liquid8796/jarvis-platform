# Parity verification runner

Run only checks related to the current patch. The runner has no default suite and exits before launching a test process when `-Suites` is omitted. Documentation, version and test-workflow changes normally need static/build checks, not a parity sweep.

Select a focused check from Windows with PowerShell 7 and the repository's .NET SDK:

```powershell
pwsh -File scripts/verify-parity.ps1 -Suites Core -TestFilter FullyQualifiedName~PermissionRulePrecedenceTests
```

`-TestFilter` is passed to `dotnet test` and combined with the runner's exclusions. UI parity classes, native WPF/Electron fixtures and live-provider tests are opt-in. A full unfiltered App suite requires `-IncludeNativeUi`; select a specific filter for App logic checks. Even a direct `dotnet test` skips guarded native fixtures unless `JARVIS_PARITY_NATIVE_UI=1` is explicitly set.

`testhost` is the .NET test runner, so a selected non-UI unit test can still create that process. Avoiding unrelated suite runs is what removes unnecessary testhost launches; the native opt-in guards separately prevent windows and browser engines from opening.

Select suites, configuration, artifacts, and reference installations explicitly:

```powershell
pwsh -File scripts/verify-parity.ps1 -Suites Core,Providers,Parity -Configuration Release -ArtifactsPath D:/verification/jarvis -ReferenceCli C:/reference/claude.exe -ReferenceApp C:/reference/desktop/app -StrictInstalledReference
```

Reference discovery runs only when Parity is selected or `-StrictInstalledReference` is explicitly requested. Without explicit reference paths, the runner uses `JARVIS_REFERENCE_CLI` / `JARVIS_REFERENCE_APP`, then the test suite's installed-reference discovery locations. An explicit missing path never falls back. Strict mode requires a PE CLI binary, `app.asar`, and a valid nonempty desktop `en-US.json` catalogue; it also rejects missing-reference skips.

Native UI checks are manual, focused operations for relevant UI work, never routine post-patch validation:

```powershell
pwsh -File scripts/verify-parity.ps1 -Suites Parity -TestFilter FullyQualifiedName~UiGeometryParityTests -IncludeNativeUi -StrictInstalledReference
```

Paid live-provider turns require an additional explicit settings fixture:

```powershell
pwsh -File scripts/verify-parity.ps1 -Suites Parity -IncludeNativeUi -IncludeLiveProviderTests -LiveSettingsPath C:/fixtures/jarvis/settings.json -StrictInstalledReference
```

Use an existing app-generated settings JSON with provider credentials encrypted for the current Windows user. Only that file is copied into each unique live smoke profile. The fixture is never rewritten; hooks, account token stores, plugins and project configuration are not copied. Each smoke process uses a fresh workspace. Session-switch exit 3 is reported as an inconclusive failure, because it did not prove the switch occurred.

`-PlanOnly` records the chosen preflight and suite/filter selection without building or running tests. For example, add it to a focused command above to inspect the selection without starting `testhost`. `-NoBuild` explicitly reuses binaries under the selected artifacts path and configuration. Build and suite time limits are configurable with `-BuildTimeoutSeconds` and `-SuiteTimeoutSeconds`; a timeout fails verification and stops only the process tree launched by that check.

Every run has a unique directory under `<artifacts>/runs/` with `summary.json`, `summary.md`, TRX files, build/test stdout and stderr, and optional native screenshots/logs. Reports record selected reference hashes, test assemblies and the product DLLs/executables beside them, git HEAD and working-tree status, skipped-test categories, and before/after hashes for default-profile configuration and browser registration. Build artifacts live separately under `<artifacts>/build/`.

The runner removes inherited `JARVIS_PARITY_NATIVE_UI` and all `JARVIS_APPROVE_*` switches from child environments. Only `-IncludeNativeUi` sets the native opt-in for the test child; it never enables reference or golden-image approval. Missing golden baselines fail without mutation. Native fixtures use `JARVIS_PARITY_ISOLATED=1` to avoid replacing installed browser native-messaging registrations or importing the user's plugins and skills; their disposable profiles and workspaces are cleaned after evidence is retained. Existing fixed-name golden profile contents are restored after the render.

Exit codes: `0` selected checks passed (or a requested plan completed), `1` failed/incomplete checks or configuration changed during the run, `2` invalid preflight or build failure. The report separates recorded fixtures, installed-reference checks and optional native/live evidence; a passing run does not claim complete product equivalence.
