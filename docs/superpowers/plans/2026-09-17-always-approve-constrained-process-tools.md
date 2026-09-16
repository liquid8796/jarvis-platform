# Always Approve Constrained Process Tools Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a persistent `Always approve` choice for the constrained `process.start` and `process.spawn` approval prompts, with revocation from Tool permissions.

**Architecture:** Keep ordinary Full permissions and scoped capability leases unchanged. Add a separate exact-ID persistent grant set for only `process.start` and `process.spawn`; load/save it atomically with the existing permission file, consult it before scoped leases, surface it in the desktop permission UI, and let the approval dialog persist the current tool before approving the active request.

**Tech Stack:** C#/.NET, WPF, xUnit, existing Jarvis permission store/policy.

**Spec:** Approved conversation design on 2026-09-17: `Deny` / `Approve once` / `Always approve`, permanent across restart/reconnect, exact-tool scope only, explicit revoke control.

## Global Constraints

- Permanent approval is valid only for exact tool IDs `process.start` and `process.spawn`.
- Existing Full permissions remain semantically unchanged and continue to be ignored for these two constrained tools unless a permanent constrained approval or scoped lease exists.
- Persist before activating in memory; failed persistence must not authorize the request.
- Existing v1 permission files must load successfully; new writes use a backward-compatible v2 document.
- Update existing Markdown, bump package/assembly/file version from 1.0.62 / 1.0.62.0, verify, commit with `type(scope): message`, push `master`, deploy/restart MCP, and verify health.

---

### Task 1: Permission policy and store

**Files:**
- Modify: `tests/Jarvis.Core.Tests/ToolPermissionTests.cs`
- Modify: `tests/Jarvis.Core.Tests/InvocationPermissionTests.cs`
- Modify: `jarvis-agent/src/Jarvis.Agent.Core/ToolPermissionPolicy.cs`

**Interfaces:**
- Produces: `ToolPermissionPolicy.SupportsPermanentApproval(string)`, `AlwaysApprovedConstrainedTools`, `ReplaceAlwaysApprovedConstrainedTools(IEnumerable<string>)`, and v2 store settings that preserve v1 compatibility.

- [ ] Add RED tests proving v2 persistence, v1 migration, exact-ID validation, permanent approval bypass, and revocation.
- [ ] Run targeted tests and confirm failure is caused by missing permanent-approval behavior.
- [ ] Implement minimal policy/store changes.
- [ ] Run targeted tests until green.

### Task 2: Desktop approval prompt and revocation UI

**Files:**
- Modify: `jarvis-agent/src/Jarvis.Agent.Desktop/Services/LocalPrompts.cs`
- Modify: `jarvis-agent/src/Jarvis.Agent.Desktop/ViewModels/MainViewModel.cs`
- Modify: `jarvis-agent/src/Jarvis.Agent.Desktop/ViewModels/ToolPermissionsViewModel.cs`
- Modify: `jarvis-agent/src/Jarvis.Agent.Desktop/MainWindow.xaml`

**Interfaces:**
- Consumes: policy/store permanent constrained approval APIs from Task 1.
- Produces: third prompt button only for supported constrained tools and `Require approval again` revocation control.

- [ ] Wire `Always approve` so persistence succeeds before the current request is approved.
- [ ] Add explicit warning copy for permanent approval.
- [ ] Show `Always approved` state and revocation action in Tool permissions.
- [ ] Build the desktop project and run permission tests.

### Task 3: Release documentation, version, verification, publish

**Files:**
- Modify: `Directory.Build.props`
- Modify: `VERSION`
- Modify: `README.md`
- Modify: `jarvis-agent/README.md`
- Modify: `CHANGELOG.md`
- Modify: `docs/BUILD-STATUS.md`

- [ ] Bump package to 1.0.63 and assembly/file to 1.0.63.0.
- [ ] Document permanent constrained approval and revocation.
- [ ] Run targeted and full Release test suites plus version verifier.
- [ ] Build/publish required artifacts and confirm clean git diff.
- [ ] Commit `feat(agent): add persistent process approvals` and push `master`.
- [ ] Deploy/restart MCP on OCI and verify public/local health after restart.
