# Prompt Continuity and Sessionless Execution Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans, superpowers:test-driven-development and superpowers:verification-before-completion. The user approved this design on 2026-09-17 and explicitly authorizes the completed patch to be committed and pushed to `master`.

**Goal:** Keep Jarvis MCP callable across consecutive chat prompts even when the host does not replay a prior `_jarvis.sessionHandle`, while preserving explicit multi-session isolation when a valid handle is present.

**Architecture:** MCP transport stays stateless and OAuth/device routing stays authoritative. Ordinary tools accept an optional `_jarvis` envelope; without it the gateway dispatches a sessionless call using an ephemeral, per-call execution context instead of rejecting before the agent. Explicit `session__*` and `workspace__*` tools continue to require a protected application-session handle. Destructive session closure is removed from the default public model surface; stopping work leaves sessions resumable.

**Tech Stack:** .NET 10, C# 14, ASP.NET Core MCP SDK, existing WSS agent router, SQLite session store, WPF/CLI host code, xUnit.

**Spec:** User-approved proposal from this conversation plus `docs/superpowers/plans/2026-09-17-multi-session.md`.

## Global Constraints

- Keep MCP Streamable HTTP stateless; do not depend on protocol-level session IDs.
- OAuth owner + selected enrolled agent remain the only routing authority.
- `_jarvis.sessionHandle` is optional for ordinary tools and required only for explicit session/workspace management.
- Missing session context must never fall back to a shared persistent OAuth workspace/session. Sessionless calls are per-call contexts with no persisted workspace/mailbox.
- Explicit `workingDirectory` or absolute tool paths remain usable sessionlessly; relative-path/process operations with no usable directory return `WORKSPACE_REQUIRED`.
- Existing explicit-session ownership for jobs/tasks/browser tabs stays strict. Sessionless owned resources use authenticated owner + enrolled device plus unguessable resource ID; they must not acquire another explicit session's resources.
- Finishing one task/turn does not close the application session. `closed` remains an explicit operator action only.
- Gateway rejections are audited without arguments, handles, tokens, clipboard or screenshot data.
- Release as product version `1.0.65` / assembly+file version `1.0.65.0`; update existing Markdown documentation, then conventional commit and normal push to `origin/master`.

---

### Task 1: Regression tests for prompt-to-prompt continuity

**Files:**
- Modify: `tests/Jarvis.Server.Tests/MultiSessionMcpTests.cs`
- Modify: `tests/Jarvis.Server.Tests/McpSessionContextTests.cs`
- Modify/Create focused Core tests under `tests/Jarvis.Core.Tests/` for agent sessionless context behavior.

**Interfaces:**
- `McpSessionContext.AugmentSchema(JsonElement, bool)` must permit `_jarvis` omission for ordinary tools.
- `McpGateway.CallAsync` must dispatch an ordinary tool when `_jarvis` is missing.
- Explicit session/workspace tools must still reject missing handles.

- [ ] **Step 1: Write RED server test**

```csharp
var opened = ParseText(await RawSessionCall(client, "session__open", new { label = "turn one" }));
var turnTwo = await RawSessionCall(client, "test__session_context", new { });
Assert.False(turnTwo.GetProperty("isError").GetBoolean(), turnTwo.GetRawText());
Assert.StartsWith("call_", ParseText(turnTwo).GetProperty("sessionId").GetString());
```

- [ ] **Step 2: Write RED schema test**

```csharp
JsonElement augmented = service.AugmentSchema(original, required: false);
Assert.True(SchemaGuard.Matches(SchemaGuard.Compile(augmented), WireJson.Element(new { path = "a.txt" })));
```

- [ ] **Step 3: Run focused tests and verify failure for current `SESSION_REQUIRED` behavior.**

### Task 2: Optional session context at the MCP gateway

**Files:**
- Modify: `jarvis-mcp-server/src/Jarvis.McpServer/Transport/McpSessionContext.cs`
- Modify: `jarvis-mcp-server/src/Jarvis.McpServer/Transport/McpGateway.cs`
- Modify: `shared/Jarvis.Protocol/SessionContracts.cs`

**Interfaces:**
- Add `TryResolve(ownerId, deviceId, handle)` or equivalent nullable resolver.
- Ordinary tool discovery exposes `_jarvis` as optional metadata.
- Explicit session/workspace tool discovery still requires `_jarvis`, except `session__open`.
- Sessionless wire `SessionId` is a non-persistent per-call ID, e.g. `call_<32hex>`.

- [ ] **Step 1: Implement minimal nullable session resolver and ephemeral call ID rule.**
- [ ] **Step 2: Change discovery so ordinary tools and `agent_task_*` do not require `_jarvis`; explicit session/workspace tools keep the requirement.**
- [ ] **Step 3: Dispatch missing-handle ordinary calls with ephemeral sessionless ID and no persisted workspace/session state.**
- [ ] **Step 4: Re-run Task 1 tests to GREEN.**

### Task 3: Agent accepts sessionless ordinary calls

**Files:**
- Modify: `jarvis-agent/src/Jarvis.Agent.Core/AgentConnection.Sessions.cs`
- Modify: `jarvis-agent/src/Jarvis.Agent.Core/AgentContracts.cs`
- Modify: task/process ownership helpers that currently call `RequireSessionIdentity()` unconditionally.

**Interfaces:**
- Add `AgentExecutionContext.HasExplicitSession` and nullable `TrySessionIdentity()`.
- `PrepareContext` accepts ephemeral `call_...` IDs for ordinary tools but never for `session.*`/`workspace.*`.
- Sessionless context uses empty/default call-local workspace semantics and does not touch `AgentSessionStore`.

- [ ] **Step 1: Add RED Core tests for negotiated-session agent receiving ordinary `call_...` context.**
- [ ] **Step 2: Implement `HasExplicitSession` / nullable identity helper.**
- [ ] **Step 3: Allow ordinary sessionless execution while keeping explicit session tools strict.**
- [ ] **Step 4: Verify Core focused tests GREEN.**

### Task 4: Resource ownership and workspace behavior without explicit sessions

**Files:**
- Modify only the process/task/resource/browser ownership code paths proven by failing tests.
- Tests: existing ownership suites plus new sessionless cases.

**Interfaces:**
- Explicit-session resources remain keyed by owner/device/session.
- Sessionless resources are keyed by owner/device plus resource/job/task ID and must not be treated as belonging to an explicit session.
- Sessionless relative operations require an explicit call working directory or absolute path.

- [ ] **Step 1: Add failing tests for sessionless process/task start/read/cancel and cross-explicit-session denial.**
- [ ] **Step 2: Implement minimal ownership changes required by those tests.**
- [ ] **Step 3: Run ownership suites GREEN.**

### Task 5: Session lifecycle and public surface

**Files:**
- Modify: `shared/Jarvis.Protocol/SessionContracts.cs`
- Modify: `jarvis-agent/src/Jarvis.Agent.Core/Sessions/SessionToolSet.cs`
- Modify server discovery tests.

**Interfaces:**
- Keep internal `session.close` for operator/UI compatibility if needed.
- Remove `session__close` from the default MCP tool list presented to the model.
- Add/retain a non-closing stop-work path through existing local operator `StopSession(close:false)`; task completion never calls store.Close.

- [ ] **Step 1: Write RED discovery test asserting `session__close` is not published.**
- [ ] **Step 2: Hide destructive close from public model discovery without breaking local UI close.**
- [ ] **Step 3: Verify idle session resumes and explicit operator close remains terminal.**

### Task 6: Diagnostics and reachability evidence

**Files:**
- Modify: `jarvis-mcp-server/src/Jarvis.McpServer/Transport/McpGateway.cs`
- Modify/add tests around `IAuditWriter` fixtures.
- Modify agent connection event/status code only where existing hooks support reason-coded states.

**Interfaces:**
- Pre-dispatch rejection audit: `tool.<name>`, outcome `rejected`, error code stored in bounded metadata/message field already supported by the audit model; never store arguments/handle.
- Distinguish at least session-context rejection from agent offline/queue failures in emitted diagnostics.

- [ ] **Step 1: Add RED test proving a gateway rejection creates one audit record before dispatch.**
- [ ] **Step 2: Implement bounded rejection audit and reason-coded connection diagnostic using existing event surfaces.**
- [ ] **Step 3: Run diagnostics tests GREEN.**

### Task 7: Documentation, version and release verification

**Files:**
- Modify: `VERSION`, `Directory.Build.props`
- Modify existing: `README.md`, `CHANGELOG.md`, `docs/ARCHITECTURE.md`, `docs/AGENT.md`, `docs/API.md`, `docs/SECURITY.md`, `docs/ACCEPTANCE.md`, `docs/BUILD-STATUS.md`
- Update versioned verification metadata/scripts only if `scripts/Verify-CurrentVersion.py` requires it.

- [ ] **Step 1: Update docs from required-handle semantics to optional ordinary-tool/sessionless continuity semantics.**
- [ ] **Step 2: Bump product package/assembly/file version to 1.0.65 / 1.0.65.0.**
- [ ] **Step 3: Run focused tests, all maintained .NET/JS suites, release build/publish verification and current-version verifier.**
- [ ] **Step 4: Review diff and working tree, then commit `fix(session): preserve MCP continuity across chat turns` and push normally to `origin/master`.**
