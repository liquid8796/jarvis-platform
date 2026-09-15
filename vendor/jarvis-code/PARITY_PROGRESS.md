# Installed Claude parity implementation

Reference measured 2026-09-08: Desktop 1.46388.3.0; desktop CLI 2.1.260 (2.1.258 also present). Baseline source 5087a04. Branch codex/parity-claude-installed.

User authorized implementation of audit items 01–15, preserving existing accounts/configuration. Implementation and delivery are complete for those groups. This records measured coverage, not universal 100% equivalence. Source, tests, installed artifacts and live behavior remain separate evidence. See PARITY_RESULT_2026-09-08.md for delivery paths and remaining boundaries.

| Item | Scope | State |
| --- | --- | --- |
| 01 | PDF pages/images and citations | Implemented and included in final Core/Providers/App suites; actual PDF text/page image and citation stream/persistence checks passed; no live vendor citation request claimed |
| 02 | Browser shared/separate session storage | Implemented; 16 tests passed including real Electron cookies/storage/restart and WPF session switching; legacy logins retained |
| 03 | CLI streaming/SDK integration | Implemented; real Python SDK 0.2.152 final-artifact smoke passed with hosted MCP, permissions, hooks, partial/structured output and local controls |
| 04 | Structured output and cost budgets | Implemented; final CLI and SDK checks passed; configured list-price estimates are distinct from invoices, unknown prices remain null |
| 05 | Remaining local CLI flags and REPL UI | Implemented; final CLI 232 passed; real ConPTY tabs/dialogs/fleet and detached lifecycle verified; Chrome bridge and native IDE transports included; trust acceptance precedes runtime activation |
| 06 | Background tasks, Monitor, Cron and session messaging | Implemented; final suites and two-process mailbox passed; old session workers/notices cannot enter replacement sessions |
| 07 | MCP schema validation/discovery | Implemented; 16 tests passed including hosted SDK registration; Ajv first-error wording and CLI host-list gates measured |
| 08 | Widget attachments/connectors/progressive input | Implemented; 29 App + 4 Core checks passed, including real trusted-input/file bridge |
| 09 | Composer, comparison and custom output styles | Implemented custom styles/editor, full comparison plus-menu/files/paste/dictation and real skill atoms/drafts/undo; focused 14 and final integrated checks passed |
| 10 | Review, panes, media, sidebar and summaries | Implemented; 81 checks, native pane/sidebar exit0 and live summary verified. Actual 1.46388.3.0 annotation toolbar additionally has six tools, undo/redo and measured-width compact layout; native interaction/export checks passed |
| 11 | Markdown/highlighting/math/Mermaid | Implemented; native render/clip, 30 highlight fixtures, 767 stream prefixes and native Markdown checks passed; engine adaptations in HANDOFF_UI.md |
| 12 | Extension updates | Implemented runner/transaction/cache/rollback; 86 checks passed; local bundles correctly ineligible; account directory provider excluded from scope16 |
| 13 | IDE integration | Jarvis VSIX installed and real VSCode diagnostics/selection/diff/accept verified; existing extensions/settings preserved. Native Claude lock-file/WS/SSE compatibility and final regression checks passed; no Claude IDE extension installed to exercise live |
| 14 | Auto permission classification and PR workflows | Implemented with bound branch/authority checks, stale-classifier/session guards and deny-before-ask-before-allow precedence; final integrated checks passed; no live GitHub mutation used |
| 15 | Installed-reference discovery and parity coverage | Final strict native suite: 7092 passed, 0 failed/skipped; two additional live tests passed on identical runtime hashes; final SDK passed; packaged/deployed files and actual startup paths verified |

Baseline evidence: artifacts/parity-audit-2026-09-08/REPORT.md; fresh isolated build selected static/corpus/request-preview checks: 441 passed, 0 failed, 0 skipped. No live UI equivalence was claimed. Do not close a row merely by removing a declared delta or deleting an assertion.

Final consolidated run: artifacts/final-parity-verification/runs/20260908T144839Z-ec58b68e/summary.json. Core 336, Providers 225, App 2373, CLI 232, Parity 3926. The two real-provider tests are retained from run 20260908T142448Z-c97da89d; App/Core/Providers/Host hashes match the delivered files. No baseline was auto-approved; a golden fixture's accidental Code background was corrected back to its intended Chat pose.

Delivery: artifacts/releases/jarvis-parity-20260908/{desktop,cli}; exact copies also replace the standard generated App/CLI bin/Debug/net10.0-windows outputs. Old outputs are backed up under artifacts/parity-delivery-20260908/backup-142953. Both deployed entry points exited 0 in isolated delivery smoke checks. Package, source, deployment and post-delivery hashes are recorded beside the release.

Preservation: default settings/ui-settings hashes are unchanged after final verification and delivery. A pre-isolation native test had pointed three browser native-host registrations at a deleted test profile; these were explicitly repaired to the existing normal-profile manifest and remain healthy. The new runner isolates those registrations. Optional cleanup of older failed fixtures/Python cache was blocked by automatic review; generated leftovers remain, as do the final smoke fixtures retained for evidence.
