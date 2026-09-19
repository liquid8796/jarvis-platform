# Coding verification and repair

Version 1.0.79 extends measured QA beyond frontend tasks. The connected model selects acceptance checks, the Agent executes them through its normal permissions, and task-owned receipts record what actually happened. A stage named TEST, an exit-zero command, or a model-supplied boolean is not a test report.

## Start from requirements and project context

For a goal needing a plan, create a durable task, use `agent_task_context` with the relevant source paths, and use `agent_task_tools` to discover enabled canonical tool IDs. Context reads go through guarded `filesystem.Read`; they supply applicable project guidance, manifests and command hints without executing project instructions. Select checks appropriate to the changed behavior rather than running every possible test.

`RemoteTaskPlan.codingVerification` and the same field on `agent_task_verify`/`repair` carry the general acceptance specification. Existing `verificationSpec` remains the frontend browser contract. A mixed task can require both; passing one does not override failures in the other. A specification-only NORMAL task can verify work done through ordinary MCP tools without a dummy edit step.

Source-affecting non-web changes and explicit backend/API/CLI intent require a coding specification before verified completion. Read-only work reports `not_run` and does not execute mutating probes. A justified task can declare `mode: "not_required"` with a reason; an exemption cannot erase an observed execution/test failure.

Example specification for a running local API:

```json
{
  "mode": "required",
  "profiles": ["backend", "api", "security"],
  "checks": [
    {
      "id": "value-contract",
      "kind": "http",
      "toolId": "developer.verify",
      "requirement": "The value endpoint returns 42 in its JSON body.",
      "arguments": {
        "kind": "http",
        "url": "http://127.0.0.1:5179/value",
        "expectedStatus": 200,
        "assertions": [{ "path": "/value", "op": "equals", "expected": 42 }]
      }
    },
    {
      "id": "authorization",
      "kind": "http",
      "toolId": "developer.verify",
      "requirement": "An unauthenticated request cannot access protected data.",
      "arguments": {
        "kind": "http",
        "url": "http://127.0.0.1:5179/auth",
        "expectedStatus": 401
      }
    }
  ]
}
```

Profiles are `backend`, `api`, `database`, `worker`, `cli`, `native`, `desktop`, `data`, `ml`, `infra`, `security`, and `performance`. They constrain the required evidence kinds; they do not claim automatic expertise or comprehensive coverage for the whole domain. For example, an API profile needs HTTP assertions or executed tests; a database profile needs readonly database assertions or executed tests; a native package can use a command contract plus artifact checks.

Every check has an ID, a concrete `requirement`, an installed canonical tool ID, arguments, timeout and required/optional status. At least one check must be required. `test` uses `developer.test`, HTTP/JSON/file/SQLite checks use `developer.verify`, and command checks use owned `process.spawn` or `process.start` with an explicit expected result. `process.spawn` argv avoids shell exit/quoting ambiguity.

## Framework tests and measured probes

`developer.test` defaults to `dotnet test` with fresh private TRX files and combines all solution/project reports. Its default Python runner uses pytest JUnit; Go uses `go test -json`. Node, native and custom frameworks use explicit argv and a supported report format. Parsers support TRX, JUnit, Jest/Vitest JSON and Go JSON. They reject missing, inconsistent, zero-executed, skipped-only and unsupported reports; a nonzero runner exit cannot pass even if a report claims success.

Example test check:

```json
{
  "id": "focused-tests",
  "kind": "test",
  "toolId": "developer.test",
  "requirement": "The affected service regression tests execute and pass.",
  "minimumTests": 1,
  "timeoutSeconds": 300,
  "arguments": {
    "project": "tests/Service.Tests/Service.Tests.csproj",
    "filter": "FullyQualifiedName~ServiceRegression",
    "timeoutSeconds": 300
  }
}
```

For an explicit runner, `{report}` and `{results}` are substituted within individual argv tokens, never evaluated by a shell. `reportFile: "-"` means the selected framework writes its machine report to stdout. A supplied workspace report file must be freshly changed by this invocation. Use `environment` for bounded fixture settings; use `environmentFromProcess` to map named existing environment variables into the child without putting credential values in the task's arguments. Resolved environment values are not emitted in command/result metadata.

`developer.verify` supports:

- HTTP method/status plus JSON-pointer assertions and a supported JSON-schema subset. Readiness uses bounded GETs; the actual mutating request is not automatically replayed. Loopback is the default; remote HTTPS requires explicit `allowRemote`.
- JSON/schema/numeric/shape assertions for data, metrics and generated artifacts. Numeric tolerance is explicit; no model-quality claim is inferred from process exit.
- File existence, content, SHA-256 and required ZIP members for CLI/native/package outputs.
- Readonly SQLite SELECT assertions for persisted invariants. Workspace boundaries, linked-path rejection, query-only mode and execution limits apply. Migration or queue actions belong in an explicitly owned fixture/command; the readonly probe verifies their result.

Unsupported assertion kinds, unavailable dependencies and timeouts produce distinct non-passing states. Native/mobile GUI QA needs a configured framework with executable assertions and a supported report; selecting `desktop` alone does not launch or validate a GUI.

## Owned service fixtures

A coding specification can include `services`: each entry has `id`, a single `process.spawn` step, `readyUrl` and `readyTimeoutSeconds`. The readiness endpoint must be loopback and unused before startup. The Agent starts the fixture, checks readiness, executes probes and cancels only the returned owned job IDs in cleanup. Existing processes are not adopted or killed.

Fixture services and their probes share a host-created cooperative resource group. Unrelated tasks remain excluded. The group is never accepted from MCP input and does not grant permissions; startup, readiness and every check still use the installed schema and local permission/Arm gates. Readiness requires the enabled `developer.verify` capability as well as process start/read/cancel.

Keep runtime databases and outputs in `.jarvis-qa` or the normal generated/artifact directories. Source snapshots exclude generated caches and runtime SQLite/log files while retaining schema/config/code inputs. Tests that modify source make their evidence stale and require a new verification run. Choose disposable test data; do not use a production database as an automatic fixture.

## Inspect, repair and complete

| Tool | Behavior |
|---|---|
| `agent_task_events` | Reads bounded live process/check events by sequence cursor. Dropped history and clipped chunks are marked. |
| `agent_task_report` | Reads a task-owned report by artifact ID, validates its hash and returns bounded text pages. It accepts no arbitrary filesystem path. |
| `agent_task_context` | Reads applicable project guidance/manifests through guarded filesystem access and returns bounded context/hints. |
| `agent_task_verify` | Runs retained or supplied acceptance checks without replaying completed edits. |
| `agent_task_repair` | Executes only newly submitted steps, preserves task identity/history, then reruns checks. |
| `agent_task_complete` | Rechecks source/report freshness and all required evidence for a task awaiting completion. |

Known process/test failures can enter `NEEDS_REPAIR`; interrupted, timed-out or otherwise uncertain mutations remain non-replayable. A new repair request retains the goal, project, mode and timeout, uses a UUID `attemptId`, and includes only the new corrective steps. Identical repeated attempts return persisted state without running again. Changing a failed acceptance specification needs a retained reason; declaring an exemption does not erase the failure.

Receipts include the acceptance requirement, safe command identity, working directory, argument digest, source before/after, verification run, timestamps, precise outcome, test counts and task-owned report hash. The host mints receipts from executed tools; callers cannot submit a `passed` receipt. Large diagnostic output is streamed and bounded; `outputTruncated` and report `truncated` distinguish incomplete evidence from report pagination. Final observed file/JSON deliverables are rechecked against their measured hash or existence assertion, including outputs in directories excluded from the source fingerprint.

Final states distinguish `not_required`, `not_run`, `running`, `blocked`, `failed`, `stale` and `passed`. Individual checks additionally identify unsupported/invalid/missing reports and timeout. A passing check never erases a failed required check. Test reports that no longer exist or whose bytes changed cannot certify completion.

Local source hashes establish the revision that was observed. They do not attest that an arbitrary external service is serving that checkout; verify a build marker when that matters. Acceptance quality also depends on selecting assertions that represent the user's requirements.

## Validation and activation

The maintained tests include a real .NET executable fixture with a loopback API, SQLite queue invariants, CLI output, package resources and a two-assertion JUnit runner. The production task host runs failing cases, accepts a new repair under the same task ID, reruns checks, exposes reports/events and cleans its owned processes. Separate OAuth/MCP tests cover server routing/schema/owner boundaries. Parser fixtures test framework edge cases; they are not presented as live execution of every external framework.

Run `scripts/Verify-CodingQa.ps1` or `scripts/verify-coding-qa.bat` for the executable acceptance corpus and its contract tests. The script requires a fresh TRX, executed tests, zero failures and a zero process exit; it retains a report hash and explicit coverage scope. `-NoBuild` is only for an already built current Release test assembly. Legacy `Autonomous/Verification` adapters and `CodingHarnessScenarios` boolean fixtures are not the production completion gate; the active path is `RemoteTaskHost` and its guarded runner/probe contracts.

Deploy server and Agent 1.0.79 together, import/enable the installed `developer.verify` tool where required, and refresh MCP discovery. Browser extension 1.4.0 remains the FE runtime. Deployment of the MCP server does not update the Windows Agent. This release does not claim identical hidden Codex/model behavior or a global QA parity percentage.
