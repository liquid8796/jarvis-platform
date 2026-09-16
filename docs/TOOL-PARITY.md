# Baseline provenance and tool coverage

Input: supplied `jarvis-code(1).zip`, version 1.0.15. SHA-256 for each original file is recorded in `baseline-manifest.json`. All baseline C# tool source is retained at `vendor/jarvis-code`; the derived browser extension is separately hosted under Agent.Windows so native host identity does not collide. `baseline-verification.json` records the actual comparison, not an assertion of full runtime compatibility.

## Registry coverage

Counts below are derived from source registration, **not a captured tool list from an executed agent**. Run `jarvis-agent.exe list-tools` on Windows to validate the actual CLI manifest. Desktop includes the three guided-teach tools because a WPF surface exists; CLI intentionally does not advertise tools that cannot display that surface.

| Group | Desktop | CLI | Baseline / integration |
|---|---:|---:|---|
| Computer | 12 | 9 | Screenshot + state-bound computer_batch + computer.get_state + six extras + three desktop-only teach tools |
| Browser | 19 | 19 | All 18 `JarvisBrowserTools.Create` tools + browser_batch |
| Visualize | 2 | 2 | read_me + show_widget; actual host delivery replaces the baseline host-dependent show behavior |
| Filesystem / document / notebook | 8 | 8 | ReadFile, ReadDocument, WriteFile, EditFile, ListDirectory, Glob, Grep, NotebookEdit |
| Git | 5 | 5 | Status, Diff, Log, Show, Blame |
| Shell | 2 | 2 | Normal and Bash variants; detached background mode disallowed |
| Workflow | 2 | 2 | Todo and AskUserQuestion |
| Managed process jobs | 6 | 6 | process.start/spawn/read/write_stdin/resize_pty/cancel |
| Durable thread runtime | 7 | 7 | create/get/append_turn/queue/search/fork/checkpoint |
| Agent Core host tools | 3 | 3 | tool_program.run + developer.symbol_search + developer.test; plugin projections are additional/dynamic |
| **Expected total** | **66** | **63** | Subject to Windows build/runtime verification |

Mouse move/click/double-click/right-click/drag/scroll and keyboard actions already exist in the baseline computer batch/browser computer implementations. They are reused, not replaced with a new incomplete mouse simulator. This is not a binary clone of a specific Codex proprietary tool protocol.

Browser requires the derived extension/native messaging host, appropriate browser/site consent and an active browser session. Its original read-page, find, text, form, JavaScript, console, network, upload, window resize, tab and batch code stays in the vendor tree. Agent creates separate local bridge pipe and native-host ID; installation writes only its own HKCU entries after explicit user action.

## Intentional host differences

Tool public names are namespaced (`computer__…`, `browser__…`, `filesystem__…`, `process__…`) and may be mapped to reviewed catalog aliases. Schemas are sourced from the installed local tool. Agent wraps execution with remote-authorization/local-consent, workspace guards and schema checks. Exact original tool **implementation source** is retained, but remote behavior is deliberately not identical for security reasons.

`show_widget` displays isolated HTML in WPF or writes a local artifact in CLI. No external CDN, arbitrary network, host bridge, `sendPrompt`, inline ChatGPT Apps resource or widget streaming preview is advertised. The baseline read_me documentation may discuss features supported by its original host; this host's limitations take precedence and are appended to tool descriptions.

The original app has unrelated orchestration/provider/task/team/scheduling functionality. Its source remains available in `vendor`, but those capabilities are **not automatically exposed as remote tools**. This delivery does not register an arbitrary remote DAP attach/injection debugger, general-purpose subagent LLM runner, arbitrary MCP server spawning or screenshot video streaming service. 1.0.54 adds an owned local DAP adapter session contract only. New tools should implement `IAgentTool` and have explicit permission/schema tests.

114 optional font binaries from bundled skill assets are omitted; `omitted-font-assets.json` lists paths. No required computer/browser C# implementation is removed. Existing third-party copyright/license notices remain with their source; review redistribution rights of baseline/reference-derived assets before public publication.

## Jarvis state-bound Computer Use - 1.0.50

The public Computer surface intentionally differs from the reused baseline: Jarvis adds `computer.get_state`, appends opaque state metadata to standalone screenshots, and requires a current `stateId` on `computer.computer_batch`. The state token is scoped to one remote session and becomes stale after another observation, explicit invalidation or input dispatch. This prevents blind reuse of coordinates from an older desktop snapshot while retaining the baseline implementation for actual input and screenshots.

Foreground/focus/accessibility observation is bounded to 200 UI Automation nodes and 32,000 output characters. A target that denies UI Automation produces partial metadata; Jarvis does not escalate privileges or bypass Windows/UAC boundaries.

## Tool Code Mode and plugin projection - 1.0.52

Jarvis now adds the host-owned `tool_program.run` composite tool in Agent Core. It is deliberately smaller than an unrestricted Node/Python REPL but supports conditional and bounded iterative composition while retaining per-tool policy checks. Plugin manifests may extend the dynamic catalog only when the local host already supplies the implementation; the SDK does not claim binary compatibility with Codex plugins and does not execute manifest-referenced code.

## Managed plugin lifecycle - 1.0.59

Dynamic plugin projection now has a production lifecycle rather than startup-only metadata. Host-bound tools/hooks are selected by SHA-256-pinned compatible manifests; skill roots and MCP dependencies are validated metadata. Debounced local hot reload publishes through the same generation/digest catalog protocol, while malformed updates keep the previous valid snapshot. Local package install/update/uninstall is atomic/rollback-aware and still does not execute entry payloads or provide a remote arbitrary plugin loader.

## Runtime tool-catalog closure - 1.0.55

The dynamic registry is now synchronized end to end rather than only inside the Agent. A hot replacement publishes generation/digest/descriptors, server-side validation/persistence completes before `catalog.ack`, and subsequent tool calls are pinned to that acknowledged identity. Plugin tools discovered by the production Windows bootstrap therefore become remotely discoverable without reconnect, while catalog presence still grants no execution permission.

Process authority is also more granular than the tool-count table suggests: `process.start` and `process.spawn` require invocation-time capability leases for Full Permission. Shell-command prefixes retain boundary-aware text matching; argv spawns compare prefix tokens directly so a grant for `dotnet test` does not authorize `dotnet build`. Otherwise the normal local approval path is used.

## Durable Thread Runtime - 1.0.58

Seven host-owned `thread.*` tools add persistent orchestration state without exposing arbitrary SQLite access. They cover create/get/append-turn, queue mutation, bounded project search, fork lineage and non-destructive compact/rollback checkpoints. The production Windows runtime, CLI manifest and Desktop permissions view use the same descriptors.

Thread events are append-only audit records. Queue rows and checkpoint columns are materialized projections for efficient state reads; neither compaction nor rollback claims to undo external tool side effects. Artifact entries store bounded references/metadata only and do not bypass filesystem/network permissions.

## Process Runtime V2 - 1.0.57

The Agent now publishes exact argv `process.spawn`, interactive `process.write_stdin` and Windows `process.resize_pty` in addition to the original managed-job surface. Piped argv jobs keep stdout/stderr identities in structured cursor events; ConPTY exposes a unified `pty` stream. The combined `output` property remains for older process readers.

This is intentionally not a generic PID/process-inspection API. All operations require an opaque ID created by the current Agent process. Windows PTY children are attached to the Agent Job Object before resume, and local Pause/disconnect/permission revocation retains ownership cleanup.

## Developer power tools - 1.0.54

`developer.symbol_search` scans at most 2,000 workspace files, skips common VCS/build/dependency directories, clips long lines and caps returned matches. `developer.test` only invokes the fixed `dotnet test` executable/verb against a selected-workspace project or directory and returns structured test counts with bounded output; callers cannot replace the executable or inject an arbitrary command string through this tool.

`DapAdapterLauncher` is not remotely published as a tool. It validates an absolute local adapter file plus workspace working directory, starts an owned stdio process with shell execution disabled, and exposes only wait/stop semantics for that process. This is a foundation for future reviewed debugger adapters rather than a generic process/injection escape hatch.
