# Frontend QA and repair workflow

Version 1.0.78 separates execution from verification. A successful build or browser command is not a verified frontend. The local Agent measures behavior through `browser.qa`; the connected model reviews the resulting images and supplies repairs through the same authenticated task gateway. No additional model provider or API key is required.

## Run QA

Start the app using its existing dev command with an owned `process.start`/`process.spawn` call. Keep a long-running dev server separate from a durable build step: build steps correctly wait for process exit. Use the observed server URL, not a guessed port. The normal process cancellation and session ownership rules still apply.

Call `agent_task_create` with `goal`, `project`, `executionMode: "NORMAL"` and `verificationSpec`. A specification-only task can verify an app edited through ordinary MCP calls; it needs no dummy edit/build step. Existing tasks can include the same specification alongside their executable steps. If a frontend task finishes its steps without a specification, it waits in `NEEDS_VERIFICATION`.

Example `verificationSpec` for a test app with a Save action:

```json
{
  "url": "http://localhost:5173/settings",
  "expectedUrl": "http://localhost:5173/settings",
  "ready": { "testId": "settings-page" },
  "viewports": [
    { "name": "desktop", "width": 1440, "height": 900, "mobile": false },
    { "name": "mobile", "width": 390, "height": 844, "mobile": true }
  ],
  "steps": [
    {
      "action": "fill",
      "locator": { "testId": "display-name" },
      "value": "QA fixture",
      "expect": { "kind": "value", "value": "QA fixture" }
    },
    {
      "action": "click",
      "locator": { "role": "button", "name": "Save" },
      "expect": {
        "kind": "text",
        "locator": { "testId": "save-status" },
        "value": "Saved"
      }
    }
  ],
  "timeoutMs": 15000,
  "fullPage": true,
  "requireVisualReview": true
}
```

Choose fixture data appropriate to the application. Each action needs an expected postcondition; hovering at an arbitrary point cannot satisfy interaction QA. Supported actions are `click`, `fill`, `select`, `check`, `press` and `assert`. Assertions cover visible/hidden state, exact text/value, checked state, match count and URL. Locators support role/name, text, test ID, CSS and frame selection. Ambiguous or non-actionable targets fail with diagnostics. Cross-origin frame actions that cannot establish trustworthy input coordinates fail explicitly rather than substituting a synthetic click.

The host records a separate image and observations after the scenario at every viewport. Navigation/interaction is observed with console and network collection already active. Visible error overlays, error/warning console entries, failed requests, missing postconditions, wrong viewport and clipped targets block measured success. A regular Vite stylesheet is not an error overlay. Screenshot existence alone is not a visual verdict.

Direct `browser.qa` provides the same measured browser primitive for ordinary MCP clients. Its `passed` field covers measured checks; inspect `visualReview` separately. Use the durable task workflow when completion must be enforced and retained by the Agent.

## Continue through the connected model

| Operation | Purpose |
|---|---|
| `agent_task_get` | Read execution status, verification state, missing/failed kinds and capture IDs. |
| `agent_task_artifacts` | Read bounded execution/verification history. |
| `agent_task_verify` | Supply/update a specification and rerun QA without replaying prior edits. |
| `agent_task_capture` | Fetch one current PNG by its capture ID as native MCP image content. Owner/device/session checks remain in force. |
| `agent_task_review` | Submit observations tied to the delivered captures, exact image hashes, verification run and source revision. |
| `agent_task_repair` | Execute only new explicit repair steps, then rerun verification. |
| `agent_task_complete` | Revalidate evidence and source freshness before final completion after visual review. |

Mutating workflow operations use a UUID `attemptId`. Repeating the same operation/payload/attempt ID returns its persisted state without replay; reusing an ID for different work conflicts. Do not create a new attempt after an uncertain response before checking the task.

For repair, retain the original goal, project, mode and timeout; submit only the new ordered steps. Successful predecessor actions are not replayed. External repair is limited to three rounds per task, and every tool call uses the existing Arm/Pause, permissions, schemas, process ownership and cancellation rules.

When the task reaches `NEEDS_REVIEW`, fetch every listed capture with `agent_task_capture`, inspect the actual images, then provide a visual review. Each image needs observations for `layout`, `typography`, `color`, `iconography`, `overflow` and `interaction`; use a concrete observation even when a category has no applicable element. Supply the original `referenceId` when comparing a supplied reference. Do not claim a reference match without inspecting that reference.

The Agent requires delivery of every current image before accepting review. This proves image delivery and ties the review to bytes; it does not prove a particular model's visual judgment is correct. An empty mismatch ledger or an unbound model assertion cannot create a visual pass or erase a measured browser failure.

## States and freshness

| Task status | Meaning |
|---|---|
| `NEEDS_PLAN` | Goal has no executable plan or QA specification; no implicit paid planner is configured. |
| `NEEDS_VERIFICATION` | Supply a QA specification or refresh missing/stale evidence. |
| `NEEDS_REPAIR` | Measured or reviewed evidence failed. Inspect it and submit new repair work. |
| `NEEDS_REVIEW` | Measured checks passed; image review is still required. |
| `READY_TO_COMPLETE` | Review passed; completion will recheck the current source and capture hashes. |
| `COMPLETED` | Execution finished. Read `verification.required`, `verification.passed` and `verification.state` to distinguish verified FE work from non-FE or read-only execution. |

Verification states include `not_required`, `not_run`, `failed`, `stale`, `needs_review` and `passed`. Read-only tasks never perform mutating browser QA and never claim rendered verification simply because their read steps finished.

Evidence includes task/run identity, source content revision, observed URL, viewport, timestamp, capture ID and SHA-256. Source revisions include untracked source/assets and exclude dependency/build caches. A bounded scan that cannot establish a complete revision blocks certification. Source changes during QA or before review/completion invalidate evidence. Artifact deletion or modification is rejected. Verification-only retries do not replay successful code changes.

The source revision binds evidence to local file contents. It does not attest that an arbitrary server is serving that checkout/build; supply the correct dev-server URL and readiness landmark, and use an application build marker where that distinction matters.

Frontend classification uses actual changed source paths, rendered-project indicators and explicit requirements, with English and Vietnamese intent hints. Stylesheet/layout work requires visual review. Classification runs again after repairs. Backend-only JavaScript is not automatically a frontend solely because of its extension.

## Install and validate

Deploy server and Agent 1.0.78 together, refresh the MCP tool catalog, and install/reload browser extension 1.4.0. The extension advertises `structured-qa-v1`; an older connection receives an explicit update error for QA. The new `qa.js` is packaged alongside `background.js` by the normal build/publish path. Building or pushing source does not replace a running Agent or restart production services.

Run `dotnet test Jarvis.slnx -c Release --no-restore` for maintained contract, host and server tests. Run `node scripts/test-browser-live.cjs` (or `scripts/test-browser-live.bat`) for real Chromium browser acceptance. See [the live harness scope](../tests/browser-live/README.md): it exercises SessionBrowserToolSet, production RPC/BrowserService, vendor tools, the shipping extension and real CDP. A test relay replaces only native-port registration/stdio launch; remote MCP authentication and model behavior are separate test layers. Failed fixtures pass acceptance only when the QA runtime detects their intended failure.

This implementation targets explicit, measurable QA behavior. It does not claim identical hidden model behavior or universal 100% parity with Codex Desktop.
