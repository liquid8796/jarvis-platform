# Codex-Parity Agent Harness Design

## Goal

Raise Jarvis coding-agent behavior toward the Codex Desktop behavior observed on this machine by moving correctness policy above the raw tool layer: agent-owned planning/repair/goal verification, a coding prompt stack, rendered frontend verification, skill instruction loading, persistent browser observations, visual-fidelity evidence, and engineering-quality harness benchmarks.

## Constraints

- Preserve the existing deterministic client-planned task path for `READ_ONLY` and `NORMAL` tasks.
- `AUTONOMOUS` may use an injected local/model-backed planner, but the Core assembly must remain vendor-neutral and must not embed API keys or silently select a paid model.
- All planned and repaired tool calls remain bounded by the current installed-tool registry, schema checks, Arm/Pause state, local approval policy, session ownership, timeouts and no-replay rules.
- No verification layer may grant permissions or bypass existing tool safety gates.
- Frontend verification must fail closed when its required evidence is absent; a successful compile/build alone is not rendered proof.
- Plugin skills are instruction documents only. Loading a skill must never execute arbitrary plugin entry code.
- Browser observations become stale after material navigation or mutating interaction and must be refreshed before refs are trusted again.
- Visual comparison is deterministic evidence plumbing; an optional model/vision judge can consume it, but Core does not require a vendor model.
- Every release patch updates existing Markdown documentation, bumps `VERSION` plus assembly/file version, runs verification, commits using `type(scope): message`, and pushes `master`.

## Architecture

### 1. Agentic task coordinator

Add `IRemoteTaskAgenticCoordinator`, extending the existing adaptive-repair concept with three bounded operations: generate an initial plan for a goal-only `AUTONOMOUS` task, repair a failed logical step, and verify the completed goal. `RemoteTaskHost` starts a goal-only autonomous task automatically only when this coordinator is configured. Otherwise legacy `NEEDS_PLAN` behavior remains.

The host persists every generated plan before executing it, validates it with `RemoteTaskRules` and the current tool registry, and runs all calls through the existing invocation path. After all steps succeed, a goal verifier receives the plan plus retained artifacts. `COMPLETED` is emitted only after the verifier passes; failure is explicit and recorded as verification evidence. This changes completion semantics without weakening permissions.

### 2. Coding prompt assembly

Introduce a vendor-neutral `CodingPromptAssembler` that produces ordered prompt layers for an injected planner: base coding policy, browser/verification policy, workspace/project context, tool capability summary, relevant skill instructions, completed/failing outcomes, and outstanding verification debt. The assembler has strict size limits and deterministic ordering so the same state produces the same prompt material.

### 3. Frontend rendered verification

Introduce a frontend-change classifier and `FrontendVerificationGate`. For frontend-affecting changes the gate requires evidence for target identity, non-empty rendered DOM/accessibility state, framework-overlay absence, console health, screenshot capture and a primary interaction with post-state proof. Layout/visual work additionally requires desktop and mobile viewport evidence. Evidence is represented by typed records so browser hosts and tests can supply it without coupling Core to Chrome.

The task completion gate checks required evidence before success. Missing or failed evidence becomes a verifier failure that can be surfaced to the agentic coordinator for repair rather than silently accepting a build.

### 4. Skills as model instructions

Add a bounded skill-document loader over validated `PluginCatalogSnapshot.SkillRoots`. It discovers `SKILL.md` files under declared roots, parses only bounded front matter/name/description plus Markdown body, and exposes metadata separately from full content. Selection is progressive: prompt assembly receives compact metadata, then selected skills are loaded into the current planning context. Paths remain contained within validated roots; symlink/reparse escapes and oversized files are rejected.

### 5. Persistent browser observations

Add a session-level browser observation tracker around the existing `SessionBrowserToolSet`. It assigns an observation generation to DOM/find/screenshot reads, invalidates the generation after navigation or material interaction, and rejects stale element refs passed back from an older generation. Existing Chrome tab/session ownership and serialized per-session execution remain unchanged.

### 6. Visual fidelity evidence

Add a `VisualFidelityLedger` model that compares expected checks against captured desktop/mobile screenshots and structural assertions. It records mismatches by category (layout, typography, color, iconography, overflow, interaction state) and blocks visual completion while required mismatches remain. An optional external vision judge can populate ledger items, but deterministic tests can use structural mismatch inputs.

### 7. Engineering benchmark suite

Extend `HarnessEvaluator` with engineering-quality metrics: build/test/browser/console/interaction/visual assertions, corrective-loop count and verification-evidence count. Add deterministic Codex-parity fixture scenarios representing modal repair, responsive clipping, API error state, stale loading state, visual regression and backend regression. These tests measure whether the harness demands the same proof, not whether one vendor model is intrinsically better.

## Data flow

`goal -> prompt context -> agentic planner -> validated persisted plan -> existing tool executor -> step evidence -> verification gates -> goal verifier -> COMPLETED`

On failure: `step/verification failure -> bounded coordinator repair -> revalidated replacement -> execute -> verify`. Browser/frontend failures are ordinary verification failures and therefore participate in the same repair path.

## Error handling

Planner output that is empty, oversized, schema-invalid, references unavailable tools, regresses stages or broadens a child task fails before execution. Coordinator exceptions are converted to bounded task failure messages without secrets. Browser verification distinguishes unavailable browser evidence from failed browser evidence; both block required frontend completion. Skill parsing failures are isolated to the offending plugin/skill and reported as bounded diagnostics rather than executing untrusted content.

## Testing

Every behavior is developed test-first. Core tests cover autonomous plan generation, goal verification, prompt ordering/bounds, frontend evidence requirements, skill containment/loading and visual ledgers. Windows tests cover browser observation invalidation and stale-ref rejection. Server tests cover task/MCP completion semantics and schema compatibility. Full `dotnet test Jarvis.slnx -c Release --no-restore`, current-version verification and release build checks are run before the final completion claim.
