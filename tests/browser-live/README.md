# Real browser QA acceptance

Run from Windows with .NET 10 SDK and Node 22 or newer:

```powershell
node scripts/test-browser-live.cjs
```

The script finds an existing extension-capable Chromium in `%LOCALAPPDATA%/ms-playwright`.
It does not install a browser or change the user's browser. An explicit executable can be supplied:

```powershell
node scripts/test-browser-live.cjs --browser 'C:/tools/chrome-for-testing/chrome.exe'
```

`--smoke` only verifies live extension/service bootstrap. `--no-build` uses the existing Debug harness
and dependencies; use it only after a successful current build. `--output <directory>` chooses the
retained evidence directory. The default is `artifacts/browser-live/<timestamp>-<random>/`.

## What is actually exercised

The test helper starts production `BrowserServiceServer` with private named pipes and a test-owned
artifact root. Its outer dispatch facade invokes production `SessionBrowserToolSet`, which uses the
real `BrowserRuntimeClient` JSON RPC, production vendor tools, the shipping extension's unmodified
`background.js` and `qa.js`, and real `chrome.debugger`/`chrome.scripting` APIs in a fresh headless
Chromium profile. No Playwright page actions, synthetic browser APIs, fake screenshot, or stub
success results are used.

The copied extension manifest points at a test-only service-worker bootstrap. The bootstrap replaces
only `chrome.runtime.connectNative` with a token-scoped loopback relay to the private production
BrowserBridge pipe, then loads the unchanged shipping background script. This avoids installing or
changing OS native-messaging registrations. Consequently this suite **does not cover** native-host
registry discovery/stdio executable launch, remote MCP authentication, model behavior, or durable
task completion. Those are separate acceptance layers. These limits are recorded in `results.json`.

## Scenarios and retained evidence

- Healthy Save interaction with expected postcondition; ordinary Vite CSS must not be an error overlay.
- Desktop and mobile observations and actual PNG artifacts, including SHA-256 and decoded PNG dimensions.
- Broken CTA, obstructed input, disabled input, late hydration, shadow-root action,
  trusted input in a same-origin frame, and rejection of unsafe cross-origin frame input.
- `console.error('boom')` and HTTP 500 generated after interaction.
- A CTA clipped by `overflow:hidden` while the document itself has no horizontal overflow.
- Required visual review remains explicitly external, rather than becoming a structural-smoke pass.
- Cross-session ownership, cross-tab reference rejection, independent observations, and remembered browser family.

Every fixture executes through the same production tool path. Negative fixtures pass the test only
when QA correctly reports their failure. A missing capability or bootstrap failure fails the run;
there is no stub fallback or silently skipped test. `results.json` records individual case results,
production handshake, extension hashes/version, screenshot evidence, errors, and cleanup status.
`extension-trace.jsonl` records real extension API start/done/error events by method name;
instrumentation delegates to the original browser API without replacing its results. Each RPC is
bounded to 120 seconds; the run has a 10-minute watchdog and stops on a transport timeout.

Only child PIDs spawned by this harness are stopped. Temporary profile removal verifies the resolved
path remains within this run's artifact directory and has the expected unique prefix. Artifact PNGs and logs are
retained for inspection. No user tabs, user extension installations, registry entries, or persistent
browser preferences are changed.
