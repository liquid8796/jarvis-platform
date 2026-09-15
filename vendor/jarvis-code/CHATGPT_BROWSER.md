# ChatGPT browser sessions

The `chatgpt-web` provider runs the same Jarvis agent loop, tool registry, permissions,
hooks, MCP tools and skills as the other adapters. The browser is a stateful transport:
it supplies the account's model picker and sends through ChatGPT's page.

## Session lifecycle

- Desktop sessions carry their local session ID. A fork gets a new identity. Each
  subagent, workflow agent and forked skill invocation gets an independent identity
  that remains stable through its tool loop and background follow-ups.
- Browser profiles are isolated by account and scope. At most four browsers per
  process are active; additional work waits, and idle browsers release their slots.
- Resume checks the scope, account, project, model, complete system blocks, tool
  contracts and local history. Changing a schema or removing a tool refreshes the
  remote context instead of leaving the model with stale instructions.
- Recovery state is invalidated before a send. An error or cancellation resumes by
  replaying accepted local history into a fresh chat. Cancelled page operations
  cannot later click Send or overlap the next operation on that page.
- Desktop and CLI keep runtime mappings in `chatgpt-state`, separately from settings.
  A filesystem lease serializes the same scope across processes; atomic per-scope
  writes preserve unrelated sessions and do not overwrite account settings.
- A configured project pin is verified directly on first use, and revalidated if another
  process changes its durable value or a required route disappears. Stale pins are
  tombstoned, the owned-project list is read in 50-item pages, and a missing name is
  created and pinned before any prompt can be sent. Authentication plus project setup
  share a 15-second deadline instead of inheriting the answer timeout; later turns use
  the in-memory pin while its durable value remains unchanged.
- Creation intent is persisted before the non-idempotent POST. If cancellation makes the
  outcome unknown, the next run reconciles by name and waits through a short safety
  window instead of immediately creating a duplicate.
- Project navigation verifies Chromium's final URL and the route again while the outgoing
  conversation request is paused. A deleted, moved or inaccessible project that reaches
  a loose/foreign composer is aborted before the network send, so it cannot silently
  create or continue a chat outside the configured project.
- Chat rotation keeps its configured threshold. A fresh remote chat receives the
  local text history and the newest eight available images; omissions are stated.

## Tool and output fidelity

Tool descriptions and JSON Schemas are transmitted in full, including nested
objects, required fields, unions and parameter descriptions. Tool output and skill
instructions have no browser-specific character truncation. Core's persisted-output
policy still applies before provider serialization.

WebFetch does not open an auxiliary ChatGPT browser conversation to summarize fetched
pages. An empty Jarvis tool list cannot disable ChatGPT's native tools, so this provider
declares tool-free auxiliary inference unsupported. Jarvis fetches and converts the page
locally, then returns the bounded source content with its untrusted-content framing and
an explicit not-summarized notice to the main agent. API providers that support tool-free
inference retain model processing. This avoids native sandbox/search/connector calls in
the WebFetch summarization path; it does not disable ChatGPT's native tools globally.

Only a complete action-only answer can dispatch `JARVIS_ACT` calls. A malformed
batch is rejected as a whole. Quoted, fenced and explanatory examples remain text.
All accepted calls still pass through Jarvis hooks and permission checks.

If a verified final response has an invalid action envelope, no prefix of that batch
runs. The provider can ask the same conversation to correct the complete batch up to
two times, keeping its model and permissions and without resending attachments. Every
correction passes the stored-response and foreign-tool checks again; prose cannot
stand in for a corrected action batch. Exhaustion or cancellation accepts no actions
from the rejected batch. Remote message counts include corrections for safe resumption,
and their token costs remain explicitly estimated. Unfinished stored finals are polled
for completion before an action-format correction can begin.

Stored responses keep their channel boundaries: only `final` assistant text reaches
the action parser for channel-tagged turns. Progress/commentary and analysis cannot
prefix, replace or supply executable actions. Unchanneled legacy turns retain their
original text order. The complete active turn is still checked for foreign tool use;
selecting final text does not hide earlier sandbox or connector calls.

Long text is pasted in chunks. Before the real browser client sends its conversation
request, a request-stage gate preserves the intended text without changing model,
image attachments or authentication fields. An unknown request shape fails visibly
before sending. Browser page changes can therefore require a transport update.

Continuations wait for a live, editable composer after the previous answer and re-read
it across page updates. Each chunk must visibly land. If synthetic paste is rejected
before Send, the transport clears that unsent draft and retries preparation once with
Chromium's native text input; it keeps existing attachments and only then sends. Native
recovery selects only the active editor, uses trusted Backspace to clear it, and waits
for a stable empty editor before insertion. Up to three preparation attempts handle a
restored draft or remounted editor; a persistent failure sends nothing. Cancelling
native input closes its page before another turn can reuse it.

The web provider does not emit unverified answer previews, including for tool-less
chat. Progress remains visible; only verified final text is persisted or used for
actions. The generic CLI preview event remains available to other provider paths.

## Controls and measurements

ChatGPT still owns the model and Power controls, but Desktop mirrors their live state
instead of maintaining a release-time catalogue. Opening Jarvis's model menu refreshes
the account's selectable rows; `Latest` keeps its stable selector key while a resolved
pill such as `6 Pro` remains display metadata. The effort popover reads every live Power
rung, restores the page's original value after discovery, and stores the selected raw
key per model. Each turn verifies model and Power before any prompt text is pasted, so a
stale option fails without sending. Native web search and the output-token ceiling remain
controlled only by ChatGPT. Jarvis-hosted tools retain their own normal settings.

Every web turn requires a verified answer from the active stored conversation branch,
including requests with `Tools=[]`. Rendered page text is never an execution-provenance
fallback. Native workspace execution, connector calls and unknown native tool families
reject the turn before asset
downloads, Jarvis action dispatch or completion, even when the answer also includes
Jarvis action lines. Unverified answer previews are held back; progress remains visible.
Only the known `web` and `image_gen` native families remain allowed for search and image
responses, not as evidence of local workspace work. A tool-less response containing
literal action syntax is text, never a Jarvis tool call. A protocol revision invalidates
old remote contexts so resumed sessions receive the new execution contract without
changing their local transcript.

After a prompt POST is observed, a supplementary in-flight guard reads the current
conversation's provenance: one bounded request at a time, with a three-second timeout
and five-second gap. Only the exact expected active-branch user count is considered;
historical, abandoned, cyclic or incomplete branches cannot trigger it. Credential-bearing
backend reads and asset metadata requests run in a private JavaScript world bound to a
non-recyclable document context, not the page's replaceable Promise/fetch functions. Reads
use the fixed ChatGPT origin, reject redirects, and keep credentials out of diagnostic records.
On detected native work, Jarvis attempts Stop and quiesces/closes its page before
releasing the turn; it does not automatically resend. This can interrupt a long wrong-
runtime turn early, but cannot prevent its first server-side tool call or guarantee that
OpenAI has stopped work. Final stored-response verification is still mandatory. Native
execution and unverifiable provenance are terminal failures: CLI fallback, context-error
recovery and stall retries cannot silently replay them or discard completed local tool results.
Other browser Ask failures are also terminal for automatic fallback, since an error or cleanup
failure cannot prove the remote request never started. Manual retry remains a user decision.

On a new session, model selection can replace the Power row while the composer is
still initializing. Readiness checks resolve the connected, visible row and its
slider together on each poll; discovery, value reads and trusted keyboard input
use the same live-control resolver. Hidden stale rows are ignored without rejecting
the visual slider's intentional `aria-hidden="true"` attribute.

Token counts are estimates of the visible conversation, not account quota or
billable API usage. `Usage.IsEstimated` survives aggregation and persistence. The
context indicator and `/context` identify estimates; the context limit is configured
locally. Dollar-budget enforcement is unavailable for this provider.

## Audited inference paths (2026-09-14)

The audit covers built-in App, Core and CLI provider calls, including calls through
decorators and the orchestrator. No private server-side disable flag is assumed.

| Path | ChatGPT web behavior |
| --- | --- |
| Chat/Code, routines and side chat | Shared provider and in-flight provenance checks on every send. |
| Subagents, forks, skills and `Workflow.agent()` | Same guarded orchestrator route, with independent scope identities. |
| Internal action-format corrections | Same guard, expected-message count and bounded retries; no bypass for repair turns. |
| WebFetch | Local HTTP fetch and bounded, fenced source; no auxiliary browser inference. |
| Automatic permission review | Ask the user; never run browser inference to approve an action. |
| Session summaries | Existing cache remains readable; fresh browser-model summaries are unavailable. |
| Advisor and prompt hooks | Require a tool-free-capable API model; unsupported hook review returns an explicit unsuccessful verdict. |
| Manual, automatic and precomputed compaction | Full model summary and applying old model-summary cache are refused without changing history or deleting the cache; existing microcompaction remains separate. |
| CLI prompt suggestions | No passive browser-model prediction call. |
| CLI JSON-schema repair | Validate the main result; no tool-less browser correction call on invalid output. API retries remain. |
| CLI fallback and automatic recovery | Terminal provenance failures stop before fallback/history trimming, even if their text resembles rate-limit or context errors. |
| Provider connection tests | Read model-picker metadata only; no test prompt or authentication/inference claim. |
| Vendor WebSearch | API-only route remains; defensive capability check rejects an unsafe browser side-query. |

Unsupported auxiliary functions report their limitation instead of silently selecting
another paid model, fabricating a summary/verdict, or dropping user constraints. An API
provider explicitly configured for advisor/hooks can still perform that isolated work.

## Validation

Run focused checks from PowerShell 7:

```powershell
./scripts/verify-parity.ps1 -Suites Core,Providers,App,Cli -TestFilter 'FullyQualifiedName~ChatGpt|FullyQualifiedName~BrowserSessionIntegration|FullyQualifiedName~BrowserUsagePresentation|FullyQualifiedName~PrintProtocolTests' -ArtifactsPath artifacts/chatgpt-integration
```

The tests cover lifecycle, scopes, tool protocol, complete schemas/skills, images,
usage provenance, previews, provider-native control persistence and CLI registration.
Offline JavaScript fixtures execute the production composer code and cancellation
boundaries. The separately selected `ChatGptTransportNativeTests` fixture uses actual
Chromium with a local HTTPS server, an isolated profile and no account cookies; it walks
and restores a dynamic Power ladder, applies model and effort before the real outgoing
body, and checks cancellation. Cold-start cases send the first prompt without a
discovery warmup, replace a sliderless Power shell, ignore hidden stale controls,
and cancel while the control is mounting without sending later. These checks do not
establish availability of a user's live ChatGPT account or compatibility with every
future version of its page.

`ChatGptInflightWorkGuardTests` additionally verifies live provenance interruption,
single-send cancellation, hostile page Promise/fetch overrides, navigation races,
fixed-origin/redirect restrictions, and isolated authenticated reads/downloads using
offline Chromium fixtures. `BrowserAuxiliaryIsolationTests`, `ToolFreeCompactionTests`,
and the CLI terminal-failure fixtures check zero-call auxiliary gates and preservation
of real history when fallback is forbidden. These tests do not start live model jobs.
