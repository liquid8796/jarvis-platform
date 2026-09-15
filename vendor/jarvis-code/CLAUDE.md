# Jarvis platform integration — 1.0.16 (2026-09-14)

This copy is the supplied Jarvis Code 1.0.15 baseline. The new Jarvis Agent reuses
its computer-use, browser and filesystem implementations through an adapter.
Only this document and Directory.Build.props are intentionally changed in this
vendor tree. Original tool source and assets remain byte-identical; see the root
`docs/baseline-manifest.json` and `docs/BASELINE-PARITY.md`.
The historical publishing instructions below are provenance, not authorization
to push the new platform or operate its remote machines.

---

# Jarvis Code

> Current implementation follow-up (2026-09-08): installed-reference parity work is
> tracked in [PARITY_PROGRESS.md](PARITY_PROGRESS.md). Historical descriptions and
> test counts below predate that work; check the ledger and current source before
> treating an older "not implemented" paragraph or test total as current.

## Patch publishing policy (user instruction, 2026-09-08)

- After each completed patch, run the relevant checks, bump the Jarvis version,
  commit the patch, and push it to `origin/master` without asking for confirmation again.
- Select checks by the changed behavior. Do not run the full App/Parity/solution suite,
  UI screenshots, geometry, WPF/Electron fixtures, or live-provider checks for unrelated
  patches. Version-only, documentation and workflow patches use static/build checks as
  appropriate; they do not need `testhost`, a UI smoke run, or a repeated parity sweep.
- UI tests are manual opt-in. Run only a focused UI check when explicitly requested or
  needed for the UI behavior being changed; never add `-IncludeNativeUi` as routine
  post-patch validation. The runner requires explicit `-Suites`, supports `-TestFilter`,
  and native fixtures require `JARVIS_PARITY_NATIVE_UI=1` even with direct `dotnet test`.
- Use the user's exact commit-title format: `type(scope): message)` (including the
  final `)`). Example: `fix(cli): preserve session ownership)`.
- Keep the .NET product version in `Directory.Build.props` so Desktop and CLI stay
  synchronized. Increment the patch component by default unless the user requests
  a different version change. Read the current version from that file before bumping it.
- Fetch and preserve remote work before pushing. Do not force-push or skip failing
  required checks. If a branch rule blocks the push, report the concrete blocker.

A .NET 10 agentic coding engine with a native WPF desktop app:


- `JarvisCode.Core` — agent loop, tool registry, sessions, settings, permissions, markdown AST
- `JarvisCode.Providers` — Anthropic / OpenAI / Gemini / Ollama / NVIDIA Build (NIM) / Kimi (Moonshot)
  / OpenRouter / TokenRouter / DeepSeek / Zhipu AI / MiniMax / LLM API (llmapi.pro) / AWS Bedrock /
  Google Vertex AI HTTP + SSE adapters, plus whatever endpoint the user adds by hand.
  **A string is read off the wire the way the reference reads it** (`Http/WireJson.cs`):
  a stream that splits an astral character across two deltas has to send each half as
  its own escape — one delta ends `"…\ud83d"` and the next opens `"\ude80…"` — which is
  well-formed JSON that the reference's JavaScript reads without noticing and that
  `System.Text.Json` refuses in both directions, ending the turn on an
  `InvalidOperationException` where the reference simply concatenates the halves back
  into the character. Every adapter's response reads therefore go through `AsText()`,
  which keeps `GetValue<string>()` as its fast path and, only where that runtime
  refuses, unescapes the raw token itself and hands back the half a .NET string holds
  perfectly well; a value that is not a JSON string still throws, since that is a shape
  error in the response rather than a character this runtime dislikes. The three places
  that re-parse *accumulated* tool arguments (`AnthropicProvider.ParseArguments`,
  `AgentOrchestrator`, `CompactionStripper`) close the same defect's other door: a
  stream cut between the two halves leaves arguments .NET cannot transcode back to
  UTF-8, and that refusal is now the malformed input it is rather than an uncaught
  throw. What is deliberately not matched is the write side — serializing a lone half
  substitutes U+FFFD instead of the escape `JSON.stringify` emits, which shows only if
  a stream dies mid-character.
  Bedrock and Vertex serve Claude through the user's own cloud account, sharing Anthropic's
  wire: the Messages event grammar is parsed once (`Anthropic/AnthropicStream.cs`) — where
  **`message_delta` overwrites every usage field it reports** and leaves `message_start`'s
  standing for the ones it omits, which is the reference SDK's own null-guarded arm
  (`output_tokens`, then `input_tokens`, `cache_creation_input_tokens`,
  `cache_read_input_tokens`, measured in claude.exe 2.1.255 at ~182,678,400 and still there
  in 2.1.258 at ~181,057,753). Reading only `output_tokens` there dropped the input and
  cache counts of any endpoint that reports the authoritative totals at the end rather
  than at the start, and a cache hit then read as a
  context that had *shrunk* — which freezes every figure derived from it, since the
  auto-compaction threshold, the context ring, `/context` and the `<total_tokens>` task
  budget all read the same number, and that budget's `max(previous, rolled + current −
  anchor)` clamps a shrinking context to zero used — and the
  body built once (`BuildRequestBody`), with each cloud swapping `model`/`stream` for its
  `anthropic_version` and never sending the server-side web_search tool (neither cloud has
  it). `Bedrock/` signs requests with a dependency-free SigV4 (`AwsSigV4.cs`, pinned to the
  official worked example) and unwraps the response's `vnd.amazon.eventstream` binary frames
  (`EventStreamReader.cs`, CRCs unvalidated — TLS already covers integrity); the key slot
  stores the secret access key, and `AppSettings.BedrockRegion`/`BedrockAccessKeyId` ride the
  provider card as plain fields. `Vertex/` posts to the publisher model's `:streamRawPredict`
  (plain SSE; `global` region uses the un-prefixed host), authenticating with the key slot's
  credential — a service-account JSON is exchanged for a cached cloud-platform token via a
  self-signed RS256 JWT (`GoogleServiceAccount.cs`), anything else is used as a raw Bearer
  token — with `VertexProjectId`/`VertexRegion` on the card. Model availability is per
  account/region on both, so neither ships built-in catalog entries: ids
  (`us.anthropic.…-v1:0` profiles, `claude-…@YYYYMMDD`) are added on the provider card,
  whose `ProviderFieldSpec` rows are the generic home for such non-URL config.
  Only Anthropic's format carries images inside a tool result; the others attach them to the
  user turn that follows it (`ToolResultImages`), so screenshots reach every provider.
  Five endpoints share one wire rather than an adapter each
  (`OpenAiCompatible/`): the aggregators **OpenRouter** and **TokenRouter**, and the vendors
  **DeepSeek**, **Zhipu AI** and **MiniMax**, all speak OpenAI's chat completions with small
  documented departures, so a vendor is an `OpenAiCompatibleDialect` — where its API root
  sits, which output-cap field it takes, whether `stream_options` is documented there, and
  its own reasoning switch — over one `OpenAiCompatibleProvider`. `OpenAiProvider` is
  deliberately untouched: it is OpenAI's own adapter, pinned by the request-wire parity
  suite, and a relay's quirk must not be able to move it. What each vendor is sent was read
  off its docs (2026-09-01) and is a 400 for the whole turn when wrong, so nothing is
  guessed: OpenRouter takes `reasoning:{effort}` and the app's whole six-rung ladder passes
  through, and it is sent no `stream_options` because its docs call that deprecated and
  no-op; DeepSeek and Zhipu take `thinking:{"type"}` and a **sibling** top-level
  `reasoning_effort`, folded onto the low/high/max the two document, with Zhipu's dial
  riding only the GLM ids that published it (5.2 and above, parsed from the id); TokenRouter
  and MiniMax document no reasoning switch at all, so none is sent and the model's own
  default stands. Reasoning that does come back is read from whichever of the three
  spellings carries it (`reasoning_content`, `reasoning`, `reasoning_details[].text`), at
  most one per chunk. Endpoint resolution is the dialect's own default, not "append /v1":
  the vendors put their roots at different depths (`/api/v1`, `/v1`, `/api/paas/v4`, nothing
  at all), so a bare host is completed with the path the vendor's documented root carries —
  which is what lets `openrouter.ai` and `api.z.ai` both land correctly. Zhipu and MiniMax
  each publish two roots for two separately-issued keys (api.z.ai / open.bigmodel.cn,
  api.minimax.io / api.minimaxi.com), offered as presets on the card, because a key sent to
  the wrong region reads as an auth failure rather than a routing one.
  **A custom provider is the same machinery with the user holding the pen**
  (`Custom/CustomProviderFactory.cs` + `Core/Settings/CustomProviderSpec.cs`): an id, a
  name, a base URL, one of the two protocols this app already speaks, and whether the
  endpoint authenticates at all. Nothing new is implemented for it — the OpenAI protocol
  reuses the shared wire and the Anthropic one reuses `AnthropicProvider` (which gained an
  optional `requiresApiKey` resolver beside the four overrides `LlmApi` already added, so a
  keyless server on the local network is not refused for a key it never wanted) — which is
  why a hand-written provider gets the same key rotation, request inspector, connection test
  and model list as a built-in one. The id is what keys, models and sessions are filed
  under, so it is validated before it is stored and may not take a built-in's: shadowing
  "anthropic" would send stored sessions somewhere else with nothing on screen having
  changed. `AppProviderRegistry` (App) is `ProviderRegistry` made re-readable — a whole
  lookup swapped at once on each settings save — because a list edited on the Providers page
  cannot be fixed at construction; a spec whose id or URL a hand-edited settings file made
  unusable is skipped rather than registered and failing on every call. Removing one takes
  its keys and its models with it, behind a confirm, since leaving them would show a card
  claiming this build has no such provider — a confusing way to describe something just
  deleted.
  `LlmApi/` is a relay rather than a vendor: llmapi.pro serves Claude models behind three
  protocols on one `sk-…` key, and each protocol wants a differently shaped base URL
  (Anthropic `https://llmapi.pro` → `/v1/messages`; OpenAI `https://llmapi.pro/v1` →
  `/v1/chat/completions`; Gemini `/v1beta/models/{model}:generateContent`), which its docs
  call the top cause of a 404. `LlmApiProvider` therefore takes whichever base URL is
  configured, strips any `/v1`, `/v1beta` or full endpoint off it and appends the path for
  the chosen protocol, then delegates to the existing Anthropic/OpenAI wires — both now take
  an optional id, display name and endpoint resolver, defaulting to their own vendor. The
  provider card offers the docs' two shapes as presets (Anthropic without `/v1`, OpenAI with
  it) and either resolves for either protocol. What it does **not** send is the
  `?beta=true` namespace Anthropic's own API takes effort-era requests at: that is the
  vendor's, and the relay documents one path with no query, so `AnthropicProvider` gained a
  `betaNamespace` flag that only llmapi turns off. Its
  Gemini protocol is deliberately unimplemented: the vendor documents it as text-only with
  tool calls unmapped. Catalog ids are prefixed `llmapi/` and stripped on the wire, because a
  session stores only a model id and `claude-opus-5` would otherwise resolve to Anthropic.
  `ChatGptWeb/` signs in with the user's exported chatgpt.com cookies through
  `IChatGptTransport`. Desktop supplies the browser at startup; CLI supplies a lazy STA
  dispatcher when its first browser-provider request runs. The real page sends messages with
  its own authentication and checks. A request-stage browser gate preserves the exact prompt
  text in the client's outgoing message and refuses unrecognized payload shapes.
  Local session identities, including distinct fork and child-agent identities, route each
  conversation to its own browser. Up to four browsers run at once; same-session calls queue.
  New-chat navigation requires the exact destination, never a prefix match against the site root.
  Cancelling aborts the active page operation, prevents later send clicks and waits for cleanup
  before another turn can use that browser. Sign-in failures retain a window the user can open.
  Browser shutdown is part of both Desktop and CLI disposal.
  The request includes all leading system blocks, the full system prompt, full tool descriptions
  and recursive JSON Schemas. A tool request must occupy the entire answer as one or more
  `JARVIS_ACT` lines; prose, quoted examples and code fences cannot dispatch tools. Tool results
  and skill `FollowUpText` remain complete, with Core's existing persisted-output policy handling
  oversized tool results. Calls still use the common tool registry, hooks and permission gate.
  The account, local scope, project, model, complete prompt, tool contract and history identify
  a resumable chat. Changes to schemas, removed tools, project or history open a fresh context.
  State is invalidated durably before sending so an interrupted remote turn cannot be replayed
  as accepted local history. Fresh contexts replay available historical images as attachments,
  with an explicit omission note when the browser's eight-image limit is exceeded.
  Live answer snapshots are transient display data, separate from authoritative text and tool
  calls read from the stored conversation. Token counts carry `Usage.IsEstimated`; context UI
  labels estimates and configured limits, and dollar-budget checks refuse unmeasured usage.
  ChatGPT controls native web search and output length. Desktop model and thinking-effort controls
  are mirrored from the account's live composer: opening the model menu refreshes its rows, and the
  effort slider stores the page's opaque Power key for the next turn. New picker options therefore
  do not require a Jarvis release; Settings can also refresh them through "Read models from ChatGPT".
  Implementation and validation notes are in [CHATGPT_BROWSER.md](CHATGPT_BROWSER.md).
- `JarvisCode.Host` — Windows storage layer: app-data paths, DPAPI key protection, settings service
- `JarvisCode.Cli` — `jarvis.exe`, the terminal front-end (see "The CLI" below)
- `JarvisCode.App` — the WPF UI (net10.0-windows, Win 10/11), written fresh against the Core API

## The UI (JarvisCode.App)

A native C#/XAML recreation of the Claude Desktop experience on top of this engine. MVVM is
hand-rolled (`Infrastructure/Mvvm.cs`); the only NuGet dependencies are `System.Speech`
(voice input) and `WpfMath` (LaTeX in chat, rendered to theme-filled geometry).
Everything that renders web content — the Browser pane, the artifact tile, question
previews, the diagram renderer, the ChatGPT session — runs on **Electron 42.10.0**,
the reference desktop's own build (see "The browser engine" below); there is no
WebView2 anywhere in the app. Highlights:

- **Two surfaces**: Chat (conversation; no agentic tools) and Code (full agentic tool set,
  permissions, checkpoints, MCP, skills, plugins). Sessions are stored per surface under
  `%APPDATA%\JarvisCode\sessions\{chat,code}`. **What a chat turn carries is the
  conversation's own choice, not the account's** (`Services/ChatConversationSettings.cs`,
  measured from the reference's tools menu — `c752b32f8-DqlexaAe.js`, its
  `toggleSearchTool("enabled_web_search", …)` and the Tool access submenu): web search,
  extended thinking and tool access are stored per session id in ui-settings, so one chat
  may search the web while the next does not. Two opt-ins add tools — desktop control from
  Settings, and the connectors the user switches on in the composer — and
  `TurnContextFactory.CreateForChat` reads all of it: `EnableWebSearch` from the
  conversation, `ThinkingEffort` from its Extended thinking switch (off sends this engine's
  no-thinking body), and the registry from Tool access, where the reference's default
  "Load tools when needed" is this engine's deferred registry.
- **Side panes are tiles, not one panel** (`Services/TileLayout.cs`,
  `Controls/TileHostPanel.cs`, `Views/Panels/PaneTile.cs`, `Views/CodeWorkspace.Tiles.cs`):
  the reference lays a Code session out as one mosaic whose tiles are the conversation and
  every open pane, so this port's single side panel became that host. The layout model is
  the reference's own tile module (ion-dist chunk `cde8ce059-Bx5SPiHJ.js`) ported operation
  for operation — normalize (a stack of the same direction is flattened, a one-child stack
  collapses into its child keeping the parent's flex), append right, append below, remove,
  rename, solo, and the three drop targets its `y` takes (insert, wrap, split) — over its
  measured constants (`Tq`: gap 12, padding 8, min tile 100, min tile *width* 280, drag
  lift 24, and the 24px edge band that separates a wrap from a reorder). Flex is shared out
  by its `x` (children under their minimum are pinned there and the rest re-shared, the
  result renormalized to sum to the child count) and the conversation carries the
  reference's own 320px `overflowMin`. `TileHostPanel` arranges every pane at the rectangle
  the tree gives it rather than nesting Grids, which is what lets a closed pane stay a
  child and keep its engine window across an open/close cycle. Each tile is a card with the
  shared chrome the reference overlays — back · ⋮ · pop out · expand (which solos the pane
  and hides the conversation) · close — plus its 44×16 drag handle centred on the top edge
  with the reference's 32×3 affordance that fades in on hover or focus. Dragging shows the
  reference's own drop overlay ("Split view" where a new stack would form, "Open here" where
  the tile would take a slot). **A session row dragged out of the sidebar gets that same
  overlay**: the mosaic accepts the sidebar's own drag format
  (`CodeWorkspace.SessionDragFormat`, which `SidebarView` now names rather than repeating),
  runs the same `DropTargetAt` and raises `SessionDropped` — "Split view" opens the session
  beside the others in the split grid, "Open here" opens it in the surface it landed on.
  The handle's arrow keys move the tile along its stack and a
  perpendicular arrow previews a split that Enter commits and Escape cancels, which is what
  its `EWfxXrA85v` instruction describes. Boundaries resize by drag with the "Resize"
  tooltip, and the mosaic is persisted whole in `UiSettings.TileLayout` — the reference
  keeps one `tileLayout` in its store rather than one per session, and so does this. A
  stored pane this build no longer has is dropped on restore rather than arranged as an
  empty rectangle. `--open=panes[:name]` poses it.
- **The panes themselves** (`Views/Panels/`): beside terminal · diff · browser · files ·
  artifacts · background tasks · tasks · side chat, the reference's remaining pane kinds are
  built — **Plan** (`PlanPanel.cs`: the session's plan file as markdown in the reference's
  68ch column, a "Tasks" section carrying the todo checklist, its Copy plan → "Copied" and
  "Open in…" header actions, and the "No plan yet" empty state over a Checklist glyph),
  **Pull request** (`PullRequestPanel.cs` + `Services/PullRequestPresentation.cs`: `#n` ·
  state badge · author · updated, then Description / Reviews / Checks / Activity and the
  changed-file list with its ± counts, Review changes, and a menu of Open on GitHub · Review
  changes · Copy link · Copy commit SHA — read from `gh pr view`, the CLI `PrActivityTools`
  already polls. The badge is the reference's own nine-state resolution: changes-requested
  beats a conflict, which beats an approval), **Runs** (`RunHistoryPanel.cs` +
  `Services/RunHistoryPresentation.cs`: the routine's own header with its Local badge, its
  schedule and a Details link, then Running and Completed sections of run rows — a session
  started by `RoutineRunner` now records the routine it ran, which is the link the pane
  needs, and one that was not started on a schedule gets the reference's "Not a scheduled
  run" empty state), **Session** (`SessionPanePanel.cs`: another session's transcript,
  read-only, which is what the reference's own session view is — it hands that view a
  context with every opener null), **Transcript** (`TranscriptPanel.cs`: this session's feed
  in a pane, rendered with the surface's own templates) and **File**
  (`FileViewerPanel.cs`: a file with its `#L5-L20` range, Find in file, the Preview / View
  source pair for markdown, Show in Explorer, Open in…, Download file, Copy, and an edit mode
  whose Cancel/Save replace them — leaving a file with edits raises the reference's
  "Unsaved changes" question answered by "Discard changes") and **Simulator**
  (`Views/Panels/EmulatorPanel.cs`, the live Android emulator the Android Emulator
  server's `attach` opens — see its own bullet). The framebuffer and watercolor panes are
  declared in `Deltas/reference-surface-deltas.tsv` rather than built.
- **Files pane search** (`Services/FileBrowserSearch.cs`): the reference's file index, not
  the Fuse the slash menu uses. `FileNameIndex` is its `Aa` ported whole (ion-dist chunk
  `cd089cf92-CPpbZ5h_.js`): a subsequence matcher over a lowercased path list with an a–z
  bitmask prefilter, adjacency worth 4, a gap costing 3 plus its length, its `Pa` bonus (8
  at the path start or after `/ \ - _ . space`, 6 at a camelCase boundary), a short-path
  bonus of up to 32, the top-`limit` selection with its early-out, and the rank fraction it
  reports as `score` — including the rule that a path containing "test" is multiplied by
  1.05 and so can never outrank its neighbour. An empty query answers the reference's
  top-level cache (first path segments, deduplicated, shortest first, capped at 100), and a
  query carrying a capital is matched case-sensitively. A query opening with `?` is the
  content search instead, capped at the reference's 200 hits and debounced at its own 250ms
  where a name query waits 120ms. Rows show the matched characters in semibold, a content
  hit carries "Ask about this" onto the composer, and a folder row opens a terminal there.
- **Diff pane review half** (`Views/Panels/ChangesPanel.Review.cs`,
  `Services/ReviewFindings.cs`): "Expand diff" opens the reference's dialog — titled
  "Changes", described `{base} → {head}`, with its Unified / Side-by-side "Diff layout" control — the
  scope menu grew a Commits group that re-scopes the diff to one commit, and the findings
  `ReportFindings` reports now drive the reference's stepper: "Finding {n} of {m}" with its
  two arrows, Apply fixes (which marks them fixing and sends them), Re-run review (once they
  are all addressed) and Dismiss, with "Applying fixes…" and "Fixes applied" as its other
  two status lines. A diff line's own menu carries Annotate and Add comment, both reaching
  the composer as a context chip, and a file row's Open item names the handler Windows
  registered for the extension — "Open in {appName}", or plain "Open" when there is none
  (`Services/DefaultApplication.cs`).
- **Session view** (Code surface, ported against desktop 1.40609.1.0): a **home view**
  that is the reference's action center (`Views/HomeView.cs` over
  `Services/HomeViewPresentation.cs`, its `LM`/`NM`/`jM` in the ccd chunk
  `c11959232`@86400) — three sections in its order, **Sessions**, **Projects** and **Pull
  requests**, each a header with "Mark all as read" and a "Show {n} more"/"Show less" toggle
  over rows carrying a coloured kind pill (Needs input · Ready for review · Unread ·
  Working), a summary, the repo, a narrow relative clock and a hover dismiss ×. How many
  rows fit is the reference's own budget (three plus one per 800/900/1000/1100px of window
  height, capped at five for sessions and folders); which sessions list is its `NM` (never
  an archived, pinned, running or dismissed one; blocked rows lead, each group newest
  first); and the greeting switches between "What's up next" and "Welcome back" on its
  `landingClear`, which is also the only state that shows the usage stats card
  (`Controls/UsageStatsCard.cs` — Overview/Models tabs, All/30d/7d range, stat tiles,
  activity heatmap, fun-fact line). The **session titlebar** is the reference's
  `epitaxy-titlebar` ported whole (desktop 1.44121.4.0, its `Nh` in ion-dist chunk
  `c66fe388e-DOFZnzRG.js`, over the slot classes it imports from `shared-17`):
  `Controls/SessionTitleBarPanel.cs` is the bar itself at its `h-[32px]` with `pl-0` and
  `pr-12`, laying out the 32×24 origin gutter, the title, the `gap-xs` and the origin pill
  against a trailing cluster that is `ml-auto flex gap-1 pl-24` and pins its controls to
  26px. It is a panel rather than a stack because the reference's fitting rules are stated
  in terms only the layout can supply — how much room is spare, and how wide the title
  wanted to be against how wide it got. Those rules are
  `Services/SessionTitleBarLayout.cs`, its `Th`/`Eh`/`Dh` over `yh=5, bh=100, xh=24,
  Sh=32, Ch=96, wh=1`: the pill collapses to its folder icon once the bar is full **and**
  the title is already clipped, and expands again only once every toggle is back, the
  budget has grown past the mark it collapsed at, and 96px are free; toggles fold into the
  ⋮ from the front, at most five, and a toggle returns only when its own width plus 24px of
  hysteresis is spare. Because `Th` charges a title at most 100px of what it wants, a long
  title truncates rather than folding the rail — which is the reference's own answer.
  The **title** carries its class verbatim (`truncate text-body-medium text-primary
  select-none`, so 14/20 at weight 500 in the primary colour) on a button that is
  `bg-transparent border-0 p-0 rounded-[3px] cursor-text` — the reference draws no border
  and no padding there — named "{name}, rename session", tooltipped "Rename", refusing a
  repeating Enter as its own handler does, and doubling as the session menu's
  `contextMenuTrigger`, so a right-click on the name opens the menu the ⋮ opens.
  `Controls/InlineRenameBox.cs` now holds **both** of the reference's rename editors,
  because they are two components: the sidebar row keeps its boxed one
  (`c19ba0b81-DU76bj3t.js`) and the titlebar takes the bare one 1.44121.4.0 gave it — no
  fill, no border, no padding, `rounded-[3px]` under a 1.5px accent outline standing 1px
  off, sized to its content between the two ends of its own `size={min(max(len,10),30)}`.
  The **origin gutter** is `Services/SessionOriginIcon.cs`, its `pp`/`mp` ported whole;
  only the local arm is reachable here and the rest is declared. A session still being read
  off disk shows the reference's skeletons instead of the previous session's name — the
  title's `h-[1lh] w-[24ch] rounded-[3px] bg-alpha-2` and an 18px origin ghost, both
  pulsing — which `MainWindow.OpenSessionAsync` arms around the load. The **origin pill**
  (`Controls/SessionOriginPill.cs`, its `bp` over `up`+`hp`) is 20px tall on 5px of padding
  at `text-footnote` in the secondary colour over `alpha-2`, and swaps its three parts on
  one flag the way its `group-data-[pills-compact]/lead` does. The trailing cluster carries
  the agent badge, the rail, the artifact expander (its `Zp`, "Expand artifact" on the
  SidebarClose glyph at 18px) and "Close pane" — which is where the reference puts a tile's
  close, so the split view no longer floats a second pill of its own over the transcript.
  The bar is also the window's drag region: its own background moves the window and
  double-click maximizes, while every child is `draggable-none`.
  `--open=titlebar[:loading|:agent|:panes]` poses it and `--titlebar-selftest` drives it on
  the real controls — the drag region, the rename editor's identity, the right-click menu
  and the pill compacting as the window narrows — exiting 0/2.
  Above the composer sits the **PR bar** (`Views/GitBarView.cs` over
  `Services/GitBarPresentation.cs`, its `bh`@161933 and the host state at ~196172): the PR
  state icon and its `#number` link, the repo chip and the branch chip, then the behind
  count, the ± diff, the CI button and the mode control — View PR · Create PR · Create
  draft PR · Manually create PR · Commit changes, with Merged and Queued reading as a
  tinted label instead of a button and the create modes offered under "More PR options".
  `Services/GitStatusProbe.cs` feeds it from `git` and `gh pr view`. Beside the branch's own
  pull request the bar carries **a row per related and stacked PR**, which is the
  reference's own second mechanism: a **stacked** parent is found by following the PR's
  `baseRefName` while it names a branch that is neither this one nor the repository's base
  (`GitBarPresentation.HasStackParent`, its `lt`; `GitStatusProbe.ReadStack` walks the
  chain), and a **related** one is a pull request this session has already been bound to
  (`Services/SessionPullRequests.cs` over `UiSettings.SessionPullRequests`, which is where
  the reference keeps its own bound-PR list). `GitBarPresentation.ExtraRows` resolves them
  the reference's way — the branch's own number and the dismissed ones dropped, one row per
  number, sorted by its state rank `{open, draft, queued, merged, closed}` and then by
  number descending, capped at five — and each row is drawn without a diff, a CI slot or a
  behind count, its control being View PR or the tinted Merged label (`rg`).
  `--open=gitbar:stacked` poses it. The **context row**
  is the reference's: the folder chip opens the "Working directory" menu (VS Code ·
  Explorer · Copy path · Change directory · Open repository on GitHub · Open in terminal · Copy
  branch name — `Services/WorkingDirectoryMenu.cs`, its `rb`@279401 over `Vm`@153322) with
  the "{cwd} · {repos}" tooltip, and only a session that has not started yet also shows the
  landing's branch picker and Worktree switch, which is where the reference puts them —
  and a pick that would collide with the working tree raises the reference's **dirty-tree
  dialog** first (`Services/BranchSwitch.cs` + `Views/BranchSwitchDialog.cs`, its
  `wouldBranchSwitchConflict` / `getWorkingTreeStatus` / `stashWorkingTree` /
  `commitWipForBranchSwitch` / `discardWorkingTree` in app.asar
  `index.chunk-B28p2L31.js`): a target that reads as an option is refused, a clean tree
  never conflicts, an unmerged entry always does, an unresolvable target counts as one,
  and otherwise the dirty paths are intersected with what `git diff --name-only HEAD
  {target}` would rewrite — parent directories included, which is the reference's own
  `ln`. The dialog is "Uncommitted changes on {currentBranch}" with the branch in the code
  face, "Handle them before switching to {targetBranch}.", the other-session count and the
  changed-file row with its ± pair, over a split button carrying **Stash changes**
  (`git stash push -u -m "epitaxy: pre-switch from {branch}"`), **Commit as WIP**
  (`git add -A` then a `--no-verify` commit of "WIP: epitaxy pre-switch from {branch}")
  and **Discard changes**, which swaps in the second step — "Discard uncommitted
  changes?" over Back and a danger Discard — and runs `git reset --hard` then
  `git clean -fd -- :/`. "No local changes to save" and "nothing to commit" count as
  success, as they do there; anything else is its "Couldn’t update the working tree."
  `--open=branchswitch` poses it. The
  pixel mascot (`Controls/MascotSprite.cs`) rides the same row, and the **composer chin**
  is the reference's order (`Fv`@267k): + · mic · Mode · coordinator | model · effort ·
  context ring, with the send arrow inside the input. The ring is its `eI` — a 12px arc at
  stroke 2 from twelve o'clock, accent under 75%, warning from 75%, danger from 90%
  (`Controls/ContextRingGlyph.cs` over `Services/ContextRing.cs`) — whose tooltip is
  "Context {used} / {max} ({pct}%)". The "+" menu is `Services/PlusMenuModel.cs`: Add files
  or photos (Ctrl+U) · Add folder · Import GitHub issue · Import Linear issue · Slash
  commands · Connectors ▸ · Plugins ▸, with the two issue rows offered only on a session
  that has not started and disabled with the reference's own reasons when they cannot run.
  **Import GitHub issue opens the reference's picker** (`Services/GitHubIssues.cs` +
  `Views/IssuePickerDialog.cs`, its `jA` inside the `IA` shell): "Import issue" over a
  search box its own 300ms debounce drives, a list capped at half the viewport whose rows
  are an open/closed dot, `#number title` and the repository with the issue's labels, and
  its four empty states — "Loading...", "Something went wrong. You can try again.", "No
  issues you’re involved with here. Search to find any issue." and "No issues match your
  search." The query is the reference's — `repo:{slug} is:issue is:open ` plus what was
  typed, or `involves:@me` when nothing was — asked for 50 rows at
  `search/issues`, with the chosen row's body read from `repos/{slug}/issues/{n}`; the
  reference calls those through its own signed-in GitHub client and this build makes the
  same two REST calls through `gh api`, which is the user's own credential. What the pick
  puts on the composer is its `E_`: a `<github_issue>` block of Repository / Issue /
  URL+Author+Labels / body, the body cut at 16,000 characters with its
  "[... truncated; full issue at {url}]" note, "(no description)" for an empty one, the
  whole thing run through its `R_` control-character strip, and `GitHub issue: {url}`
  when the body could not be fetched. Its `N_` guard — a homoglyph-tolerant regex that
  breaks the phrase "github issue" inside the body — is not carried; it is built from a
  spelling-variant engine (`Eb`/`Nb`/`jb`/`Tb`/`Fb`/`Lb`) whose tables did not extract,
  and it is defence in depth over a block the tag already delimits.
  `--open=issuepicker` poses it. The effort chip opens
  the reference's effort selector (`Controls/EffortSliderPopup.cs`, Ctrl+Shift+E): a 220px
  popover with the "Effort" header morphing the current level, a hover "?" help card, a
  Faster/Smarter row and a stepped slider (drag/click commits and stays open, digits 1-9
  commit and close, Esc closes). The ladder is the reference CLI's full six rungs —
  Low/Medium/High/Extra high/Max/Ultracode (`Services/EffortLevels.cs`; the wire spellings
  xhigh/extra alias onto their rungs); there is no "off" rung, and a legacy stored "Off"
  resolves to the recommended High everywhere the setting is read. Ultracode is the
  reference's session-scoped mode and is never persisted: picking it flips
  `ChatViewModel.UltracodeMode` (the toolless Chat surface falls back to Extra high), the
  turn runs at xhigh on the wire, and the harness flips through system-reminders —
  `SystemReminders.UltracodeEnter/StillOn/Exit`, the reference's ultra_effort_enter/exit
  wording with its Workflow-tool pointers adapted to Agent. The reference keywords ride
  outgoing messages the same way: "ultrathink" attaches its verbatim deeper-reasoning
  reminder and "ultracode" the turn-scoped orchestration opt-in — neither touches the
  payload (the old ultrathink High-effort bump is gone, matching CLI 2.x). The mode and model menus open on
  Ctrl+Shift+M / Ctrl+Shift+I, number their first nine rows for digit selection, and tag the
  default model "· Recommended". **No model ships built in** — a key is entered and its ids
  added on the provider card — so the model menu groups what the user added by provider
  (`Services/ModelMenuPresentation.cs`, unit tested: first-appearance order, one group per
  provider however the entries were interleaved, the registered provider's display name on
  the rule above it and its raw id when the build no longer carries it) and offers
  "No models yet — add one in Settings › Providers" when there are none. Group headers take
  no digit. The composer also carries the reference's coordinator toggle
  (`Services/CoordinatorMode.cs`: per-session, persisted; the turn keeps only
  todo_write/skill/Agent/TaskOutput/TaskStop/SendMessage while workers keep the full
  registry, the system prompt gains the worker-tools block, and an idle coordinator gets the
  minute check-in listing running workers) and Ctrl+Alt+Enter, which forks the session and
  sends the typed prompt in the fork (`Services/SessionActions.cs`).
- **Turn status line** (`Controls/TurnStatusLine.cs` + `ViewModels/TurnStatus.cs`): the live
  progress row at the transcript tail — spinner glyph · elapsed timer (after 2s) · token
  counter (`9.5k tokens`, 400ms count-up) · phase label, ported from the reference app's
  component: "Waiting for Claude…"/"Running tools…"/the elapsed-bucketed thinking ladder
  (15/30/45/60s), "Thought for {n}s", "Stopping…", "Compacting session…", 650ms label dwell,
  180ms morph, and the 2s label shimmer. Tokens step per completed API call (engine reports
  usage at call end), smoothed by the count-up. Like the reference, nothing persists once a
  turn completes (the old per-turn footer is gone); a stopped turn writes the CLI's red
  "[Request interrupted by user]" and calls still running settle as interrupted, not failed.
- **Transcript tool rows** (`Services/ToolGroupSummary.cs` + `Services/ToolInputRows.cs` +
  `Services/ToolRowPresentation.cs` + `ViewModels/TranscriptItems.cs`, ported from the
  reference bundle's renderer — desktop 1.44121.2.0's `cd5a31703-DPCARDPv.js` for the row
  and its body, `c78751380-0qjdp_tY.js` for the wording): the group header's sentence is
  the reference's own aggregator (`vE` over `hE`/`fE`), which is **not** the chat surface's
  vocabulary — it says "searched code" and "found files" with no count, "updated todos",
  "browsed the web", and folds every kind it does not classify (a skill, a browser action,
  a preview, tool search) into "used {n} tools". A clause counts **distinct files**, not
  calls, so re-reading one file is still "read a file"; a clause every call of which failed
  is **coloured** rather than counted, and only a partial failure gets "{meta} ({n} failed)";
  a denied call reports no outcome at all, since the user refusing a call is not the call
  failing. When every file-touching call names one file the clauses collapse onto its
  basename with their verbs conjoined ("Read and edited Base.xaml"), memory operations lift
  out in front of the rest ("Recalled a memory, saved a memory, ran a command", the
  reference's `Wo`/`_E` over the session's memory folder), and only the first clause is
  capitalised. `Controls/ToolSummaryInlines.cs` draws them one Run at a time, because a
  bound string cannot colour one clause. Consecutive
  reads/edits of one file coalesce into a single row; a running group shows the live call's
  own label with a shimmer. **A run that coalesces to one row has no header and no card**
  (the reference's `uQ`): a sentence reading "Read a file" over a card holding one row that
  reads "Read Base.xaml" says it twice, so the row is drawn on its own. The run still
  refuses to go bare while it carries memory operations, whose clauses live in the header
  it would be giving up. Such a row is passed the reference's own `inGroup`
  (`tools.length > 1`, so a *coalesced* run is still a group) and no `inCard`, and those two
  decide the rest: a lone call's images sit outside the disclosure, always visible, and the
  bodies take their standalone shapes — the shell card grows the header bar naming the shell
  it ran in and holds its own output, while a diff or a file becomes one outlined card
  carrying the path as its header rather than a plain path line over a second rounded block.
  A tool the reference files as **standalone** opens a run of its own (`lQ`); of its nine
  such predicates only the MCP-app one exists here, so a `show_widget` call closes the run
  above it — without which the next call rejoined the group sitting above the widget and the
  transcript read out of order. Its other bucket is **spawnTask**, and it is carried: a run
  of `mcp__ccd_session__spawn_task` calls never mixes with calls of another kind, because it
  is counted by what became of the chips rather than by the tool that queued them (its `gE`)
  — "Started a session, suggested 2 tasks", where only the suggested clause takes a failure
  count, since a chip that started a session did not fail. Which chips started is the
  reference's `tY`, and it is asked of the same kind of store: starting a chip records
  `spawnedFrom` — the suggesting session and the task id — **on the session that was
  spawned**, keyed by that session's own id in `Services/SessionGroupsStore.cs`, which is
  where the reference keeps it and where the engine's own Session model is left alone. Two
  things follow from putting it there rather than on the chip: the answer outlives the
  twenty-deep chip queue evicting the chip, and it outlives the transcript being reopened;
  and deleting the spawned session takes the record with it in the store's own `Prune`, so
  the row reads as a suggestion again, which is what the reference's walk of the live session
  list does. The task id is read back out of each call's own result with the reference's own
  non-global `task_id: task_[0-9a-f]{8}` regex. A row whose chip started wears the
  reference's `verbOverride` — "Started session" in place of the tool's verb, behind a done
  label and in front of the plain one. **A row is as wide as its own sentence** — the reference's row is
  `flex self-start`, so the disclosure caret follows the text rather than sitting at the far
  edge of the card, and there is **no spinner**: its settled slot renders nothing at all while
  a call runs (`yS` returns null) and the running state is the label's shimmer alone. After the
  meta the row carries the reference's trailing words — **Stopped**, **Denied**, **Failed** —
  and a row waiting on the permission card says **Needs approval** in place of its meta. A meta
  that names a file is a link that opens it, and it is drawn in the primary colour rather than
  in the code face, which the reference sets nowhere on that slot. Rows carry the reference verb set (Read/Reading/Failed to read +
  basename, Created vs Updated from the write result, Searched {pattern}, Ran skill /name,
  computer actions, "Used {server}: {tool}" for MCP), a ported verb-morphology engine
  (irregular past/gerund maps, CVC doubling, re-/un- prefixes, and/then continuations with a
  clause guard) that conjugates shell and Agent descriptions across running/done/"Failed
  to {infinitive}" — both tools gained an optional `description` arg for it — and git-aware
  shell verbs recovered from command+output (Committed <sha>, Amended commit, Pushed <branch>,
  Merged/Rebased onto, Created/Merged/Edited PR #n with the PR linked). **Expanded bodies are
  the reference's `HC`, in its order and exclusive of one another**: the checklist, the shell
  card, the diff, the file, and then the two generic ones — an errored call reports its output
  in danger and lists its arguments **under** it, an ordinary one lists its arguments and then
  its output. That last branch is what a Read answering with an image falls into, which is why
  such a row reads "file_path:" over the picture rather than showing nothing. The argument list
  is the reference's `KC`/`qC` (`Services/ToolInputRows.cs`, drawn by
  `Controls/ToolInputRowInlines.cs`): one "key: value" line each, the key in the code face at
  70% opacity, `file_path`/`notebook_path` as a chip that opens the file, the eight written keys
  (`command`, `cmd`, `script`, `shell`, `code`, `pattern`, `regex`, `glob`) in the code face, any
  other string as the sentence it is, and a non-string as its JSON — never one indented JSON
  blob. The generic pair scrolls inside the reference's own 400px cap, and a hover-revealed
  **Copy** hands over the arguments and the output one blank line apart (its `yC(gC(e), output)`,
  the tick held for 1200ms). The shell card prints the shell's own prompt glyph, `>` for
  PowerShell and `$` for Bash. Tool result images stack one under the other at the reference's
  360px ceiling, each opening `Views/ImageLightboxWindow.cs` (`ToolExecutionCompleted`
  additively carries `result.Images`). preview_start rows grow the reference's inline
  dev-server card — URL link fronting the Browser panel, View logs, Stop server, and a
  best-effort engine thumbnail — and a Agent row shows the model it ran on (its "took
  {elapsed}" belongs to the background task chip, not to this row; clicking the row opens that
  agent's transcript in the pane, below). Thinking on
  **this** surface renders the reference's Code-transcript cell (its `cF`/`dF`): always-visible
  italic pre-wrapped text, shown only in the thinking view, with a copy button revealed on hover
  or focus whose accessible name is "Copy as quote" → "Copied" and whose payload is the
  "> **Thinking** (~N tok)" blockquote (token count rounded half-up, as JavaScript's
  `Math.round` does — C#'s banker's rounding disagrees on every exact .5). The Chat surface's
  thinking is a different reference component; see the Chat bullet.
  Three more reference states ride additive Core channels: a call sits in its
  group as **awaiting_approval** while the permission card is up (PermissionRequest carries
  the call id; running wording, dimmed, no spinner; approval flips the row in place, a
  mid-prompt stop settles it interrupted — subagent-raised prompts deliberately get no
  top-level row); a Agent row carries the **subagent's own nested transcript**
  (SubagentServices.EventReporter feeds inner events; tool groups reuse the full row
  machinery, interim/final text renders as SubagentTextItem lines behind a left rule) —
  which the Background tasks pane shows, since clicking such a row opens it there rather
  than expanding in place, and only a programmatic expansion (the Verbose view, the poses)
  still renders it inline; and a
  shell row started with run_in_background stays a **live background row**
  (BackgroundTaskManager.TaskExited) — running look past turn end, settled from the task's
  real exit: done on 0, failed with "[exited with code N]", interrupted when killed.
- **Message action bar**: hovering a user bubble fades in a right-aligned row under it —
  relative timestamp ("11 minutes ago", ticked live) · copy (swaps to a tick) · rewind
  (confirm, then `FileCheckpointStore.RewindAsync` restores the files that turn changed and
  the history is cut back to the prompt) · branch (copies the history up to the prompt into a
  new session, leaving the original intact). Rewind and branch both hand the prompt back to
  the composer, and both carry the reference's own labels — "Rewind to here" and "Fork from
  here", which is what its own buttons and transcript menu call them. Commands live on
  `ChatSurface`; the icons are lucide geometry on a 24×24 grid.
- **The command palette** (`Services/CommandPalette.cs` + `Views/CommandPaletteView.cs`,
  Ctrl+K): rebuilt against the reference's own `CommandPaletteBody`
  (ion-dist `c2771e1f6-Czf-iSjS.js`) and the registry it reads (`gP` in
  `shared-6-D8hZtQZb.js`). It opens on a **Quick actions** group over the recent
  sessions; **Tab** enters the grouped command list and **Backspace on an empty query**
  leaves it again; the group order is the reference's — the untitled `other` group, then
  Navigation · Chat · Project · Task · Session · Settings · General — and a typed query
  puts the matched **Actions** group ahead of the quick action rather than after it. Rows
  are one line with the chord as **outlined keycaps on the right**, swapped for a return
  arrow on the row the keyboard is on, and the footer (Select ↑↓ · Actions Tab · Open menu
  Ctrl+K) shows only at rest — a typed query or an entered mode hides it, as the reference's
  `ve` does. Filtering is the reference's own search helper (`c2b74c150-BU9Zxu9s.js`): Fuse
  over `descriptionLower` .7 and `hintsLower` .3 at **threshold .2 with `ignoreLocation`**,
  sorted with the rows whose description begins inside the query first and then by score,
  and a query over 50 characters matches nothing. The rows are the registry's, filtered the
  way its own `isAvailable` filter filters them — a row nothing here can execute is dropped
  rather than listed dead — plus one "Settings → {group} → {section}" row per settings
  section and one "Customize → {section}" row. `--open=palette` poses it.
- **The shortcuts sheet** (`Services/ShortcutSheet.cs` + `Views/ShortcutsView.cs`, Ctrl+/):
  the reference's Code-surface sheet (`GP` in `shared-17-DsNaDSP_.js`) — General · Panes ·
  Composer, one row per binding with its caps on the right and alternatives joined by "or".
  Chords are stored in the reference's own spelling (`cmd+shift+m`) and rendered through a
  port of its keycap splitter, which sorts modifiers ctrl → alt → shift, folds `cmd` onto
  Ctrl on this platform and draws shift as ⇧. The sheet carries **40 of its 41 rows** —
  1.44121.2.0 grew that sheet from 36, splitting its sidebar-tab row into "Next sidebar
  tab" / "Previous sidebar tab" and adding "Search or start a session", "Toggle sidebar",
  "Settings" and the changed-file row, which 1.46388 rewords to "Toggle file list in
  changes". The one it does not carry is "Toggle fast mode", which the reference itself
  shows only when its desktop bridge exposes `setFastMode`. The chords the sheet
  advertises are owned by a port of the
  reference's pane keymap (`Services/PaneKeymap.cs`, its `LI`), which is what moves the
  prompt jumps to **Alt+↑/↓** and gives Ctrl+B (Background tasks), Shift+Tab (cycle the
  permission chip), Ctrl+T (new Browser tab), Ctrl+Shift+Y (fold the changed-file list
  away, which is what its `toggleDiffFileList` does) and Ctrl+Shift+S (the Browser element
  picker) something to do. A sheet is a promise, so every chord it advertises is answered: the seven
  the window had not bound — Ctrl+Alt+←/→ (the reference's previous/next surface, whose
  sheet rows it split in two), Ctrl+Alt+L (Copy session link), Ctrl+U (Upload file),
  Ctrl+Shift+Backspace (the second archive chord), Esc (stop the response, once no card is
  waiting on an answer), Ctrl+. (Toggle sidebar) and Ctrl+Shift+N (New session with current
  settings) — are wired, along with the registry's hidden Ctrl+D (dictation) and
  Ctrl+Shift+, (settings). `--open=shortcuts` poses it.
- **Toasts** (`Services/Toasts.cs` + `Controls/ToastHost.cs`): the reference's toast manager
  (its `ErrorsProvider` in `shared-1-BK5wDqY-.js`, the `Toast` component in
  `shared-16-DFDNRrwQ.js` and the provider the app mounts in `index-DEczO-db.js`). Three
  variants — neutral, warning, danger, which its five adders map onto — a **6500ms default
  with a 6000ms floor** every requested timeout is raised to, a card carrying an action that
  **never times out**, and dedupe by text that counts the live card up to "{title} (×N)"
  rather than stacking a second copy. The viewport is bottom-right, 16px in and 360px wide,
  and the cards behind the front one are lifted 14px and scaled 4% each, three deep. Every
  toast the reference raises for a session action — fork, archive, unarchive, delete, the
  pull request, the clipboard copies, the worktree progress, the connector states — is
  routed through it in place of the message boxes this port used to show, and
  `ToastQueue.Current` is how the panels with no services of their own reach it.
  `--open=toast` poses one of each.
- **Error cards** (`Services/ErrorCards.cs`): a failed turn leaves the reference's own
  API-error card rather than a red notice. The classifier is its `kg` (`shared-13`'s `mg`
  regex table in order, its `yg` error-type map, then the HTTP status ladder) and the copy
  is its `qx`/`$x`/`Bx`/`Wx` tables for all 21 categories, including the second hint each
  rewindable category keeps for a conversation with nothing to rewind to. The card is its
  `tR`: warning icon and headline, hint, "Request ID: {id}", collapsible details with their
  own Copy error details → Copied (1500ms), and the actions the category says would help —
  Compact only for a full context window, Rewind only where rewinding helps, Try again only
  where retrying does, and "Switch model and retry", which is the reference's own answer to
  an overloaded model. The session-error card's titles and bodies are its `Mg`/`Cg` for the
  reasons this app can reach; the rest are declared. `--open=errorcard[:kind]` poses it.
- **Toasts and dialogs** (`Services/SessionDialogs.cs`, `Views/ConfirmDialog.xaml`,
  `Views/GroupNameDialog.cs`): the confirm dialog grew the reference's remaining pieces — a
  `confirmVariant` that is primary while a check is still running, a scrolling `<pre>` for
  the paths a delete would discard, and a third answer for the one dialog that has three.
  Removing a session whose folder is a **linked worktree** asks git what it still holds
  (`Services/WorktreeChanges.cs`) and lists up to ten paths with "… and {n} more" under
  "Delete anyway"/"Archive anyway"; deleting a group says how many sessions it releases;
  naming a group is the reference's own Cancel/Save dialog with its "Group name"
  placeholder. **Workspace trust** (`Services/WorkspaceTrust.cs`) is asked once per folder
  before a Code session works in it, with the path in code font, the settings files that
  would allow execution listed under "Execution allowed by:", Cancel focused, and a refusal
  leaving the reference's "Workspace trust needed" card; an imported CLI session is
  confirmed once before it is continued. Both answers live in `ui-settings`.
  A session whose stored transcript is gone gets the reference's **"Session not found on
  disk"** card instead of a dead click (`Services/SessionNotFound.cs`, its `wN` over the
  `transcriptUnavailable` flag): its title, "Send a message to start fresh in this
  directory." and its three actions — Import CLI sessions (offered only where there are
  CLI transcripts to import from, as the reference offers it only where its bridge exposes
  the importer), Archive and Delete, whose accessible names are its own "Archive session"
  and "Delete session". The reference raises it when a local session's own transcript file
  has gone while its message buffer is empty; this port's transcript for a session *is*
  its stored session file, so `MainWindow.OpenSessionAsync` raises it when the row is
  listed and the store cannot load it, binding a placeholder session on the folder the row
  named so the body's promise is literally what happens next. `--open=sessionnotfound`
  poses it.
- **Session deep links** (`Services/DeepLinks.cs`): "Copy session link" copies a link that
  works. The app claims `jarvis-code:` for the current user — the same HKCU mechanism the
  Explorer entry already uses — and `jarvis-code://session/{id}` reaches the running
  instance through the single-instance pipe. A link carrying a path traversal is refused
  before it is parsed.
- **Session sidebar** (Code surface, re-measured against desktop 1.44121.2.0):
  **there is no standing "Recents" row.** The reference hangs the filter icon and the
  header menu off the *trailing slot of the first section's own header* (its
  `trailing: index === 0 ? headerTrailing : undefined`, `shared-19-BctYnjt1.js`), so the
  chrome rides "Ungrouped", or the first folder, or the first State bucket; "Recents" is a
  section **label** only while the list is ungrouped, which is the one case its `yO`
  renders, and a standing header appears only when the list is empty. The filter popover is
  its own (`Services/SidebarFilterModel.cs`, the popover at `shared-19`@243500): Status ▸
  (Active · Archived · All), **Last activity** ▸ (1d · 3d · 7d · 30d · All) — offered
  on exactly the condition the reference offers it, while the grouping is State, over its
  own list `[0,1,3,7,30]` (`shared-2-dTgTvujb.js`@94866) at its default of 7 — a rule,
  **Group by** ▸ (Date · Folder · State · Custom groups ·
  None) and **Sort by** ▸ (Name · Date created · Last activity), a rule, the two checkbox
  rows and Clear filters — each submenu trigger carrying the chosen value in accent when it
  is not the default. The defaults are the reference's own: `groupByByMode[mode] ??
  xg(mode, isDesktop)` answers **Folder** for the desktop code sidebar, and
  `sortByByMode[mode] ?? "recency"` answers **Last activity**. `Services/SidebarPresentation.cs`
  applies them — pinned rows lead under their own header whatever the grouping, archived
  ones close the list and only while the status filter admits them, and "Date" uses the
  reference's three headings (Today · Yesterday · Older).
  **Group by State is live state, not pull-request state**
  (`Services/SessionRowStates.cs`): its `dk` over the row ladder, in its own bucket order
  `iu` with its own headings `au` — **Needs input · Ready for review · Working ·
  Completed** — where an archived row is Completed, an error or a wait is Needs input, a
  running turn is Working, and only a pull request its `uk` calls reviewable (open, draft,
  approved, changes-requested, conflicting — never merged or closed) lifts a row into
  Ready for review.
  **A bucket shows twenty rows** and folds the rest behind the reference's own
  "Show {count} more" (its code sidebar runs at `cap = Infinity, truncateAt = 20`,
  `shared-19`@231097), which reveals another twenty at a time; the row is muted, sits in
  the icon column and reads out as "Show {n} more in {bucket}". **A side session nests
  under the session it came from**: the `spawnedFrom` a spawn_task chip records on the
  spawned session (`SessionGroupsStore.SpawnParentOf`, the reference's own home for it) is
  resolved to the *root* of
  its chain by a port of its `Ro`, ordered by its `ED` and grouped into runs by its `DD`,
  and a run is drawn under the accordion guide — a one-pixel hairline down the leading
  slot's centre — behind a Collapse/Expand caret on the parent.
  **The row has no leading icon.** The reference passes `icon: null` for a code session
  and puts the status mark on the **trailing** edge as a `rightDecoration` that steps
  aside for the ⋮ on hover (`Zz`/`Xz` in `shared-18-6A6evEfS.js`@262430), drawn only while
  the state is neither idle nor a bare pull request. What that state *is* is its `Mp`
  ported rung for rung (`shared-11-CL4cxK09.js`@23600) over its precedence table
  `{error, awaiting, running, ready, pr, idle}` — an archived row answers only
  ready-or-idle, an unread the user marked by hand outranks a pull request, and the mark's
  accessible name is its `XE` ("Awaiting input", "Awaiting answer", "Running", "Unread
  response", "Idle"). **A title fades out rather than ellipsing**: its `dframe-fade-label`
  mask, 24px at rest and 44px→20px once the row is decorated or its controls are showing,
  with the tooltip raised only when the text really overflows (its `zE`, measured against
  the width less the 44px the controls take). The geometry is the stylesheet's own
  (`Services/SidebarMetrics.cs`, `shared-styles-DHnDvusg.css`): this build is the desktop
  variant at the comfortable density, so row 30px · font 14 · gap 8 · row padding 2 ·
  control 24 · leading slot 28 · pill radius 8 · group padding-top 14 · group font 13,
  with a row whose menu is open keeping the fill and the controls it had on hover,
  with the label inset, the nested-row indent and the guide offset derived from them as
  the reference derives them. Holding **Ctrl+Shift** raises its jump-hint keycaps — digits
  1–9 on the first nine rows after its own 200ms, in the order the list was actually
  drawn, which is the order Ctrl+1…9 now follows.
  A row carries the
  session's colour as a leading bar drawn without moving the title, and
  a PR badge; its ⋮ menu is the reference's row for row
  (`Services/SessionMenuModel.cs`, its `Z` in `ce4f4374c`@2188 — Open PR · Go to routine ·
  Open in ▸ · Move up · Move down · Pin/Unpin · Mark as read/unread or Mark as completed ·
  Rename · Color ▸ · Transcript view ▸ · **Output style ▸** · Export · Copy link · Fork ·
  Move to group ▸ · Archive/Unarchive · Delete — the Output style row sits where
  1.44121.2.0 puts it, between Transcript view and Export, and writes
  `UiSettings.SessionOutputStyles` so one session may run at a style the account default
  does not), rendered by `Views/Panels/SessionMenuRenderer.cs` with the
  reference's single-letter accelerators live while it is open and digits inside a submenu.
  Rename edits the row in place; Copy link copies the session deep link
  (`Services/DeepLinks.cs`); Go to routine opens the Scheduled page on the detail of the
  routine whose run started the session — `RoutineRunner` stamps `Session.RoutineId`, the
  summary carries it to the sidebar row and the header, and both menus gate the row on it;
  Color is the reference's eight (`Services/SessionColors.cs`,
  its `Bo`/`jo` table, hexes included) under a Default row. Ctrl+click opens a row in its
  own window and so does dragging it out of the sidebar, a **double-click renames it in
  place** (which is what the reference's row does when it is given an `onRename`),
  Shift+click selects a run, and the
  header ⋮ then offers the reference's bulk rows — Mark N as unread · **Set model for N ▸**
  · Move N to group ▸ · Archive N · Delete N. With nothing selected that menu carries its
  **"Bulk actions for older sessions"** submenu instead (`Services/SessionBulkActions.cs`):
  Archive all ({n}) and Delete all ({n}) over every unpinned session more than a week old,
  each behind its own question ("Archive older sessions?" / "Delete older sessions?") and
  each reporting what it moved. The same rows build the **session header's** ⋮ menu through
  `SessionMenuModel.ForHeader`, which passes the reference's `canPin:false` and
  `hideMoveToGroup:true` and leads with the pane checkbox rows. Groups drag-drop, with
  a "Drag or move sessions here" zone, and a custom group's section is reordered
  the reference's two ways — dragged by its header, and **Move up / Move down** from
  that header's menu (its `onCommitDrag` and `onMoveSection`); while
  nothing is pinned, a drag reveals its **pin target** under a Pinned header, labelled by
  its three-step ladder (Drag to pin → Drop here → Let go), and the first pin from a menu
  raises its one-time tip. The reference session chords are live: Ctrl+W close,
  Ctrl+Shift+]/[ and Ctrl+Tab/Ctrl+Shift+Tab cycle, Ctrl+1…9 jump in sidebar order,
  Ctrl+Alt+P/R/U/G/O/A pin/rename/unread/open-PR/fork/archive, Alt+click opens a row in the
  split grid, and /resume focuses the sidebar search. Layout, colours and the home view's
  dismissals live in the App's own `session-groups.json`
  (`Services/SessionGroupsStore.cs`); the engine's Session model is untouched. The Chat
  surface keeps date buckets (Today/Yesterday/…).
  `--open=sidebar[:showmore|:hints|:state]` poses the list, and `--sidebar-selftest`
  drives it on the real controls — the popovers cannot be captured by a render-to-bitmap,
  so what a screenshot cannot prove (one filter icon and one header menu in the whole
  list, the Last-activity submenu appearing on exactly the State grouping, the bulk-older
  submenu, Output style between Transcript view and Export, the collapse caret, the
  show-more row revealing another twenty, and the double-click rename) is asserted there
  and exits 0/2.
  Deliberately absent, each declared in `Deltas/reference-surface-deltas.tsv`: the Continue
  in ▸ Cloud destination and Move to project, Edit environment, Release runner and Share
  (all cloud), the Open in ▸ Desktop app row, the header menu's Debug submenu (which the
  reference gates on the empty string, so its own build never draws it either), the origin
  gutter icon that names where a session runs, the filter's Environment and Reset to
  defaults sections, the Linear issue picker, the home view's review and working kinds,
  which the reference reads off a task board this build has no source for, the sidebar's
  own **Projects** section (a claude.ai entity with no counterpart here — which is why
  this port's earlier "Recent folders" section is gone rather than kept: the reference's
  Code sidebar has no folder list, and the home view's folder rows are where that belongs),
  the Output style submenu's "New style…" row (there is no style editor to open), the
  reference's row-holding stabiliser `prevVisibleKeys`, the two empty-Pinned lines (this
  build draws Pinned only when it has rows), the `{truncated}` clause its bulk-archive
  bodies close with (unreachable where every session is on disk), and the status mark's
  own glyph, whose component did not resolve in 1.44121.2.0 — its slot, its gate and its
  accessible names are the reference's and only the shape is this build's.
- **A turn belongs to its session, not to the surface**: `ViewModels/SessionViewModels.cs`
  keeps one `ChatViewModel` per session and `ChatSurface.Bind` rebinds the view, so selecting
  another session leaves the running turn streaming into its own transcript and it is still
  live on the way back. Idle view models are evicted on every switch; a deleted session's
  view model is `Discard()`ed so the cancelled turn's tail cannot save the file back.
- **Customize** (`Views/Customize/`): the sidebar's Customize entry swaps in the reference's own
  four sections — **Skills** (`c5e558aae`), **Connectors** (`c40525e86`), **Plugins**
  (`c76f00e40`) and **Memory** (`c71860c77-BRN4k43v`) — over the installed personal plugins and
  an Organization plugins folder, with a hub above them because this nav can be arrived at with
  nothing selected (the reference's own lands on Skills). Each page is the reference's tabbed
  list: **Yours over Browse**, one search box, the facet and sort pickers, the **Needs
  attention** and **Created by you** sections with their counts, and a row that opens a detail
  page. What each page shows is decided by a pure service the tests pin
  (`Services/CustomizeSkills.cs`, `Services/CustomizePlugins.cs`,
  `Services/CustomizeConnectors.cs`, `Services/CustomizeDirectory.cs`,
  `Services/OrganizationPlugins.cs`, `Services/CustomizeMemory.cs`); the pages themselves are
  partials of one `CustomizeSurface` over the shared builders in `Views/Customize/CustomizeUi.cs`.
  **Skills**: the list's credit, section split, search and four sort orders are the reference's
  `Wi`/`Me`/`ls`/`qi`/`Gi` — Plugin name is offered only while plugin rows are listed and Most
  used by me only once something has been used, and a sort whose option is not offered falls
  back to Last edited. Usage comes from the `skillUsage` store that already existed, counted
  inside the reference's 90-day window. **The card is on/off, not the tri-state**: the reference
  exposes only Enable skill / Disable, so the switch writes `skillOverrides` "off" and the
  user-invocable-only state stays a frontmatter mechanism with no control of its own. The Add
  skill menu is Upload skill / Create a skill / Create with Jarvis, plus this build's own Import
  from Claude Code. **The editor** (`Views/Customize/SkillEditorDialog.cs`) is the reference's
  `mi`: the name sanitized per keystroke by its `oi` (whitespace and connector punctuation to
  hyphens, marks and the two zero-width joiners kept, lower-cased, cut at 64) and frozen once
  saved, the reserved words `anthropic` and `claude` refused by name, a description with the
  counter from 900 characters, the 1024 limit and the XML-tag refusal — which is a bracket pair
  with something between them, so "a < b and c > d" is refused there too — and a footer reading
  Draft / Unsaved changes / `v{n} · {date}` / `Saved {date}` / Current version. **Versions are
  this app's own storage**: the reference keeps them on its backend and nothing in a Claude Code
  skill folder holds one, so `SkillVersions` snapshots each save under the profile's
  `skill-versions/{skill}/v{n}/SKILL.md`, out of every skill folder, and the detail page restores
  one. Upload takes .zip/.skill/.md through the reference's review step and its refusals
  verbatim; a taken name is a conflict until Replace is asked for, and the replaced file is
  snapshotted first. **Plugins**: rows carry the Show and Sort facets, a Disabled badge and a
  scope suffix, and the switch writes `UiSettings.DisabledPlugins` keyed `{scope}:{name}` — a
  disabled plugin stays on disk and contributes nothing to a turn (`Plugins.LoadAll` grew the
  include predicate and the scope stamp). The detail page is the reference's: Contents (Skills /
  Agents / Commands / Hooks / MCP servers / Monitors), a meta strip of Source / Version /
  Scope / Author / Installation / Last updated, Enable plugin · Disable plugin and Remove
  behind its confirm.
  **Monitors are real** (`Services/PluginMonitors.cs`): the reference's "background watch
  scripts the host arms as persistent Monitor tasks (unsandboxed, same trust tier as
  hooks) so plugins need not instruct the model to arm them". The schema is its own zod
  declaration (CLI 2.1.257 at ~181,975,000) — `name`, `command`, `description` and a
  `when` that is `always` or `on-skill-invoke:{skill}` — declared in `plugin.json`'s
  `monitors` field as the array itself or a path to it, falling back to
  `monitors/monitors.json` at the plugin root; a row missing a name or a command is
  dropped and a name may appear only once per plugin. A monitor's command is run in the
  session's own folder with `${CLAUDE_PLUGIN_ROOT}`, `${CLAUDE_PLUGIN_DATA}`,
  `${CLAUDE_PROJECT_DIR}` and any `${ENV_VAR}` substituted, and **each stdout line reaches
  the model as a task notification**, which is what its doc promises. `always` monitors arm
  when a Code session opens and an `on-skill-invoke:` one arms the first time that skill is
  dispatched (`SkillCatalog.SkillInvoked`); the runner dedupes by the monitor's name, as
  the reference does, so a reload or a repeat invoke cannot spawn a second process.
  The Contents rows carry its two fields, "Runs" and the description, over the shell
  command or its "No command is declared for this monitor." `--open=monitors` poses them.
  **Three install scopes**, the reference's own with this app's folders: the profile's directory,
  `{cwd}/.jarvis` shared through git, and `{cwd}/.jarvis.local` gitignored — the shape its
  `~/.claude`, `.claude` and `.claude.local` name. Marketplaces keep an `installed.json` beside
  each root so Check for updates and the remove confirm know where a plugin came from, and the
  refresh answers in the reference's sentences with its four sync-failure reasons.
  **Connectors** is the reference's Connector / Type / Status table over `McpConfig` and
  `McpManager`: a stdio server is Desktop, a remote one Web, a plugin's one Plugin, and the
  status comes from what the manager last did — `McpManager.LastFailures` is the additive Core
  channel that tells a 401 from every other failure, so a row reads Needs authentication rather
  than Failed to connect. **Add custom connector is the reference's two steps**: name and
  https-only URL first (its transport picked by a path ending in `/sse`, with the deprecation
  note that case raises), then the trust warning with OAuth Client ID and Secret behind Advanced
  — `McpServerConfig` gained both as optional fields, and a configured client id replaces the
  dynamic registration a server that issued one would refuse. **Signing in happens in the app**
  (`Core/Mcp/McpOAuthFlow.cs`, `Views/Customize/ConnectorSignInDialog.cs`): the one-shot
  `McpOAuth.AuthorizeInteractivelyAsync` the CLI uses is split so a UI can drive it, and when the
  browser does not come back the reference's paste-the-callback-URL fallback takes over.
  **The Directory** is the Browse tab and the cross-section search, with the reference's Category
  / Status / Source facets, its three sort orders, its five-per-section preview and "See all
  {n}", and its "Contains skill: {name}" / "Matched on …" metadata lines. **Memory** lists this
  app's per-project memory folder, each row opening on the text under its frontmatter, over the
  reference's "Use memory in sessions" switch (`UiSettings.MemoryEnabled`). **Organization
  plugins** scans a folder the user points at and edits the reference's own tool policy —
  `mcpServers.{server}.toolPolicy.{tool}` with its four values and its coercion of anything else
  to `blocked` — which `ToolPolicies` turns into rules the gate already speaks: `allow` and
  `blocked` are ordinary rule lines on `mcp__{server}__{tool}`, `ask-session` is the card's own
  standing approval, and `ask` becomes `UiPermissionGate.AlwaysAskTools`, a set that refuses both
  the session grant and the button that would remember one. `--open=customize:{skills|plugins|
  connectors|memory|directory|orgplugins}` poses each page. Deliberate deltas are declared in
  `Deltas/reference-surface-deltas.tsv`: the claude.ai catalog behind the reference's Directory
  (so Most popular has no count to read and falls back to installed-first), View on claude.com,
  Share, the content scan that decides Needs attention there, Monitors, and the
  device-management mount this folder replaces.
- **The command menu** (`Services/SlashMenu.cs` + `Services/FuzzySearch.cs`, rendered in
  `Views/ChatSurface.xaml[.cs]`): the popup a typed `/` raises, ported from the desktop's
  `CCDSlashCommandMenu` (ion-dist `shared-10-3-tqq7pk.js` for the engine, `c5cd1b4dc` for
  the ccd item builder, `ccc334009` for the rows, `c17e03edc` for the card).
  **A row is one line** — `label (matched-alias) …… subtitle` — carrying the name **without
  the slash the user already typed**, its first case-insensitive match of the query in
  semibold, and the alias that matched in parentheses; **the description is not in the row**
  but in a floating card to the right of the menu, dark in every theme as the reference's
  own `always-*` tokens are, clamped at ten lines and closing the `via {repo}` /
  `{pluginName} plugin` line beneath it. The menu carries the reference's geometry at its
  comfortable density (`--cds-h-control` 32, `px-2.5`, `gap-xs` 8, radius 8, `min-w-60` /
  `max-w-lg` / `max-h-96` — 240 / 512 / 384) and hangs off **the typed slash** rather than
  the whole box, top-start, 10 above and 16 left, capped by the room above the caret and
  never flipped below it (the reference passes `disableFlip`). A `Type to filter` hint
  rides just past the caret while the slash carries no query.
  **Filtering is Fuse.js, ported constant for constant** rather than approximated, because
  the *order* is what the user reads and two of the sort's tiers read Fuse's own numbers:
  `FuzzySearch.cs` is the Bitap the desktop ships (`vendor-utils-DWaswhiK.js`) with its
  three JavaScript details reproduced — `Math.round` is half-up, the zero-score guard
  multiplies by `Number.EPSILON` (2^-52, nothing like .NET's `double.Epsilon`), and the
  32-bit bit arrays read 0 past their end. The weights are deliberately **not** normalized:
  Fuse's KeyStore does divide them by their sum, but an object-list search scores against
  the *index's* keys, which are the raw configs — normalizing leaves every order intact and
  every score wrong by the weight total. Over that sit the reference's own rules: threshold
  .3 / location 0 / distance 100 with `label`3 `qualified`3 `aliases`3 `parts`2 `source`2
  `description`.5, a rejection of results that matched only the description, a preference
  for the subset that contains the query outright, and a nine-tier sort (exact label →
  exact alias → label prefix → shorter label → alias prefix → shorter alias → qualified
  prefix → shorter qualified → the `floor(10 * score)` bucket) whose last tier is a usage
  rank the ccd menu leaves at zero — it calls the shared filter with two arguments, so that
  rung never fires there and nothing is persisted. A query over 50 characters matches
  nothing, and a spaced query whose first word names a button that takes arguments resolves
  to that button alone.
  Aliases are alternate names on one entry rather than entries of their own, which is how
  the reference models them (`/bg` shows the row `background (bg)`); Escape shuts the menu
  until the command line is left; Tab activates exactly as Enter does; and a row the user
  never arrowed to only runs when it still answers what was typed, so a fuzzy match cannot
  be run by a return meant for something else. **Selecting completes the composer** with
  `/name ` and submitting is what runs the command — the reference inserts an atomic
  rich-text chip there, and the seven deliberate deltas (that chip, `slashRouting`, the
  `search-input` and `connector-tool` row kinds, the `Custom command` subtitle, `sourceRepo`
  and the card's clamping ellipsis) are declared row by row in
  `Deltas/reference-surface-deltas.tsv` under its new `component` kind. Its virtualizer and
  the sticky min-width that exists to compensate for lazy measurement are not carried:
  neither changes what is on screen.
  The ordering is pinned by `SlashMenuFilterParityTests` against a recording of the
  reference's *own* code (`Captures/SlashMenu/`, generated by running the shipped Fuse and
  the verbatim `ix`/`sx`/`rx`/`lx`/`cx`/`ux`/`px` region over a fixture), scores included —
  the labels alone leave a wrong constant invisible — and the rendering by
  `--slash-selftest`.
- **Bundled skills** (`Services/BundledSkills.cs`, `Assets/Skills/*.md`): skills this build ships
  embedded, parsed by Core's own frontmatter reader and added **last** in `SkillCatalog` so a
  user, project or plugin skill of the same name shadows one. The reference's built-ins are not
  stored as whole literals — its string table holds them in fragments the CLI assembles at
  runtime, so extracting one from the bundle yields its command registration rather than its
  text — and the faithful way to get one is to invoke it against a capture listener and take the
  block the CLI sends. `/simplify` is that recording, pinned line for line against the installed
  CLI by `BundledSkillParityTests`. Only skills whose instructions are true here are bundled, and
  a check enforces it: `/run` wants apt-get, xvfb, tmux and chromium-cli and reads folder
  resources it ships beside itself, `/fewer-permission-prompts` writes `.claude/settings.json`
  and reads Claude Code's own transcript directory, and `/code-review` is already a command here.
  **The reference's folder built-ins ship as folders** (`Assets/BundledSkills/{name}/`, content
  files loaded by `Skills.LoadDirectory` under bare names): `dataviz` with its `references/` and
  `scripts/`, `verify` with its `examples/` (listed to the user and withheld from the model, as
  the reference's `disable-model-invocation` has it), and `claude-api` with its 68 per-language
  files — the bodies are the ones the CLI sent when each was invoked at the listener (2.1.257),
  recorded under `Captures/Skills/` and compared byte for byte, and the resource files are
  checked against the CLI's own extraction under `%LOCALAPPDATA%\Temp\claude\bundled-skills`
  when this machine holds it; `workflow-authoring`, which needs no files, is embedded and
  recorded the same way. `update-config` and `keybindings-help` are deliberately not bundled:
  they configure the reference's own `settings.json` and `keybindings.json`, neither of which
  is this app's.
- **Skill packs** (`Services/SkillPacks.cs`, `Assets/SkillPacks/{pack}/{skill}/`): 15 third-party
  folder skills shipped with the build — 14 from anthropics/skills and one from
  vercel-labs/skills — vendored verbatim and joined to the catalogue beside the embedded ones,
  so they list, invoke, render and switch off exactly like a skill the user wrote. They are
  **content files rather than embedded resources**, which is the whole difference from the
  bullet above: a folder skill's body names its own resources ("run `scripts/with_server.py`",
  "the fonts in `canvas-fonts/`") and Core answers that with the folder's real path, at the top
  of the rendered body and behind `${CLAUDE_SKILL_DIR}` — an embedded copy has no path to hand
  over, so a packed skill would arrive telling the model to open files that are nowhere.
  A pack is one directory and its name prefixes every skill inside it
  (`anthropic/canvas-design` → `anthropic:canvas-design`), which is `Skills.LoadDirectory`'s own
  nested-folder naming and the reference's for plugin skills; the prefix is what keeps a shipped
  skill from taking a name the user might want, and the "/" menu still finds it part-wise on
  `/canvas`. **What is in a pack is a licensing question, not a taste one**: only work licensed
  to redistribute is vendored, each skill keeps the LICENSE it was published with, and
  `Assets/SkillPacks/THIRD-PARTY-NOTICES.txt` records each pack's origin commit and the skills
  left behind with the reason — Anthropic's `docx`/`pdf`/`pptx`/`xlsx` are source-available with
  redistribution expressly forbidden, and `doc-coauthoring` ships with no license at all, so all
  five stay installable from the marketplace rather than shipped. Nothing is edited on the way
  in, rebranding included: a skill whose body no longer matches its upstream is neither the
  skill its author wrote nor one anybody can update. `SkillPacksTests` pins the shipped list
  (a dropped folder still parses and still lists, and fails only when the model opens the file),
  that each skill carries a description, a real base directory and its license, and that every
  path a body names into its own folder is really beside it.
- **Skills, reference mechanics** (parity round against CLI 2.1.251, mined from the binary —
  the entire invocation pipeline matches the reference):
  **One command namespace** (`Services/SkillCatalog.cs`, 3s-cached): skills from
  `%APPDATA%\JarvisCode\skills`, `~/.claude/skills` (read live, not just the Import button),
  `{cwd}/.jarvis/skills`, `{cwd}/.claude/skills`, **directory-scoped skills** (any
  `<subdir>/.{claude,jarvis}/skills` under the repo — free names keep the bare name with the
  reference "applies when working under {dir}/" description suffix, collisions become
  `{dir}:{name}` with `UnqualifiedName` kept), plus legacy commands (user + `.jarvis/commands` +
  `.claude/commands`, loaded as flat skills — the reference retired its SlashCommand tool, so
  ours is no longer registered) and plugin entries. Resolution is find-first (user shadows
  project, like the CLI). Nested skill folders name as `a:b`.
  **Frontmatter** (`Frontmatter.ParseRich`: quoted scalars, block/inline lists, raw nested
  blocks, `-`/`_`/case-insensitive key lookup): `description`, `when-to-use` (listing suffix
  "desc - whenToUse"), `argument-hint` ("/" menu), `arguments` (named `$arg` substitution),
  `allowed-tools`/`disallowed-tools` (session permission grants via
  `UiPermissionGate.AddSessionRuleLines`, CLI tool names mapped — `SkillPermissionRules`),
  `disable-model-invocation` (Skill-tool refusal with the reference message, unlocked when the
  user typed /name this turn), `user-invocable: false` (hidden from the "/" menu,
  `<skill-format>` envelope), `model`/`effort` (session overrides applied to following turns),
  `context: fork` + `agent` + `background` (subagent execution, background by default, the
  reference result strings, recursion guard), `hooks` (`SkillHooks.Parse`, join the session on
  invocation), `paths` (conditional skills, dormant until a read/edit touches a matching file),
  `shell` (bash default, `powershell` opt-out).
  **Listing**: a `<system-reminder>` on the user message ("The following skills are available
  for use with the Skill tool:"), full on the first message and **only new skills afterwards**;
  budgeted like the reference (desc cap 1536 chars, total 1% of window×4, over-budget entries
  degrade to `- name` in recency-weighted-usage order — `SkillInvocation.BuildListing`,
  usage persisted in ui-settings `skillUsage`, 60s write throttle). The system prompt keeps
  only the guidance line. Plugin entries list only with a declared description/when-to-use.
  **Invocation**: typed `/name` sends the reference envelope
  (`<command-message>/<command-name>/<command-args>`) plus the rendered body as separate blocks
  of one user message (the transcript shows the typed `/name args`); the model-side `skill`
  tool (schema `{skill, args}`, the reference tool doc + coordinator suffix) returns only
  "Launching skill: {name}" in the tool_result and injects the body into the same user turn
  via the additive `ToolResult.FollowUpText` → `ToolResultBlock.FollowUpText` →
  `ChatMessage.FromToolResults` channel (wire-verified against a local capture listener).
  Rendering (`SkillInvocation.RenderBody`) = the "Base directory for this skill:" header
  (folder skills only) + `$ARGUMENTS`/`$N`/`$ARGUMENTS[N]`/named args/`\$` escape with
  re-scan-proof tokens + `${CLAUDE_SKILL_DIR}` (fwd slashes) / `${CLAUDE_PROJECT_DIR}` /
  `${CLAUDE_SESSION_ID}` / `${CLAUDE_EFFORT}` + `` !`cmd` `` / ```` ```! ```` shell
  preprocessing through the real permission gate (`SkillShellRunner`; bash needs Git Bash,
  else "shell: powershell"). Re-invocations elide ("Skill /x is already loaded above…",
  the post-compaction "NEW invocation" variants); compaction marks the tracker and the next
  message carries the reference **invoked_skills reminder** ("Do NOT re-execute…").
  Stacked commands peel up to 5 with the reference cap notice. The typed path runs the
  blocking `user_prompt_expansion` hooks (reference payload: expansion_type/command_name/
  command_args/command_source/prompt); the Skill tool fires none, matching the CLI. Skills
  declaring allowed-tools/disallowed-tools/hooks/shell prompt "Execute skill: {name}"; plain
  skills run silently. Coordinator sessions load read-only ("Loaded skill instructions
  (read-only)…", shell placeholders, no grants/usage). Subagents carry the skill tool and
  the listing (via `SubagentServices.Skills`). Chat surface has no skills (typed `/skill`
  gets the reference "/x isn't available in Chat" notice) — the desktop gates them on a
  server-side capability we don't have. `/skill-doctor` renders the reference table
  (skill · source · context · 7d tokens · uses · last used) with its legend and advice lines.
  Errors match: "Unknown skill: x. Did you mean y?", the directory-scoped-variants message,
  and the unscoped-name variants note ride the tool results.
- **The pane rail and its chords** (`Views/Panels/PanelRail.xaml[.cs]`,
  `Services/SidePanes.cs`): the rail is built from the reference's own spec list (its
  Xb/Jb/ey) - terminal, diff, then the preview toggle - each toggle's tooltip carrying
  the label and the chord as one keycap per key, the same string standing as its accessible
  name. The diff toggle grows the reference's 4px accent dot at its top-right corner while
  the working tree is dirty (`Services/GitWorkingTree.cs` polls `git status --porcelain`)
  and its tooltip then reads "Changes (uncommitted changes)" - desktop 1.46388.1.0 renamed
  the diff pane to **Changes**, which its own pane-title table settles (preview/artifact/
  diff/terminal/file/plan/tasks/subagent/session/runs/pr map to Preview, Artifacts, Changes,
  Terminal, Files, Plan, Background tasks, Agent, Session, Runs, Pull request), and the
  pane title, the View-menu row, the shortcut-sheet row and the expand button follow it.
  Toggles the header cannot fit
  fold into the overflow menu as checkbox rows, ahead of the reference's own pane group
  there (Artifacts, Files, Background tasks, Plan, Runs). The chords are the reference's
  default keymap ported constant for constant (`Services/PaneKeymap.cs`, its LI in ion-dist
  chunk shared-16-DFDNRrwQ.js, with cmd folded onto Ctrl the way its DI does), which
  `PaneShortcuts` spells for the tooltips so the two cannot drift: Ctrl+backtick
  terminal, Ctrl+Shift+D diff, Ctrl+Shift+B / Ctrl+Shift+P browser, Ctrl+Shift+F files,
  Ctrl+backslash close pane, Ctrl+; side chat, Ctrl+B background tasks, Ctrl+Shift+S the
  element picker, Ctrl+T a new browser tab. One table feeds both the tooltips and the
  window's dispatch, and every command the keymap declares is also a `keybindings.json`
  action (`Services/UserKeybindings.cs`), taken from the enum rather than retyped so a new
  command can never exist with no way to bind it.
- **Provider settings** (`Views/Settings/Provider*.cs`): the Providers page is a searchable list
  of expandable cards — status dot · name · badge, a "2 keys · 3 models" line, and a body holding
  the credential editor, the base-URL box with presets (Ollama/NVIDIA), that provider's models
  (all the user's own, added and removed in place; an id already added under another provider
  is refused by name, since the assembled catalog keeps only the first entry carrying one)
  and a Test connection button that runs
  one real turn. The rows come from `ProviderCatalog` — pure descriptors + status text, unit
  tested — while the engine supplies the name and `RequiresApiKey`, so a local Ollama endpoint
  reads "no key needed" live. Adding a provider is a registration in `AppServices` plus one
  `ProviderPresentation` entry; a provider missing from that table still gets a working key card,
  and keys or custom models left behind by a provider that is gone stay visible so they can be
  removed. `ChatGptSessionCard` holds the one credential that is not a key (the cookie export).
  A **Configure…** row at the foot opens the reference's own
  **"Configure third-party inference"** window (`Views/Settings/InferenceConfigWindow.cs`,
  ported from ion-dist `c71860c77-DuPx-LoQ.js`): its 50px header with the Configurations
  picker (New configuration · Duplicate… · Rename… · Import configuration… · Show in
  Explorer · Delete, the applied one badged "applied") and an Export button carrying the
  sensitive-values warning and the Templates group, a 200px nav with its own "Search
  settings" box, and the Discard/Save/Apply footer whose Apply asks to relaunch. A
  configuration is a named snapshot of the settings that decide *where inference goes* —
  the endpoints, the custom providers, the model catalog — kept one JSON file per
  configuration under `%APPDATA%\JarvisCode\inference` (`Services/InferenceConfigurations.cs`,
  with the reference's four import refusals). Its four sections are real: Connection edits
  the base URLs and runs one real turn, Models runs **Test model discovery**
  (`Services/ModelDiscovery.cs` — `GET {base}/models` or Ollama's `/api/tags`, answering
  with the reference's "found {n} models" / "found {n} models; {m} of yours not in the
  list" / "Not returned by discovery: …" / "…and {n} more"), Credentials runs a
  **credential helper script** under a timeout and classifies it with the reference's own
  eleven outcome sentences (`Services/CredentialHelpers.cs`: a bare token or a JSON object
  of auth headers), and Network is the **Firewall allowlist**
  (`Services/FirewallAllowlist.cs` — the hosts the configured endpoints name, "Test
  connectivity", "{n} of {m} reachable", Copy hostnames, Download .txt). The MDM-profile
  and bootstrap-URL read-only rows and the organization-plugins mount folder are declared
  in `Deltas/reference-surface-deltas.tsv`: there is no managed-settings channel here to
  set one.
- **The settings dialog is the reference's nav** (`Views/Settings/SettingsDialog.xaml.cs`,
  from ion-dist `shared-16`'s `z`): **Settings** (General · Providers · Permissions · Usage ·
  Jarvis Code · Import & export), **Extra** (Themes · Features · Fingerprints) and
  **Desktop app** (General · Extensions · Developer · Debug), with Providers, Permissions,
  the Extra group and Debug this build's own. A page carries no heading of its own — the nav
  names it and the content starts with its first section, as the reference's pages do — and
  the rows are built from `Views/Settings/SettingsRows.cs`, a port of the reference's own
  `SettingsSection` / `SettingsRow` / nested card / segmented control at the CDS token sizes
  the classes name. A description carrying markup tags (`<link>…</link>`) renders as real
  links through `Views/Settings/SettingsRowsProse.cs`.
  **Search runs over every row, not just the nav** (`Services/SettingsSearchIndex.cs`): the
  index is the reference's own shape (`{section, label}` per row, ion-dist
  `ca25db325-CwyIN-8d.js`) and the matcher is its `xe` ported constant for constant — 0
  exact, 1 prefix, 2 every query word starting a word or a run of words, 3 every word
  present somewhere, spaces squashed in the first two tiers — over which sit its
  section-then-rows ranking, the three-rows-per-section cap with a "+N more" line, and the
  "Matching “{query}” across all sections." / "configured in {section}" / "No matches."
  lines.
- **Settings › Jarvis Code** (`Views/Settings/ClaudeCodePage.cs`, the reference's Claude Code
  page `c71860c77-D1Dqt2F3.js`) in its own section order as a Windows build renders it:
  **Code appearance** (its `Ea`) — the light and dark theme selects over the reference's own
  `greet.ts` before/after preview (`Views/Settings/CodePreviewCard.cs`), and the Code font
  box with its "e.g. JetBrains Mono" placeholder; **Appearance** (`la`) — Interface font,
  Transcript text size and Transcript width, whose three choices are the stylesheet's real
  content widths (768 / 960 / 1280 px inside a 32px gutter each side,
  `Services/TranscriptWidths.cs`, measured from `c6a992d55-iGxCOsRk.css`) and which the
  transcript column takes live; **Local sessions** (`wa`) — Allow bypass permissions mode
  (the composer's mode menu stops offering Bypass when it is off), Dynamic workflows, the
  three notification levels, Notification sound, Draw attention on notifications, Archive
  inactive sessions over the reference's own [0, 1, 2, 7, 14, 30] with "Never" for 0, and
  Worktree location, which `Services/WorktreeTools.cs` reads; **Browser** (`qa`) — Browser
  tools (the switch decides whether the `mcp__Claude_Browser__*` suite reaches the model at
  all), Open links in built-in browser, Persist sessions and Allowed sites, which
  `Services/BrowserOriginGate.cs` reads; **Mobile simulators**; **Pull requests** (`Oa`) —
  Branch prefix with the reference's own validator (`Services/BranchPrefixes.cs`: its `sb`
  regex, its reserved set and its `Fa` order, so "main" is reserved and "trailing." is a
  format error), Create pull requests automatically, Create as draft and Auto-archive after
  PR merge or close; and the **Plugins** row, whose Manage opens Customize.
  **The 28 code themes are the reference's list, with measured colours**
  (`Services/CodeThemes.cs`): its `w` table from `c5d464fa3-TkKojVeh.js` in its order, each
  theme's `editor.background`, `editor.foreground` and the winning `tokenColors` rule for
  the five scopes this tokenizer's kinds correspond to (`comment`, `string`, `keyword`,
  `constant.numeric`, `variable.other.normal`) read out of the Shiki theme JSON the desktop
  itself bundles (`cd14a9327-jHAmUVkN.js` and its siblings). The two Claude themes are the
  reference's own palettes instead — light from the same chunk's hex table, dark from
  `shared-12`'s hsl set converted with the chunk's own `h()`, which is what makes its
  foreground `#eaecf0` rather than the `#ebecf0` a hand conversion lands on.
  `Controls/CodeText.cs` paints with the active theme and falls back to the app's semantic
  brush for a kind the theme leaves unnamed.
- **Settings › Desktop app › General** (`Views/Settings/DesktopPage.cs`, the reference's
  `c71860c77-j8GBEGCP.js`): **General desktop settings** (Run on startup, the Quick Entry
  shortcut recorder, System tray, Keep computer awake), **Browser use** (Allow all browser
  actions) and **Computer use** with its Beta badge (Enable computer use, Unhide apps when
  Jarvis finishes, and the **Denied apps** list whose "Add app" menu offers every windowed
  process — `Services/ComputerUseAppPolicy.cs`, which `request_access` consults and refuses
  by name). The recorder is the reference's `ne`: it records a chord, refuses the reserved
  combinations by name and reports the OS's answer with its own three sentences
  (`Services/QuickEntryShortcuts.cs` + `Services/GlobalHotkey.cs`, whose `Register` reports
  registration-failed and invalid-accelerator apart). The reference's "Quick access
  shortcut" picker and "Voice shortcut" row are **not** drawn, because the reference does
  not draw them here either: their features answer `{status:"unavailable"}` off darwin
  (app.asar `index.chunk-DnlgCaT3.js`, its `Qon` and `nsn`), and so do the Background /
  Full control modes and the two macOS permission rows — all declared in the surface
  manifest.
  **Notification levels are a table, not a switch** (`Services/NotificationPolicy.cs`): the
  three types, the levels each offers (Task complete has no "Badge only"), the
  absent-entry-reads-as-banner rule, the disabled "Draw attention" while both attention
  types are off, and the flash itself — `MainWindow.FlashTaskbarButton` is
  `FLASHW_TRAY | FLASHW_TIMERNOFG`, the reference's `requestUserAttention` on Windows.
- **Settings › Desktop app › Extensions** (`Views/Settings/ExtensionsPage.cs` +
  `Services/DesktopExtensions.cs`): the `.MCPB` / `.DXT` bundles the reference installs, as
  a drag target with Configure / More options → Details / Uninstall, the "No extensions
  installed" empty state and an Advanced settings sheet carrying the auto-update switch. A
  bundle is a zip holding a `manifest.json` whose schema was measured from the reference's
  own validator (app.asar `index.chunk-DnlgCaT3.js`: `mJe` for the document, `lJe` for
  `server`, `iJe`/`cJe` for `mcp_config` and its `platform_overrides`, `pJe` for a
  `user_config` field), and its server is resolved the way its `lYe`/`cYe` resolve one —
  the platform's overrides first, then `${__dirname}`, `${pathSeparator}`, `${/}`, the four
  system directories and `${user_config.KEY}`, an array element that is exactly one
  `${user_config.key}` expanding into its values, and an extension with an unfilled
  required field contributing no server at all. Installed bundles live in
  `%APPDATA%\JarvisCode\extensions`, and their resolved servers are written to one
  generated `mcpServers` file every session joins as an extra MCP config.
- **Settings › Desktop app › Developer** (`Views/Settings/DeveloperPage.cs`, the reference's
  `c71860c77-Cjeeh0xx.js`): the **Local MCP servers** two-pane table — the server list on
  the left, and on the right its status chip, Command, Arguments, Error, View Logs, a delete
  that asks the reference's own question and, behind Advanced options, Environment
  variables — plus Edit Config, the "No servers added" empty state and the Developer docs
  link. A server an extension installed carries the reference's "This server is managed by
  an extension" note and no delete button.
- **Settings › Import & export** (`Views/Settings/ImportExportPage.cs` +
  `Services/SessionTransfer.cs`, the reference's `c71860c77-BZhYsZYJ.js`): Import from
  another install's data folder over its three ranges (Last 30 days / Last 90 days /
  Everything), the Claude Code CLI conversion this build already had, Export sessions to a
  zip in Downloads with the count and size of what is in range, and **Import history** with
  Remove / Remove all, which deletes the sessions an import brought in. Two of the
  reference's three import sources need a claude.ai account and are declared.
- **Settings › Usage** (`Views/Settings/UsagePage.cs`): the reference's `O` — the
  description, its three Time ranges (7 / 30 / 90 days, bucketed a day per bar up to 30 and
  a week per bar above it), a tile per surface, the Chart metric control and the stacked bar
  chart, over this build's own heatmap and per-model rows. `Core/Sessions/UsageStats.cs`
  gained an additive per-surface and input/output split so the chart is real. The **Cost**
  metric is drawn *disabled* with the reference's own reason — which is the branch its own
  `C` takes when no turn in range carries an estimate, and the only branch this build can be
  in, since this engine never prices a turn.
- **Settings › General** (`Views/Settings/GeneralPage.cs`, the reference's
  `c0db37792-CMcO5owL.js`): **Profile** (Avatar with Randomize/Clear, Full name, "What
  should Jarvis call you?", the reference's own twenty work functions in its order, and
  Instructions for Jarvis with its four rotating example hints), **Appearance** (the
  System/Light/Dark control, Chat font under the reference's four labels, Motion, New chat
  view) and **Notifications** (Response completions, Code notifications, Code permission
  requests), then this build's engine defaults. The reference stores all of these on the
  claude.ai account and applies the instructions **server-side** — they reach a turn as the
  request field `include_conversation_preferences`, never as a prompt block the client
  writes — so this build stores them in `Services/UiSettings.cs` and says so in the row's
  description rather than repeating a sentence about accounts it does not have.
- **Claude Code compatibility** (additive Core upgrades): `Skills.LoadDirectory` also loads
  folder skills (`<name>/SKILL.md` + resources; rendering prepends the reference
  "Base directory for this skill:" header), `Frontmatter` parses YAML block scalars
  (`>-`, `|`), quoted scalars, lists and nested blocks, `McpConfig` reads the
  `{"mcpServers":{…}}` shape, and plugins may ship `.mcp.json`. `~/.claude/skills` and the
  project's `.claude/{skills,commands}` are read live; the Skills page's
  "Import from Claude Code" button still copies `%USERPROFILE%\.claude\skills` across for
  users who want a local copy. **The plugins Claude Code has installed are read live too**
  (`Services/ClaudeCodePlugins.cs`), which is where most third-party skills actually arrive:
  its layout cannot simply be listed, since `~/.claude/plugins` holds `cache/`,
  `marketplaces/` and an `installed_plugins.json` whose entries carry the real
  `installPath` (`cache/{marketplace}/{plugin}/{version}`), so the manifest is read, each
  key's name is the part before its `@`, and that product's own `settings.json`
  `enabledPlugins` decides which are on — only an explicit `false` switches one off, as
  there. `Plugins.LoadDirectories` is the additive Core overload that makes it possible: a
  plugin filed under a version folder has no directory named after it, so the name has to
  travel beside the path. They join through `PluginLibrary.LoadUser`, so they reach a turn's
  skills, commands, agents, hooks and MCP servers, list on the Plugins page under their own
  scope, and answer this app's own disable switch; a plugin installed *here* shadows Claude
  Code's copy of the same name. Read-only — this app installs, updates and removes only under
  its own roots — and declared as this build's own addition, since the reference has no
  counterpart: it **is** Claude Code.
- **Theming** (`Theming/`): 97 bundled palettes (embedded JSON under `Assets/Themes/`, sourced
  from claude-desktop-extra) + user themes in `%APPDATA%\JarvisCode\themes\*.json`. Tokens
  ("--bg-000" …) become frozen brushes (`Bg000Brush`) swapped live via DynamicResource.
  Theme picker: Ctrl+Shift+T. Per-theme animated spinners (`Views/SpinnerGlyph.cs`).
- **Fields** (`Controls/PlaceholderText.cs`, `Services/InputFieldAudit.cs`): a text box's
  placeholder is the field's own, not a label laid over it. `PlaceholderText.Text` is an
  attached property that every input template draws from the box's **own** padding, border
  and font, so the hint and the caret start at the same point by construction instead of by
  two hand-typed numbers agreeing — which they did not: hints sat at 12 over fields whose
  text began at 11, at 13 over a 14px box, anchored to the top of a box whose content host
  was centred. Three defects underneath it are fixed with it. A template must **not** repeat
  `Padding` as the content host's margin — WPF insets the text by the box's own Padding, so
  every bordered field was padded twice and its text sat 10px right of its hint — and the
  hint is drawn **over** `PART_ContentHost`, which WPF paints in the box's Background and
  which therefore covers anything behind it. The hint's text is *pushed* from the property's
  changed callback rather than bound: a template can only reach an attached property through
  a parenthesised path, and that path resolves against the parser context, so the fields
  templated earliest in a run came up `PathError` and drew no hint at all while later ones
  bound fine. And **every field's caret is bound to its own Foreground**, because WPF derives
  an unset caret from the *inverse of the Background colour*: a box on `Transparent` or on
  this app's 5%-ink `FieldFillBrush` inverted to near-black, invisible on every dark theme.
  What a screenshot cannot show — a caret's contrast, a placeholder one pixel off, a binding
  that failed silently — is measured on the laid-out tree by `--input-selftest`, which the
  reference has no counterpart for and which is declared as this build's own.
- **Permission gate** (`Services/UiPermissionGate.cs`): deny rules absolute; allow rules and
  session "Always allow" apply to Standard risk only; Escalated always prompts; AutoAllowable
  runs silently in every mode. The inline prompt card follows Claude Code Desktop's: title
  naming the file, the workspace-relative path (absolute once it escapes; nothing for shell),
  the escalation reason, the subject in a monospace box, then Deny on the left
  with Always allow and Allow once right-aligned, each printing its digit and key caps
  (1/Esc · 2/Ctrl+Shift+Enter · 3/Ctrl+Enter). Escalated calls drop Always allow, which the
  gate would not honor anyway.
  **A read outside the working directories is answered the reference's way** (CLI 2.1.257):
  with `AppSettings.BlockReadsOutsideWorkingDirectories` on (its
  `permissions.blockReadsOutsideWorkingDirectories`) the file tools refuse it with the
  reference's sentence, and in auto mode the first such read raises its one-time question —
  "Read outside the working directories — Allow reads outside the working directories?" —
  whose three answers ride the card's three buttons: Always allow is "Yes, keep allowing" for
  the session, Allow once is "No, ask again next time", and Deny is "No, block … from now on",
  which sets the setting and answers the model with "The user did not allow this read outside
  the working directories." The denial reaches the model at all because the gate implements
  Core's `IDenialReasonSource`, which the orchestrator reads in place of its generic line.
  Network paths (UNC shares, `/net/` automounts) are refused as working directories and
  attachments before being touched, with the reference's two sentences
  (`Core/Utilities/NetworkPaths.cs`).
  **The composer's mode picker is the reference's own** (`Services/PermissionModeMenu.cs`,
  measured against the desktop's Code surface — the descriptor `WP` and the order table
  `VP` in ion-dist `shared-10-3-tqq7pk.js`, the item builder, the danger predicate and
  the digit assignment in `ca80fca8d-DkeN2GSR.js`). The popup opens on a **"Mode" group
  label** over one row per offered mode, bounded 128..320 wide, top-start; the rows are
  the reference's five in its order — **Manual · Accept edits · Plan · Auto · Bypass
  permissions**, auto pushed rather than unshifted since `autoFirst` is a rollout flag
  outside its cohorts — each with the reference's own description under it, truncated
  rather than wrapped. The mode the session is in carries an **accent check**, not bold
  text, and the mode Settings names carries a **"Default" pill**: two different facts the
  reference shows separately, which is why picking a mode no longer rewrites
  `AppSettings.PermissionModeName`. Digits are handed out the reference's way (`Iv` over
  `Ev`: row order, skipping anything disabled, capped at nine) and drawn as **keycaps** —
  `Controls/MenuItemExtras.cs` adds the trailing slot and the corner radius WPF's MenuItem
  lacks, and the shared row template now renders every shortcut as one cap per key, as the
  reference does in every menu.
  **A mode that stops asking is confirmed first**, once per workspace: the reference's
  danger predicate (bypass always; auto only where auto is not the default, which it is
  here) gates the pick behind "Bypass all permissions?" with the workspace path and its
  "You won't be asked again for this workspace." footnote, Cancel focused as the reference
  asks; the ack is filed under `{workspace}:{mode}` in `UiSettings.PermissionModeAcks`,
  so approving it in one checkout approves nothing in another. A pick is a **session**
  choice that is also remembered for the folder (`UiSettings.FolderPermissionModes`, never
  Plan, ignored on read if it says Plan), and the reference's layering decides what a
  session opens in — the pick, then the project's own settings files
  (`permissions.defaultMode` in `.jarvis/settings.local.json` then `.jarvis/settings.json`,
  read by `ProjectPermissions.LoadDefaultMode`, whose `auto` and `bypassPermissions` are
  ignored as CLI 2.1.257 ignores them: a mode that stops asking comes from user or
  managed settings or the flag, never from a checked-in file), then the configured
  default, then the folder, then the gate's built-in Auto — which is why Settings ›
  Permissions › Default mode gained a **Not set** row: a default that is always set would
  sit above the folder forever. The CLI applies the same project layer when no
  `--permission-mode` was given. A
  store that will not take the choice rolls the session back and says so, with the
  reference's "Permission mode couldn't be changed. You can try again." The chip's tooltip
  is the group label plus its chord, or the reference's warning while the mode is
  dangerous. `--open=mode` poses the menu for a real screen grab, as the model menu's flag
  does. What is deliberately not carried is declared in
  `Deltas/reference-surface-deltas.tsv`: the "Enable auto mode?" consent (its copy
  promises a model-side classifier with injection safeguards, and this build's Auto is a
  local risk-class gate that always escalates), the disabled Bypass rows and their four
  account/organization/root reasons, the model-gated Auto rows, the auto-setup pill, the
  security-guide link, the second settings layer and the badge's source tooltip.
- **Terminal pane** (`Views/Panels/TerminalPanel.xaml[.cs]` + `Services/TerminalTabs.cs`):
  the reference's tab strip over the real ConPTY views. Its labelling rule is the one worth
  pinning - a tab keeps whatever name the user gave it, an unnamed one reads plain
  "Terminal" while there is only one, and every unnamed tab is numbered "Terminal {n}" as
  soon as there are two - and its menu is the reference's Rename terminal / Close terminal /
  Close other terminals, with Delete or Backspace closing a focused tab (which is what its
  own tooltip promises) and the overflow folding into "More terminals". A shell that ends
  draws the reference's overlay over the view: "Shell exited." - or "Shell disconnected:
  {reason}" where one is known - over a "Restart shell" button, and a selection raises its
  floating "Ask about this", which attaches the output to the composer.
- **Agent capabilities** (App-level tools joined into every Code turn): a real
  ConPTY **terminal** tile (`Services/ConPtyTerminal.cs` + `Services/TerminalScreen.cs`,
  a line-model VT interpreter), live **artifacts** rendered in the engine (`artifact` tool),
  **Computer Use** (`screenshot` + `computer_batch` driving SendInput; the grant answers
  are the reference's JSON and sentences in `Services/ComputerUseGrantResults.cs` —
  request_access resolves every requested name against the installed index **and the
  running applications** (`Services/InstalledApplications.cs`) *before* raising a card and
  short-circuits the whole call when one resolves to neither, saying plainly that the user
  was never asked and offering the near misses. Both halves are the reference's `Yt`
  (desktop 1.44121.2.0, `index.chunk-b0FbQZTl.js`), and this port used to carry only the
  first: its refusal already said the name matched no "installed or running application",
  and an application that was open but unindexed was refused by a sentence that had just
  promised it would not be. What "installed" means moved with it — the Start menu is the
  shell's **AppsFolder**, not the `.lnk` tree, and a packaged application has no shortcut
  file at all, so a scan of the two Programs folders answered 113 names on this machine
  where the AppsFolder answers 152, "Notepad" among the difference. Both are read and
  unioned, the AppsFolder late-bound through `Shell.Application` on an STA thread inside
  the reference's own 1000ms enumeration budget; it requires `reason`, has no app cap (the reference has
  none) and answers `{granted, denied, tierGuidance?, screenshotFiltering}` with the
  four tier-guidance paragraphs; list_granted_applications answers
  `{allowedApps, grantFlags}`; the clipboard pair carries its two grant sentences, its
  `{"text": …}` answer and its refusal to write while a tier-"click" app is frontmost;
  open_application checks the grant first and switch_display carries its five sentences.
  A coordinate is validated with the reference's own four refusals (its `V` and `H`:
  required, an array of length 2, a tuple of non-negative finite numbers, and the one
  naming the frame it fell outside), `scale` with its own, `cursor_position` answers in
  its two coordinate spaces — `image_pixels` inside the last frame, `logical_points`
  outside it, with its note — and text past sixteen characters is pasted rather than
  typed a keystroke at a time, answering "Typed (via clipboard).", which is its win32
  rule and its own `clipboardPasteMultiline` default.
  Ported against the
  installed desktop's own computer-use server — `app.asar` `.vite/build/index.chunk-U7g3hHKL.js`:
  like the reference there is **no single-action tool**, every click/keystroke rides a batch
  (its 17 actions — key/type/mouse_move/left_click/left_click_drag/right_click/middle_click/
  double_click/triple_click/scroll/hold_key/screenshot/zoom/cursor_position/left_mouse_down/
  left_mouse_up/wait), actions run 10ms apart, stop on the first error, and report the
  reference's lines verbatim (`[2/5] left_click: Clicked.`, `Batch stopped at actions[2]
  (key). 2 completed, 1 remaining.`, `Batch aborted after N of M actions (user interrupt).`,
  images dropped as `[Image omitted due to error]` when a batch fails) with its argument
  bounds (`repeat` ≤ 100, `scroll_amount` ≤ 100, `duration` ≤ 100s) and refusals. Screenshots
  are **JPEG** (~3.4x smaller than the PNG they replaced, measured) at full resolution with a
  `scale` in [0.1, 1] that shrinks the image only: **coordinates are always pixels in the
  captured region's own frame**, never in the scaled image, and `zoom` re-captures a region of
  that frame for small text. `screenshot` still takes `display` — a 1-based number or `all` —
  and names the other monitors **by name** in its result; `switch_display` takes one of those
  names (or `auto`) and pins it. With none pinned, a standalone `screenshot` **follows the
  granted apps** — the reference's `autoTargetDisplay`, whose defaults table has it on: the
  capture display is resolved from where the granted, running apps have their windows, and
  re-resolved only when that app set changes (`ComputerUseService.ShouldResolveDisplay`,
  keyed by the sorted app names), so an unchanged set keeps the display it already chose and
  a pinned one is never overridden. A screenshot *inside* a batch keeps the frame instead,
  which is the reference forcing the flag off for the duration of a batch. The reference hands
  the resolution itself to a native resolver, so the tie-break is ours: the display holding the
  most granted windows, the primary breaking a tie. Its sibling flag `pixelValidation` — a 9x9
  patch compared at the click point before delivering a click — is **off in that same defaults
  table** and is set true nowhere in the build, so this port deliberately does not click-validate
  either; turning it on here would diverge from the installed app rather than match it. `save_to_disk` writes a batch's images next to ui-settings and
  returns the paths. **Grants are the reference's tier system** (`Services/ComputerUseGrants.cs`,
  live only while Settings › Features › "Require app grants" is on): request_access takes the
  reference's `apps`/`reason`/`clipboardRead`/`clipboardWrite`/`systemKeyCombos` schema and
  proposes a tier per app from the reference's own win32 name sets — browsers and
  trading/wallet apps `read`, terminals/IDEs and the Windows shell `click`, everything else
  `full` — with its restricted-tier explanations; the tier gates every action (pointer / plain
  left-click / full-mouse / keyboard), the **click target** is resolved with WindowFromPoint so
  a coordinate landing in an ungranted window refuses by name, OS chords (alt+tab, win+r, …)
  need the systemKeyCombos flag, the clipboard tools need theirs, and `list_granted_applications`
  reports the lot. Screenshots run the reference's win32 `mask` filtering: every visible window
  of an ungranted app is painted over with a solid rectangle (granted windows above it are
  re-blitted from the untouched capture, so z-order still reads). `ComputerUseTools.CreateIfEnabled`
  honours the two Settings › Features switches. **Teach mode** completes the set
  (`Services/TeachMode.cs` + `Views/TeachOverlayWindow.cs`): `request_teach_access` asks the
  user, hides the app and raises a full-screen click-through overlay; `teach_step` shows one
  tooltip and waits for Next before running its actions and returning a fresh screenshot;
  `teach_batch` queues a page's worth in one round trip. The card is the reference overlay's
  geometry (280–420 wide, radius 16, 10px arrow 16px off the anchor, 20px edge margin,
  below → above → right → left placement with the arrow sliding after clamping, step ⇄
  "Working…" swap) on the app's own theme brushes; the window is `WS_EX_TRANSPARENT` except
  while the pointer is over the card (the reference's mouseEnter/mouseLeave, polled), and a
  finished turn closes it. Deliberately absent: macOS's `native` compositor-level filtering,
  the `app_*` background-accessibility family, and keyboard operation of the tooltip — the
  overlay never takes focus, because the app being taught needs it.
  **What a session may drive is that session's own** (`Services/ComputerUseSessionGrants.cs`):
  the reference files a grant on the session — `cuAllowedApps` / `cuGrantFlags`,
  written with `saveSession`, read back through `getAllowedApps()`, taken away one
  app at a time by `revokeComputerUseGrant` — where this port kept one global
  allowlist, so approving Notepad once approved it for every conversation the
  installation would ever hold while the tool's own description said "for this
  session". Grants are per session now (capped at 100 sets, evicted in the order
  they were opened), every read threads the session id, and the standing list the
  Settings page edits is unioned underneath, since the reference has no such page
  and a grant made by hand needs an owner. `request_access` resolves a name
  against the installed index **and the running applications** before raising
  anything (its `Yt`), refuses this application outright with its `Xt`, and
  answers the first request in a session that names a browser or a terminal with
  its `Qt`/`$t` plus `Zt` — a one-time, turn-scoped confirmation rather than a
  block, tracked per category by `getAccessWarned`/`onAccessWarned`.
  **The card it raises is the reference's own** (`Views/ComputerUseGrantDialog.cs`
  over `Services/ComputerUseGrantPrompt.cs`, its Code-surface card in
  `cd5a31703-DPCARDPv.js`): a title that names the one app, counts several, reads
  "these capabilities" once a flag is in the set and "your computer" when nothing
  resolved; a Reason line; a row per application with its icon, its tier ("View
  only" / "Click only" / "All", or "Already allowed") and "(not installed)" on a
  dimmed row; a warning under anything that can reach past itself — "Can run
  commands on your computer" for the 19 terminals and IDEs its `ue` names, "Can
  access all your files" for Explorer, "Can change system settings" for Settings;
  a row per grant flag; the sentence naming what will be hidden; and Deny / Allow
  for this session on Esc and Ctrl+Enter. A headless run falls back to the
  question card the transcript can show. `--open=grant` poses it.
  **Ungranted windows step aside while it drives** (`Services/ComputerUseHide.cs`,
  the reference's win32 `prepareForAction` with its `hideBeforeAction` sub-gate,
  which its defaults table `Rln` ships true): the running applications that are
  neither granted, nor the shell, nor one of the 22 system processes its `BX`
  names are hidden before a batch and before a screenshot — never per action, as
  the reference forces the flag off inside a batch — the frontmost is re-read up
  to five times, what was hidden is named to the model above the next screenshot
  in both branches of its `yn`, and it all comes back at turn end behind
  "Unhide apps when Jarvis finishes", which is the switch this build already drew
  and had nothing behind. A window is minimized rather than hidden outright, and
  that is declared: a hidden window can only be restored by the process that hid
  it. **A window can be pointed at** (`Services/ComputerUseWindowHints.cs`): the
  composer's `@` menu lists the open windows beside the files and the peers, and
  the pick rides the next message as the reference's `<cu_window_hints>` block —
  which is not a grant but the answer to the question that makes one fail, the
  exact name to pass to request_access, as its own not-installed guidance says.
  **A driven desktop wears a rim of light** (`Views/ComputerUseGlowWindow.cs` +
  `Services/ComputerUseGlow.cs`): the reference draws a transparent, frameless,
  click-through window at the screen-saver level over one display's whole bounds, loading
  a generated `cu-glow.html` — its `Uwn` page and the `Qwn`/`$wn`/`eTn` create/show/hide
  trio in desktop 1.44121.2.0's `index.chunk-BHbE7U4N.js`, raised from `cuLockChanged`,
  which is the same edge `Services/DesktopLock.cs` now reports. Its geometry is carried:
  the three inset shadows composited into one edge ramp 150px deep, the 2s pulse, the
  0.3s fade, the 320ms hide delay (its `Vwn`) and the pill that starts at the centre of
  the screen, holds for 88% of six seconds and settles at `top: 40px; left: calc(100% -
  280px)`, its dot pulsing 1.8s, in the page's own clay - `rgb(217, 119, 87)` for the rim
  and its dot, `rgba(255, 247, 242, 0.95)` for the pill under them. One thing differs and
  is declared in `Deltas/reference-surface-deltas.tsv`: it is drawn natively rather than in
  an engine window, because an indicator that says a machine is being driven must not wait
  on a 374MB download. It is kept out of the screenshots this
  app takes twice over — `SetWindowDisplayAffinity` excludes its pixels, and
  `ComputerUseGrants.MaskUngranted` skips its handle, since that mask paints by window
  bounds and a full-screen window it did not know about would grey out every capture
  taken while it was up. `--open=glow` poses it, and the pose is the only thing that
  lifts the capture exclusion, so a screen grab can see what a screenshot may not.), and
  **Jarvis Browser** — our own MV3 extension (`Assets/JarvisBrowser`) talking
  to the app over native messaging (`BrowserBridge` pipe + `BrowserHostRelay` stdio mode).
  Several browsers may connect at once (each browser's relay is its own pipe client;
  connections are named from the UA the extension's ready event announces — Chrome, Edge,
  Brave…), and `browser_list_browsers`/`browser_select_browser` — the reference
  claude-in-chrome's list_connected_browsers/select_browser — pick which one every
  `browser_*` command drives (default: the first ready connection; a dropped selection
  falls back to the next).
  At parity with the reference Browser pane: tabs (`browser_tabs/tab_new/tab_select/tab_close`),
  `browser_navigate` (incl. back/forward), `browser_read_page` (YAML a11y tree with `ref_N`),
  `browser_find`, `browser_get_page_text`, `browser_computer` (CDP-trusted clicks/keys/
  scroll/drag/zoom + `Page.captureScreenshot`, by coordinate or ref), `browser_form_input`,
  `browser_javascript` (REPL eval), `browser_console` + `browser_network` (chrome.debugger
  capture, attached lazily per tab, buffers cleared on main-frame navigation),
  `browser_resize` (mobile/tablet/desktop presets + prefers-color-scheme), `browser_batch`
  (sequential, stop-on-error, no nesting), plus the legacy CSS-selector `browser_click/type`.
  Tree building and ref bookkeeping run in the page (`pageA11y`; refs persist per document);
  rendering/filtering live in `Services/JarvisBrowserFormat.cs` so they unit-test without
  a browser.
  The tree spans **iframes** (`allFrames` injection): each document numbers its own refs, so
  `ElementRef` carries the frame — the main frame reads `ref_5` like the reference, an iframe
  `ref_f3r5`, and the frame id rides every call back. A ref inside a *cross-origin* frame
  refuses (its page position is unknowable from script) and says to click by coordinate.
  `browser_file_upload` covers the reference's file_upload + upload_image: by `ref` it marks
  the input and hands paths to `DOM.setFileInputFiles` (a file input reads as `button`, as in
  the reference tree, and refuses `browser_form_input`), by `coordinate` it synthesizes a
  drop with a real `File`. `browser_gif` is gif_creator: the extension screencasts frames and
  logs the input events, and `Services/GifComposer.cs` draws the overlays (click rings, action
  labels, progress bar, watermark) and assembles GIF89a by hand — single-frame GIFs from
  System.Drawing spliced under one screen descriptor with per-frame delays and a NETSCAPE
  loop, thinned to 120 frames.
  **Browser-control parity with the reference extension** (audited against the Claude extension
  1.0.85 installed in the user's Chrome — its `accessibility-tree.js` and
  `agent-visual-indicator.js` content scripts): the a11y tree **redacts** password/hidden inputs
  and anything whose `autocomplete` names a credential, one-time code or card field
  (`[value redacted]`, the reference's own set) and, like the reference, keeps hidden and
  off-screen elements unless `filter` narrows it — `browser_read_page` now defaults to the whole
  tree. Every driven tab lives in a Chrome **tab group** ("Jarvis", orange): tabs join by being
  opened through the tools or by an explicit `browser_tab_select`, and anything else refuses by
  name. **Per-site consent** (`Services/BrowserOriginGate.cs`, one grant list per app run) asks
  before the first action on a site — navigation is checked against where it is going, every
  other command against the site the tab is on (`tab_origin`); with no UI to ask in (CLI,
  subagents) the tool-level permission stands. The **on-page indicator** is the reference's four
  pieces in two states: a glow border, a phantom cursor that animates to each click, and a
  "Stop Jarvis" button while acting (its click posts `stop_requested`, which cancels the turn),
  and a quiet "Jarvis is active in this tab group" pill the rest of the time; a finished turn,
  a dropped port or a 60s lull takes it down. `browser_find` matches by plain language (role
  synonyms + phrase and word scoring, best first, the reference's "use a more specific query"
  notice) and `browser_computer` takes `save_to_disk`. Deliberately absent: the side panel and
  its chat client, options/pairing pages, notifications and sounds, the cloud relay behind
  `switch_browser`, and `shortcuts_list`/`shortcuts_execute`.
- **The in-process MCP shell** (`Services/InternalMcpServers.cs`): the desktop does not
  hand its engine a flat list of app tools. It declares **in-process MCP servers** and
  proxies each tool to the model as `mcp__{server}__{tool}` — the partition is part of
  the surface, because the wire name says which subsystem answered. Ported from the
  reference's `InternalMcpServerManager.createProxyServers` (app.asar 1.44121.2.0,
  `index.chunk--4WVxkx1.js`), with its three steps in its order: an `isEnabled(session)`
  predicate that removes a whole server, a `GetDynamicTools()` that adds tools once a
  capability is live, a per-tool switch keyed the reference's way
  (`local:{server}:{tool}`, in `UiSettings.InternalMcpTools`, where only *false* means
  off), and a server left with no tools skipped entirely. Tools stay ordinary `ITool`s —
  Core is untouched — and the rename happens at this boundary.
  **Eleven servers, 72 of the reference's 75 tools**, the other three declared in
  `Deltas/reference-surface-deltas.tsv`: `Claude_Browser` (19 — the pane suite plus
  preview_start/stop/list/logs, which the reference declares in the *same* array and
  this port used to register bare), `claude-in-chrome` (19 of 22 — the Jarvis Browser
  extension family, renamed from `browser_*` onto the reference's names),
  `computer-use` (10), `ccd_session` (4), `ccd_session_mgmt` (7), `ccd_directory` (2),
  `terminal` (1), `mcp-registry` (3), `scheduled-tasks` (4), `visualize` (2), and
  `Claude_Code_Android_Emulator` (1 — its `control` tool, whose declaration is a
  recording of its own because the reference flags that server off; see the Android
  bullet below). Each renamed tool keeps its old bare name as an `IAliasedTool` alias so
  a stored session still replays — `control` keeps four of them, the android_* family it
  replaced — and the bare *current* name deliberately is not one, because
  `Claude_Browser` and `claude-in-chrome` both have a `read_page`, a `computer` and a
  `navigate`.
  **The servers are a recording, not a remembered list**
  (`Captures/Mcp/internal-servers-1.44121.2.0.json`, written from the extracted app.asar
  by `Captures/Mcp/gen-internal-servers.js`, plus
  `Captures/Mcp/android-emulator-1.44121.2.0.json` for the eleventh): all eleven servers,
  all 75 tools, each with
  its description and its **declared** input schema. Nine servers declare plain JSON
  Schema literals in the Electron main bundle and `scheduled-tasks` declares zod
  builders handed to the Agent SDK's `tool()`, so the generator reads both forms rather
  than restating them. `InternalMcpDocsParityTests` compares what this build advertises
  against it field by field **and in declaration order** — a reordered `properties`, a
  missing `required` entry or an absent `minItems` all change what the model is told it
  may send, and none of them is visible to a name check. Three adaptations are declared
  with the reason each differs (`.jarvis/launch.json`, the window request_teach_access
  names, and the scheduled-tasks directory create's doc interpolates), and a fourth test
  fails when one of them stops matching the reference.
  **A schema that needs no adaptation is generated rather than retyped**
  (`Services/CapturedMcpSchemas.cs`, the arrangement `Services/CapturedToolDocs.cs`
  already has for the CLI's tools): claude-in-chrome's 19, computer-use's ten,
  mcp-registry's three, and the ccd/terminal/scheduled servers' are the reference's
  bytes. `Claude_Browser` keeps hand-written schemas beside its behaviour, because its
  preview_start names this app's own launch.json.
  One measured caveat rides with all of it: what the desktop *declares* and what its
  model *receives* are not the same JSON. The declarations go through the Agent SDK's
  JSON-Schema-to-zod converter, which drops optional properties' descriptions, the
  numeric bounds and type unions before the schema reaches the wire — measured on a live
  ccd session, where `read_page` arrives with `filter`/`depth`/`ref_id`/`max_chars`
  undescribed and `required` cut to `["tabId"]`. This port advertises the declaration,
  which is the surface the reference authored and the one its own `getAppServersInfo`
  reports.
  **`isEnabled` is the reference's, per server, answered per turn.** The Browser pane
  reads its `launchEnabled` setting (`!== false`, so only an explicit no takes it away)
  and `!isSSH`; `ccd_directory` and `terminal` read `!isSSH` — always true here, since
  this app has no remote-host mode, and carried so the predicates read as the
  reference's; `computer-use` is answered against the switch on every turn instead of
  being snapshotted when the window built its tools, so flipping it is felt on the next
  model call; `claude-in-chrome` asks about the extension bridge rather than the session
  type, which is the reference's own
  `shouldEnableChromeExtensionBridge() && !isDisabled()`; and `mcp-registry` is gone for
  a third-party provider, its `JD().type !== "3p"`.
  **`alwaysLoad` decides what is deferred, not the server's provenance.** The
  reference's own predicate reads it first (CLI 2.1.251, `AO(e)`: `if (e.alwaysLoad ===
  true) return false`) and then defers **every** other MCP tool — its own servers
  included. In a **ccd** session exactly four set the flag: `Claude_Browser` (at the
  push site), `ccd_session` and `terminal` (on each of their tools) and `visualize`;
  `claude-in-chrome` gets it only when `le(model, sessionType) = ce(model) &&
  sessionType !== 'ccd'`, which is never true here. `terminal` joined that set in
  1.44121.2.0, where `read_terminal` declares `alwaysLoad:!0` and a live session
  receives it loaded. Measured live: those servers' tools arrive loaded and the other
  six arrive deferred. This port used to exempt all ten by provenance and so advertised
  68 schemas where the reference advertises 25;
  `InternalMcpServerDefinition.AlwaysLoad` now carries the flag and
  `TurnContextFactory.Deferrable` is the reference's rule. The engagement *threshold*
  stays this build's own — the reference gates on the model supporting tool_reference
  blocks (its `B1t`), which there is no capability table for here — but it is now
  measured over everything deferral would hide rather than over configured servers
  alone, which used to leave the mechanism switched off on exactly the sessions it
  exists for; a parity test asserts the shell's own servers clear it unaided.
  **A proxied call is raced against a stall timeout** (`Wft()` in
  `index.chunk-C5__TEgr.js`: `max(300s, mcp.toolTimeoutSec + 60s)`, 300s when unset,
  which is where this build leaves it — it has no such setting). The timer **re-arms
  instead of firing** while a permission card is up (`hasPendingPermission`, wired to
  `UiPermissionGate.HasPendingPermission`), and on expiry answers with the reference's
  own line — "`{tool} timed out after {n}s. The underlying operation (browser
  extension, CDP, Apple Events) may be stuck or unresponsive.`" — **without cancelling
  the call**, exactly as the reference only stops waiting for it.
  **Servers carry `instructions`, and they reach the model** (`Services/McpServerInstructions.cs`):
  the harness renders each under `## {server}` inside a `# MCP Server Instructions`
  section, following the agent roster and preceding the skill listing in the same
  mid-conversation system turn. The mechanism and both blocks are the **CLI's**, not the
  desktop's — the desktop bundle carries neither, and they sit in `claude.exe` 2.1.251
  (the chrome one at 187109115, computer use at 187110152). Its `Nln` decides which ride:
  `claude-in-chrome`'s only when tool search is available *and* some tool of that server
  is deferred (the block is entirely about loading deferred tools), `computer-use`'s
  unconditionally. Announced once per session, like the roster
  (`SkillSessionState.AnnouncedMcpInstructions`). **`# Unavailable MCP Tools` is fed**
  (`Core/Mcp/McpToolSchemas.cs`, the reference's `yat`/`ot`/`nt`/`x`/`Nr`/`Lr` around
  byte 208828602): every tool a server reports goes through its two passes — a top-level
  `anyOf`/`oneOf`/`allOf` is flattened into one object schema with the "Input constraint:"
  note prepended to the description, and the result is checked against its property-key
  rule `/^[a-zA-Z0-9_.-]{1,64}$/`. A tool that fails is dropped for a **repo**-scoped
  server (`McpServerConfig.Scope`, its `Ye` over `Fr`: the checked-in project file) and
  kept with a warning anywhere else, and `McpManager.DroppedToolEntries` renders the
  reference's own lines — `"{tool}" (MCP server "{server}"): "{reason}"`, or one counted
  line past its cap of 30 — which ride the block as a per-session delta. What is still
  not carried is the second half of its `Lr`, an Ajv-compiled draft 2020-12 meta-schema
  check: that is a state the reference itself ships, since when its `qr()` cannot build
  one it logs "tool schema checks fail open" and returns valid. The disconnected-servers
  notice stays declared, along with a *configured* server's own `instructions`, which
  this build does not read off `initialize` yet.
  Three things key off the shell rather than off a name prefix: the turn's deferral
  predicate (`InternalMcpServers.IsAlwaysLoad` — a tool is deferrable unless the server
  that composed it said otherwise), the transcript renderer (`ShortName`, the
  reference's own prefix normalisation, so a row is titled by what the tool does while a
  *configured* MCP server's tool still reads "Used {server}: {tool}"), and the tool-kind
  classifier that groups browser rows.
  **`claude-in-chrome` carries the reference's docs, not only its names.** All 19 tool
  descriptions and schemas are the reference's own (`index.chunk-C5__TEgr.js`), which
  also renames the argument every page-facing tool takes from `tab_id` to **`tabId`**
  and makes it *required* where the reference does (read_page, find, form_input,
  computer, get_page_text, the console/network readers, resize_window, gif_creator,
  file_upload). `tabs_context_mcp` gained the reference's `createIfEmpty`, which opens a
  tab rather than answering an empty list, and `navigate` its two runtime rules —
  a protocol-less url defaults to `https://`, and `back`/`forward` refuse without a
  `tabId`. Every renamed argument is still *read* under its old spelling so a stored
  session replays, and three of them were advertised without being read at all until
  this round: `read_console_messages`' `onlyErrors` and `clear` and
  `read_network_requests`' `urlPattern` and `clear` did nothing, and both `limit`s
  defaulted below the 100 their own docs promise. `clear` now reaches the extension,
  which empties the buffer it has just handed over. `navigate` did the opposite of what
  its doc says as well — it opened a **new** tab when no `tabId` was given, where the
  reference calls `tabs_context_mcp{createIfEmpty:true}`, navigates the group's **first**
  tab and appends that listing to the result; inside `browser_batch` a step without a
  `tabId` is refused instead, as the same doc promises. `upload_image` is carried now
  (`Services/CapturedImages.cs`): every screenshot the extension's `computer` takes is
  written and remembered under an id the result prints, which is what makes the
  reference's `imageId` contract resolvable. The id format (`img_1`, `img_2`, …) is this
  app's, since the reference prints none in a result this session could read.
  `resize_window` and
  `tabs_create_mcp` lost the extra arguments this port had added, since the reference
  declares neither and the Browser pane's own resize_window is where the preset contract
  lives. Two tools keep the reference's name over a different source and say so on
  themselves: `list_connected_browsers` answers its deviceId/platform/on-this-computer
  contract from a local pipe, and `select_browser` takes `deviceId`.
  **The four new servers** are real, not stubs: `terminal`'s read_terminal reads the
  ConPTY tile with the reference's buffer shaping (CRLF normalised, each line collapsed
  to whatever followed its last carriage return, last N lines with N clamped to
  [1, 1000] and defaulting to 200) and both its "not open" sentences. Its doc, its three
  property descriptions and its "not open" sentence were all rewritten in 1.44121.2.0 and
  this build follows; what it does **not** follow is that build's `offset_lines` / `grep`
  paging, which rides a gate (`hT("1923867086")`) that is off in the installed build — a
  live ccd session receives `lines` / `tab_id` / `wait_for_output_ms` and nothing else.
  `ccd_directory`
  grants a folder and moves the session at turn end, with the reference's check order
  and the guard that refuses when canonicalising moved the path the user approved;
  `ccd_session` backs spawn_task with a 20-chip queue (oldest evicted, six dismiss
  outcomes, the `task_[0-9a-f]{8}` id) rendered as chips above the composer that spin a
  suggestion into its own session, its `cwd` guarded by the reference's `Xr` — a UNC
  path, an automount root and a "."/".." segment are each refused by their own sentence
  before the path is touched, because the spawned session inherits it — and mark_chapter
  draws a titled divider
  (`ViewModels.ChapterItem`); `ccd_session_mgmt` lists, inspects, searches, reads,
  archives, renames and messages other sessions through the existing session store,
  carrying the reference's escaping verbatim — angle brackets (ASCII plus the fullwidth
  and small-form twins) become entities before another session's title or path reaches
  the model, and the cross-session envelope is attribute- and XML-escaped. Its rows are
  the reference's fields in the reference's key order — `prNumber`/`prState` in the list,
  `originCwd`/`worktreeName`/`scheduledTaskId`/`agent` in get_session — with `group`
  omitted until a window has reported its sidebar groups and `pinned` omitted until a pin
  has been recorded, since "unknown" and "no" are two different answers; and send_message
  refuses an unattended session (a scheduled-task run or a dispatched one) by name.
  `scheduled-tasks`
  is the desktop's SKILL.md-backed store (`Services/ScheduledTasks.cs`), distinct from the
  CLI's `CronCreate/CronList/CronDelete` this app also carries, with a real 5-field cron
  evaluator in local time (`Services/CronSchedule.cs`: lists, ranges, steps, day-of-week
  0-7 folding, and the Vixie rule that a restricted day-of-month *or* day-of-week fires)
  and one-shot `fireAt` runs, fired by RoutineRunner's minute tick — **delayed by the
  reference's dispatch jitter** (`Services/ScheduledTaskJitter.cs`, its
  `getJitterSecondsForTask`: none for a one-shot or ad-hoc task, otherwise
  `sha256(taskId)[0..4)` big-endian modulo `min(10min, firing-interval − 1min)`, which is
  the "small deterministic delay of several minutes at dispatch time" both scheduling
  tool docs promise and which this port used to omit along with the paragraph). The
  jitter also moves the `nextRunAt` list_scheduled_tasks reports, beside the reference's
  `jitterSeconds` field. Its refusals and result bodies are the reference's own, down to
  the duplicate-id and spent-one-time-task guards and the tool-approvals paragraph every
  create closes with. `mcp-registry` likewise answers in the reference's **JSON** —
  `{connectors, keywords, note}` with its two note sentences — rather than prose, and
  `search_mcp_registry` takes its `keywords` array and answers
  `{results:[{name, description, tools, url, iconUrl, directoryUuid, connected,
  enabledInChat}]}` capped at the reference's ten rows. The registry underneath is still
  the public one (registry.modelcontextprotocol.io) rather than the account's connector
  directory, so the two fields that directory has and this one does not — a server's tool
  list and its icon — come back empty rather than invented. It used to be registered
  twice, bare as well as under this server, which is two tools with one name and no way
  to tell which the model reached; the bare name survives as this server's alias so a
  stored session still replays.
  **`visualize` is the tenth server, and the only one that serves a resource**
  (`Services/VisualizeTools.cs`, `Services/VisualizeCorpus.cs`): `read_me` hands the model
  the design corpus its widgets are written against and `show_widget` renders one inline
  in the transcript. The corpus is the reference's own bytes — the chunk holds every
  section as a string literal and composes them in a pure function, so
  `Captures/Mcp/gen-visualize-corpus.js` runs that function's code and writes the sections
  out under `Assets/Visualize/` — and the rule over them is measured, not guessed: `base`,
  then the sections of every requested module **deduped in first-seen order**, then
  `footer`, joined by a blank line, with the platform picking a width first (`mobile` 380,
  everything else including an unrecognised name 680) and the width picking three of the
  sections, because a narrow widget is told different things rather than the same things
  with a different number in them. `VisualizeCorpusParityTests` reproduces all **24** of
  the reference's own recorded `read_me` answers by sha256 through
  `VisualizeCorpus.ReadMe`, which is what makes the composition a check rather than a
  claim. `show_widget` renders nothing itself: it answers the reference's one sentence and
  the widget is drawn from the call's arguments. Both tools carry `readOnlyHint`, so
  neither asks; both ride the server's `alwaysLoad`, so neither is deferred.
  **The shell learned resources for it** (`InternalMcpServerDefinition.Resources` /
  `ReadResource`, `InternalMcpServers.ListResources` / `ReadResource`): the reference's
  `handleListResources` answers one entry — `ui://imagine/show-widget.html`, name
  `visualize widget`, mime `text/html;profile=mcp-app` — and `handleReadResource` the
  widget runtime with the `_meta.ui` block carrying its CSP domain lists and its
  `clipboardWrite` permission. The shapes mirror Core's `Core/Mcp/McpClient.cs` rather
  than inventing new ones; the same `isEnabled` that removes a server's tools removes its
  resources.
  Deliberate deltas, each with its reason in the manifest: `upload_image` needs
  a captured-image registry that does not exist here; `switch_browser` and the two
  `shortcuts_*` need the reference extension's side panel; the `app_*` family and the
  full-control pair are macOS; and `screenshot` stays **bare** as this port's own, because
  the reference has no standalone screenshot tool at all — every capture rides
  `computer_batch`.
- **The widget surface** (`Controls/WidgetBlock.cs`, `Services/VisualizeWidgetPage.cs`,
  `Services/VisualizeWidgetStrings.cs`, `Services/VisualizeWidgetCalls.cs`): what
  `show_widget` actually draws. The reference's MCP-app tool row **is** the widget (its
  `tO` in ion-dist `c360a9e1c-DUoNQd2W.js`), so this transcript replaces the call's row
  with one rather than putting a widget beside a "Used visualize: show_widget" line: one
  button carrying the label, the tool's short name in the code face and a caret, over the
  widget itself. The label is "Rendering widget" with a shimmer while the call runs or the
  page has not answered the handshake and "Widget from {server}" afterwards — the
  reference's own `ge = me || !U` — and its geometry is its tokens (`gap-g2` 3, `mt-p3` 4,
  `rounded-r6` 8, and the `p-p6` 8 it applies only to a built-in server's visualize widget,
  its `isBuiltIn && Il(name)`). The row opens expanded, as its `useState(true)` does, and
  the body is kept at zero height with no border until the page initializes so nothing
  flashes.
  **The page is the reference's own runtime, hosted the way its `AppRenderer` hosts one**
  (ion-dist `cd9b7fccf-BIkz8mbE.js`): it loads the resource into a sandbox proxy on a
  separate origin and speaks MCP Apps JSON-RPC to it over `postMessage`. Here the proxy is
  a session of the app's own engine — `VisualizeWidgetPage.WrapperHtml` is that proxy, the
  runtime goes into its iframe under `VisualizeWidgetPage.ContentSecurityPolicy`
  (connectDomains → `connect-src`, resourceDomains → `img-src`/`script-src`/`style-src`/
  `font-src`/`media-src` plus the reference's own appended `https://assets.claude.ai`,
  `frame-src 'none'`, `base-uri 'self'`), and the iframe is sandboxed **without**
  `allow-same-origin`, so the widget cannot reach the host channel through its parent.
  That channel is the engine's rather than a browser control's: the proxy answers through
  a **DevTools binding** added before it loads (`Runtime.addBinding`, arriving as
  `Runtime.bindingCalled`) and the host answers back with one `Runtime.evaluate` of the
  single function the proxy exposes — the arrangement the diagram renderer already uses
  since `window.chrome.webview` went away with WebView2. `ui/initialize` is answered with the host
  context the page themes itself from, `ui/notifications/tool-input` carries the call's
  arguments, `ui/notifications/size-changed` sets the row's height,
  `ui/update-model-context` is what `read_widget_context` reads back, `ui/download-file` is
  refused as the reference refuses it, and `ui/message` — the global `sendPrompt(text)` —
  **prefills the composer** rather than sending, which is what the reference's ccd row does
  with its `onPrefillComposer`. `ui/open-link` follows only https and asks first
  (`Services/WidgetLinkPrompt.cs`, its external-link dialog: the address, and its two
  warnings for a punycode label and for embedded credentials). The tile takes an
  engine session of its own like the other embedded surfaces, and unlike the diagram block
  it keeps that window in the tree rather than handing back a bitmap — a diagram is a
  picture and can be drawn offscreen, and this is a live page with buttons and forms.
  Two things had to be true for that window to sit inside a card: the engine lays its
  view out from the window's own content size and has to be asked to do it again after
  the window is moved from this side (`ElectronPaneSession.LayoutAsync`, on the view's
  own SizeChanged, which runs after `HwndHost`'s), and a frameless Electron window keeps
  a resize border in its window rect that reparenting does not take away — so
  `Controls/ElectronPaneView.cs` sizes the window by `window rect − client rect` rather
  than to the container, which is what stops an 8px band of the container showing around
  every embedded page. `--open=widget` poses it; a `RenderTargetBitmap` cannot
  capture an engine window, so that pose is checked with a real screen grab.
- **The CLI's own built-in-MCP surface** (`Core/Mcp/McpBuiltInServers.cs`,
  `Core/Mcp/McpAutoBackground.cs`): the other half of the shell above. The desktop
  *hosts* those ten servers; the CLI **receives** them over its `sdk` transport, labels
  them `sdk_host_builtin_mcp`, and carries rules of its own that key off the fact that a
  server is the harness's. Measured from CLI 2.1.251 (`chunk-fp51h7wf.js`,
  `chunk-0v0qa21m.js`) and identical in the 2.1.247 build the desktop runs.
  **Two normalizations, deliberately different.** `ln` is the wire one — everything
  outside `[a-zA-Z0-9_-]` becomes `_`, hyphens survive, a `claude.ai ` connector
  additionally has its underscore runs collapsed and trimmed — and `mcp__{ln(server)}__{ln(tool)}`
  (its `xc`) is now what `McpToolAdapter` builds, **with no length cap**: the 100-char
  truncation this port used to apply could collapse two long tool names onto one wire
  name, and `DistinctBy` then dropped one of them silently. `DP` is the looser one used
  by the inventory test: it also folds case and hyphens.
  **The inventory is extracted, not pinned.** `ptr`'s three lists — two names that are
  internal whatever else is configured (`ide`, `remote-devices`), eight matched whole
  (`workspace terminal office visualize window_halo dev_debug ccd_session ccd_session_mgmt`)
  and sixteen matched by prefix (`claude_in_chrome claude_browser claude_preview
  claude_code_ios_simulator claude_code_android_emulator computer_use framebuffer plugins
  skills mcp_registry scheduled_tasks cowork session_info dispatch remote_devices
  ccd_directory`) — are read back out of the installed binary by
  `McpBuiltInSurfaceParityTests`, so a release that adds a name lands as one failure
  naming it. Its second clause is live: a **configured server of the same folded name
  takes the name back**, and `InternalMcpServers.Compose` drops the shell server rather
  than letting both compose `mcp__terminal__…` and one vanish into `DistinctBy`. The
  reference's own consumer of `ptr` is its Artifact capability-manifest validator, which
  this app does not have; the inventory is carried for the name rules, and the nine names
  with nothing behind them here are declared in `Deltas/reference-surface-deltas.tsv`.
  **The `ide` server is filtered, not hosted.** The reference publishes exactly two of
  its tools to the model (`mcp__ide__executeCode`, `mcp__ide__getDiagnostics`) and calls
  `openDiff` / `close_tab` / `closeAllDiffTabs` itself; `McpManager` applies that filter,
  so a manually configured `ide` server gets the reference's surface. Its `sse-ide` /
  `ws-ide` transports and the editor integration behind them are declared, not built.
  **A slow MCP call stops holding the turn open** (`getMcpAutoBackgroundMs` /
  `callMcpToolWithAutoBackground`): past **120s** — `CLAUDE_CODE_MCP_AUTO_BACKGROUND_MS`
  overrides, a print run needs `CLAUDE_AUTO_BACKGROUND_TASKS`, an IDE transport and a
  subagent never move — the call is **unlinked from the turn's cancellation first** and
  adopted by `BackgroundTaskManager`, the model gets the reference's notice verbatim
  ("MCP tool "{server}/{tool}" is still running after {N}s… To stop it, use TaskStop with
  task_id …"), and the result arrives later as a task notification with the reference's
  own two statuses. A call blocked on an **elicitation** is not late but waiting on the
  user, and is left on the turn — the reference's own guard.
  **Two more rules ride the same seam.** The permission floor (`kx`): `Claude_Browser`
  and `Claude_Preview` never take Bypass mode's blanket approval, because bypass is a
  statement about this machine and not consent to drive a browser signed in to the user's
  accounts — the extension stays on the ordinary policy, which is the reference with its
  `chromeClassifierFloorEnabled` gate off, and `mcpPermissionModeOverrides` is declared
  rather than built (both of its accepted values collapse onto this app's one mode). And
  an upload may only read what the session may read: `file_upload` validates every path
  against the workspace and its added directories and refuses in the reference's words
  ("Cannot upload "…": only files this session is allowed to read can be uploaded…").
  **A tool's four `_meta` annotations are read** (`McpToolDescriptor`): the reference's
  `anthropic/alwaysLoad` exempts it from deferral, `anthropic/searchHint` gives ToolSearch
  a phrase to score after the name, `anthropic/maxResultSizeChars` sets the size past
  which its result is persisted rather than sent (capped, like the reference's own
  declarations, at 50,000), and `anthropic/requiresUserInteraction` keeps a call that is
  waiting on a person from being moved to the background for taking too long. A server may
  set its own request `timeout` — with
  `request_timeout_ms` folding into the same field, capped at the reference's 300s and
  only when no explicit timeout was given.
  **A remote entry may mint its own headers** (`Core/Mcp/McpHeadersHelper.cs`): the
  reference's `headersHelper` is a command that prints a JSON object of headers, usually a
  short-lived credential, and its output overlays the entry's static `headers` (its `Sat`)
  and takes the Authorization slot from the stored OAuth token. The runner is its `F_t` —
  a shell, a 10s timeout, a 1 MB output cap — and its four ways of being wrong carry the
  reference's own sentences (`did not return a valid value` / `did not return valid JSON` /
  `must return a JSON object with string key-value pairs` / `returned a non-string header
  value`), with `CLAUDE_CODE_MCP_SERVER_NAME` and `CLAUDE_CODE_MCP_SERVER_URL` in its
  environment. The reference gates it on workspace trust, refusing to run a helper declared
  in an untrusted repository's settings; this app's MCP config has no such tiers, so the
  command is **asked about instead** — once per server per session, again whenever the
  command text changes, and refused outright in a headless run, which is the decision a
  hook command already gets. That prompt is declared as this build's own.
  **A remote entry's discovery listing is cached between runs**
  (`Core/Mcp/McpDiscoveryCache.cs`): `discoveryCache` is an **opt-out**, not the opt-in its
  name suggests — the reference's eligibility table refuses on exactly `false` and admits
  every other value — and separately refuses a server carrying a `headersHelper`, a
  transport that is not http or sse, or an unexpanded `${…}` in its url or headers. What is
  cached is `tools/list`, `prompts/list` and `resources/list`, keyed by the server's own
  normalized config with those two eligibility-only properties removed (its `Ge`), under
  the reference's ladder: the strike threshold, its clock-skew guard, the stale window and
  the TTL capped by it, with `MCP_DISCOVERY_CACHE_TTL_S`, `MCP_DISCOVERY_CACHE_MAX_STALE_S`
  and `MCP_DISCOVERY_CACHE_STRIKES` honoured under their own names, over an 8 MiB entry
  cap. Two pieces are declared rather than carried: its entries are sealed with AES-256-GCM
  under a key derived from the account token and the server's OAuth refresh token and filed
  by that account identity, which there is nothing here to derive — and an entry holds tool
  schemas rather than a credential — and it serves a stale entry while refreshing behind
  the turn, where this port lists live and falls back to the stale entry only when that
  listing fails, taking the same strike.
  Deliberately declared rather than carried, with reasons
  in the manifest: the `sdk` / `ws` / `claudeai-proxy` / IDE transports,
  `role`, and `toolPermissions` / `tools[].permission_policy` — which
  are **not** local knobs but an organization policy arriving with a managed server — plus
  the three per-server name allowlists, which are telemetry-safe name lists rather than
  tool surfaces (reading computer-use's 42 as its tool list is the misreading they invite).
- **Browser pane, drivable** (`mcp__Claude_Browser__*`): the right-side Browser panel is
  itself a model surface, at parity with the reference desktop's in-app Browser pane and
  distinct from the `browser_*` extension family — the system prompt carries the reference's
  `<browser_surfaces>` note pointing the model at the pane by default.
  `Services/BrowserPaneTools.cs` registers the reference suite under its exact wire names
  (navigate, computer, read_page, find, get_page_text, form_input, javascript_tool,
  read_console_messages, read_network_requests, resize_window, tabs_context/create/select/
  close, browser_batch) with the reference tool docs and schemas; `TurnContextFactory`'s
  `mcp__*` deferral predicate skips the prefix (they are app tools, not MCP-server tools).
  The implementation is a **byte-level port of the installed desktop's** (mined from
  app.asar 1.40609.0.0 and verified against its live pane): `Services/BrowserPaneScripts.cs`
  embeds the reference's own content script VERBATIM (`__claudeElementMap` string refs, the
  reference role/name maps, password/OTP/credit-card **value redaction**, select options as
  children, href/type/placeholder attributes, the viewport filter, 10k node cap, and the
  YAML tree built in the page) plus its ref-to-point, form_input and get_page_text page
  halves, and `Services/BrowserPaneHandlers.cs` ports the handler layer with the exact
  result and error strings — read_page "(empty page)"/"Viewport: WxH", find
  "Found N match(es)", get_page_text's Title/URL/Source-element header, "filled ref_N with
  value", clicks echoing "left_click at (x, y) [ref_N]", the **screenshot coordinate-frame
  cache** (coordinates are pixels in the most recent screenshot's full-resolution frame,
  scaled to the live viewport at dispatch; "outside the coordinate frame" / "requires a
  prior computer{action:\"screenshot\"}" refusals), **JPEG** screenshots with "Screenshot
  size:" and the "-scale view; coordinate frame:" note, zoom as full screenshot (region
  crop unsupported, like the reference), key as whitespace-token sequences dispatched by
  DOM key name, resize_window's exact sentences, tabs_context's indented JSON +
  displayed/hidden line, and the **Tab Context trailer** on every page-facing result.
  browser_batch is the reference's validator/executor (25-action cap, prefix-normalized
  names, "[name:action]" success lines, "actions[i] (label) failed: … (N completed, M
  remaining)" with collected images dropped). What a step may name is the reference's
  own `$Tr` — the ten page tools and nothing else, so a `preview_*` or a `tabs_*` inside
  a batch is refused by name rather than run; and the run carries its `budgetMs` of
  180s, checked from the second step on, which hands the rest back with its sentence
  ("Stopped after N of M actions (time budget for one call)…") instead of running past
  it. Each call is raced against the reference's `QEr` — 30s, or 45s for
  javascript_tool, since that one waits on the page's own code — answered with its
  sentence and, like the reference, only stopping the wait rather than tearing the call
  down. `preview_logs` filters `level: "error"` on the four words its doc names
  (error/exception/failed/fatal; a warning is not one of them) and defaults and clamps
  to the 200 lines the reference's handler uses, where its doc says 50.
  **A closed pane is answered, not driven** (`BrowserPaneHandlers.ClosedPaneAnswerAsync`,
  the reference's guard ahead of its dispatch): with no pane open, every tool answers
  "No preview is open. Use `preview_start` or `navigate` with {"url": …} to open a browser
  tab at a URL, or `preview_start` with {"name": …} to start a dev server from …" instead
  of running — `navigate` carrying a url is the one exception, because opening the pane is
  what it does, and a history move cannot open one (the reference's own lEr rejects
  back/forward). A `browser_batch` **step** may not open the pane either and gets the
  reference's dedicated line ("…a `browser_batch` step can't open it. Call `navigate` with
  this url on its own…"); the batch itself is never guarded, so its steps answer one at a
  time. tabs_context and tabs_create answer with their own text and are **not** errors —
  the empty `{browserOpen: false, tabs: []}` listing and "No tab was created. …". Every one
  of those sentences is byte-compared against the installed bundle; the single deliberate
  edit is the launch.json it names, which is the `.jarvis/launch.json` our own
  preview_start reads. The reference also tags these results `pane_not_open` in `_meta`
  and then **deletes that field** before the result is sent (its `$On` strips errorClass,
  paneVisibility and appWindowState into a telemetry event), so the tag has no reader here
  and is deliberately not carried.
  **A hidden pane is reaped after five minutes** (`BrowserPanel.HiddenReapDelay`, the
  reference's `RNn = 300000`), and only a pane something can rebuild: the reference skips
  arming whenever the view is not a Claude page, has no dev-server port and holds no
  exportable content, so a plain browser tab the user hid is left alone however long it
  stays hidden and only a preview pane is released. The countdown is cancelled the moment
  the pane comes back, and re-checks that it is still hidden before releasing it
  (`ShouldArmHiddenReap` / `ShouldReapNow`, both unit tested). The **Tab Context trailer is the pane's own**
  (`Services/BrowserPaneTrailer.cs`, unit tested): the reference's `hQ` names only the tab
  the call ran on — the all-tabs listing in the same bundle belongs to the *extension*
  bridge — with its origin label (http(s) origin, a leading "www." dropped from three or
  more labels but never from a .local name, the bare scheme otherwise, "(no page)" and
  "page content" as the two fallbacks) and the reference's blocked-media Note. Camera and
  microphone are refused outright in the pane, as they are in the reference, the refused
  kinds accumulate per tab for that Note, and the user gets the reference's own toast.
  A **clipboard guard** rides the four input-dispatching primitives (the reference's FOn
  set): `Services/ClipboardWatch.cs` takes Windows' clipboard sequence number around each
  dispatch — which answers "did it change" without opening the clipboard — and a change
  becomes the reference's trailer Note plus its toast, one-shot and retired on the next
  committed navigation. Deliberately partial: the reference picks between three sentences
  by classifying the dispatch's activation origin (page or user), which Electron exposes
  and this engine does not, so only its unattributed sentence is emitted — the one it uses
  when it cannot tell either — and the two accusing variants stay unported. **Refs are main-frame-only `ref_N`, exactly
  like the reference: read_page never traverses iframes** (measured live — neither
  same-origin nor cross-origin iframe content appears in the reference's tree), and
  embedded content is reached by screenshot coordinates; the `ref_fNrM` iframe machinery
  remains extension-only. The pane has **real tabs**: each owns its engine view, switching
  swaps visibility so background tabs keep running, tabs close by × or middle-click,
  closing the last tab closes the pane, and `target=_blank` lands in a new pane tab
  (`NewWindowRequested`) or the system browser when "Open links in Browser panel" is off.
  Driving is CDP over `CallDevToolsProtocolMethodAsync` (`Services/BrowserPaneCdp.cs`:
  reference-shaped input events, `Runtime.evaluate` with the repl/callFunctionOn dance and
  the top-level-return retry, viewport+Android-UA+touch emulation at deviceScaleFactor 2
  scaled to fit the pane) with console/network capture always-on per tab
  (`Services/BrowserPaneCapture.cs`, buffers cleared on main-frame navigation).
  `Services/BrowserPaneDriver.cs` marshals every call onto the UI thread, and navigate/
  tabs_create open the pane first: an engine view outside the visible tree never finishes
  initializing. The pane re-syncs each tab's color-scheme emulation to the app theme on
  theme change and pane reopen (resize_window's documented contract). Deliberate deltas
  from the reference: no press-attribution system (our permission gate covers tool calls),
  no serverId plumbing in tab results, and not the pane's own per-*origin* card
  (`requestPreviewOriginPermission`, which is live in the reference and is what fills its
  `launchPreviewAllowedOrigins`) — here that list is only edited in Settings, which is
  what makes the transition gate below rare rather than routine.
  **Moving the pane to another site is consented to first**
  (`Services/BrowserPaneDomainTransitions.cs`, its `requestPreviewDomainTransition` /
  `vJn` over the sentences its navigate handler `msr` maps the outcomes onto): a tab
  remembers the last ordinary web origin it committed — a dev server or a local file
  never becomes one, and going past one leaves the previous value standing, which is the
  reference's own `lastExternalCommittedOrigin` — and a `navigate` that would take it to
  a *different* origin is put through the gate before it moves, as is a `back`/`forward`
  whose entry would (the reference resolves that entry first, so the engine grew a
  `tab.historyTarget` peek beside its `tab.history` move). Six outcomes, five sentences,
  all verbatim: `not-required` and `allowed` navigate, `denied` answers "The user
  declined this domain transition…", `suppressed` the repeatedly-declined sentence,
  `retry` the one naming a card already up or a tab that moved during the prompt, and
  `refused` the "cannot be shown in this context" one, which is what a `browser_batch`
  step, a subagent or a headless run would get.
  **What the gate answers is what the installed build answers: allowed, always.** Its
  `RequestAsync` mirrors the reference's `BZt` — one line, no card, whatever it is
  given — so this pane asks nothing, exactly as 1.44121.2.0 asks nothing. The decision
  those six outcomes describe is `DecideAsync` beside it, kept and covered because that
  is how the reference ships it too: everything around the stub is live code, and
  re-enabling the card is the one method calling the other. Whether a transition is considered at all is the
  reference's `$G`: both origins have to be ones the pane may already act on, which is
  Settings › Jarvis Code › Browser › Allowed sites (its `launchPreviewAllowedOrigins`),
  an https origin covered by a grant of its http twin and a loopback host never granted.
  An allowed pair is remembered under the reference's own `{src}→{dest}` key and not
  asked about again; three declines of one pair, or nine across the session, suppress the
  card (the live sibling prompt's `IZt`/`LZt`); clearing the pane's browsing data clears
  both. **The installed build ships this decision stubbed** — its `DQt` returns null and
  its `BZt` returns "allowed", and its card-handler setter `uZt` is an empty
  function — so 1.44121.2.0 never asks and the five sentences are unreachable there;
  what a transition *is* was therefore read from the live machinery around the stub. The
  persisted `launchPreviewAllowedDomainTransitions` half this port does not carry is
  declared in `Deltas/reference-surface-deltas.tsv`.
  **A tab that hosts a running dev server is not closed silently**
  (`Views/Panels/BrowserCloseDialog.cs` + `Services/BrowserCloseDialogText.cs`, the
  reference's ES): closing it - or "Close other tabs" - asks "Close {host}?" or "Close
  other tabs?" over one of its three bodies (the plural one names the count, the singular
  ones differ only in "this tab" against "a tab"), with Cancel on the left and the danger
  Stop server(s) beside the primary "Keep running" on the right, which takes the focus. A
  server no other tab is showing is what counts as orphaned; nothing is asked when the tab
  hosts none. **The toolbar's pencil is the reference's annotation editor**
  (`Views/Panels/PreviewAnnotationOverlay.cs`): the page is captured as it stands and drawn
  on with its five tools (Freehand pen, Line, Rectangle, Ellipse, Text), its four ink
  colours (#E03131 #1971C2 #2F9E44 #1F1E1D) and its 4px stroke, with Clear, Cancel and Save;
  Save writes a PNG the composer attaches as an image, and closing with strokes on it raises
  the reference's "Discard your annotations?" question.
  Pane chrome at reference parity: the ⋮ menu (Save screenshot, Show dev
  server logs, Color scheme Light/Dark/System via `Profile.PreferredColorScheme`, Viewport
  Responsive/Mobile/Tablet, Open links in Browser panel, Persist sessions — off is the
  reference default and scrubs the profile at next launch — Enable/Disable auto verify,
  Clear browsing data behind a confirm card), the detect-dev-server flow ("Reading your
  project files…" → `Services/DevServerDetector.cs`, a **deliberate local scan** of
  package.json scripts+deps / dotnet launchSettings / manage.py instead of the reference's
  model call → "Use this" writes `.jarvis/launch.json` → "Setting up preview" polls the
  URL), the dev-server error state ("Dev server failed to start" · Copy error log · Try
  again · Back to browsing, with the log tail delivered as a task notification — "Error
  details were sent to Claude", wired from `BackgroundTaskManager.TaskExited`), and Preview
  HTML (file:// pick). **Auto verify** lives in launch.json (`autoVerify`, where the
  reference stores it too, and on unless the file says false — its own default for a
  launch.json that parses) and is the reference's two-piece mechanism rather than a
  reminder (`Services/PreviewVerification.cs`): while it is on, the Code system prompt
  carries the `<preview_tools>` block verbatim — `<when_to_verify>` plus the eight-step
  `<verification_workflow>` whose loop is "read source code to diagnose, edit source files
  to fix, then re-check from step 3" and whose last step is sharing proof — and a pair of
  built-in **function hooks** does the nudging: after the first Edit/Write of a previewable
  extension (`.js .jsx .ts .tsx .vue .svelte .astro .css .scss .less .html .htm`) a
  PostToolUse hook asks for verification **once per turn** (the reference's
  `after_first_write`), wording picked from whether a dev server runs here, in another
  chat, or nowhere; the Stop hook arms it again, a file the pane can show directly is
  shown instead ("{path} is now visible in the Browser pane."), and an approved plan's own
  `## Verification` / `## Test Plan` / `## Testing` section is read back out of the plan
  and handed to the implementation. Three pieces of the reference's are deliberately not
  carried: the second wording of `<verification_workflow>` for its older
  preview_snapshot/preview_click toolset (this app has only the pane), the paragraph an
  organization administrator's policy adds about non-localhost pages (no managed policy
  here), and the launch.json path, which reads `.jarvis/launch.json` — the same swap the
  pane's closed-pane answer already makes. Stop hooks run on the desktop surface only, so
  a goal is a desktop feature; the CLI has never run them. Cookie import from the user's browser is deliberately
  absent (credential handling; Chrome's app-bound cookie encryption blocks it regardless).
- **Tool names are the reference's** (`ShellTool`, `ReferenceToolDocs`, `ToolNames`,
  `SkillPermissionRules`): the model sees `Read` / `Write` / `Edit` / `Glob` / `Grep` /
  `Agent` / `Skill` / `Workflow` / `NotebookEdit` / `Monitor` / `WebFetch` / `WebSearch` /
  `TaskOutput` / `TaskStop` / `SendMessage` / `Cron*` / `*Worktree` / `ListAgents` /
  `ReportFindings` / `ScheduleWakeup` / `Task*`, not the snake_case names this port used
  to send — a capture used to share **none** of its 27 tool names with the reference and
  now shares every name the CLI registers for a tool this app also has; the exceptions are
  declared below rather than left to be discovered. Two shells, as the reference ships on
  Windows: `ShellTool` runs powershell.exe and is named **PowerShell**, and a second
  instance (`ShellKind.Bash`) runs Git Bash as **Bash**, which is what lets the prompt's
  Shell line be verbatim. Deliberately *not* renamed: the five `git_*` tools and
  `list_directory` / `memory` / `read_document` / `todo_write` / `config` (this app's own,
  with no reference counterpart — the reference no longer ships TodoWrite at all), and the
  `browser_*` / computer-use families and `android_logcat`, which the reference exposes
  under `mcp__*` names instead or (for logcat) does not have at all. Three spellings that look like tool names and are not, each
  guarded in the rename: Anthropic's server-side `web_search` tool (the API fixes that
  name), the `shell` skill-frontmatter key, and the `shell` app-grant category.
  Missing against the reference, each with its reason in the parity suite's
  `Deltas/reference-surface-deltas.tsv`: `DesignSync`, `PushNotification` and
  `RemoteTrigger` ("Manage scheduled remote Claude Code agents (routines) via the claude.ai
  CCR API"), all three cloud. One name is carried and means something else — the reference's `Artifact`
  publishes a page to claude.ai and reads it back, while this app's `artifact` renders into
  its own engine tile, so it deliberately keeps the lowercase name rather than claim
  semantics it does not have. Five more used to share a name and not a source, and no longer
  do: the reference's `ListSkills` / `SearchSkills` / `ListPlugins` / `SearchPlugins` /
  `SuggestSkills` query the user's claude.ai catalog through `session.host` with the account
  credentials, where this app's read the local skill directories and the installed
  marketplaces. That is the kind of difference nothing looks wrong about until somebody
  assumes the behaviour follows the name, so this build's own are named apart —
  `list_local_skills`, `search_local_skills`, `list_local_plugins`, `search_local_plugins`
  and `suggest_local_skills`, snake_case like every other tool of its own — and the five
  reference names survive only as aliases, resolvable so a stored session still replays and
  never advertised. The reference's own five are declared not carried, because the account
  they read is not here.
- **Tool docs and schemas are the reference's bytes** (`Services/CapturedToolDocs.cs`,
  generated; `Services/ReferenceToolDocs.cs` applies it): measured on CLI 2.1.257 by
  capturing this build's own CLI at the listener and diffing its request tool by tool
  against the reference's. Every reference tool's description and input schema ride
  verbatim, the description in its **lean or classic form** after the model's prompt
  form — nine tools have two docs (Agent, Bash, Edit, Glob, Grep, Read, WebFetch,
  WebSearch, Write), the rest one — and two are built per model: **Agent** (its "Reach for
  this…" sentence follows the delegation stance) and **Bash**, whose commit trailer names
  the model (`Services/CommitTrailers.cs`: "Co-Authored-By: Claude Opus 5", "Claude Fable
  5.1", "Claude Opus 4.5"…) and whose dedicated-tools bullet the fable-5-1 bundle drops. A
  captured schema stands in only where the tool accepts every argument the reference
  declares — so the Core tools took the reference's arguments: Grep's ripgrep set (`glob`,
  `type`, `-i`, `-n`, `-o`, `-A`/`-B`/`-C`, `head_limit`, `offset`; the old names still
  read), **including its `output_mode` default of `files_with_matches`** — the schema this
  tool advertises is the reference's and says so, and an absent mode used to answer
  `content` instead, which is the one thing a tool doc must never do: tell the model one
  contract and honour another, on every Grep call that omits the argument. Read's `pages` (a PDF is refused by name — no renderer here), Skill's `skill`,
  TaskOutput's `block`/`timeout`, TaskStop's `shell_id`, WebSearch's
  `allowed_domains`/`blocked_domains`, Bash's `timeout`/`dangerouslyDisableSandbox`,
  CronCreate's `cron`/`prompt`/`recurring`/`durable` on the routine store (a 5-field cron
  evaluated by `Services/CronSchedule.cs`, one-shots deleted after firing, 7-day expiry,
  non-durable jobs owned by the session and swept at the next start), EnterWorktree's
  `path` and the reference's name rule, ExitWorktree's `action`/`discard_changes` (remove
  refuses and lists uncommitted files and unmerged commits), Workflow's ignored
  `description`/`title`, and SendMessage's `notify_when_idle` — a one-shot subscription on
  the other local session's view model that delivers the reference's own
  "[Cross-session idle notice]" line when that session finishes a turn with nothing
  queued, with its refusals for a subagent, a teammate, a worker target and the session
  itself. Agent keeps its own schema (`name` and `cwd` beyond the reference's six) and
  says so in the manifest.
  **The Agent doc has a third form, for the fork agent** (`Services/ForkAgentDoc.cs` over
  `Core/Agent/ForkAgent.cs`): the reference's `subagent_type: "fork"` spawns a child that
  inherits the parent's whole conversation and runs on the parent's model, and its own `y`
  rewrites nine places of the doc at once rather than adding a section — the head sentence,
  the SendMessage bullet's clause, the background bullet (replaced by the fork note),
  "## When not to use" (gone from the classic form), the "Don't race" bullet (gone), two
  sentences of "Writing the prompt", the added "## When to fork" and a different set of
  examples — so both forms are carried whole. The gate is not a rollout flag: its `VAo`
  answers "disabled" for a **non-interactive** session and "default" otherwise, with
  `CLAUDE_CODE_FORK_SUBAGENT` forcing it either way and coordinator mode taking it away.
  That is why the branch is in no `-p` capture and why a live desktop session carries none
  of it — the desktop hosts the CLI over the SDK, which is not interactive — so this port
  resolves it false on its own window and true in the CLI's REPL, the same answer the
  reference gives on both surfaces. Carried with it: the `<fork-boilerplate>` preamble and
  the `Your directive: ` line, the worktree-inheritance sentence, the two refusals (remote
  isolation, a fork inside a fork), the 200-turn cap its definition declares, the ignored
  `model` override, and the conversation its `gKn` builds — the parent's history through
  the assistant message carrying the call, then a user turn answering **every** tool_use in
  it with "Fork started — processing in background" and carrying the directive. The
  reference's roster deliberately does **not** list it (the roster is built from
  `M8e(activeAgents)`, and `XRn` refuses when `fork` is among those), so neither does this
  one; its `whenToUse` sentence surfaces only in the "Available agents" list of a
  missing-subagent_type error. Two additive Core channels carry it:
  `ToolExecutionContext.ConversationSnapshot`, filled by the orchestrator with the turn's
  messages at the call, and `SubagentServices.ForkEnabled`/`ParentSystemPrompt`. Every schema of this build's own is shaped the way the
  reference's zod output is — `$schema` first, description-first properties,
  `additionalProperties: false` last — tools are advertised in **ordinal name order**, and
  a print run drops AskUserQuestion, Monitor and the plan-mode tools, as the reference's
  `-p` list does. `ToolDocsParityTests` pins the buildable tools' docs and schemas against
  `Captures/Tools/tools-cli-2.1.257.json`, and the fixture is the generator's other output.
- **Output styles** (`Services/OutputStyles.cs`): the reference's four built-ins — **Proactive**,
  **Concise**, **Explanatory**, **Learning** — measured out of CLI 2.1.247, where they sit in one
  table with `name`, `description`, `keepCodingInstructions`, `prompt` and an optional
  `turnReminder`. All four set `keepCodingInstructions`, so a style is **appended** to the harness
  prompt rather than replacing it, and that is the only mode this port implements. The prompt
  bodies are verbatim with their interpolated tails resolved; the descriptions name the assistant
  to the *user* in the picker, so those say Jarvis. Proactive and Concise also carry a reminder
  that rides **every** message (`SystemReminders.Attach`), which is how the reference repeats them
  rather than stating them once. `/output-style` lists and switches; the choice is global
  (`UiSettings.OutputStyle`), which is where the reference keeps it too.
- **Reference harness prompt** (`Services/ReferencePromptBuilder.cs`): Code turns run on a
  port of the harness prompt the installed reference **actually sends**, measured by driving
  CLI 2.1.257 (and the desktop's bundled 2.1.255) at a local listener with a cleaned
  environment. **The lean prompt is a per-model section list**, not one document
  (`Services/PromptModelProfile.cs` holds the predicates, read off the build's own
  `fable_5_1_prompt_bundle` / `opus_5_prompt_bundle` / `fable_5_mitigations` gates):
  every lean model gets the intro, the security policy, **# Harness**, the pronouns rule,
  the hard-to-reverse paragraph, **# Session-specific guidance**, **# Memory**,
  **# Environment**, **# Context management** and the "act when informed" paragraph;
  opus-5 adds **# Delivering work**, **# Corrections** and the reduced-delegation line
  ("Do not use the Agent tool, workflows, or deep-research unless…"); fable-5-1 adds its
  own identity paragraph, **# Writing for the user** and the autonomy append; fable-5 the
  identity and the append; opus-4-8 nothing beyond the skeleton. The knowledge-cutoff line
  is the reference's per-model table and is absent for a model it has no date for. The
  identity sentence is **its own cached system block** ahead of the prompt (the reference's
  shape: billing header, identity, then for fable/mythos 5.1 the uncached
  **# Reporting outcomes** block, then the prompt), and the `<total_tokens>` budget block
  closes the prompt (`Core/Agent/TotalTokensReminder.cs`: the reference's five modes, its
  roll-over and re-anchor arithmetic, a live block after tool results, a fresh budget per
  subagent). Four 2.1.257 recordings pin the four lean forms. **# Scratchpad Directory**
  rides between Environment and Context management on the desktop only, naming a real
  per-session directory (`Services/SessionScratchpad.cs`, `%TEMP%\jarvis\{project}\{session}\
  scratchpad`, created eagerly because the block says it already exists) — a CLI capture,
  with the desktop entrypoint too, carries neither it nor any host append, so the headless
  front-end sends none. The Shell line follows the shell the CLI was launched from
  (`Services/ShellEnvironment.cs`: "bash" from Git Bash, where the reference registers Bash
  alone; the two-shell sentence elsewhere).
  **Four more sections ride behind a gate this port can reproduce**
  (`Services/GatedPromptSections.cs`, measured in the reference's own section builder — its
  `sS` — which is one ordered list, so each block's slot is read off that list rather than
  guessed): **# Language** after # Environment, driven by the settings key `language`
  ("Preferred language for Claude responses and voice dictation") and rendering its `l2e`
  with the language in all three of its slots; **# Background Session** after the output
  style, gated on `CLAUDE_CODE_SESSION_KIND=bg` plus a `CLAUDE_JOB_DIR` (its `B3o`) with
  the three isolation variants `CLAUDE_BG_ISOLATION` selects and the commit-before-finishing
  tail every isolated job carries — the scratchpad section stands down in a bg session, as
  the reference's `TNe` does; **# Focus mode** after # Context management in its lean or
  classic wording, which the reference gates on `viewMode: "focus"` and this port answers
  with the session's own Summary transcript view, the view that shows prompts and responses
  and nothing between them; and **## Delegating to subagents** between # Corrections and the
  reduced-delegation line, which needs the Agent tool in the turn and the latched steer at
  `counter_steer` — of that steer's four sources the environment variable
  `CLAUDE_CODE_THISTLE_GREBE` is the only one a local build can reproduce, so it is the one
  read. Both prompt forms carry all four, since the reference builds both from the one list.
  **# Advisor Tool** is the fifth gated section (`Services/AdvisorPrompt.cs`): 2,017
  characters measured off CLI 2.1.257's `NQt`, sent as the **last** text of the system
  prompt, after gitStatus — measured by capturing a print run with
  `CLAUDE_CODE_ENABLE_EXPERIMENTAL_ADVISOR_TOOL=1` and an `advisorModel` in settings,
  which also shows the tool declared as the server-side spec
  `{"type":"advisor_20260301","name":"advisor","model":"…"}` with no description and no
  input schema. That server half is Anthropic's — the API runs the reviewer model and
  forwards the conversation itself — so `Services/AdvisorTool.cs` re-derives the contract
  the block states rather than the machinery behind it: no parameters, the whole
  conversation forwarded (`ToolExecutionContext.ConversationSnapshot`, tool calls and
  results included), a stronger model answering. Block and tool are gated together, so
  the model is never told about a tool the turn does not carry.
  Two neighbouring sections are declared instead of built: **# Saving skills**, whose gate is
  the `remote_cowork` entrypoint and whose every variant asserts that skill files on disk are
  a read-only cache — false here, where the Customize editor writes the SKILL.md the
  catalogue reads back — and the parameter-tag rule, gated on a clientData blob and a
  growthbook rollout that is off in every capture.
  **The desktop appends sections of its own** (`Services/HostPromptSections.cs`): five blocks
  live in `app.asar` and in **no** `claude.exe` on this machine (2.1.247, 2.1.251 and the
  2.1.237 VM build all answer absent), handed to the CLI through `systemPrompt.append` (the SDK's
  `appendSystemPrompt`) — **not** `systemPromptRendererAppends`, which an earlier
  round named here and which is the cowork/chat channel instead. The desktop
  contributes **14** blocks by that route, not five; the ten this build does not
  carry are declared in `Deltas/reference-surface-deltas.tsv`. They land after the CLI's last line and before gitStatus, in the order
  a live session sends them — read off the assembly itself in app.asar
  (`D = v ? "…isolated workspace…" : "…a git worktree…"`, then `dt`, `ft`, `pt`) and
  confirmed against a live session's prompt: the **worktree paragraph** ("You are
  operating in a git worktree." with its path and name, or the WorktreeCreate-hook
  wording), the **markdown-link rule** (`dt`), the **Run-button rule**
  (`ft`), the **terminal-dialog note** (`pt`) and **`<browser_surfaces>`** (`CEr`). The
  worktree block rides whenever the session's directory is a linked worktree, which
  `HostPromptSections.LinkedWorktree` answers by the `.git` **file** at its root rather
  than by this app's own `.jarvis-worktrees` folder — so a checkout made by plain
  `git worktree add` says so too. Each is
  gated on the capability it describes, the way the reference gates them on
  `hasInAppBrowser`/`hasChromeBrowserSurface` — a block promising an affordance this app
  lacks would be a falsehood the model cannot check — so two of them ship with the UI that
  makes them true: a **Run button on shell-tagged fences** (`MarkdownView.RenderCodeFence` →
  `TerminalPanel.Send`, which is why the reference asks the model to fence commands as
  ```bash) and **relative file links** that resolve against the session's working directory
  and reveal the file rather than shell-executing a path the model wrote. The
  terminal-dialog note is the one adaptation: two of the four commands it names are real
  here. The fifth block (`mt`, auto-fix pull requests) is declared rather than carried, and
  the reason is what it promises rather than what it says: the desktop app watches the
  session's PR and wakes the model with a `ci-monitor-event` message of its own, tells it to "fix it,
  verify, commit, and push without asking first", and names the `/babysit-pr` it replaces.
  This build has the watcher half — `subscribe_pr_activity` polls `gh` each minute — but it
  delivers a task notification rather than that event; there is no `/babysit-pr`; and
  pushing without asking is not a mode here, because the permission gate still asks for the
  push whatever the prompt says. Shipping the text alone would promise behaviour nothing
  implements. The context reminder also carries **`# userEmail`**, sourced from
  `git config user.email` since this app has no account to read one from.
  **There are two prompts, and the model decides which** (`Services/ClassicPromptBuilder.cs`):
  capturing CLI 2.1.251 twice with nothing changed but `--model` records a 9,706-char
  **lean** prompt for `claude-opus-5` (the sections above) and a 27,705-char **classic**
  one for `claude-opus-4-5` — `# System` / `# Doing tasks` / `# Executing actions with
  care` / `# Using your tools` / `# Tone and style` / `# Text output` /
  `# Session-specific guidance` / `# auto memory` / `# Environment` /
  `# Context management`. **Which one a model gets was measured, one capture per model**
  (`ReferencePromptBuilder.UsesLeanPrompt`, `Services/PromptModelProfile.cs`): CLI 2.1.257
  driven at a local listener with a cleaned environment, over all thirteen models its
  catalog names and will still route — `claude-opus-4-0` and `claude-opus-4-1` are remapped
  to Opus 5 before a request is built, so they have no wire of their own. opus-4-8, opus-5
  and the whole fable/mythos family are sent the lean form; sonnet-5, every haiku, the
  Claude 3 generation and opus 4.5 through 4.7 the classic one — `claude-sonnet-5` despite
  being a 1M-context Claude 5 model with adaptive thinking and mid-conversation system
  turns, which is why the gate is not "the 5 family". **The catalog's `lean_prompt`
  capability looks like that gate and is not it**: `claude-mythos-5` ships
  `capabilities:[]` and is still sent the lean document *and*
  `# Communicating with the user`, exactly like `claude-fable-5` beside it — so the
  reference is reading the family and version, and a round that "fixed" this port onto the
  capability array broke mythos-5 in three places at once (its prompt, its fable sections
  and its task board) before the captures caught it. `ModelCatalogParityTests` now pins that
  divergence by name, so a build that changes it says to re-measure rather than letting the
  port drift on either reading. What the catalog *is* read for is the **knowledge cutoff**
  (`Services/ReferenceModelCatalog.cs`, generated from the binary by
  `Captures/Models/gen-model-catalog.py` and compared row for row against the installed
  build): every capture agrees with it, and it closes the one real gap a hand-written table
  had — the 4.0 models are filed under an explicit `-0` (`claude-sonnet-4-0`,
  `claude-opus-4-0`), which a bare `claude-{family}-{major}` key missed, so a
  claude-sonnet-4 session printed no cutoff line where the reference prints "January 2025".
  A model on another provider matches none of the lean families and takes the classic form,
  which is the reference's own default for a model it does not recognise — and, since that
  is a default rather than an answer, **the one prompt decision this build hands to the
  user** (`Services/PromptFormPreference.cs` over `AppSettings.OtherProviderPromptForm`,
  Settings › General › Engine defaults, Classic by default). It is not a port of a
  reference knob but a gap the reference does not have, which is what separates it from
  the switch declared below. It answers only for an id the reference's tables cannot place
  at all — no catalog row **and** no canonical `claude-{family}-{version}` — so every
  Bedrock, Vertex and relay spelling of a real model is untouched, and an unreleased
  Anthropic id (`claude-sonnet-9`) still follows the family rule rather than the setting.
  Classic is the default on tier grounds rather than caution: the lean document marks a
  model its vendor tuned for the shorter form rather than a capability tier, and
  `claude-sonnet-5` is the control — frontier, 1M context, adaptive thinking — which the
  reference still sends the classic form, so a third-party flagship is at best its peer.
  Deliberately
  **not** carried: `CLAUDE_CODE_SIMPLE_SYSTEM_PROMPT`, which the reference's `M` reads above
  the capability in both directions — captured with it set on opus-5 and cleared on
  sonnet-5, and **neither run moved**, so the switch has no effect in this build and
  shipping it would be a knob that only this port honours. The classic form is **not**
  an earlier generation this port had outgrown — that is what an earlier round of this
  file claimed, and deleting it left the app sending the lean prompt to models the
  reference never sends it to. 25,938 of its characters are pinned byte for byte against
  a recorded capture by `ClassicPromptParityTests`; only the `/help` line and the feedback
  address are adapted, and the test proves those are the only two. Two placements come with it, both the reverse of what this app used to do:
  **project instructions ride the first message's context reminder** (with the memory index
  and `# currentDate`, in `SystemReminders.ContextReminder` — byte-identical to the
  reference's block, trailing blank line included), and **gitStatus rides the prompt**,
  computed once per session and memoized (`TurnContextFactory.GitStatusForSession`) because
  it says "at the start of the conversation" and sits inside the cached prefix.
  A capture of the patched build now differs from the reference's prompt only in the two
  lines the environment decides — the memory directory and the model — with every ported
  sentence verbatim, tool names included. Deltas are declared row by row in the
  parity suite's `Deltas/ported-text-deltas.tsv`. Chat keeps its short chat prompt;
  subagents get `Services/ReferenceSubagentPrompt.cs` — the reference's own
  subagent document, captured per agent type and compared byte for byte, rather
  than Core's `SystemPromptBuilder`, which an earlier round left them on.
  `Services/ReferenceToolDocs.cs`
  completes the pair: an App-level wrapper swaps the built-in tools' descriptions for ports
  of the reference tool docs (Read/Edit/Write/glob/grep/shell/todo_write —
  exact-unique old_string rules, dedicated-tools-over-shell, the `# Git` etiquette, todo
  discipline), adapted to our parameters and PowerShell; Core tools keep their own text.
  It also gives Agent the reference Task-tool doc, which points at the roster
  ("Available agent types are listed in `<system-reminder>` messages in the
  conversation.") rather than inlining it — the session's agent types
  (Explore/general-purpose/Plan/custom — the reference's own spellings, which a capture shows
  on the wire; `SubagentTool.CanonicalAgentType` still resolves this harness's older
  explore/general/plan, so a stored session replays) ride the harness system message instead
  (`Services/HarnessSystemMessage.cs`, below) — returns the reference "Todos have been modified
  successfully…" line from todo_write, and the prompt carries project instructions in the
  reference claudeMd framing ("OVERRIDE any default behavior", per-file
  checked-in/private labels, the may-or-may-not-be-relevant tail).
  **The instruction-file mechanism is the reference's whole loader**
  (`Core/Agent/ProjectInstructions.cs`, ported from CLI 2.1.251: `BXn` eager,
  `Bg` one file plus its imports, `LM` a rules directory, `Sxt`/`$1t` the nested
  load a touched file triggers), with the reference's own names swapped for this
  app's — `JARVIS.md` where it reads `CLAUDE.md`, `.jarvis/` where it reads
  `.claude/` — and the reference spelling kept **in the same slot**, first match
  winning, so a repository written for Claude Code loads unchanged and a
  `JARVIS.md` shadows a `CLAUDE.md` beside it.
  **Four tiers, in the reference's order**: `Managed` (a machine-wide policy
  directory plus the `jarvisMd` key of its `managed-settings.json`, which is the
  reference reading `policySettings.claudeMd` — from where the organization
  writes, never from the user's own settings), `User` (this installation's
  configuration directory: its `JARVIS.md` and its `rules/`, in force in every
  project), then the working directory's **whole ancestor chain root-first** —
  not bounded by the git root, because a repository is not the edge of where a
  user may keep instructions — each directory contributing `JARVIS.md`,
  `.jarvis/JARVIS.md`, `.jarvis/rules/**.md` and the private `JARVIS.local.md`,
  and finally the session's added directories behind the reference's own
  `CLAUDE_CODE_ADDITIONAL_DIRECTORIES_CLAUDE_MD` gate. A linked worktree nested
  inside its main checkout skips the main checkout's project files, as the
  reference's `L0e` does. `Services/InstructionScopes.cs` installs the two
  installation paths once so Core keeps taking them from outside and a subagent,
  a skill or `/doctor` loading by working directory alone still sees every tier.
  **`@`-imports are real**: an `@path` is resolved against the importing file's
  own directory and followed **five deep** (the reference's `DXn`), cycle-safe,
  skipping anything whose extension is not one of the 109 text kinds the
  reference lists. One that points outside the working directory raises the
  reference's own question once per project — its title, its warning, its list
  of the offending imports capped at eight with the rest counted, its closing
  note, and its two buttons with the refusing one first — and the answer is
  remembered whichever way it goes, so a "no" is as final as a "yes"
  (`Services/InstructionPrompts.cs`, the answer in ui-settings per project).
  There is no settings switch beside it, because the reference has none: its
  `/config` row is a read-only managed-policy display. The user tier may always
  import from anywhere, since what it reaches is the user's own. An HTML comment
  is stripped from what the model sees, as the reference's `ege` strips it.
  **What counts as an import is decided by markdown, not by a regex**
  (`Core/Agent/InstructionMarkdown.cs`): the reference lexes the file with
  marked and scans every token that is neither `code` nor `codespan`, skipping an
  `html` token outright unless it opens with a comment. So four spans are not
  scannable — a fenced block, an indented block, an HTML block and an inline code
  span — and this walker finds exactly those with marked's own rules ported
  across (its `Re`, `Ae`, `Pe`, `Q` and the codespan rule), including the two
  that a regex gets wrong: an indented block cannot interrupt a paragraph, so an
  indented *continuation* line still carries its import, and a fenced block
  inside a list item is code even though it is indented. Inline tags are removed
  as marked's inline `html` tokens, which is what keeps `@style.md</span>` from
  reading as one path.
  **A rule can be lazy**: `paths:` frontmatter turns a `rules/*.md` file into a
  conditional one, kept out of the eager load until a tool touches a matching
  file — and a list that is empty or says nothing but `**` leaves it
  unconditional rather than making it match everything (the reference's `rXn`,
  trailing `/**` stripped). The same touch pulls in **every directory between
  the working directory and the file**: its `JARVIS.md`, its config-directory
  copy, its `JARVIS.local.md` and its rules, each as its own
  `<system-reminder>Contents of {path}` on the next message and each once per
  session — the reference's nested_memory attachment, which is drained when a
  message is composed rather than mid-turn. **A read is what wakes them**, and
  only a read: the reference pushes that trigger from the Read tool's own body
  (its `f2n`) and from no other tool, so an edit or a write does not.
  **Instruction files are sent whole**: measured against the reference (which
  delivered this repo's 100k CLAUDE.md uncut, 102,964 chars in its first user
  message), a primary file and its `.local` overlay are never truncated — the
  port used to cut them at 40k, which is the reference's *warning* threshold
  `max(40_000, window x 5% x chars-per-token)`, not a cap. A file over
  `ProjectInstructions.MaxFileBytes` (4 MiB, the reference's own read limit) is
  skipped whole rather than cut, since half a rules file can contradict the half
  that was dropped; `/doctor` carries the reference's warning verbatim ("Large
  CLAUDE.md will impact performance (99,507 chars > 40,000)") and the session
  says the same thing in the transcript when it opens. **The instruction files
  other tools own are no longer read at all** — .cursorrules, .cursor/rules,
  copilot-instructions, .windsurfrules and AGENTS.md — because the reference
  reads none of them: its own loader touches only CLAUDE.md, .claude/CLAUDE.md,
  CLAUDE.local.md and .claude/rules, and `/init` reads the others as *material*
  rather than loading them. `AppSettings.InstructionFileExcludes` is its
  `claudeMdExcludes` (globs or absolute paths, brace alternatives expanded,
  matched against the whole path with forward slashes) and applies to the User,
  Project and Local tiers only — a policy file cannot be excluded by the machine
  it governs — and `CLAUDE_CODE_DISABLE_CLAUDE_MDS` is the reference's own kill
  switch, kept under its own name like every other `CLAUDE_CODE_*` variable this
  port honours. **Subagents receive none of it**: a capture with a `CLAUDE.md`
  present shows the parent loading it and the child receiving it in neither its
  prompt nor its messages, so this port follows and an earlier claim here that
  subagents "load the same set" was wrong.
  **The `instructions_loaded` hook is per file**, carrying the reference's whole
  payload (`file_path`, `memory_type`, `load_reason` of
  session_start/nested_traversal/path_glob_match/include/compact, `globs`,
  `trigger_file_path`, `parent_file_path`) and matched on the **reason** rather
  than on a tool name. A compaction takes the instruction files out of the live
  context with the rest of the history, so the next message carries them again
  and the hook says the reason was the compaction; moving the session's working
  directory reloads them the same way. `/memory` is the reference's picker:
  "User instructions", "Project instructions", `@-imported`, "dynamically
  loaded", the two files that do not exist yet marked `(new)` and its "Open
  auto-memory folder" row, with the chosen file opened for editing and a `(new)`
  one created first.
  `Services/SystemReminders.cs` adds the reference `<system-reminder>` attachments: the
  first message of a session carries the **context reminder** (project instructions under
  `# claudeMd`, the memory index, then `# currentDate`), the gitStatus snapshot (branch,
  main branch, git user, porcelain status trimmed and truncated at 2k, last 5 commits)
  is built here but rides the **system prompt** rather than a reminder, and the plan-mode
  "you MUST NOT make any edits" reminder (with the reference "## Plan File Info")
  rides the **harness system turn**, once, rather than every message — measured
  across two turns of one session, where the second carries no plan text at all.
  The mode and output-style notices ride that same tail
  (`Services/SessionModeNotices.cs`), and only the style reminder repeats.
  Reminders persist inside the stored user
  message like the reference; the transcript hides them via `SystemReminders.VisibleText`,
  which answers nothing at all for a **harness system turn**. That message is
  harness-authored end to end and the reference renders none of it; the trailing one is
  composed in Core (`AgentOrchestrator`, the batching reminder and the token block), which
  cannot reach the App's provenance stamping, so `ChatMessage.HarnessSystemTurn` is its
  provenance — the rule `ToolResultReminders.IsGenuineUserMessage` already reads. Without
  that the unstamped blocks fell through to the prefix-sniffing fallback, which keeps any
  text not opening with `<system-reminder>`, and a reopened session drew them as a user
  bubble — the two blocks concatenated, which is the shape that gave the defect away.
- **The harness system turn** (`Services/HarnessSystemMessage.cs`): the agent-type
  roster, the reference's parallel-agents note and the skill listing do **not** ride
  the user's message — they are their own message, whose role on the Anthropic wire is
  literally `system` (the `mid-conversation-system-2026-04-07` beta the app already
  sent). Measured by capturing CLI 2.1.251 twice, the second run through `--continue`:
  request 1 is `[user[reminder, "hi"], system[roster]]`, and request 2 replays that same
  system message *from history* and adds no second one — so it is persisted like any
  other message and emitted once, which is what makes the static roster cost its tokens
  a single time. Later turns that discover new skills send a further system message
  carrying only those (`SkillSessionState.AnnouncedAgentTypes` gates the roster; whether
  the reference repeats it there is unmeasured). `ChatMessage.HarnessSystemTurn` is the
  additive Core flag: only the Anthropic wire renames the role and refuses to fold it
  into its neighbours, while every other wire folds it back into the user turn it
  accompanies (`Providers/HarnessSystemTurns.cs`) — Gemini's `contents` must alternate,
  so an unfolded extra message would be a second consecutive user turn there.
  **The system turn is a model capability, not a prompt form** (`Services/HarnessTurnComposer.cs`,
  measured on CLI 2.1.257): only the models whose wire carries the
  `mid-conversation-system` beta (opus-5, opus-4-8, sonnet-5, fable/mythos) get the
  message; opus-4-6, opus-4-5, sonnet-4.x, haiku and 3.x get the same sections as
  `<system-reminder>` blocks **leading** their first user message — roster, skill
  listing, plan workflow, token budget, then the context reminder that was attached
  first, then the prompt — which `SystemReminders.Lead` places. The roster itself grew
  the reference's `claude`, `statusline-setup` and (desktop entrypoint only)
  `claude-code-guide` lines, each closing with the tools its definition grants, and its
  Explore description differs between the lean and classic prompt forms.
  **The deferred-tools delta leads that turn** (`Core/Agent/DeferredToolAnnouncements.cs`
  + `Services/DeferredToolNotice.cs`, measured on CLI 2.1.257 at 190556359 and against a
  live session's own prompt): once tool search engages, the harness names the tools the
  request no longer advertises — "The following deferred tools are now available via
  ToolSearch. Their schemas are NOT loaded …" and one ordinal-sorted name per line —
  ahead of the agent roster and the MCP instructions, and the whole thing rides one
  `<system-reminder>`. It is a **delta**: the first message lists every deferred tool and
  a later one only what appeared since (`SkillSessionState.AnnouncedDeferredTools`).
  The same block carries the reference's server paragraphs, each after a blank line:
  **needs authentication** (`McpManager.NeedsAuthServers`, from the 401 the HTTP client
  already raises — sent only in a non-interactive session, which is what its own sentence
  says the session is), **failed to connect** (`McpManager.FailedServers`, printed as the
  reference's `{name} ({code}): "{error}"`), and the **still connecting** and
  **no longer available** paragraphs the port renders but has nothing to feed yet. A list
  past the reference's cap of 30 is comma-joined and counted, with `mcp__server__tool`
  folded onto `mcp__server__*`. `# MCP Server Instructions` follows the roster and now
  carries a **configured** server's own `instructions` from its initialize handshake
  (`IMcpClient.Instructions`, `McpManager.ServerInstructions`) beside the shell's blocks,
  with the reference's retraction — "The following MCP servers have disconnected. Their
  instructions above no longer apply:" plus its ambient-context note — when one goes away.
- **Harness provenance** (ported from the reference subagent hand-back's
  `harnessNoteCount`/`harnessTailCount`/`harnessSectionHash`): a composed message records
  how many of its leading and trailing content blocks the harness wrote, bound to a
  fingerprint of the content those counts were computed against
  (`ChatMessage.HarnessNoteCount/HarnessTailCount/HarnessSectionHash`, additive Core
  fields nothing on the wire ever sees; stamped by `SystemReminders.UserMessage`/
  `Attach`/`HarnessMessage`/`AddUserBlocks`). `VisibleText` slices by the counts instead
  of sniffing for a `<system-reminder>` prefix, so text the **user** typed is shown even
  when it opens with one; a rewrite, insertion or reorder invalidates the counts and the
  old sniffing runs, the reference's own rule that a hook rewrite must invalidate rather
  than misplace. The hash covers each block's kind plus text only, so microcompaction
  clearing a tool result's body leaves the counts valid. **Reminders lead the
  message**, which is where a captured CLI 2.1.251 request puts them (its first
  user message is `[<system-reminder>, "hi"]`); composer attachments join ahead of
  whatever still trails — hook output, whose position no capture pinned down — so
  that run stays contiguous and the counts keep describing it. Messages stored
  before the counts existed read as unstamped and fall back.
- **Fingerprints** (Settings › Extra › Fingerprints, `Services/ClientAttribution.cs`): the
  request-identity fields the reference CLI sends, each an opt-in switch, **all off by
  default** — the attribution system block
  (`x-anthropic-billing-header: cc_version=…; cc_entrypoint=…;`, whose version suffix is
  the reference's 12-bit prompt fingerprint: three sampled characters of the
  conversation's *first* user message, salted with the client version and hashed to three
  hex digits, so it is stable for the session and does not churn the prompt cache),
  `metadata.user_id` (`{device_id, account_uuid, session_id}`, pinned through the existing
  `BodyOverride` merge patch so a request-inspector edit still merges on top), and the
  `X-Claude-Code-Session-Id` header (carried by a new additive
  `LlmRequest.ExtraHeaders`/`AgentTurnContext.ExtraHeaders`, applied by the Anthropic
  adapter in both its send and preview paths). The mechanism is the reference's; the
  identity is ours — `cc_version` reads `jarvis-code/{version}`, the salt is our own and
  the install id is generated here rather than read from another client's config, so a
  request never claims to have come from a client that did not send it. Deliberately
  absent: the reference's first-party-only `cc_prev_req`/`cc_prompt_id`/`cch` fields and
  its account uuid (no such session exists here), `cc_is_subagent` (subagent turns are
  built from Core's own prompt builder, which this block never reaches), and its TLS
  certificate pinning (a gateway feature this app does not have).
- **The turn loop has no cap** (`Core/Agent/AgentOrchestrator.cs`): the reference's query
  generator takes an *optional* `maxTurns` and writes every check as `if (maxTurns && …)`,
  and its `--max-turns` flag is documented "only works with --print" — so an interactive
  session runs until the model stops, a hook stops it, the user does, or the provider
  fails. `AgentTurnContext.MaxIterations` is therefore `int?` and null by default (the CLI
  sets it from `--max-turns` in print mode only); a turn is counted per **tool
  round-trip**, as the reference counts it (its `Wk=Yn+1` after the results are appended),
  and per-agent caps live on the agent definition rather than on the loop: the reference's
  Explore, Plan, general-purpose, teammate and workflow-subagent declare none, `fork`
  declares 200 (which a `context: fork` skill uses), and a custom agent may declare its own
  `max-turns`. `TurnEndReason` is the reference's own nineteen terminal reasons with its
  snake_case spellings on the wire (`TurnEndReasons.WireName`, which `-p --output-format
  json` reports); members this build cannot reach yet (budget_exhausted,
  structured_output_retry_exhausted, tool_deferred*) say so on themselves.
  With no cap catching a bad turn, the loop carries the reference's **recovery ladder**
  (`Core/Agent/TurnRecovery.cs`, attempt cap 3 = its `LAt`): an answer that stopped at the
  output-token limit is resumed ("Output token limit hit. Resume directly…"), a stream that
  ended mid-message is resumed in print runs only (the reference gates that one on
  `isNonInteractiveSession`), a tool call the provider never delivered is retried once —
  the broken assistant turn is taken back out of the conversation and the host told to drop
  it (`AssistantMessageRetracted`), and a second failure ends the turn as
  `malformed_tool_use_exhausted` — and a response with nothing user-visible is nudged once.
  None of them advance the turn count. Providers now report their stop reason
  (`ResponseCompletedEvent.StopReason`, `StopReasons.FromOpenAiFinishReason` for the
  OpenAI-wire family; null means "no opinion" and the ladder does not act).
  **A turn never ends on an answer nobody can read.** The nudge is gated on the
  response, not on a list of stop reasons — gating it on the four names this engine
  knew let a relay's own fifth word fall past every rung and finish the turn as
  `completed` with an empty message behind it, which is a blank transcript and no
  error (measured 2026-09-01: a `thinking` block opened, signed and closed with no
  delta, no text after it). Still unreadable after the nudge ends the turn as
  `model_error` naming the stop reason (capped at 60 chars); `refusal` ends it there
  at once, without arguing with a decision the model made, and
  `model_context_window_exceeded` ends it as `prompt_too_long`. `StopReasons` names
  those two beside the original four, `content_filter` maps onto `refusal`, and the
  CLI REPL prints every reason that means "no answer came back", not only `Error`. Prompts typed
  while a turn runs **fold into it** between model calls (`FoldQueuedMessages`) instead of
  waiting for it to end; one carrying attachments or a skill expansion still waits.
- **Auto-compaction & prompt-submit hooks** (Core): the orchestrator summarizes older
  history mid-turn once the last call's context crosses the reference threshold.
  `Core/Agent/ContextWindows.cs` is that arithmetic ported constant for constant from CLI
  2.1.251 — **threshold = min(configured, model) window − min(max output, 20k) − 13k**
  (its KYe/k4e), so a 200k Opus 5 compacts at **167k**, not at 187k — plus the window
  resolver (`GA`: env `CLAUDE_CODE_AUTO_COMPACT_WINDOW` → the setting → the per-model
  table pinning sonnet-4-6/opus-4-6/opus-4-8/opus-5 to 200k and sonnet-5, fable-5, fable-5-1,
  mythos-5 and mythos-5-1 to 1M, the last four from the 2.1.257 catalog's
  `context:{window:1e6, native_1m:true}` → an unrecognized model enforced at its declared
  window under the reference's `unknown-model` source, unless
  `CLAUDE_CODE_DISABLE_UNKNOWN_MODEL_WINDOW_ENFORCEMENT` stands it down → auto),
  the `ok/warn/compact/blocked` ladder (warn 20k early, blocked 3k below the raw window)
  and the precompute arm point at a fifth of the window (`v9`). **/autocompact names a
  window, not a percentage**: `[auto|<tokens>]`, 100k–1M with `500k`/`1m`/`200` shorthand,
  persisted as `AppSettings.AutoCompactWindow` and mirrored by the `config` key
  `autocompact_window`; its sentences live in `Services/AutoCompactCommand.cs` and are
  parity-pinned. On/off stays on `config autocompact`, as it stays on /config there.
  `CLAUDE_AUTOCOMPACT_PCT_OVERRIDE` is the only percentage dial, as it is in the
  reference. The summarizer **forks the conversation** rather than pasting a rendered
  transcript, carries the reference's no-tools preamble and tail reminder around the
  summarization instructions, accepts `/compact <optional custom summarization
  instructions>`, and leaves the "This session is being continued…" continuation; the App
  archives history and shows "Compacted session · from N tokens".
  **Both breakers are live** (`CompactionState`, per session): three refills inside three
  turns trips the rapid-refill breaker with the reference's thrashing notice, three failed
  attempts trips the circuit breaker, and a **PreCompact hook can refuse** an automatic
  compaction as well as a manual one — all three surface as `ConversationCompactionBlocked`
  and leave the turn running on the context it had.
  **Microcompact is the reference's keep-recent pass** (`Core/Agent/Microcompactor.cs`, its
  `lqn`/`Pan`): keep the last **5** results of the tools that produce bulk output
  (Read/Edit/Write/Grep/Bash/PowerShell/Glob/WebFetch/WebSearch), clear every older one to
  `[Old tool result content cleared]` — or to the `<persisted-output>` stub when a writer
  stores the body — count media at 2000 tokens each, and refuse the pass below **20k**
  tokens saved. It is **off by default**, because the reference reaches it only from the
  context-hint reject path whose server flag ships false; a microcompact boundary renders
  as nothing, as it does there.
  What survives a full compact follows the reference group policy: messages split into
  groups (each assistant message opens one, carrying its tool results) and the last group
  preserved verbatim. **A summarizer prompt the provider calls too long is retried on a
  truncated fork** (`ConversationCompactor.TruncateForRetry`, the reference's `BRt`): the
  oldest groups go — as many as the provider's own token gap names
  (`ContextOverflowDetector.TryParseOverflowGap`, its `qj`/`iL`), a fifth of them when it
  names none — the last group is never dropped, a fork left opening on an assistant turn
  is led by `[earlier conversation truncated for compaction retry]`, and a marker from an
  earlier pass is stripped rather than stacked. Three retries (`URt`), then
  "Conversation too long. Press esc twice to go up a few messages and try again."; an
  empty history is refused up front with "Not enough messages to compact."
  **The context wall is a real refusal** (`AgentOrchestrator`, the reference's
  `blocking_limit`): past `raw window − output reserve − 3k` the turn ends before a
  request is built, with `fH` — "Prompt is too long", or its `Eve` variant naming the
  compaction that failed to make room, capped at 300 characters. The gate stands down
  exactly where the reference's `Hd` does, while auto-compaction is on **and** the model's
  own window is the one in force — so a pinned model like opus-5 (source `model-default`)
  is gated, an unlisted one (source `auto`) is not. The rapid-refill breaker ends the turn
  the same way, with `rapid_refill_breaker` and the thrashing notice; a PreCompact hook
  refusing an *automatic* compaction says nothing at all, because the reference suppresses
  that notification and lets the gate judge the turn instead.
  **The next summary is written before it is needed** (`Core/Agent/PrecomputedCompaction.cs`,
  the reference's `XRe`/`YRe`): a fifth of the window below the top, the conversation so
  far is summarized off the turn, and crossing the threshold swaps that summary in whole
  with everything written since kept verbatim. One run at a time, three consecutive
  failures end it for the session (`CRt`), a narrowed window under the model default never
  arms it (`v4e`), a compaction or a rewind invalidates what it wrote, and it owns its own
  cancellation so a closed session cannot spend a request. Off by default behind
  `AppSettings.PrecomputeCompactionEnabled` and the `config` key `precompute_compaction`,
  which is where the reference leaves it — `tengu_sepia_moth` ships false.
  **The summary outlives the process** (`Core/Agent/PrecompactSidecar.cs`, the reference's
  `precompact.json`): once written it is stored beside the session, and the next process to
  open that session reads it back once — before the first arm — and takes it up only while
  it still describes the conversation. The reference's whole acceptance set is checked in
  its order and each refusal names itself: `too_large` (8 MB, `O$`), `parse_error`,
  `version`, `session_mismatch`, `model_mismatch`, `bad_timestamp`, `too_old` (seven days,
  `vWn`), `boundary_missing`, `grew_too_much` (+150k, `EWn`), `shrank_too_much` (below half
  the tokens it was written at) and `preserve_uuid_missing`; anything but "absent" deletes
  the file rather than leaving it to be refused again, and so does taking it up. Writes are
  atomic and 0600 where the platform has modes. Off by default behind its own
  `AppSettings.PrecomputeSidecarEnabled` and the `config` key `precompute_sidecar`, because
  the reference gates it separately from the background summary itself and its flag
  (`tengu_amber_packet`) also ships false. Two deliberate differences: the reference keys
  the messages it must still find on their uuids, which this engine's `ChatMessage` does
  not carry, so the same invariant is re-derived from position plus a content hash — a
  message that moved, changed or vanished no longer matches, which is what a missing uuid
  told the reference; and its file lives inside a per-session directory, where this port's
  flat session files put it in `sessions/code/precompact/{id}.json`, out of the `*.json`
  glob that lists sessions. The `storageV5` branch — a remote key-value store, and the
  read-ahead prefetch that exists to hide its latency — has nothing to drive it here.
  **Cold compact** (`Core/Agent/CompactionStripper.cs`, its `ixe`/`o1n`/`$Rt`, reached by
  `CLAUDE_CODE_COLD_COMPACT`): before a summarization request goes out, images become
  `[image]` and every tool argument and result is cut to 100 characters with
  `…[truncated, original N chars]`, never splitting a surrogate pair. Strings inside a
  tool call are previewed in place and the JSON around them is kept, re-serialized with
  JSON.stringify's escaping so the ellipsis stays one character.
  **A hook that prints broken JSON is told so** (`HookRunner.MalformedJsonObject`): stdout
  that opens like a JSON object but does not parse gets the reference's diagnostic —
  "{Event} hook output invalid: Hook output looks like a JSON object but is not valid JSON —
  {error}. Emit the payload with a JSON encoder (jq, ConvertTo-Json, json.dumps) rather than
  string concatenation…" — as a transcript notice on the desktop and on stderr in the CLI.
  SessionStart carries the reference's `source` (startup or resume here; clear, compact and
  fork are declared).
  **Hooks have five kinds, not one** (`Core/Hooks/`): a shell `command`, an LLM
  **`prompt`** hook (`HookKind.Prompt` + `PromptHooks.cs` + the App's
  `PromptHookEvaluation`), an **`http`** hook, an **`mcp_tool`** hook
  (`RemoteHooks.cs` + the App's `HookTransports`), and an in-process **function**
  hook the host registers (`FunctionHook`, which is how the reference registers
  its own preview-verification pair). All of them answer the same two things a
  command does — did it pass, and what did it say — so every event path reads
  them alike: a refusal is a non-zero exit and the answer is stdout, which is why
  a prompt hook now works on any event instead of having its condition handed to
  a shell. An **http** hook POSTs the payload and passes on a 2xx, with its
  headers interpolating only the environment names it declared (narrowed by
  `AppSettings.HttpHookAllowedEnvVars`, CR/LF/NUL stripped so a value cannot start
  a second header), its URL matched against `AppSettings.AllowedHttpHookUrls` when
  that is set, redirects off, and the connect refused for any private or
  link-local address — loopback excepted, since a hook served from the machine is
  the ordinary case. SessionStart and Setup never post one, as the reference
  refuses them there. An **mcp_tool** hook calls a tool on a connected server,
  its `input` filled from the payload by `${dotted.path}` (a path that resolves to
  nothing becomes an empty string, an object becomes its JSON), and refuses by
  name when the server is not connected. A prompt hook hands its condition to a model — on Stop and SubagentStop with the
  transcript in front of it and the reference's own system prompt, thinking off, a
  `{ok, reason}` tool it must call exactly once, 30s default, `continueOnBlock` — and an
  unmet condition becomes the blocking error `[{condition}]: {reason}` that keeps the turn
  running, while `{"impossible": true}` retires it instead. **`/goal` is exactly that**:
  the standing goal is registered as a Stop prompt hook, the turn cannot end while a model
  reading the transcript says it is unmet ("Goal not yet met… continuing"), and the goal
  retires itself when met ("Goal achieved") or judged unachievable ("Goal could not be
  achieved"). Eight consecutive blocks end the turn anyway with the reference's notice
  (`CLAUDE_CODE_STOP_HOOK_BLOCK_CAP`). **A goal waits for background work rather
  than judging a transcript still being written** (`Core/Agent/GoalCheckins.cs`):
  a turn ending with agents or shells still running stands the goal's hook down
  and, once the wait has gone on long enough, says where things stand — the
  reference's two check-in bodies, one naming the work (`- id · kind · detail`,
  cut at 120) and one for work that finished without reporting. The interval is
  `CLAUDE_CODE_GOAL_CHECKIN_MINUTES` (30 by default, 0 off) and doubles per
  check-in up to four times the base; an idle session gets at most three of them
  before they pause "until your next message". Each arrives the way any harness
  input does — the summary as a transcript notice, the body as the message the
  session wakes on (`--open=goalcheckin` poses one). A PostToolUse hook's `additionalContext` rides the
  tool result back to the model, which is how the verification nudges reach it.
  `user_prompt_submit` hooks run before a prompt starts a turn: non-zero exit blocks it,
  and its stdout rides the message as the reference's `hook_success` attachment —
  `<system-reminder>` around `{hookName} hook success: {stdout}` (its renderer at
  190556359, for SessionStart, UserPromptSubmit and UserPromptExpansion only, and nothing
  at all for empty output). The `<user-prompt-submit-hook>` / `<session-start-hook>` tag
  pair this port used to invent is gone: the reference carries that spelling only inside
  the classic prompt's sentence about hooks, never as a wrapper it emits. `pre_tool_use` runs
  before every tool call (non-zero exit blocks the call, stderr returns to the model) and
  `post_tool_use` after it (advisory), both from the orchestrator; the App adds its own
  `turn_completed` when a turn ends. The engine also carries
  the reference's remaining events: `session_start` (once per session, stdout rides the
  first message as `SessionStart:{source} hook success: {stdout}`), `stop` (non-zero exit blocks ending the
  turn — the reason returns to the model and `stop_hook_active` guards loops), the
  observational `session_end` (bounded 3s at window shutdown), `subagent_stop`,
  `subagent_start` (fired by SubagentTool before the inner turn; subagents also inherit
  the turn's hooks via `SubagentServices.Hooks`), `notification`, and `pre_compact`
  (manual /compact; auto-compact runs mid-turn inside Core and deliberately doesn't fire
  it), plus **`permission_request`** — consulted only when a prompt is about to be shown:
  stdout JSON `{"decision":"allow"|"deny","reason":…}` settles the call in place of the
  prompt (one call at a time, never "always allow"; wired through
  `UiPermissionGate.PermissionRequestHookAsync`), while failures and garbage have no
  opinion. The 2.1.247 parity round added the CLI enum's remaining locally-meaningful
  events, all observational: `post_tool_use_failure` (error results, tool-matched, fired
  inside AfterToolAsync), `post_tool_batch`, `user_prompt_expansion` (custom command/skill/
  MCP prompt expansion), `stop_failure`, `post_compact` (manual + auto),
  `permission_denied`, `setup` (/init · /terminal-setup · /auto-mode-setup),
  `elicitation`/`elicitation_result` (MCP elicitation), `config_change` (config tool),
  `worktree_create`/`worktree_remove`, `instructions_loaded` (once per session),
  `cwd_changed`, `file_changed` (a FileSystemWatcher that exists only while subscribed,
  only files the session's tools touched, only outside a turn), `directory_added`, and
  `message_display`, and the teammates-bound `task_created`/`task_completed`/
  `teammate_idle` that the task board and the team file fire.
  **`pre_model_switch` is the one blocking event outside tool use**
  (`Core/Hooks/ModelSwitch.cs`): it is handed the switch before it happens — the
  model moved from and to, what was asked for, the source, the context tokens the
  next request would have to re-send and whether the prompt cache is still warm,
  which is the cost a switch forfeits and the reason the reference gives this one
  a veto — and a non-zero exit or a `{"permissionDecision":"deny"}` verdict leaves
  the session on the model it had, with the default unwritten too.
  `post_model_switch` reports the switch afterwards. The reference offers Pre for
  a switch somebody chose (`command`/`picker`/`sdk`) and Post for those plus
  `auto`/`resume`; two of the five arise here — `command` from the CLI's
  `/model <id>` and `picker` from the desktop's model menu, which is what the
  desktop's `/model` opens. The two payload fields it cannot compute
  (`estimated_cache_write_usd`, `pricing` — this engine does not price turns) and
  its reading of an `ask` verdict as a refusal, there being no prompt to raise for
  a model switch, are declared in `Deltas/reference-surface-deltas.tsv`.
- **Plan mode 2.x** (reference plan-file flow): plan mode keeps the **whole tool set**, as
  the reference's `-p` plan capture on CLI 2.1.257 shows (Bash, Edit and Write advertised),
  and the gate holds the line the reference's way (`Services/UiPermissionGate.cs`,
  `PlanFilePath`): a non-read-only call that is not the plan file at
  `{cwd}/.jarvis/plans/{sessionId}.md` is **asked about**, not denied — "Cannot write to
  {path} while in plan mode." for a file write, "Cannot call {tool} while in plan mode."
  for anything else, no "always allow", nothing remembered — a run with nobody to ask
  answers no, and the plan file itself is allowed outright. The blanket allow-Write rule
  and the scoped write wrapper this port used to install are gone. `ExitPlanMode`'s inline
  card renders the plan with Keep planning / **Edit plan** / Approve, and an
  `AskUserQuestion` tool rides every interactive Code turn (a print run drops both, with
  EnterPlanMode and Monitor, as the reference's `-p` tool list does).
  The reminder is the reference's **five-phase workflow** (`Services/PlanModePrompts.cs`),
  not a paragraph: Phase 1 explores with up to N Explore agents in parallel
  (`CLAUDE_CODE_PLAN_V2_EXPLORE_AGENT_COUNT`, 3), Phase 2 designs with Plan agents
  (`CLAUDE_CODE_PLAN_V2_AGENT_COUNT`, 1 — above 1 the multi-agent guidance appears),
  Phase 3 reviews, Phase 4 writes the plan file (Context section first, recommended
  approach only, representative paths, **and a verification section** — which is what the
  auto-verify hook reads back), Phase 5 calls ExitPlanMode; it carries the planExists
  branch, the end-turn rule ("your turn should only end with either using the
  AskUserQuestion tool OR calling ExitPlanMode"), and `--plan-mode-instructions` replacing
  the phases. Two more forms follow the reference: a **sparse** reminder once the workflow
  is already in the conversation, and a **subagent** form carrying the restriction without
  the workflow. Approval answers with the plan — "User has approved your plan… Your plan
  has been saved to: {path}… ## Approved Plan[ (edited by user)]:" plus the
  spawn-named-teammates line when Agent is available — with the reference's three other
  variants for an empty plan, a subagent and a teammate awaiting its lead; **Edit plan**
  makes the card editable and what the user approved is written back to the plan file.
  Leaving plan mode restores the mode it was entered from (`UiPermissionGate.EnterPlanMode`
  / `ExitPlanMode`, the reference's prePlanMode) and the rule lines plan mode replaced,
  and `EnterPlanMode` carries the reference's own tool doc and asks the user first, as
  that doc says it does. AskUserQuestion follows the reference schema (1-4 questions, 2-4
  options, multiSelect, auto-added Other), rendered as an inline card
  (`ViewModels/InteractionPrompts.cs`; dev flags `--open=question` / `--open=plan` /
  `--open=plan:edit`, usable without `--screenshot` to keep the app open;
  `--open=goalcheckin` poses a delivered goal check-in).
  Approval swaps the FULL tool set back mid-turn: the registry is live
  (`ModeSwitchedRegistry` + the orchestrator re-reads `Tools.All` before every model
  call), so artifact/computer/terminal return the moment plan mode ends — and it is live
  in BOTH directions: every non-coordinator Code turn is wrapped in
  `ModeSwitchedRegistry`, the full set carries an **`EnterPlanMode`** tool
  (`PlanModeTools.CreateEnterTool` — flips the gate to Plan + sets the plan-file allow
  rules; the plan set swaps in before the next model call, and `ToolExecutionContext`
  always carries PlanFilePath/PlanApprovalAsync so a mid-turn enter can still exit), and
  **`/plan`** turns plan mode on from the composer (or shows the current plan file).
  Subagent read-only forcing uses the live `IsPlanModeActive`, so entering restricts and
  an approved exit frees them mid-turn. Agent also gains the reference **`plan` agent
  type** (read-only architect returning a step-by-step implementation plan;
  `SubagentTool.PlanRolePrompt`). Options may
  carry the reference `preview` field — a self-contained HTML fragment rendered
  side-by-side in the engine (`Controls/HtmlPreview.cs`; single-select only, focus follows
  hover/selection, and previews suppress the click-to-resolve shortcut). Note an engine
  view must be in the visual tree before its window is reparented, or there is no
  container to reparent into.
- **Queued messages & transcript views**: prompts typed while a turn runs queue above the
  composer and drain one per turn. The stack is the reference's own
  (`Services/ChatQueue.cs`, ported from `c11959232-DM8o5ho4.js`): the header carries the
  count alone ("3 messages queued") and, collapsed, the next message beside it ("Next:
  {preview}"), while the sentence this port used to draw — "N messages queued. Will send
  after the current response." — is what the reference gives an assistive reader and rides
  the stack's accessible name. A row previews the reference's way (the text, then a marker
  per attachment — `@name` for a file, `[Image]` for an image — whitespace collapsed, and
  an ellipsis when there is nothing to show) and carries its ⋮ menu: **Edit in composer ·
  Send now · Remove from queue**, with **Clear all** under an expanded stack and the grip
  revealed beside the menu on hover. Removing one on Chat asks first — "Discard queued
  message?" over "This message will be removed from the queue and won't be sent." — which
  is what the reference's chat queue does and its Code composer does not.
  The composer also remembers what was sent (`Services/ComposerHistory.cs`, unit tested):
  **Ctrl+R** opens the reference's reverse search with its own hint ("Search history:" in
  danger when it matches nothing, the query, then ↑ ↓ cycle · esc cancel), **Up** on an
  empty composer walks the history with "History {index}/{total}" and the reference's
  "↓ to restore your draft", and the prompts live in the App's own `prompt-history.json`,
  newest first, capped at 200 and shared by both surfaces as the reference's one composer
  is. The running-tasks chip ({n} running tasks → the Background tasks pane, shown alone
  with the static glyph when idle) completes the reference status line.
  The **four transcript views** follow the reference's semantics
  (`Services/TranscriptViewModes.cs`, unit tested): Normal hides thinking, Thinking and
  Verbose show it plus the one-line recap above each tool group
  (transcriptModeShowsThinking), Verbose additionally holds every group open without
  touching the rows' own expanded state, and a run it forces open is **frozen** there as
  the reference freezes it: its row handler returns on `if(p||!K)return` before touching
  state and its `zW` renders no disclosure at all while `forceExpanded` is set, so the
  caret is not drawn and a click does nothing. Leaving Verbose lands back on whatever the
  reader had in the ordinary view, because the run's own expanded state was never
  written to — and Summary keeps just prompts and responses
  (a filter stand-in — the reference's Live-summary view is a summarizer feature we
  don't have). The ⋮ menu's radio submenu hides Thinking until the session has thinking
  (its check falling back to Normal), **Ctrl+O** cycles the modes silently with the same
  gate, the status line's phase label is a button only while thinking is active and the
  view isn't Verbose — clicking flips Normal ⇄ Thinking with the reference
  "Switched transcript view to…" notices — and the mode is sticky per session
  (`UiSettings.TranscriptViewBySession`, newest-last and capped at 100 like the
  reference's transcriptModeBySession; unknown sessions open in Normal).
- **Background workers (Dispatch mechanics)**: `Agent run_in_background:true` runs the
  subagent off the turn (Core `AgentWorkerManager`), its report arriving as the reference
  `<task-notification>` XML on a hidden user message — immediately when idle, else after
  the current response; `SendMessage` continues a finished worker on its kept context,
  `TaskStop agent-N` stops one; workers ride the Background tasks pane and the tasks chip.
  `ChatViewModel.DeliverTaskNotification` is the shared delivery channel.
  **A notification is wrapped before the model sees it** (`Core/Agent/TaskNotifications.cs`,
  the reference's `NAe`/`$Vt`/`nSt`/`UQn`/`bbn` at 184560362): the event arrives on a user
  turn and would otherwise read as the person answering a pending question, so the
  reference prefixes "[SYSTEM NOTIFICATION - NOT USER INPUT]" and its paragraph inside the
  `<system-reminder>`, with a second wording for one delivered in the same turn as a
  message the user really typed. A closing tag inside the notification is escaped, so a
  task's own output cannot end the reminder around it. **The launch results are the
  reference's too**: `Agent run_in_background` answers with its `async_launched` block
  (`SubagentTool.AsyncLaunchResult`, measured at 188466905) — the internal-metadata
  warning, the `agentId` line naming SendMessage, the you-know-nothing-until-the-
  notification paragraph, and the `output_file` naming the agent's own JSONL transcript
  with the instruction not to read it (`Core/Agent/AgentTranscriptFile.cs` writes it under
  `%TEMP%\jarvis\{project}\{session}\agents`); a launch with no transcript file gets the
  reference's other tail. A backgrounded **shell** command answers the reference's own
  sentence run (`ShellTool.BackgroundResult`, at 188616140) — one of four openings for why
  it is in the background, then "Output is being written to: {path}.", the completion
  promise and "To check interim output, use Read on that file path." — with the output
  mirrored to `%TEMP%\jarvis\{project}\tasks\{id}.output`
  (`BackgroundTaskManager.OutputDirectoryFor`), and a command that changes directory is
  told the session's cwd did not move with it.
- **Oversize tool results are persisted, not sent** (`Core/Tools/ToolResultPersistence.cs`,
  the reference's `J`/`NV`/`Bfe`/`G5e` at 184899134): a result past its tool's threshold —
  a declared `maxResultSizeChars` capped at 50,000, else 400,000 — is written whole to
  `{project}/{session}/tool-results/{tool_use_id}.txt` and the model gets
  `<persisted-output>` naming the file with the first 2,000 characters as a preview, cut
  back to the last newline when that keeps more than half. An empty result becomes
  "({tool} completed with no output)", a result carrying images is never persisted, and a
  write that fails leaves the content as it was.
- **Two reminders follow a batch of tool results** (`Core/Agent/ToolResultReminders.cs`,
  the reference's `vBn`/`vQo` at 189099019 and 190257842): the **batching** reminder
  ("First privately list what you need next; …") after a batch none of whose results
  reports a refusal or an interruption, and the **silent-turn** reminder ("The user hasn't
  heard from you in a while. …") once five assistant turns have passed without a word to
  the user, at most three times between two genuine user messages. **They ride one
  trailing harness turn whose `content` is a single string**, the sections joined by a
  blank line and the `<total_tokens>` block after them — measured by driving CLI 2.1.257
  at a local listener, where a turn carrying both arrives as
  `{"role":"system","content":"{batching}\n\n{secondary}"}` appended as the **last**
  element of `messages`. There is no `<system-reminder>` wrapper and no per-section block;
  the names `batching_reminder` and `secondary_reminder` are the reference's own and never
  reach the wire (`AnthropicProvider.HarnessSystemText` is what serializes it, and the
  same string form carries the agent roster).
  **The model gate is absolute and sits ahead of the text**: `TBn` checks `fTe` — the
  mid-conversation system role — before resolving anything, so claude-opus-5 and
  claude-fable-5-1 carry the turn while claude-haiku-4-5 and claude-opus-4-5 carry no
  system role at all, whatever `CLAUDE_CODE_TOASTY_THIMBLE` /
  `CLAUDE_CODE_GENTLE_PARASOL` / `CLAUDE_CODE_SILENT_TURN_REMINDER` are set to. What an
  override buys is text for a model owning none of its own — only a
  `fable_5_1_prompt_bundle` model owns either (`KU`) — never an exemption from the gate.
  **What suppresses them is not symmetric**: the reference's `d` gates the batching
  reminder alone (`k = d ? QHo(…) : null` beside an ungated `T = JHo(…)`), so a refused
  call or a pending `queued_command`/`teammate_mailbox`/`poll_events` delivery silences
  the nudge to batch and leaves its secondary sibling standing; the shared `wBn` gate —
  the turn follows a user message of tool results — is what silences both. A refusal is
  carried structurally here (`ToolResultBlock.Refused`) rather than by prefix, because one
  of this build's refusal wordings opens with the tool's name where all eight of the
  reference's `e1o()` constants are fixed. **Only a refusal the user themselves gave
  counts**: each of those eight is a person declining or a turn being interrupted, and a
  configuration denial is neither — measured on CLI 2.1.257, where a settings deny rule
  answered "Permission to use Bash with command echo hi has been denied." and both
  reminders still rode the turn. So `IDenialReasonSource.TakeDenialWasUserRefusal` reports
  which it was and the gate marks only the three sites where the person answered (the two
  permission cards and the read-outside question's Block); a rule, `--restricted`, a hook
  and a run with nobody to ask all deny without suppressing. Each reminder's text is **latched per
  conversation and model** (its `VHo`/`KHo`), so a variable changed mid-conversation moves
  nothing. `Captures/HarnessTurn/reminder-wire-cli-2.1.257.json` records all eight
  configurations and `HarnessTurnWireParityTests` checks this build against them.
- **Where the reminder text may come from is a settings choice** (`Services/ReminderOverrides.cs`,
  Settings › General › Engine defaults): the reference resolves each reminder through an
  environment variable, then a `client_data` map its config endpoint serves per account,
  then the model's own text. There is no such endpoint here, and measurement found its
  server serving no value for either slot to any account, so **Reminder text overrides**
  switches the whole mechanism on or off and **Override source** picks between the
  reference's environment tier alone and that tier plus a local file in the same
  model-pattern-to-text shape (`reminder-overrides.json` in the profile, matched by
  `ToolResultReminders.MatchModelPattern`, a port of the reference's `_ce` — exact id
  first, then the glob with the most literal characters, then bare `*`). On with
  Environment variables is the default, which is what the reference does.
- **Background tasks pane** (`Views/Panels/BackgroundTasksPanel.xaml[.cs]` +
  `Services/BackgroundTaskPresentation.cs`, ported from the reference's tasks pane —
  chunk `c360a9e1c`, ~619k-640k, found by its own strings): the side panel titled
  **"Background tasks"**, a **Running** and a **Finished** section of cards (Finished
  collapsible with its row count and a **Clear** action, collapsed by default and sticky
  per session in `UiSettings.BackgroundFinishedExpandedBySession`; an empty section is
  hidden entirely), the `AgentsSimple` empty state **"Background work appears here"**, and
  one card per row: the title (shimmering while running, with a hover-only caret when it
  expands), a kind · status · elapsed line — kind from the reference map (Agent / Remote
  agent / Bash / Workflow / Monitor / Dream / Task / Loop / Preview), the status word only
  once a row leaves Running (Failed in danger), and a live 1s timer that settles into the
  run's duration — then a detail line (model, `{n} tokens`, `{n} tool uses`, the tool a run
  is in, **View transcript**), a Stop button carrying the reference's labels, and an
  expanded body with the shell command as a bash block plus the output. **Loops and preview
  servers ride the same pane**: "Loop" with its schedule and a `Next {countdown}`,
  "Preview" with Starting…/Stopping…, elapsed and `localhost:{port}`. Ported logic lives in
  `BackgroundTaskPresentation` (unit tested): the duration and countdown ladders, running
  sorted by `StartedAt` ascending and finished by `CompletedAt` descending, the output
  shaping (8000-char cap with the three truncation notices), and the chip count, which now
  adds the session's loops. Rows update in place across the tick so an open card keeps its
  scroll and selection, and clicking a background `Agent` row in the transcript opens
  the pane **at that row** — expanded, centred, unfolding Finished when it lives there.
  A **Workflow** row's expanded body opens on its phase list (above the command and
  output), and clearing a finished row drops the progress the host kept for it.
  **The pane navigates in place**, as the reference's does: "View transcript" on an agent
  row — and a click on any Agent row in the transcript — pushes a *subagent view* onto
  the pane's own stack (the reference's `subagentOpener` →
  `pushPaneView("tasks", {kind:"subagent", toolUseId, description})`, one opener for both
  entry points, which is why an agent row never expands in place). The header takes the
  agent's description — the call's own `description` argument from a transcript row, the
  row's title from the pane — and grows a back arrow that returns to the list; the pane
  keeps its rows and simply stops repainting them while the view is up. The view itself is
  the reference's: the model it ran on, the prompt it was given, the agent's own steps
  rendered by the transcript's DataTemplates (`ChatSurface.CreateSubagentTranscript` hands
  the pane one host element carrying this surface's resources, so the rows resolve those
  templates by the ordinary tree walk instead of duplicating them), then its report — in
  danger colour when it failed. **A background agent's report is not its tool result** —
  Agent answers a background call with a launch acknowledgement, so the report is the
  one the worker hands back when it finishes (`ToolCallItem.SubagentReport`, set from
  `OnWorkerFinished` by the worker's tool-use id), and until then the view says the agent
  has not reported yet. A run whose steps were never stored says so, which is what a
  session reopened from disk shows, since inner events and worker reports ride the turn and
  not the session file; one stopped or denied before it ran says that instead. Switching session pops the view, and a row whose call the transcript no longer
  holds leaves the pane on its list rather than opening an empty one.
  **An agent row is a plain button, not a toggle.** The reference's row is
  `role="button"` and drops `aria-expanded` on exactly the rows that open the pane
  (`ae = isAgentRow && hasOpener`), so the template keeps both controls and shows one: a
  `ToggleButton` bound to `IsExpanded` for rows that open in place, a `Button` for agent
  rows, sharing one `ToolRowHeaderContent` template (explicit `ContentTemplate` on both —
  the content is a ToolCallItem, and the implicit template would nest the row inside
  itself). A mouse press, Space or Enter, and an assistive tool's Invoke therefore all
  raise `Click` and open the view, and the agent row advertises no toggle it cannot
  honour. Its caret follows the same split: the reference draws a caret that always points
  right on an agent row (its `Sy`, `CaretRight`, and only once the agent settles) and the
  turning disclosure caret only on rows that expand (`My`, shown when `!ae`), so ours has
  a second `OpenCaret` style — a style trigger beats a setter in a derived style, so it
  cannot be `BasedOn` the turning one. The one delta left: the reference also routes
  *workflow* rows to `openTasksPaneAtTask`, where ours keeps `FocusTask` for the pane's
  poses and does not wire that click.
  Deliberate deltas: no workflow "detailed" rows, no remote-agent "View session" and no
  "View artifact" (neither exists here), and the reference's async output states
  ("Loading output…", "Output unavailable") cannot occur because our output is already in
  memory rather than read from a file.
- **Run in background** (`Core/BackgroundTasks/BackgroundMoveRequests.cs` +
  `BackgroundTaskManager.Adopt`): the reference's action for a tool call that is already
  running. A `shell` call registers its call id while it runs; the transcript group's
  "Run in background" button asks through `ChatViewModel.BackgroundMoves`, and the call
  hands its process to the task manager — output it has already produced comes across and
  the rest keeps streaming — returning "Moved to the background as task-N". The manager
  grew *adopted* tasks for it (no process of its own; a stop request is relayed to the
  owner). A call that has already finished gets the reference's "Tool call couldn't be
  moved to the background. Try again."
- **A command is over when its process is, not when its pipes close**
  (`Core/Tools/BuiltIn/ShellTool.cs`, `Services/SkillShellRunner.cs`): the reference
  resolves a shell call from the child's own `exit` event and never from `close`
  (claude.exe 2.1.258, `class YWe`: `once("exit")` beside `once("error")` and the
  timeout, then whatever the output sink holds at that moment), because a command may
  leave a **detached descendant that inherited the write end** of the redirected pipes —
  under `Start-Process` the child gets the parent's inheritable stdout handle whatever
  its own redirection says — and that pipe then reaches EOF only when the last
  descendant exits, which for a dev server is never. This port used to wait for that EOF
  with no timeout and an uncancellable read, so a turn that started one hung forever with
  nothing on screen to say why: `ShellTimeoutSeconds` guards only `WaitForExitAsync`,
  which had already returned on the shell's own clean exit. The reads now get
  `DrainAfterExit` (2s) to hand over what the OS already holds and are then abandoned to
  the process teardown, which is the reference's liveness and its output too — its
  default case writes to a temp file rather than a pipe (`pMo`: stdio `[stdin, fd, fd]`,
  pipes only where the caller passed an `onStdout`), so settling at exit truncates
  nothing there. Both the foreground call and the adopted background task settle this
  way, the latter being what its `background()` does.
- **Assistant message footer** (Code surface; `AssistantFooterItem` +
  its ChatSurface template): the reference's message action bar, in its order —
  **Copy · Fork from here · 👍 · 👎 · Run in background · {model} · {relative time}** (mined
  from the epitaxy call site: the reference also passes copy-link, Reply, reactions and
  Pin as chapter, all of which are cloud/teammates features this app does not have, and no
  Retry — that stays Chat-only). Its clock and the user bubble's follow the reference's
  `timeFormat` / `timeZone` settings (`TranscriptTime.Stamp`): "auto" keeps the relative
  clock, "12-hour", "24-hour" and "24-hour-utc" show a wall clock, a strftime pattern is
  honoured directive by directive, and an IANA zone moves them. It is the turn's own transcript item: `BeginTurnFooter`
  opens it when the turn starts, everything the turn produces is inserted *above* it, and
  `EndTurnFooter` settles it with the answer to copy, the finishing time and the message
  count a fork cuts at. Reopening a session rebuilds one per assistant message. Like the
  reference's `Xf` container it is hover-gated — including while a call is running, which is
  when "Run in background" appears in it (the reference is hover-gated there too). Fork
  from here copies the conversation *through* this answer into a new session
  (`ChatViewModel.CopySessionUpToAsync`, shared with the user bubble's branch).
- **Preview & PR watching** (`Services/PreviewTools.cs`, `Services/PrActivityTools.cs`):
  `preview_start` runs a `.jarvis/launch.json` configuration (`.claude/launch.json` compat;
  url-only opens directly, url-no-command attaches) as a background task and points the
  Browser panel at it; `preview_logs`/`preview_stop`/`preview_list` manage it.
  **The port is settled before anything is launched** (`Services/PreviewPorts.cs` over
  `Services/PreviewPortProbe.cs`, the reference's `Jor` over `qor`/`Gor`/`Kor` in app.asar
  `index.chunk-BHbE7U4N.js`): `autoPort` is a **tri-state**, and each arm is a different
  answer rather than a boolean's two — `true` binds a fresh OS-assigned port and hands it to
  the server through `PORT`, which is what its own advice promises; `false` says the port is
  required and names what to stop; and an absent field asks the user to choose between those
  two. A port held by *another session's* preview server is its own arm again, because
  `preview_stop` cannot reach another session's server, and a failed reassignment is two more.
  All seven sentences are the reference's, byte-checked by the ported-text suite, and a
  launch.json `name` is quote-neutralised before it is interpolated into any of them (its
  `jM`: first line only, quote-likes to an apostrophe, control and lone-surrogate characters
  to U+FFFD, 120 code points, ellipsis on any change) — a code-point scan rather than a regex,
  since .NET's `\p{Cs}` matches each half of a pair where the reference's `u`-flagged class
  means a lone one. Binding is a real bind-and-release and the external holder is named from
  `GetExtendedTcpTable`; where that cannot answer, the unnamed branch is taken, which is the
  branch the reference itself takes when its native `listTcpListeners` binding is absent. The
  entry's `env` rides the started process (`BackgroundTaskManager.Start` grew an additive
  `environment`), and a reassigned port moves a localhost `url` with it. **What port an entry
  runs on is the reference's own chain** (`PreviewServers.ResolvePort` /
  `PortFromCommand`, measured on desktop 1.46388.2.0): the entry's own `port`, then the port
  its `url` names, then whatever its command spells out, then 3000 for an entry that has a
  command at all. The command search is its `extractPortFromCommand` — `env.PORT` outright,
  then `--port`/`-p` taking the next token or its own `=` suffix, then three patterns each
  tried across every token before the next pattern is tried at all, over `program`,
  `runtimeExecutable`, `runtimeArgs` and `args` alike. One adaptation is declared in
  `Deltas/reference-surface-deltas.tsv`: the messages name the launch file that was actually
  read rather than the reference's hardcoded `.claude/launch.json` — the same swap the
  closed-pane answer makes, and the reason advice naming the other file would be an edit
  that changes nothing.
  `subscribe_pr_activity` polls `gh` each minute and delivers new PR comments, failed or
  timed-out checks, and close/reopen/merge as task notifications (CI success, pushes and
  merge-conflict transitions deliberately don't arrive, per the reference);
  `/pr-comments` fetches a PR's comments through the model. App-level tools re-join on
  every session switch via `ChatSurface.ViewModelBound` (they used to drop after a rebind).
  Sandbox parity is deliberate absence: the reference's Windows sandbox is WSL2-based and
  is neither enabled in the installed build nor configured on this machine.
- **Chat response chrome**: assistant messages on the Chat surface grow the
  reference action bar (hover: copy-with-tick · Good response · Bad response ·
  **Read aloud** · Retry — retry cuts back to the prompt and regenerates; feedback is a
  local toggle), user bubbles swap rewind/branch for the reference "Edit message" (cut
  back + refill the composer; rewind/branch stay Code-only), and the exact "Jarvis is AI
  and can make mistakes. Please double-check responses." line sits under the composer.
  Read aloud (`Services/ReadAloud.cs`) swaps to **Pause** while it speaks, reads one
  answer at a time, and flattens the markdown first — fenced code goes, a link keeps its
  text, and the marks that only mean something on screen are dropped, because a voice
  saying "asterisk asterisk" is worse than no button. The reference speaks through a
  claude.ai voice; this is the Windows synthesizer, which is what an offline app has.
- **An unfinished chat turn says so** (`Services/ChatTurnNotices.cs` +
  `ViewModels.ChatErrorItem`, ported from the reference's own card in
  `c3e2391e3-3lB_ip9x.js`): a stopped turn ends with "Jarvis's response was interrupted."
  and a failed one with "Jarvis couldn't finish this response. Try again in a moment.",
  each over the two actions the reference offers — **Edit prompt**, which it shows only
  for a turn the user stopped, and **Try again**. The Code surface keeps the CLI's
  `[Request interrupted by user]` line instead, which is what the reference shows there.
  Its third state, a lost stream with "Check now", is declared: it polls a server-held
  conversation this client has no equivalent of.
- **The Chat surface's waiting line is its own component** (`Services/ChatStatusLabels.cs`
  + `ViewModels/ChatTurnStatus.cs` + `Controls/ChatStatusLine.cs`, ported from
  `ca2ef848d-D8BWZk64.js`): the reference runs `TurnStatusLine`'s epitaxy indicator on
  Code and this one-sentence line on Chat, and they share neither labels, timings nor
  layout — there is no spinner, no elapsed clock and no token counter here. Five seconds
  into a wait one of the five-entry pick list appears ("Gathering my thoughts…",
  "Contemplating…", "Pondering…" twice over, "Ruminating…"), fifteen seconds in it becomes
  "Still working on it…" and thirty seconds in "A bit longer…"; the line stands down the
  moment the answer starts arriving, which is the reference gating it on the streaming
  message's content length rather than on the turn. A transport retry replaces it with the
  cause and a countdown — "Rate limit reached. Retrying in 3s (attempt 2 of 4)" — which is
  live because `ProviderHttp` now reports each scheduled retry through
  `Core/Providers/ProviderRetries.cs`, an `AsyncLocal` sink so a notice raised inside a
  provider lands on the turn that provoked it. Compaction replaces both with the
  reference's indicator: its sentence over a 192×4 bar filled to
  `round(min(95, 100·(1 − e^(−ms/25000))))`, kept monotonic, 100 only once the pass ends.
- **The empty chat screen is the greeting alone** (`Services/ChatWelcome.cs`,
  measured against desktop 1.44121.2.0's `ca2ef848d-C_oPm_EH.js`): its `oT` is a
  centred `figure` carrying one `h2` — the mascot at `state="waiting"` beside
  "What can I help you with today?" — and nothing else, so this port's
  Write/Learn/Code/Life-stuff chips are gone with it. The heading is its `font-title`
  (the UI serif at **28px**, weight 500, `line-height:1.3`) with the mascot `gap-2`
  away, and its `flex-col … sm:flex-row` stacks the two into a column below **640px**;
  the whole screen arrives on `animate-empty-chat-fade`, a 300ms fade on
  `cubic-bezier(.25,.1,.35,1)` after a 200ms delay, which a reduced-motion reader does
  not get. The first chat of all gets the onboarding block instead (its `fT`):
  **left-aligned at the top of the column**, `pt-4` over `px-4`, with the greeting
  ("Welcome, {name}! I'm Jarvis."), then "Bring me anything—…" `mt-1.5` under it, then
  "Where do you want to start?" `gap-3` under that, and the mascot below at
  `mt-5` + `mt-5 pl-4`. The reference gates the onboarding on a server-side rollout
  variant this client cannot read, which is declared.
  **The paragraphs are revealed one at a time, and each waits for the one above it**
  (`Controls/FadeInText.cs` over `Services/FadeInWords.cs`, its whole
  `c4b7ce3b5-DNSas4Nx.js` and that chunk's stylesheet): not a typewriter but a
  per-word fade — one element per word, each fading 0→1 over **400ms ease-out** at its
  own delay, on a schedule that *decelerates* (`max(50, 150 / max(1, remaining / 2))`,
  so the opening words sit on the 50ms floor and the last take 150ms each), reporting
  completion **400ms** after the final word starts. Its `**bold**` toggle is carried
  too. The mascot writes until the last paragraph lands and then goes idle, which is
  the reference's `S = y ? "idle" : "writing"`.
  **The mascot is the CDS Spark, and it can be poked** (`Controls/SparkGlyph.cs` and
  `Controls/PokeableSpark.cs` over `Services/SparkStates.cs`): the reference's sprite
  is a vertical strip of frames stepped by `steps(frameCount, jump-none)` over
  `speed × frameCount` ms, looping except for the one-shot set `{entrance, exit,
  tickle}`, which holds its last frame (`fill: "forwards"`) and then calls back, with
  `idle` — and a reduced-motion reader — getting the static mark. All seven states and
  their measured constants ride here (waiting 16×600, thinking 9×90, writing 8×90,
  entrance/exit 6×70, tickle 7×40) at its own default width of 32. A press bumps a
  counter, flips the sprite to `tickle` and changes the tooltip, which is the
  reference's own five-rung ladder (`q_`, hardcoded rather than catalogued): past 5
  "Yes, yes. What can I do for you?", past 12 "Are you still doing that?", past 18
  "Alright, alright, you have my attention!", 25 to 31 "Ugh, well you can't do that
  forever", and otherwise the greeting — there is no arm above 31, so the
  thirty-second poke wraps back to it. A busy mascot neither tickles nor talks
  (its `state === "thinking" || state === "writing"`). What is deliberately not
  carried is the strip itself: those frames are Anthropic's artwork of the Claude
  mark, so this steps the active theme's own mark through the reference's schedule,
  declared in `Deltas/reference-surface-deltas.tsv`. An
  **incognito chat** (Ctrl+Shift+I, the reference's own chord on Chat) opens on "You're
  incognito" with "Incognito chats aren't saved to history." under it, and is never
  written to the session store — `ChatViewModel.Incognito` routes every save through a
  guard rather than leaving one call site to forget.
- **A new chat can open side by side** (`Views/Chat/ComparisonView.cs` over
  `Services/ComparisonSession.cs` and `Services/ComparisonStrings.cs`, ported from
  the reference's `ComparisonRoute` — ion-dist chunk `cf6c0e3a1-Bi64U0E_.js`, where
  it sits behind two rollout gates and the account's `iron_swift_default_opt_out`,
  which is the **New chat view** setting this app already carried and which now
  decides something). One prompt, two models, two real conversations named the
  reference's way ("Compare 1 (model): {first 48 characters}", the model left out
  while the names are hidden). Its 52px header is the reference's — "Model
  Comparison", then **Hide model names** (on by default, its
  `iron_swift_hide_model_names`; turning it on clears both arms' memory-off and
  reshuffles the sides, it is refused with the reference's own sentence while a
  memory-off arm is running, and it is drawn at all only when there are two models
  to hide — the reference gates it on its own rollout list holding two, which here
  reads as the models the user added), **Hold responses** (its `iron_swift_hold_responses`,
  which covers both panels until both finish), the "{n} vote/votes this session"
  counter and **New chat**. Each panel carries its own model picker — which never
  rewrites the account's default — its own ⚙ **Panel settings** popover over the
  memory switch that is fixed once the chat starts, its own read-only transcript,
  and its own error strip; an empty one is its label over "Both models answer the
  same prompt, independently. Vote on each pair." The footer walks the reference's
  phases under one composer: **vote** ("Which response do you prefer?" over ◀ A ·
  Tie · B ▶, also on a bare ArrowLeft/ArrowRight), **reason** ("Why {label}?" or
  "What made it a tie?" over its two default chip lists and a comment box), and
  **saved** ("Preference recorded — {winner} · {reasons lowercased}" with Change).
  `ComparisonSession` is that state machine with the reference's guards intact — a
  vote saves once per turn, an errored pair asks for no preference, sending out of
  the reason phase saves and out of the vote phase skips — and it is unit tested
  without a window. The sides are reversed on a coin flip before the first turn, so
  the left column carries no information.
  `--open=comparison[:vote|:reason|:saved|:send]` poses it, the last by really
  running both arms. What is deliberately not carried is declared in
  `Deltas/reference-surface-deltas.tsv`: the vote's destination
  (`SaveComparisonFeedback` under an organization uuid), the clipboard report and
  its share links, the rollout-configured reason vocabulary, the cross-panel tool
  approval and its permission-asymmetry flag (the Chat surface has no tools), the
  reference's own composer behind `hideModelSelector`, the per-panel effort its
  picker also sets (this Chat surface has no effort control), and its
  internal-account footnote. One local decision beside those: the reference draws a
  bare 52px strip when no model is selectable, which here would be a new chat with
  nowhere to go, so an installation with no model added yet gets the ordinary
  greeting instead.
- **The Chat composer's menus are the reference's** (`Services/ChatComposerMenu.cs`,
  unit tested): the "+" menu is its three groups in its order — Add files or photos ·
  Take a screenshot · Add to project, then Skills · Connectors · Plugins, then Web
  search — separated by the two rules its own grouping table (`shared-10-3-tqq7pk.js`,
  `QS`/`JS`) puts between them, and a group with nothing in it takes no rule with it.
  Connectors carries the per-conversation toggles with "Manage connectors" leading and
  the **Tool access** submenu closing it, both of its rows with the reference's hints
  ("Chats compact less since tools aren't pre-loaded."); a disconnected connector is
  counted on the row ("2 need reconnection"). "Add to project" is a searchable submenu
  over the projects with "Start a new project" in its footer.
  **Extended thinking is a row of the model menu, not a chip on the chin**: the
  reference renders its thinking control between the model list and the effort menu
  (its `Ar` after `gT` in `shared-11-CL4cxK09.js`, drawn by `jg` in
  `shared-8-DYJ3OSaf.js` as a `keepOpen` item with a trailing switch), so the chin
  carries nothing for it here either and Ctrl+Shift+E still flips it. Two pieces of
  that row are declared rather than built — its `mt-1 text-footnote text-muted`
  description and its list of more than one mode, both of which come from the
  account's model catalogue this build does not ship. The
  model menu also grows the reference's **Search models** box and its "No matches" row,
  and the model chip reads out as "Model: {name}". Ctrl+Shift+. opens the model menu and
  Ctrl+U the file picker, which is where the reference puts them on Chat.
- **The composer's placeholder is a ladder** (`Services/ChatPlaceholders.cs`, unit
  tested; the reference's `XF` in `shared-11-CL4cxK09.js`): "How can I help you today?"
  is only its **new-conversation** rung, and this port used to send that one sentence
  for every state of the surface — the one rung a reader would never see. In order:
  the voice session ("Connecting..." · "Listening..." · "Processing..." ·
  "Jarvis is speaking..." · "Reconnecting..."), which outranks everything; a supplied
  placeholder; "Something else" for the role picker; the hidden question itself;
  "Or reply directly…"; "Address these review comments"; the new conversation;
  "Reply at any time, even when Jarvis is working"; "Reply…"; and finally
  **"Write a message…"**, which is what a conversation that has started reads.
- **The send button is the reference's primary action** (`Services/ChatComposerActions.cs`):
  its `lx` is `icon:"ArrowUp"` on `variant:"brand"` — a filled accent button, not the
  ghost return arrow this port used to draw, which is its own `replyLook` variant — and
  while a turn runs it swaps for the secondary Stop, which advertises Escape and reads
  out as "Stop response" over the tooltip "Stop Jarvis response".
  **A turn that is still running does not take Enter away** (`ChatComposerActions.ShowsStop`,
  read off the reference's `_y` and `fy()` in `shared-11-bvIrsIEM.js`, desktop 1.44121.4.0):
  its composer keeps `sendShortcut:"enter"` for the whole time an answer streams, offers
  `Interrupt` on cmd+enter beside it, and hands the button's slot back to the primary Send
  the moment there is something to send — `O = hasDraftContent && !sendDisabled` — leaving
  Stop there only while the composer is empty, since a session composer has
  `canQueueMessages` (`isSessionConversation || isLocalAgentRoute`). So Enter submits into
  the queue that folds into the running turn, Stop is the button on an empty composer, Esc,
  or **Ctrl+Enter**, and draft content is a typed prompt or an attachment (its `ie`). This
  port used to answer Enter with `CancelTurn`, which killed the turn and left the prompt
  sitting in the box.
- **Projects** (`Services/ProjectStore.cs` + `Services/ProjectInstructionsBlock.cs`):
  a chat can be filed under a project kept in the App's own `projects.json` — a name, a
  description, standing instructions, context folders and links — and the project's block
  rides that conversation's system prompt. The block is the reference's own builder
  (`shared-5--MfpzEVV.js`, its `uV`) ported byte for byte, including the three escapers it
  composes: a url keeps everything but its angle brackets, text inside a tag takes the four
  XML entities, and a name is NFKD-normalized first — stepping over U+202F and U+2033,
  which normalize into something else and carry meaning in a name. Links ride their own
  `<project_links>` block under a 2000-character budget, with the rest counted rather than
  sent. This is the desktop's local-project wording ("their local project"), not
  claude.ai's server-side project prompt: the desktop builds it in the client, which is
  why it can be read at all.
- **The chat list and the activity drawer**: the sidebar's Chat list is the reference's two
  lists composed (`Services/ChatListSections.cs`, unit tested) — a collapsible **Starred**
  section with the hover-revealed Show/Hide from `c71dee58b-DV0t1uRZ.js`, then the recents
  in the day buckets of `shared-17-BG9iAXbK.js`'s `OD` (Today, Yesterday, then the date,
  and one "Older" past the seventh midnight), twenty at a time behind a "Show more", with
  both sections standing down while a search is running. Rows carry the reference's own
  labels there — Star/Unstar, Rename chat, Delete chat. The **activity drawer**
  (`Services/ChatActivityPanel.cs`) is the reference's detail panel: a title, an uppercase
  count line, the searches newest first with their results, and its centred "No web
  searches yet". It lists the searches this engine can see — our web_search and the
  reference's WebSearch, both of which pass a query and return links — and a search
  Anthropic runs server-side reports only its name, which is declared.
- **Two markdown renderers, because the reference ships two**
  (`Controls/MarkdownMetrics.cs`, `Controls/MarkdownView.cs`): the installed desktop
  renders a chat conversation with `standard-markdown` and a Claude Code transcript
  with `epitaxy-markdown`, and they disagree on nearly every value — so one renderer
  cannot be faithful to both. `MarkdownProfile` picks which, bound from the surface
  through `SurfaceMarkdownProfileConverter`, and every constant is measured rather
  than guessed: Chat is Anthropic's serif at 16/24 with a 12px block gap, headings
  22/18/16/16/14/14 (bold, h6 semibold), `**strong**` at 700, an inline code chip in
  **danger-000 with a 0.5px border** at radius 6.4, tables ruled with nothing but a
  bottom line, and a code card on `bg-000/50`; Code is the UI sans at 14/20 with a
  10px gap (`--chat-item-gap` = body × leading × .5), headings 16/15/14 (semibold,
  h4-h6 medium), `**strong**` at **medium**, a chip in the text colour with no border
  at radius 4, and tables as rounded filled tiles (`border-spacing:2px`, th on `--t2`,
  td on `--t1`, radius `--r2`). The tint ramp `--t1`..`--t4` and the chat renderer's
  own compositions are derived in `Theming/ThemeService.cs`, so all 97 palettes keep
  their own look.
  **Neither renderer draws a header bar over a fence** — both set
  `disableFileHeader` and float an icon-only action cluster over the top-right corner
  (`Controls/CodeBlockCard.cs`). Chat fades that cluster in on hover, prints the
  fence's language above the code in `text-500` and only when the fence named one,
  and wraps only an untagged fence; Code shows the cluster always, prints no language,
  always wraps, shrinks the block to its content, and adds Run in terminal / Open in
  terminal (`TerminalView.Type` types without running) beside Copy, which flips to a
  tick and resets after the reference's 1200ms. Credential-shaped spans in a Code
  fence are covered until the reader reveals them (`Controls/CodeSecrets.cs`); a
  masked block copies what is on screen and refuses to run.
  **A `mermaid` fence is a diagram, in the chat renderer alone**
  (`Services/MermaidDiagrams.cs`, `Services/MermaidRenderer.cs`,
  `Controls/MermaidBlock.cs`, `Assets/Mermaid/`): its CodeBlock returns the
  diagram component before it builds any of the code chrome, which is why a
  rendered fence has no copy button in the reference either, and the transcript
  renderer has no such branch at all. mermaid **11.16.1** comes from npm (MIT,
  vendored with its licence and its provenance under `Assets/Mermaid/`) and is
  byte for byte the build the reference ships, which is what lets a copy taken
  from the package be checked against what the reference renders with —
  `MermaidHostPageParityTests` compares the two by hash. What runs is the
  reference's own module (`Ql` and its sandbox helper `zl` in ion-dist chunk
  `shared-12-4ZL7iq1e.js`, behind `claude_ai_markdown_mermaid_render`): its
  config (`startOnLoad` false, `htmlLabels` false, `maxTextSize` 20000,
  `maxEdges` 400, `theme` "base", `securityLevel` "sandbox" over its twelve-key
  `secure` allowlist, and `fontFamily` resolved off the body the way it resolves
  its own `"inherit"`), its two measured `themeVariables` tables, its sandbox
  iframe with the CSP forced onto it, its 5s render race, and its preprocessing —
  CRLF normalised, front matter and `%%{…}%%` directives lifted out and put back,
  `@{…}` shape data stripped over at most 32 passes with its two refusals, and a
  block diagram's `space:N` clamped at 100. A diagram is cached by mode and source
  and re-rendered when the window width moves past the reference's 120px
  tolerance. Two deliberate deltas are declared in
  `Deltas/reference-surface-deltas.tsv`: the reference injects the SVG into its
  own document, where an engine view in WPF is an HWND island that paints over its
  ScrollViewer instead of clipping to it — so one hidden view renders every
  diagram in the app and hands back a bitmap, and the diagram's text is not
  selectable — and with nothing injected there is no DOMPurify pass to run over
  it. Everything around it is the reference's: the card at `p-4` with the code
  card's radius, border and fill, the diagram centred and capped at the column,
  the 96px pulsing placeholder that keeps the height the block already had, and
  the plain unhighlighted `<pre>` a fence falls back to while the answer streams
  and again whenever a render fails. `--open=markdown:mermaid` poses all three.
  **The transcript's text size is the reader's** (`Controls/MarkdownMetrics.cs`,
  Settings › Themes › "Transcript text size"): the reference offers Small/Medium/Large
  and applies them with `data-chat-text-size`, deriving everything else from the
  body size, so this port derives them too rather than storing three tables —
  leading is 10/7 of the body, headings 8/7 and 15/14 of it, `--text-code` is
  13/14, the block gap is half of body × leading and the footnote size is
  max(12px, body − 2px). The stylesheet writes those ratios truncated (1.42857,
  1.14286, 1.07143); the exact fractions are used here so the default 14px body
  still lands on 20px rather than 19.99998. The chat renderer has no such
  setting, so it ignores one.
  **Two elements only the transcript has**: a literal `<kbd>` tag, which its own
  inline scanner reads with the reference's 64-character cap on the content, and
  `~x~` single-tilde strikethrough — the chat renderer passes remark-gfm
  `{singleTilde:false}` and has no kbd element, so its dialect leaves both as
  text. **GFM itself is dropped while a turn streams** and added back when the
  message settles (the reference's `gt` against `mt`), so a table arrives whole
  instead of as a run of half-formed rows, and a streaming table's columns are
  pinned to the widths they have already had so a new row cannot make every
  column jump. **Footnotes** render the reference's way: a reference is a
  bracketed superscript rather than a link, and a definition sits at the
  footnote size in the secondary colour. A fenced block past **204800 bytes**
  renders unhighlighted, the reference's own limit, checked on characters first
  and then on UTF-8 bytes.
  **The Code block is really `w-fit`**: its width is measured from the longest
  line at the code font rather than left to fill the column, which is what makes
  a short fence sit narrow the way the reference's does.
  **The transcript's right-click menu carries the reference's code and link
  entries** (`Controls/TextSelectionScope.cs`): Copy code block, Copy code, Copy
  path, Open in default browser and Copy link, each shown by what was clicked.
  The reference reads that off the element rather than sniffing the text — a
  block carries `data-code-text`, a chip `data-epitaxy-inline-code`, a file
  reference `data-epitaxy-file-ref` — so this port marks its own chips and cards
  with a `CopyKind` instead of guessing from their content.
  **The chat thinking cell uses the chat renderer**, not the transcript one: the
  reference renders it with StandardMarkdown at `typography:"inherit"`, which
  keeps that renderer's rules while taking the size from the cell around it, and
  `Controls/ThinkingCell.cs` now does the same.
- **The parser is CommonMark with GFM** (`Core/Markdown/MarkdownParser.cs`,
  `Core/Markdown/MarkdownAst.cs`): nested lists and quotes as real blocks, list-item
  continuation lines and child blocks, setext headings, backslash escapes, underscore
  emphasis with CommonMark's flanking rules (so `snake_case_name` stays literal),
  `***both***`, inline styles that recurse through links and emphasis, GFM autolinks
  with its trailing-punctuation rule, images, pipe tables with `\|` escapes and
  `:---:` alignment, and hard breaks. `MarkdownOptions.Conversation` is what both
  reference renderers use: it adds **remark-breaks**, so a single newline inside a
  paragraph is a line break rather than a space — the difference that shows up in
  almost every multi-line answer. A marker change ends a list, as CommonMark says.
- **Streaming follows the reference, which draws no caret**
  (`Controls/StreamingMarkdown.cs`): a construct still being typed is held back so
  its delimiters never flash as literal text (`holdBack`), and the characters that
  arrived since the last render fade in — the trailing run is split at the delta
  boundary so settled text never re-animates. An unterminated fence is content the
  reference does render, so only an opener with nothing under it yet is withheld.
  The blinking accent caret this port used to draw is gone: a sweep of the whole
  reference bundle finds `caret-blink` on input fields only, never on a response.
  Math renders via WpfMath, images sit behind the reference's "Show Image" button
  until the user allows the fetch, a `#rrggbb` code span carries the reference's
  colour swatch, and each block takes its writing direction from its first 80
  characters, as the reference's `dir` does.
- **Chat thinking cell** (`Controls/ThinkingCell.cs` + `Services/ThinkingLabels.cs`, ported
  from the reference conversation renderer's `Pg`/`Bg` and the timeline row `Jc`): the Chat
  surface renders thinking as the reference's conversation cell, not as the Code surface's
  italic block. While the turn streams, the body is visible, markdown-rendered (muted, one
  heading level down) and never clamped; when it settles it collapses behind a header —
  "Thought for 12s" / "1m 4s" / "2h 5m", or "Thought process" when nothing timed it — which
  starts closed, reveals its caret on hover or focus, and opens the cell. Inside, a body over
  **200px** is clipped under a 40px gradient with a hover-revealed **Show more** / **Show
  less** (300ms `cubic-bezier(0,0,.2,1)`, ported as `CubicBezierEase` since WPF has no CSS
  curve). The header's duration is the sum of the message's thinking blocks, each timed from
  its own deltas — never a wall clock over the turn — and stamped onto the stored blocks
  (`ThinkingBlock.DurationSeconds`, additive; `Services/ThinkingDurations.cs`) so a reopened
  session still reads "Thought for …". An empty block renders nothing, `redacted_thinking`
  carries no text and so shows no cell (and adds nothing to the header), and the Normal
  transcript view hides thinking only on the Code surface, matching the reference's gate.
  Two deliberate deltas: the reference's icon is a glyph of its own font (Anthropicons,
  `ExtendedThinking`, U+E068) that cannot be shipped, so the gutter carries lucide sparkles
  at the same size and colour; and the "Copied" toast has no home in this app, where the
  button's tick already confirms.
- **Transcript affordances** (`Controls/TextSelectionScope.cs`, `ViewModels/TranscriptItems.cs`):
  the right-click menu is the reference's own list in its own order (`cf6337626-CjEKXdRe.js`)
  — the code entry (Copy code block / Copy path / Copy code), the link group (Open in default
  browser · Open in Browser pane · Copy link), **Copy message** and **Copy message as
  Markdown**, then the attach row worded by what is selected ("Attach selection as context"
  against "Attach message as context"), and finally **Rewind to here** and **Fork from
  here**. An image offers **Copy image** and **Save image**, as the reference's own image
  menu does. A **chapter divider** renames in place on a double-click (its field's accessible
  name is "Chapter title", Enter commits, Escape restores) and can be hidden; both are
  remembered per session in `ui-settings`. A **compaction** draws the reference's row in its
  three forms — "Compacted session · saved {n} tokens" when both sides are known, "· from {n}
  tokens" when only the first is, and the bare sentence otherwise. A **digit picks an option**
  while a question card is up, the **arrow and page keys** move the transcript, the
  scroll-to-bottom pill names itself **"New messages"** once something arrived while the
  reader was away, and a filename clicked in an Edited or Wrote row opens in the diff pane
  rather than in Explorer. **spawn_task suggestions** moved from chips above the composer to
  a notification in the transcript's top-right corner, which spins the task off into its own
  session or hands its prompt to this one.
- **The transcript is virtualized** (`Controls/VirtualizingTranscriptPanel.cs` over
  `Services/TranscriptWindow.cs`, `Services/TranscriptOffsets.cs`,
  `Services/TranscriptRowEstimates.cs`, `Services/TranscriptEntrySplit.cs`,
  `Services/TranscriptViewportStore.cs`, `Services/TranscriptPerfCounters.cs`): the
  reference runs **one** virtualizer under both of its transcripts (ion-dist
  `ccb6edd6b-BJ2sSjUO.js`, driven by its Code transcript `cd5a31703-Bbz821zb.js`
  and its Chat transcript `ca2ef848d-BDGEfEJa.js`) and so does this — where this
  port used to lay every row of a session into one `StackPanel` and pay for all of
  it on open. **The range function is ported step for step**: the window is the
  viewport plus an overscan of the reference's `c0` = **600px** above and below,
  spent as a *pixel budget over whole rows* rather than as a row count (one 900px
  answer above the fold is the whole budget, and so are forty short tool rows),
  and then the two end-snaps — within **two viewports** of either end the window
  reaches it, so the ends of a long transcript feel solid instead of paging in.
  A **first-paint window** (its `initialWindow`) opens a session building downward
  only and releases the full overscan after its own **2 presented frames**, which
  is counted here on `CompositionTarget.Rendering` rather than on layout passes.
  What a row is worth before anybody builds it is its `e0`/`i0` estimator, constant
  for constant: a line 20, a blank line or a fence marker 10, a table row +8, 7.5px
  to the character divided again by the reader's text size, a user bubble clamped
  at 256 and an entry at 4000, a tool run its visible rows × 40, a thinking cell 24,
  a chapter 48, a compaction 40. Its kinds are not this port's — a claude.ai channel
  row has no counterpart here — so a kind takes the reference's nearest and anything
  text-bearing is measured its way instead of given a constant. Its `shouldSplitEntry`
  (`n0` over `t0`: **16 items or 2000px**) decides whether a list is windowed at all,
  so a short session and a short tool run are built whole, which is both cheaper and
  steadier than windowing them. **Every list the transcript can grow without bound
  goes through it** — the run's calls, the bodies, a diff's lines, an argument list —
  because the row that would not fit is the one the reader just expanded.
  A **measured height replaces its estimate past 0.5px** and a row that measured as
  an empty box is skipped and counted rather than stored as zero; when a row above
  the reader resizes, the scroll offset is corrected by the difference the way its
  `onAnchorCompensation` corrects it, standing down while the disclosure anchor owns
  the click that caused the resize. Its **per-session snapshot** is carried whole
  (`{isPinned, anchorKey, anchorOffsetPx, sizes, viewport}`, in memory for the run as
  it is there), so stepping to another session and back neither re-measures a
  thousand rows nor lands somewhere else — and it is dropped rather than restored
  when the column or the text size has moved, because a height taken at one width
  describes no other. Its **`hasOlder`/`loadOlder` paging** is carried onto the local
  session file: a session opens on its last `TranscriptPageSize` (400) messages and
  takes the page above when the reader comes within a viewport of the top or the
  content does not fill it, with Find and Select all asking for the whole
  conversation first. A prompt's number is counted from the top of the session, so a
  page that starts mid-history still numbers its prompts correctly. The offsets are
  prefix sums rebuilt lazily behind a dirty flag, and a row is keyed by its position
  in the stored session (`Services/TranscriptKeys.cs`) — which is what lets a page of
  older history arrive above a row without costing it its measured height or the
  anchor that names it. `--transcript-selftest` drives the lot on the real controls
  and exits 0/2: a screenshot cannot tell 2000 built rows from 11, which is the whole
  point of the change. Measured there: **1500 rows, 11 built** over a quarter-million
  pixels of extent, an expanded 300-call run built 35 rows deep, and 150-230ms to
  construct every view model against 30-40ms a screen to scroll. Four deliberate
  deltas are declared in `Deltas/reference-surface-deltas.tsv` — its momentum-scroll
  `translateY` compensation (WPF has no inertial scroll to fight, so the write its
  own code takes off a gesture is always taken), the five inputs of its
  `keepMountedRef` set this port does not compute (it carries the two it measured -
  the tail of `Zg = 3` rows and the focused row - and holds rows for a live drag
  selection besides, which the reference has no counterpart for), its paging's
  server fetch, and its snapshot keys, which are server
  uuids where these are re-issued on every rebuild and so are pruned on every change
  rather than only when the list shrinks.
- **Transcript text selection** (`Controls/TextSelectionScope.cs`): browser-style selection
  over the whole transcript — drag across paragraphs, messages, fences and tool rows
  (per-line highlight on an overlay canvas that scrolls with the content), double-click a
  word, triple-click a block, Shift+click extends, and a drag past the viewport edge
  auto-scrolls. Ctrl+C / right-click → Copy / Select all reassemble the covered text in
  reading order: LaTeX rides its paragraph as an embedded element and is stitched back in
  (the `CopyText` attached property carries its source), and islands
  sharing a visual row (list marker + item, table cells) join with a space.
  **An inline code chip selects like the text it is** (`ChipTextIsland`): the chip is an
  `InlineUIContainer`, which WPF counts as one symbol, so it used to be an atomic blob —
  a drag inside it selected the whole thing and a double-click on it selected nothing at
  all, where the reference renders a chip as an ordinary `<code>` and both work per
  character. A paragraph carrying chips therefore gets an island that lays a second offset
  space over it — its runs at their own length, each chip expanded to the length of its own
  text, and everything else it embeds still one offset wide — and maps points, highlight
  rectangles, words and copied text through it. A chip covered end to end highlights as its
  whole inline box, padding included; a partial one from its own glyphs. Presses on
  buttons, links, scrollbars or inside a natively selectable box (fences, tool results)
  are left alone; a focused box with its own selection keeps Ctrl+C, and the terminal
  always does.
- **Reference desktop parity, round 2** (all ported against the installed app's strings):
  **composer attachments** — right-click a message or a transcript selection → "Attach as
  context" (chip above the input, folded into the next message), Ctrl+Shift+L attaching the
  terminal's selected output (file picker with no selection), the Browser pane's element
  picker attaching the picked element (page · tag · selector · text · markup) as context,
  and the diff pane's file rows carrying Open / Copy path / Attach as context (an
  @-mention), Ctrl+V attaches clipboard
  images (≤20/message, sent as image blocks) and files, pastes >2k chars become a "Pasted
  text" chip, drag-drop and "Add files or photos" split into image chips and @-mentions
  (`ViewModels/ComposerAttachment.cs`); **transcript nav** — floating Scroll-to-bottom
  pill, Ctrl+↑/↓ prompt jumps, and **the wheel belongs to the page unless the region under
  the pointer still has somewhere to scroll**, which is how a browser treats a nested
  scroll box: WPF's ScrollViewer marks the wheel handled whether or not it moved, so the
  renderer's own viewports — the one a table sits in, a diagram's, and the capped tool-call
  bodies — swallowed it and left the transcript standing still. **One step then serves the
  whole transcript** (`Services/TranscriptScrolling.cs`): a fence, a table and the paragraph
  between them move by the same amount, where a fence used to move 120px a notch against the
  prose's 48 — two and a half times faster for passing the pointer an inch to the left.
  `1.0×` is defined as exactly what WPF itself scrolls (`WheelScrollLines × 16px`, and
  Windows' "One screen at a time" arrives as a negative count and pages), so the default
  feel is unchanged and `/scroll-speed` is finally the multiplier its own description
  promises. Measured at one notch: 48.6px in the transcript against 49.7px on an untouched
  WPF scroller in the same window, and 97.2px at `2.0×` over prose, table and fence alike.
  **A disclosure the reader opens does not
  walk out from under the cursor**: the transcript sticks to the tail while an answer
  streams, and that same rule used to re-pin on the height change a click caused — measured
  at 77px on a posed tool run, which is what made a group impossible to close on the second
  click. A click on a disclosure (a `ToggleButton`, or either half of a thinking cell) is
  now the anchor for the layout it causes, held across that layout the way browser scroll
  anchoring holds one, and only then is the tail re-tracked; **Command Palette** (Ctrl+K, `Views/CommandPaletteView.cs`; on Chat it
  carries the reference's own chat rows with the chords it binds them to — Incognito chat
  Ctrl+Shift+I, Toggle extended thinking Ctrl+Shift+E, Open model menu Ctrl+Shift+.,
  Upload file Ctrl+U, Toggle dictation, Toggle side chat Ctrl+; and Delete chat) and the
  keyboard-shortcuts sheet (Ctrl+/, `Views/ShortcutsView.cs`); **zoom** Ctrl+= / Ctrl+- /
  Ctrl+0 / Ctrl+wheel on the body grid, persisted; **notifications** — tray balloons
  "Claude finished work" / "Claude needs your input to continue" when the window is in the
  background (rate-limited per session, Settings toggle); **auto-archive** — idle Code
  sessions past N days (default 30) on start + every 6h, plus opt-in archive on PR
  merge/close; **CLI import** (`Services/CliSessionImporter.cs`) converts ~/.claude/projects
  transcripts (roles merged for provider alternation, sidechains skipped, reruns skip
  existing) with `Services/SessionListCache.cs` — a size+mtime summary cache persisted next
  to the session folder — keeping 350+ imported sessions instant to list; **split view**
  (sidebar row menu / palette / Alt+click) tiles up to four Code sessions in an adaptive
  grid (1 full · 2 side by side · 3 two-up-one-across · 4 2×2), per-tile close pills,
  Ctrl+]/[ focus cycling and Ctrl+\ close (side panel first, then the focused tile; closing
  the primary hands it the next tile's session), dynamically created panes riding the same
  window wiring (PaneAdded) and parked panes keeping their turns alive, with guards against
  double-opening one session; **Side Chat** — a toolless Chat surface as a panel (and,
  on the Chat surface, the reference's own side chat in the drawer: `Views/Chat/SideChatView.cs`
  with `Services/SideChatStrings.cs`, opened by Ctrl+; as the reference opens it, its header
  saying how many messages of the main chat it can see and that it is read-only, its empty
  state, and **Copy answer** / **Branch to new chat** revealed under each answer — read-only
  because it runs on Core's own `SideChat` contract, where the main transcript rides the
  system prompt as an excerpt and nothing said in the panel reaches the session it reads), with
  Clear, "Open in new window" (closing the window re-docks it), and "Send to
  side chat" on the transcript's right-click menu staging the selection in its composer;
  **slash built-ins** — /clear /compact /export /init /model /resume /rewind /theme
  /settings plus the CLI-parity round below, custom commands, skills, and MCP prompts
  (/review and /pr-comments were removed to match CLI 2.1.247, which dropped both —
  /code-review and /security-review replace them);
  **Keep computer awake** (Settings › Notifications, SetThreadExecutionState while any turn
  runs); the plan-approval card's comment box ("Plan comment", Enter keeps planning with
  the comment as feedback); **shell** — tray "New Code Session"/"Continue Last Code Session", `--code-dir=`
  (forwarded through the single-instance pipe; opt-in HKCU Explorer context-menu entry in
  Settings), and git **plugin marketplaces** (`Services/PluginMarketplaces.cs`) reading
  `.claude-plugin/marketplace.json` or plugin-shaped subfolders, with install/update/remove
  on the Plugins page.
- **CLI parity round** (audited against the machine's bundled CLI 2.1.247 / standalone
  2.1.251; feature surface extracted from the Bun binary):
  **Core tools** — `NotebookEdit` (.ipynb cell replace/insert/delete by id or index,
  outputs cleared on edit), `monitor` (blocks on a background task until an `until` regex
  matches or it exits; timeout is a normal result), `list_mcp_resources`/
  `read_mcp_resource` (McpClient grew resources/list+read and prompts/list+get; servers
  without the capability read as empty),
  **MCP remote transports** — an mcp.json server may carry `url` (+ `type` "http"|"sse",
  `headers`) instead of `command`: streamable HTTP with legacy-SSE fallback
  (`Core/Mcp/McpHttpClient.cs`), Bearer tokens from `McpTokenStore` (DPAPI-sealed via the
  App), one silent refresh on 401, and interactive OAuth (`Core/Mcp/McpOAuth.cs` — RFC
  9728/8414 discovery, RFC 7591 dynamic registration, PKCE through the system browser
  with a loopback redirect) via **/mcp-auth <server>**; an unauthenticated server reads
  as "requires authentication — run /mcp-auth" in the refresh messages, and the
  Connectors card accepts a URL in the command box (env lines become headers).
  **MCP elicitation** — elicitation/create from any transport renders mid-call on the
  AskUserQuestion card (enum property → options, free text via Other; Decline/dismiss map
  to decline/cancel), fires the elicitation hooks through `ToolExecutionContext.FireHook`,
  and is declined when no UI is attached.
  `slash_command` (Core class kept but no longer registered — the reference retired its
  SlashCommand tool; custom commands ride the skill namespace and the skill tool now), and
  **`ToolSearch`** + `DeferredToolRegistry` (Core/Tools/DeferredTools.cs): the reference's
  own name for the tool this port used to send as `tool_search`, which stays an
  `IAliasedTool` alias so a stored session replays without ever being advertised. Its doc
  is the reference's (`te + re + ne`, the form a live fable-5-1 session sends) and a query
  that matches nothing answers the reference's ordinary "No matching deferred tools found"
  rather than an error listing what is left. **What defers is the reference's predicate**
  (`ToolDeferral.ShouldDefer`, its `fpe`/`oZn` at 184577546): `alwaysLoad` exempts, then
  ToolSearch itself and `EnterWorktree` in a `CLAUDE_CODE_SESSION_KIND=bg` session, then
  every MCP tool, then a built-in that declares `shouldDefer` — 28 of them, measured off
  the binary and against a live session's own loaded/deferred split (CronCreate,
  CronDelete, CronList, DesignSync, EnterPlanMode, ExitPlanMode, EnterWorktree,
  ExitWorktree, ListPlugins, ListSkills, SearchPlugins, SearchSkills, SuggestPluginInstall,
  Monitor, NotebookEdit, SendMessage, TaskOutput, TaskStop, Task{Create,Get,List,Update},
  WebFetch, WebSearch, PushNotification, RemoteTrigger and the two MCP-resource tools).
  Read, Bash, PowerShell, Edit, Glob, Grep, Agent, Skill, Artifact, AskUserQuestion,
  ListAgents, ReportFindings, ScheduleWakeup, SendUserFile, SuggestSkills and Write stay
  loaded, as they do there. This build's own `todo_write` is deliberately not deferred:
  the reference declares `shouldDefer` on the TodoWrite it retired, and the live surface
  shows it in neither the loaded nor the deferred set, so there is nothing to follow.
  **Tool search engages by the reference's own rule, not by a size threshold**
  (`Core/Tools/ToolSearchAvailability.cs`, its `eJe`/`sW`/`y_` at byte 183761252): the
  mode defaults to tool-search, `ENABLE_TOOL_SEARCH` overrides it with its own `auto:N`
  parsing (0 → on, 100 → off, `auto` → its auto arm, truthy → on, falsy → off), and the
  model carries it unless its id contains `claude-3-5-haiku` or `claude-3-haiku`. This
  port used to gate on a 20k-char MCP doc size, which has no counterpart there. Once on,
  everything deferrable is hidden behind a fetch by name ("select:a,b") or keyword; enabling grows
  `All`, which the live-registry re-read advertises next call. **A fetch lasts the
  session, not the turn**: the reference's ToolSearch answers with `tool_reference`
  blocks the API expands server-side, and the tool_result carrying them is replayed on
  every later request, so a fetched tool never goes back to being deferred and nothing
  local tracks it. This port writes the schemas into the result itself, so
  `SkillSessionState.LoadedDeferredTools` carries the fetched names and seeds the
  registry each turn builds — without it a tool fetched in one turn was silently
  deferred again in the next, while the announcement delta (session-scoped) stayed
  quiet, so the model kept the schema in its context and the call answered
  "Unknown tool". Deferring also hides a tool from the advertised set without taking it
  away: the harness tells the model a deferred call "will fail with
  InputValidationError", which is an answer about the arguments rather than about the
  name, so `DeferredToolRegistry.Find` resolves a deferred tool — and its aliases, which
  is what lets a stored session replay a renamed MCP tool's old bare name. Subagents never receive
  ToolSearch (their registry is a snapshot). The one clause of `y_()` not carried is its
  refusal for a base URL that is not a first-party Anthropic host: that exists because
  its ToolSearch answers with `tool_reference` blocks the API expands server-side, so a
  proxy that drops them breaks the mechanism, where this port writes the fetched schemas
  into the tool result and needs nothing forwarded. **That clause is also what makes a
  capture misleading**: pointing `ANTHROPIC_BASE_URL` at a local listener turns tool
  search off, so a captured `-p` run shows all 27 built-ins loaded and no reminder — which
  is what an earlier round of this file read as the reference's default. A live desktop
  session on a real endpoint defers from the first message.
  **MCP prompts** surface as `/mcp__server__prompt` slash commands on the Code surface;
  the typed argument feeds the prompt's first declared argument and the expansion is sent
  as the user message.
  **App tools** — `EnterWorktree`/`ExitWorktree` (`Services/WorktreeTools.cs`: create/
  reenter a `.jarvis-worktrees/{repo}-{name}` worktree on branch `jarvis/{name}` and
  repoint the session live; exit returns to the main checkout, `discard:true` removes)
  and `config` (`Services/ConfigTool.cs`: get/set of a deliberate allowlist — effort,
  autocompact(+percent), web_search, shell_timeout_seconds; permission mode and
  credentials deliberately excluded).
  **read_document** (Core) — native text extraction for the zipped-XML Office formats
  (`Core/Documents/OfficeDocumentReader.cs`): xlsx/xlsm (sheets in workbook-rels order,
  shared/rich/inline strings, tab-separated rows that keep column gaps), docx (paragraphs,
  tabs/breaks, tables) and pptx (slides numerically ordered + speaker notes). Pure
  zip+XML with local-name matching (strict OOXML uses another namespace family), DTDs
  prohibited; the portable half of the reference's attachment-analysis sandbox — parsing
  only, so unlike the reference it needs no opt-in switch.
  **Loops** (`Services/LoopPrompts.cs`, full 2.1.247/2.1.251 parity) — `/loop [interval]
  <prompt>` parses the reference's three rules (leading `^\d+[smhd]$` token, trailing
  "every N unit" clause with word units and the `check every PR` guard, else dynamic;
  seconds ceil to the 1-minute floor) on a fixed interval (ticks landing mid-turn are
  skipped) or the dynamic mode: the message carries the reference's six-step instruction
  block (watcher protocol mapped to Agent run_in_background/subscribe_pr_activity +
  task-notifications, prompt passed back `/loop `-prefixed and re-expanded at fire time)
  and **`ScheduleWakeup`** enforces the reference contract — `delaySeconds`/`reason`/
  `prompt`/`noop` all required unless `stop:true` (the exact error strings), clamp
  [60,3600] with the "(clamped to Ns…)" result, `stop:true` cancels **only** dynamic
  wakeups (fixed loops keep ticking, the result says so), and consecutive `noop:true`
  ticks collapse into a streak notice in the live transcript view. A bare `/loop` runs
  the reference **autonomous default** (the verbatim "# Autonomous loop check" preamble)
  and `/loop 5m` schedules it recurring; a `loop.md` (`.claude/loop.md` first, 25k
  truncation) replaces it as the loop-tasks file — sentinels
  `<<autonomous-loop[-dynamic]>>`/`<<loop.md[-dynamic]>>` expand at fire time (full
  instructions on the first fire, after a compact, or when loop.md changed; short tick
  reminders otherwise, incl. the loop.md-absent variant). A user abort cancels pending
  dynamic wakeups; `CLAUDE_CODE_LOOP_KEEPALIVE` re-arms 1200s once when the model forgot
  to reschedule (reference budget 1). Deltas stay deliberate: fixed loops ride the
  session's own timer (not CronCreate crons — they die with the view model), and the
  reference's cloud-schedule offer and push-notification suffixes are omitted with those
  features; `/loops` lists, `/loops stop <id|all>` ends them (the reference's /loops is
  a disabled stub).
  **A tick is the harness speaking, not the user** (`ChatMessage.IsMeta`): a fired loop
  prompt reaches the model as an ordinary user message — that is how the model reads it as
  an instruction — but it is marked the reference's `isMeta`, which its own transcript
  renders as an event rather than a user bubble
  (`c06cf64bb`: `if(isSynthetic||isMeta){…w("user",[{kind:"event",…}])…continue}`) and which
  every one of its "is this a real user message" predicates excludes
  (`type==="user" && !isSynthetic && !isMeta`). Measured across 817 of its own transcripts,
  `isMeta` marks exactly that class — an injected skill body, the local-command caveat, an
  image placeholder, "Continue from where you left off." So a tick leaves a notice in the
  transcript instead of a bubble, and never ends the silent-turn run. What the event *says*
  for a tick is this build's own wording: the reference's loop has fired in none of those
  817 sessions, so there is no string to copy.
  What is deliberately **not** done is hiding the `ScheduleWakeup` row: its CLI tool
  definition declares `userFacingName(){return ""}` and `renderToolUseMessage(){return null}`,
  but those belong to the terminal renderer, and the desktop transcript this port implements
  receives the call over the SDK like any other — measured with `--output-format stream-json`,
  where the `tool_use` block is emitted normally and `system/init`'s `tools` is a list of bare
  names carrying no display metadata.
  **`CronCreate`/`CronList`/`CronDelete`** write the same Scheduled routine store
  the Routines page shows (RoutineRunner's minute tick picks changes up).
  **Cross-session messaging** — `SendMessage` first tries the session's own workers,
  then `ToolExecutionContext.SendToSessionAsync`: the surface resolves another local Code
  session by title or id prefix and delivers the message as a task notification there (no
  reply comes back); **`ListAgents`** lists both. **`ReportFindings`** (typed
  code-review findings rendered as a card) and **`SendUserFile`** (file card in the
  transcript) complete the reporting pair (`Services/ReportingTools.cs`), and
  `list_local_skills`/`search_local_skills`/`list_local_plugins`/`search_local_plugins`
  (`Services/DiscoveryTools.cs`) read skills and plugins from disk + marketplaces. They are
  named apart from the reference's `ListSkills`/`SearchSkills`/`ListPlugins`/`SearchPlugins`,
  which read the user's claude.ai catalog: the two are not interchangeable, and a shared
  name said they were. The old names stay as never-advertised aliases, and both halves are
  declared in `Deltas/reference-surface-deltas.tsv`.
  **`suggest_local_skills`/`SuggestPluginInstall`**
  (`Services/SuggestionTools.cs`) complete that block of six: the reference's tool docs
  verbatim — most of both are about when *not* to call them, which is the whole point of a
  tool that volunteers something — with its schema, its 16-plugin and 128-char caps and its
  "Plugin card rendered. The user enables the plugin out of band…" note, all pinned by the
  ported-prose check. What they offer is local: skills already on disk that are **switched
  off**, and skills inside cloned marketplace plugins that are **not installed** — the two
  things that are one click from available here, which is the property the reference's card
  is about. An empty *proactive* search returns the reference's own instruction to say
  nothing, so a search that found nothing costs the user no sentence.
  **`search_mcp_registry`** (`Services/McpRegistry.cs`) queries the official MCP registry
  (registry.modelcontextprotocol.io) for servers to suggest for the Connectors page.
  **Computer-use extras** (`Services/ComputerUseExtras.cs`) — `open_application`,
  `read_clipboard`/`write_clipboard`, and the reference's per-app grants simplified:
  with "Require app grants" on (Settings › Features), computer-tool input into a
  foreground app with no grant refuses and names `request_access`, which asks the user
  and persists granted process names in ui-settings.
  **The Android emulator is the reference's own server, not a family of bare tools**
  (`Services/AndroidEmulatorTools.cs` over `Services/AndroidEmulator.cs` and
  `Services/AndroidEmulatorMessages.cs`, with the live pane in
  `Views/Panels/EmulatorPanel.cs`): one `alwaysLoad` tool named `control`, reached as
  `mcp__Claude_Code_Android_Emulator__control`, with the reference's ten actions —
  attach · launch · screenshot · tap · swipe · touch_path · text · button · open_url ·
  detach. Its description and input schema are the reference's bytes
  (`Captures/Mcp/android-emulator-1.44121.2.0.json`, written by
  `Captures/Mcp/gen-android-emulator.js` and fed into `Services/CapturedMcpSchemas.cs`
  by the same generator the other servers' schemas come from), and
  `AndroidEmulatorParityTests` compares what this build advertises against it field by
  field **and in declaration order**, plus the action enum, the button enum and the
  gate. The server is feature-flagged off in the installed build
  (`claudeAndroidEmulatorAccessEnabled`), so no live ccd session exposes it and the
  declaration had to be read out of `app.asar`'s `index.chunk-DIYseJ6l.js` rather than
  captured off the wire — which is also why it is a second recording beside
  `internal-servers-*.json` rather than an eleventh entry inside it.
  **Everything under it is the reference's own bridge** (`index.chunk-DpY2AzrH.js`, its
  `Di` device service): `/^emulator-\d+$/` as the only serial shape a physical device can
  never match, `adb devices -l` parsed by its `jr` with each booted row renamed to
  `{AVD name} ({serial})` from `ro.boot.qemu.avd_name` (its `ci`), `wm size` plus
  `dumpsys input`'s `SurfaceOrientation` for the display (its `zr`, a quarter turn
  swapping the sides), and Anthropic's image budget (`pxPerToken` 28, `maxTargetPx` and
  `maxTargetTokens` 1568) deciding the screenshot's own pixel space — which is the
  coordinate space every tap, swipe and touch path is expressed in and which
  `attach`/`launch` report in their own result. Coordinates map back to device pixels
  with its `Q`, rounded **half up** the way JavaScript's `Math.round` rounds and
  clamped to the display. `text` sends at most its 4096 characters, drops what
  `input text` cannot carry (non-printable ASCII plus ``` ` $ ; | & < > ( ) ```) and
  reports the count, the line breaks among them and the resume index; `touch_path`
  is `input motionevent` DOWN/MOVE…/UP capped at 256 samples and 30s; `open_url`
  refuses the five schemes its `Bur` blocks. **`attach` cold-boots a named AVD** (its
  `Ar`: spawn `emulator -avd`, then poll for a serial that was not there before whose
  `sys.boot_completed` reads 1, for at most 180s), which is what the tool doc and the
  pane's own "Shut-down devices boot automatically" promise.
  **The pane is the reference's `EpitaxySimulatorPanel`** (ion-dist
  `c49d37e5f-WlOcFq3Q.js`), filed under its `simulator` pane kind and titled by its `$H`
  ("Android Emulator"): the attach prompt with its three sentences and its
  Booted / "Available (will boot)" picker, the live view, and the control bar its
  `SimulatorControlBar` draws for `isAndroid` — Back · Home · Recents | Save screenshot
  | Detach emulator, the iOS half (Record, Rotate, Shut down) not drawn.
  `--open=emulator` attaches it to a booted emulator; `--open=panes:simulator` poses the
  prompt. Clicking the view taps, dragging swipes and typing sends `input text`.
  **`<simulator_tools>` rides the prompt with it** (`Services/HostPromptSections.cs`,
  the reference's `Tn(hasIos, hasAndroid)` in `app.asar`'s `index2.chunk-DTg3UwuF.js`),
  gated the same way it gates it — on that server being live this turn — and carrying
  only the Android arm, since the iOS one describes a simulator this platform has not
  got.
  Four deltas are declared in `Deltas/reference-surface-deltas.tsv` with what was
  measured for each: the remote flag has no channel here, so the gate is adb being
  installed plus the existing Settings switch (Settings › Jarvis Code › Mobile
  simulators › Android Emulator), which the "turned off in Settings" result names; the
  reference's panel streams h264 out of `adb exec-out screenrecord` and decodes it in
  Chromium, where this pane pumps `screencap -p` frames at 250ms because WPF has no h264
  decoder; its per-device consent dialog is replaced by the ordinary permission gate; and
  its device bezel, side buttons and edge gestures are not drawn.
  **What `control` does not cover stays this app's own**: `android_logcat` has no
  counterpart action and keeps its bare name (`Services/AndroidTools.cs`, declared as an
  addition). The other four — `android_devices`, `android_screenshot`, `android_input`,
  `android_install` — became actions of `control` and survive as never-advertised
  aliases, so a session stored before the rename still replays.
  **Thinking & effort wire** — measured by capturing the installed CLI 2.1.257's real
  requests (ANTHROPIC_BASE_URL → local listener, one capture per model class and effort
  level, sixteen in all) and verified the same way against the app's own outgoing
  request. **The wire has four axes, and they do not move together**
  (`Anthropic/AnthropicEffort.cs`, `Classify` by family+version with date stamps
  excluded): *thinking* — adaptive for Opus/Sonnet 4.6+ and the whole 5 family, the fixed
  `budget_tokens:31999` for the rest of the 4 generation and haiku, no field at all for
  Claude 3.x; the *effort dial* (`output_config.effort` + `effort-2025-11-24`) on every
  adaptive model **and on opus-4-5**, which takes it with enabled thinking; the *harness
  system turn* (`mid-conversation-system-2026-04-07`) on opus-5, opus-4-8, sonnet-5 and
  fable/mythos only — opus-4-6, opus-4-5, sonnet-4.x, haiku and 3.x get their harness
  sections as `<system-reminder>` blocks leading the user message instead; and the
  *trailing betas*, `afk-mode-2026-01-31` on every adaptive model and
  `fallback-credit-2026-06-01` on opus-5 and fable/mythos, stable across two builds and
  every run and therefore pinned (the earlier note that this slot was a rollout flag read
  a one-build sample). `advisor-tool-2026-03-01` is sent by no model in 2.1.257 and is
  gone; haiku-4-5 alone puts `claude-code-20250219` last. **The output cap is the
  catalog's own `max_output_tokens.default`, a separate axis rather than a consequence of
  the thinking class**: 64000 for the adaptive models *except* `claude-sonnet-4-6`, which
  is adaptive on a **32000** cap, then 32000 for the enabled class and for 3-7-sonnet, and
  8192 for 3-5-sonnet. Reading the cap off the thinking class instead sent sonnet-4-6
  double what the reference sends — measured by capturing it at a listener with opus-4-6 in
  the same run as a control, which reproduced its own fixture's 64000. No wire fixture is
  recorded for it: an ad-hoc capture's beta list is not comparable to the sixteen committed
  ones (they carry `afk-mode-2026-01-31` and no `advisor-tool-2026-03-01`, and a capture
  taken here is the reverse, on a clean config directory as well as a real one), so the cap
  is pinned by `AnthropicEffortTests` instead. The four
  known wire gaps of the previous round all had one root (a one-axis classifier) and
  closed with it; `known-wire-gaps.tsv` is empty. `context_management`
  (clear_thinking_20251015, keep all) rides whenever thinking is on. Deliberate deltas from the CLI: no `metadata.user_id`
  (account/device telemetry) and no `thinking.display` ("omitted"/"summarized" would
  suppress the thinking stream this app renders). Bedrock/Vertex share the thinking shape
  but skip the beta-gated body extras (`effortExtras:false`); Off still sends the exact
  pre-effort body. The other providers fold XHigh/Max onto their top rung
  (reasoning_effort "high", Gemini's top budget, Kimi/NVIDIA "max").
  **Memory** — topic recall (`Services/MemoryRecall.cs`): memory files (never the index)
  sharing ≥2 significant words with the prompt ride it as a `recalledMemories`
  system-reminder, top 2 by score, 2.5k chars each; `/pause-memory` stops recall and
  removes the memory tool + index for the session.
  **Info commands** (`Services/SessionInfo.cs`) — /status (version · model · effort ·
  mode · cwd · context · MCP), /doctor (git, gh, settings writable, browser engine, key for the
  default model, MCP connected counts, search endpoint probe), /insights (local report
  from UsageStatsStore + session list), /skill-doctor (listing cost + per-session
  invocation counts), /hooks (loaded definitions + config paths), /memory (opens the
  project memory folder), plus /goal (standing-goal reminder on every message), /brief
  (brevity reminder), /focus (Summary⇄Normal transcript view), /copy [N], /recap, and
  /plan.
  **Five more the surface audit found missing**, each carrying the reference's own
  description: **/setup-bedrock** and **/setup-vertex** open the provider card that already
  holds the credential, region and model ids their forms edit (the reference hides both
  unless `CLAUDE_CODE_USE_BEDROCK`/`_VERTEX` is set; ours are always listed),
  **/list-agents** — and its reference alias **/peers** — shows the user exactly what the
  `ListAgents` tool reports, the two sharing one builder rather than two renderings of the
  same list, **/voice** toggles the composer's dictation, and **/daemon** opens Scheduled
  and names the Background tasks panel. Two of those diverge and say so in
  `Deltas/reference-surface-deltas.tsv`: the reference's voice mode is hold-to-talk through
  a claude.ai voice service where this one is offline System.Speech, and its /daemon also
  fronts a daemon process this app does not run.
  **Three more are real in both command tables** — the desktop's in
  `Views/ChatSurface.xaml.cs` and the CLI's in `Repl/ReplCommandTable.cs`, each carrying the
  reference's own menu description and argument hint (CLI 2.1.257).
  **/batch** (`Services/BatchCommand.cs`, its `kt()` registration and the prompt builder
  `Gn`) is a prompt rather than machinery, exactly as the reference registers it: an empty
  instruction is answered with its three examples, a working directory outside a repository
  with its "This is not a git repository." refusal, and otherwise the model gets the
  reference's three-phase orchestration prompt verbatim — plan mode and foreground research
  subagents, then `5`–`30` self-contained units (`bt`/`vt`), one background `Agent` per unit
  at `isolation: "worktree"`, and the status table it re-renders as each `PR: <url>` line
  arrives. Every tool it names exists here under the same name, and its worker template
  (`Hn`) rides the prompt quoted, as it does there.
  **/debug** (`Services/DebugCommand.cs`, its `Co()` registration) arms this session's
  debug log and hands the model the reference's Debug Skill: the log path, the last 20 lines
  of its final 64KB (`he`/`wo`, formatted by its own `Ut`), the three settings files, and
  its five instructions. `SessionDebugLog` is the reference's `enableDebugLogging()` — arm
  if not armed, answer whether it already was — and because both hosts here arm the log
  while composing their service graph, its "Debug Logging Just Enabled" section is normally
  absent; the CLI's `--debug-file` now names where that log goes, which is what makes the
  section's restart sentence true. Its `## Daemon` section is dropped: this build runs no
  background daemon for it to describe, and its `allowedTools` of Read/Grep/Glob is a
  no-op here, where the gate runs read-only tools without asking.
  **/release-notes** (`Services/ReleaseNotes.cs`, its picker module) carries the reference's
  whole shape — the `## version` parser, "Show all" over the versions newest first with the
  count beneath it, `Version {v}:` with `·` bullets, and `formatAll`'s opposite (oldest
  first) order — over a different source, which is why it is a `diverged` row rather than a
  closed one: the reference fetches anthropics/claude-code's CHANGELOG.md, and this reads
  the GitHub releases of `liquid8796/jarvis-code`, the feed `Services/AppUpdateService.cs`
  already polls for updates. Showing the reference's cached changelog would present another
  product's notes as ours; this repository publishes no releases today, so what the command
  normally renders is the reference's own empty state, "See the full changelog at: …". The
  CLI draws its picker (`Repl/Dialogs/ReleaseNotesPicker.cs`, ten rows visible); the desktop
  prints the notes as a transcript notice. The reference caches its changelog on disk and
  races the fetch against 500ms; with no cached copy to fall back on this waits five seconds
  for the answer and caches only a successful read, so a run that was offline can still find
  the notes later.
  **Terminal-only ports** — /statusline (a shell command gets session context as JSON on
  stdin; its first stdout line renders under the composer via `Services/Statusline.cs`,
  refreshed on bind + turn end, command in ui-settings), keybindings.json
  (`Services/UserKeybindings.cs`: extra chords for a fixed action list, additive to the
  built-ins, comments allowed; /keybindings opens the file), /scroll-speed (wheel
  multiplier 0.25–5 on the transcript), /tui (chromeless-maximized toggle, persisted),
  /background·/bg (hide to tray), /exit·/quit (real quit even with close-to-tray on),
  /heapdump (minidump with private RW memory to the Desktop), and /terminal-setup
  (`Services/TerminalSetup.cs`: `jarvis-code.cmd` shim in `{profile}\bin` + user PATH +
  Explorer context menu — `jarvis-code` in any terminal opens a Code session there).
  **/init** keeps the CLI's verbatim old prompt and, like the reference, switches to the
  new CLAUDE.md-files/skills/hooks flow only when `CLAUDE_CODE_NEW_INIT` is set
  (`Services/ReferencePrompts.cs` holds the extracted new-init and security-review
  texts; deltas only where mechanics differ — Agent naming, shell-run git context
  instead of the CLI's inline !`cmd` preprocessing, /import branches dropped).
  Still deliberately absent after the audit: everything account/cloud-bound (teleport,
  remote control, ultraplan/ultrareview, push notifications) and the WSL2 sandbox.
  Teammates and dynamic workflows were on that list until the 2026-08-31 round measured
  them: both execute locally in the reference (tmux panes and a JS script), so both are
  ported — see the two sections below.
- **Teammates & the task board** (ported against CLI 2.1.251): a named `Agent`
  spawns a **teammate** — `name` carries the reference's regex
  (`^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$`), its reserved recipients ("main", "team-lead",
  agent-id shapes `a(?:[\w-]{1,63}-)?[0-9a-f]{16}`) and its refusal wording
  (`Core/Agent/Teams.cs`). A teammate joins the session's single implicit team, whose
  file is `{profile}/teams/{slug}/config.json` (`TeamStore`: members with
  agentId/name/joinedAt/mode/isActive, atomic writes), and carries the verbatim **Team
  Coordination** system-reminder. It stays addressable while it runs: workers resolve by
  canonical name, and a running teammate has an **inbox** the orchestrator drains before
  each model call (`AgentTurnContext.DrainInbox`), so `SendMessage` reaches it mid-run
  instead of only after its report; `"team-lead"`/`"main"` address the session that
  spawned it and arrive as a task notification. Finishing marks the member idle and fires
  the new **teammate_idle** hook, beside **task_created**/**task_completed**.
  The **task board** is the reference's TaskCreate/TaskGet/TaskList/TaskUpdate under this
  harness's names (`TaskCreate`/`TaskGet`/`TaskList`/`TaskUpdate`,
  `Core/Agent/TaskBoard.cs` + `Tools/BuiltIn/TaskBoardTools.cs`): the reference tool docs
  including the segments it only shows inside a team, the `pending → in_progress →
  completed` workflow with `deleted` removing, mirrored blocks/blockedBy, the implicit
  claim when a teammate starts an unowned task, metadata merge with null-deletes, and its
  result lines ("Task #1 created successfully: …", "#1 [pending] Subject (owner) [blocked
  by #2]", "No tasks found", the completed-in-a-team next-task nudge). Completed blockers
  stop blocking, `_internal` tasks stay out of the listing, and the board is persisted per
  session. The Tasks pane renders the board above the plain checklist (`--open=taskboard`
  poses it), `/tasks` prints it, `/teammates` lists the team with each member's state, and
  a named agent's row in the Background tasks pane is titled by its name with the task
  beneath. Backend deltas are the reference's own on this machine: `teammateMode`
  (tmux · iterm2 · in-process · auto) resolves to **in-process**, because tmux is not
  installed here and the reference refuses agent swarms without it — the refusal strings
  are ported verbatim; `teammateMode` itself is a Settings › Features row and a
  `config` key, and a spawn that asked for an unavailable backend says so in its
  result before running in-process.
  The team file records what a spawn actually did: a teammate's `worktreePath`
  with the branch and base commit it started from, and `awaitingLeaderApproval`
  while its plan sits with the lead (`ExitPlanMode` raises the flag,
  the lead's `plan_approval_response` clears it, either way it answered).
  Both cleanups follow: deleting a session ends its team — each recorded
  worktree is released the way an agent's own cleanup releases one, so an
  untouched checkout goes and one holding work stays — and a startup sweep
  removes the teams whose session no longer exists, which is how a process that
  died mid-run stops leaving a team behind. A worktree whose base commit was
  never recorded is kept rather than judged.
- **Dynamic workflows** (`Core/Agent/Workflows.cs` + `Tools/BuiltIn/WorkflowTool.cs`): the
  reference's Workflow tool runs a script, not a prompt ("orchestrate subagents with
  deterministic JavaScript workflow"), so the port hosts one in a bare ECMAScript realm
  (**Jint**, the engine's only new dependency) with the reference's script API —
  `agent(prompt, {label, phase, schema, model, agentType})`, `pipeline()` with no barrier
  between stages (a throwing stage drops that item to `null`), `parallel()` as a barrier
  whose failures become `null`, `log()`, `phase()`, `args` and a live `budget` object —
  and its caps: min(16, CPUs−2) concurrent agents, 1000 agents per run, 4096 items per
  parallel/pipeline call. Determinism is enforced with the reference's own shim and
  messages: `Math.random()`, `Date.now()` and argless `new Date()` throw because they
  break resume, while `new Date(value)` still works; `with` and `import()` are refused at
  compile time. **Resume is real**: each `agent()` call is journalled to
  `journal.jsonl` beside the run and `resumeFromRunId` replays the calls a rerun makes
  again while edited or new ones run live (ours keys entries by call content, not by
  position — concurrent calls have no stable order). The tool requires the reference's
  `export const meta = {name, description}` header, persists every script under the run
  directory, refuses with the reference's lines for managed settings / the /config toggle
  / named-only sessions, and answers a schema-carrying `agent()` with a validated object.
  **`meta.phases` are real progress groups** (`Core/Agent/WorkflowProgress.cs`, ported from
  the reference's own progress feed and renderer): every declared `{title, detail}` is
  announced before the body runs, so the groups exist even for phases no agent reaches;
  `phase(title)` and an agent's `opts.phase` announce further ones, each title keeping the
  index it was first given (the reference's `Y7e` title coercion included, so `phase({})`
  reads "[object]"). A run's feed is the reference's `workflowProgress` array — phase and
  agent entries keyed by `type:index` and replaced in place, log entries appended and the
  oldest of them trimmed once the feed passes twice the 500 cap — and an agent moves
  `start` → `progress` → `done`/`error`, where "start" is the launched-but-queued state
  the reference counts as *pending* until the concurrency gate lets it run. Grouping is
  the renderer's own (`hD`/`fD`/`gD`/`pD`): an agent with no phase joins the one most
  recently announced (or a synthetic "Phase 1"), a phase first seen as an agent's index
  keeps the placeholder title until a real one arrives, a running agent quiet past 90s
  counts as stalled *and* running, and the phase status ladder is Pending / Running /
  Done / Error with a settled run reading its still-running phases as done.
  The Background tasks pane renders it as the reference's **Phases** list: one
  collapsible row per phase — title, `{done}/{total}` once it has agents, a caret — over
  its agent table (Agent · Model · Tokens · Time), open by itself while the run is live
  and the phase has agents, and an empty phase carrying the notice for the run's own
  status ("No agents have started yet" / "No agents ran in this phase" / "Stopped before
  any agents started" / "Failed before any agents started"). Every one of those labels is
  pinned by reference message id in the parity suite.
  Runs appear in the Background tasks pane as **Workflow** rows and `/workflows` lists the
  saved scripts and recent runs. The tool answers to the reference's alias as well
  (`run_workflow`, its RunWorkflow) — an alias resolves through both registries without
  being advertised twice (`IAliasedTool`) — and its switches are the reference's own:
  `CLAUDE_CODE_DISABLE_WORKFLOWS` and `CLAUDE_WORKFLOW_NAME_ONLY` (read for raw
  truthiness, as the reference reads them), a Settings › Features toggle, and the
  `dynamic_workflows` key on the `config` tool. The script's `budget` comes from the
  CLI's `--task-budget`, a hidden reference flag our parser now validates with
  commander's own framing (byte-compared against the reference in the parity suite);
  the desktop app sets no target, which is the reference's "no target set".
  `agent()` also takes the reference's `effort` ('low'…'max', per call) and
  `isolation: 'worktree'`, which Agent carries too along with `cwd`: a worktree is
  created per agent (`Core/Agent/AgentWorktrees.cs`) and removed again only when the agent
  left it untouched — a clean tree whose branch carries commits is kept, so work is never
  discarded. `isolation: "remote"` refuses as the cloud feature it is.
  **Workflow size** is the reference's advisory scale: unrestricted / small / medium /
  large with the 5 / 15 / 50 caps, medium by default, appended to the tool doc as its own
  block ("keep workflows under N agents. This is a guideline, not a hard limit…", plus the
  "/config" hint only while the default stands) and empty when unrestricted. It rides
  Settings › Features and the `config` key `workflow_size`.
  Deliberate deltas: no `remote` runs (cloud), and the
  reference's reactive fact-world internals are not reproduced — the script contract is.
- **Background agents, the last 20%**: the worker register persists what it started, so a
  process that dies mid-run is reported when the session reopens ("No completion record
  was found for N background agents from the previous session: …", which states plainly
  that the in-process state is gone rather than pretending to resume), and an interrupt
  stops the session's agents with the reference's banner — `Background agent "x" was
  stopped by the user.` for one, a counted list for several. Addressing by name works for
  `SendMessage`, `TaskOutput` and `TaskStop`.
- **Context-window popup and /context** (`Controls/ContextWindowPopup.cs` +
  `Services/ContextBreakdown.cs` + `Services/ContextReport.cs`): the slices are the
  reference's own, in its push order — System prompt, System tools, MCP tools, the two
  "(deferred)" rows that are listed but not counted, Custom agents, Memory files, Skills,
  Messages, then **Autocompact buffer** (`wie`, the room the threshold keeps free, and
  absent exactly when auto-compaction is on and the model's own window decided it) or
  **Compact buffer** (`Tie`, 3k, when auto-compaction is off), then Free space.
  `/context` prints the reference's thin-client markdown report — the one the desktop
  runs, not the terminal's colored grid — with its `## Context Usage` header, the category
  table measured against the raw window, the over-limit notice that distinguishes a hard
  limit from an overshot compaction window, and the MCP/agents/memory/skills detail tables.
  The popup itself:
  clicking the composer's context pill opens the reference popup — "Context window
  {used} / {cap} ({pct}%)", a segmented bar colored per category, and the chevron-expanded
  breakdown (Messages/System tools/MCP tools/Skills/Memory files/System prompt/Custom
  agents/Free space with tokens + percent, then the MCP/memory/agents count rows).
  Numbers are char/4 estimates; when the provider has reported a real context size the
  Messages slice absorbs the difference so the total matches the pill. The reference's
  plan-usage-limits half is account-bound and deliberately absent. Dev flag
  `--open=context`.
- **Request inspector** (`Controls/RequestInspectorPopup.cs` + `Services/RequestPreview.cs`
  + `Services/RequestOverrides.cs`): the composer footer's status dot is a button — it
  opens the exact call the session's next model turn would make (method, URL, headers with
  the credential shown as `••••••••`, and the JSON payload), with the payload editable.
  Truthfulness comes from reuse, not reconstruction: Core gained
  `AgentTurnContext.ToRequest()` (the orchestrator's own request builder, extracted) and an
  optional provider capability `IRequestInspector`, which every HTTP adapter implements by
  calling its own `BuildRequestBody` — llmapi previews through the wire its protocol
  selects, so the stripped `llmapi/` prefix and the dropped web-search flag show up;
  ChatGptWeb has no JSON body and deliberately doesn't implement it (the popup says so).
  The preview runs the real `TurnContextFactory` (side-effect free: `BeginTurn` writes
  nothing, and the gate's rule lines/hook are restored afterwards).
  **Editing** is stored as an RFC 7386 merge patch (`RequestBodyOverride.Diff`), not a
  frozen body — a turn makes many model calls and the message list grows between them, so
  only the keys the user actually changed are pinned and everything else keeps tracking the
  adapter. `LlmRequest.BodyOverride` carries it, each adapter applies it right after
  building the body, and `ChatViewModel.RunTurnAsync` reads it per turn. Overrides live in
  memory per session (never persisted — a forgotten one rewriting every request with
  nothing on screen would be worse than losing it), the dot fills with the accent color
  while one is active, and pinning a loop-critical key (`messages`/`contents`/`stream`/
  `model`/`tools`) says so in red. Unapplied editor text survives a click outside as a
  draft. The editor wraps deliberately: the system prompt is one JSON string of ~120k
  characters and WPF force-breaks such a line unwrapped, then clips each segment
  (`CodeEditorTextBox` in `Styles/Base.xaml` is InputTextBox with a stretched content host,
  because the composer's centred one puts a payload in the middle of the box). Dev flag
  `--open=request`.
- **Debug pane** (Settings › Desktop app › Debug; `Services/ModelTrafficLog.cs` +
  `Services/ModelTrafficHandler.cs` + `Views/Settings/DebugPanel.cs`): the calls the
  providers **actually made**, where the request inspector above shows the one they
  *would* make. Capture is a `DelegatingHandler` on an HttpClient built only for the
  providers, so `JarvisCode.Providers` is untouched, MCP / web fetch / SearXNG keep the
  shared client and stay out of the log, and every attempt `ProviderHttp` makes is its own
  row — a 429-then-rotate reads as the two calls it was. The response is **teed as the
  agent loop reads it** (`ModelTrafficStream`), never buffered: an SSE answer would
  otherwise arrive as one late blob. A row therefore settles Completed at EOF and
  **Interrupted** when a stopped turn closes the stream early, while a send that throws
  settles Failed with the exception. Bodies are kept **head-and-tail** (192 KB each end,
  omitted bytes counted in between), because a request's head carries the model and
  parameters while its tail carries the newest messages. Credentials are blanked *at
  capture*, which is what makes the Copy button safe — `Authorization` keeps its scheme
  and loses its secret, `x-api-key`/`x-goog-api-key`/cookies and the credential query
  parameters go entirely, and the real key never enters the record. Rows carry a status
  chip, the model read from the body (or the `models/{id}` path segment Gemini and Vertex
  use), duration and time; the selected call opens on a Request/Response segmented control
  — a finished call on what came back, one still in flight on what went out — with headers
  behind a toggle and the body pretty-printed when it parses as JSON, or shown as hex when
  the content type is binary (Bedrock's `vnd.amazon.eventstream`). The last 10 calls live
  in memory only and are never written to disk: these bodies hold the whole conversation.
  A headless host (the CLI) installs no handler at all, having no pane to show it in, and
  the ChatGPT web session never appears because it drives a page instead of an API. Dev
  flags `--open=debug[:inflight|:failed|:empty]`.
- **Web search**: Code turns carry a `web_search` tool (Core) that queries the SearXNG
  instance in `AppSettings.SearxngBaseUrl` (Settings › General › Search endpoint) and is
  read-only, so it runs without a permission prompt like the reference app's. Those turns
  set `EnableWebSearch = false` on the context on purpose: Anthropic would otherwise attach
  a server-side tool of the same name and the request would carry two. Chat turns, which
  have no tools, still use the vendor one. **Switching the option off swaps in the
  reference CLI's WebSearch instead** (`Services/VendorWebSearchTool.cs`, ported from the
  installed 2.1.251's source plus one captured side-query request): the tool runs a side
  query against the session's own model and effort — system prompt "You are an assistant
  for performing a web search tool use", user "Perform a web search for the query: …", the
  `web_search_20250305` server tool (max_uses 8, the model's allowed/blocked_domains passed
  through), no prompt caching and no context_management — all pinned as a `BodyOverride`
  merge patch on the adapter's normal body, so max_tokens/output_config/betas keep tracking
  the session's effort class. Three of those fields are **model-dependent, and the port
  follows the reference's own request-builder rules** (measured by capturing the CLI twice
  with only `ANTHROPIC_MODEL` changed): `thinking:{"type":"disabled"}` rides only a
  first-party wire for a model that accepts having thinking turned off (its NOe allowlist —
  opus 4.0/4.1/4.5/4.6/4.7/4.8, opus-5, sonnet 4.0/4.5/4.6, sonnet-5, haiku-4-5), so
  **claude-fable-5 gets no thinking key at all**; a forced `tool_choice` is demoted to
  `{"type":"auto"}` exactly when thinking stays live, which is that same case; and
  `output_config.effort` is clamped to `high` whenever thinking really was turned off.
  claude-opus-5 therefore sends the forced choice, disabled thinking and a clamped effort,
  while claude-fable-5 sends auto, no thinking and the session's own effort.
  Results fold like the reference: text runs split at `server_tool_use` boundaries, each
  `web_search_tool_result` becomes a `Links: […]` JSON line (`No links found.` when empty,
  `Web search error: {code}` on an error block), under the "Web search results for query"
  header and above the mandatory-sources REMINDER line. Gating is the reference's
  `isEnabled` mapped to our providers — Anthropic always, **llmapi on its Anthropic
  protocol** (the reference reads its provider kind from the `CLAUDE_CODE_USE_*`
  environment alone, so a custom `ANTHROPIC_BASE_URL` — which is what that relay is — stays
  `firstParty` and keeps web search on; its OpenAI protocol carries no server tool to
  forward), Vertex only for the fable-5/opus-4/opus-5/sonnet-4/sonnet-5/haiku-4 families,
  Bedrock and everything else never (the reference has no OpenAI/Gemini path at all) — and
  the per-session budget is
  its 200-search cap (`CLAUDE_CODE_MAX_WEB_SEARCHES_PER_SESSION`), refused with the
  reference's budget notice as an ordinary result. The lean tool doc and the captured
  input schema (`minLength: 2`, domain arrays) ride the tools block verbatim. Deliberately
  absent: the reference's CCR search proxy (`CLAUDE_CODE_WEBSEARCH_USE_CCR_PROXY` plus a
  cloud session routes the search through Anthropic's own `web-search` route instead of the
  model call) and its Foundry check, neither of which has anything to drive it here.
- **Web fetch** (`Services/VendorWebFetchTool.cs` + `Services/HtmlToMarkdown.cs`): the
  reference's WebFetch, replacing Core's own `web_fetch` on Code turns. It takes the
  reference's schema — `url` **and `prompt`** — and answers the way the reference does:
  fetch the page here (http upgraded to https, an explicit port kept, credentials and
  single-label hosts refused, a 2000-character URL cap), follow redirects **only within the
  same site** (scheme, port and registrable domain, `www.` ignored; a cross-host one comes
  back as the reference's "REDIRECT DETECTED" report asking the caller to fetch it again),
  convert the HTML to markdown, then run the caller's prompt over the content with the
  session's model and return **the model's answer, never the page**. The content rides the
  reference's wrapper — "Fetched {url} (HTTP …, {type}, N characters)", the UNTRUSTED-web-
  content framing, the reporting rules, and a `<fetched-web-content>` tag the page itself
  cannot close — truncated to the reference's budget (50000 − 2000 − the header). Pages are
  cached for 15 minutes, the self-cleaning sweep running on every fetch. Deliberate deltas:
  the overflow summariser (a second model call for the part that did not fit), the binary
  download path, the fetch proxy and the preapproved-documentation-domain list are not
  ported — the last only suppresses the reporting rules, so its absence is the stricter
  behaviour; a "small, fast model" slot does not exist here, so the session's model runs
  the pass; and the domain preflight (`api.anthropic.com/api/web/domain_info`, unauthenticated)
  is ported but **fails open**, because the reference's fail-closed answer would make
  Anthropic's reachability a precondition for reading a URL on a session that may run
  entirely on another provider. The fetch identifies itself as this app rather than as the
  reference's `Claude-User` crawler: a site's allow- or blocklist is a decision about the
  client actually knocking.
- **Scheduled** (`Views/RoutinesView.xaml[.cs]` + `Views/Scheduled/`): the host-side scheduler
  the engine leaves open, with the reference's own three views over it —
  `Services/RoutineRunner.cs` fires due `Routine`s on a minute timer into fresh headless
  sessions with a promptless gate, and notifies through the tray.
  **One list over both stores**, as the reference renders it (`[...scheduledTasks, ...routines]`):
  the routines this page edits and the SKILL.md tasks `Services/ScheduledTasks.cs` holds project
  into one `ScheduledItem`, so search, filters, sort and the status ladder are written once in
  `Services/ScheduledTaskPresentation.cs` and unit-tested without a window.
  **The status ladder is the reference's, and it is not about failures**: measured from
  desktop 1.40609.1.0 (`shared-7-pSrlloWM.js` `Zh`, folded by `cc630ea76-DIPg7mm0.js`, read by
  the list's `$s` in `ccd3f68fe-BCipNHOa.js`), enabled is **Active**; otherwise an
  `ended_reason` decides — `run_once_fired` reads **Ran** rather than as a failure, anything
  else is **Auto-disabled** — a `suspension_reason` turns Active or Paused (and only those)
  into **On hold**, a `device_absent` hold is dropped by the list, and a one-time item that
  has run reads Ran with no stored reason at all. Neither consecutive failures nor missed
  runs enter into it. Of the reference's twenty-three auto-disable reasons only two are
  locally determinable and are the two it also marks schedule-fixable — an invalid cron and a
  sub-hourly one — so `RoutineRunner` sets those itself on the tick and the enable switch is
  withheld until the schedule is edited (its `ae`). Nothing local produces a suspension, so
  On hold is reachable only through a hand-edited file; the state is carried whole rather than
  trimmed to what fires today. `Core/Routines/Routine.cs` grew the optional fields the page
  edits (description, one-time `FireAt`, ended/suspension reason, permission mode, worktree,
  branch, notify) and `RoutineEndedReasons` carries the reference's own spellings.
  **The editor is its local routine form** (`c0243d234-BOJof1xz.js`): name with the reference's
  slug, reserved-name and collision checks, description, instructions, working folder, branch,
  worktree, permissions, model, the completion notification, and the frequency row —
  Manual · One-time · Hourly · Daily · Weekdays · Weekly · Custom, with One-time offered only
  for a task that already carries a moment, the time and day controls only for the three
  frequencies that need them, and the jitter note on everything that recurs
  (`Services/ScheduleFrequencies.cs`). Its cron validation is the reference's ladder in the
  reference's order, which is what decides **which** of its four sentences a user reads: shape
  and a sub-hourly minute first, then named days/months and the `? L W #` symbols, then a 7 for
  Sunday, then field ranges. Two measured deltas: the reference converts the chosen local time
  to UTC when it builds a cron because its routine triggers evaluate server-side, and this app's
  crons are evaluated in local time by `Services/CronSchedule.cs` — which is also what the
  reference's own `create_scheduled_task` doc promises of the SKILL.md store — so the shift is
  deliberately not carried; and where the reference describes an arbitrary cron with cronstrue,
  this port shows the expression, a describer being a new dependency and a hand-rolled one a
  partial imitation. The "Next run" beside it still comes from the real evaluator.
  **The detail view is its local routine detail** (`cfc18e0f4-DP8WK7zq.js`): Status, Folder,
  Repeats/Runs, Always allowed, Instructions and History, with Run now, Edit, Delete behind the
  reference's confirm ("Delete “{name}”? Any sessions from this routine will be archived." plus
  "Also delete files on disk") and the schedule switch. **History is a store of its own**
  (`Services/RoutineRuns.cs`, one JSON file per routine under `routine-runs/`, newest first,
  capped at 500): a run carries its session and what it reported, a missed slot carries one of
  the reference's two skip reasons, and the file outlives the session it names being archived.
  **Always allowed is the grants a run collected** (`Services/RoutineApprovals.cs`, the
  reference's `addApprovedPermissions` / `removeApprovedPermission` /
  `shouldAutoApprovePermission` / `updateChromePermissions` in app.asar
  `index.chunk-DnlgCaT3.js`): an unattended routine still names the mode that stops it
  asking, otherwise the section is a chip row — a **Browser** chip reading "All websites"
  or "{n} websites", then one chip per stored tool rule — each with the reference's
  "Remove approval" ×, and "Approvals you grant during a run appear here." when there are
  none. Both live on the `Routine` (`ChromePermissionMode`, `ChromeAllowedDomains`,
  `ApprovedPermissions`, all optional so an older routine still loads), which is where the
  reference keeps them on its scheduled task. A rule's identity is its
  `{toolName}\0{ruleContent}` key; a **content-less** approval of Bash, PowerShell, Read,
  Write, Edit, MultiEdit, NotebookEdit, Grep, Glob or WebFetch is never stored, because it
  would grant the whole tool rather than one call; and auto-approval needs every requested
  rule to be stored by its own key or covered by a bare approval of its tool, with the
  browser/computer sentinels (`browser:`/`computer:`), the directory mount and the
  scheduled-task tools always asking. `RoutineRunner` re-applies them to the next run —
  the tool rules as the gate's own session rule lines, the sites through
  `BrowserOriginGate.ApplyRoutineGrant` — and writes back what the run collected, plus the
  reference's own dispatch rule that an Auto or Bypass run is granted every site.
  `--open=scheduled:approvals` poses the row.
  The six starter templates are vendored verbatim with their cron, title, description and prompt
  body (`Services/ScheduledTemplates.cs`); "Create with Jarvis" and a template card hand their
  prompt to a fresh Code session, which is where the routine actually gets set up.
  Notices are toasts, where the reference puts them — the missed-run and started lines and the
  two skip sentences — while the tray keeps the two that must reach someone not looking at the
  app: a finished run, and the reference's own failure notification ("Routine “{name}” failed" /
  "Open the run to see what happened.", `c8d44c418-BlnxlBN8.js`) behind
  `UiSettings.NotifyOnRoutineFailure`. The sidebar's Scheduled row carries the run count.
  `--open=scheduled`, `--open=scheduled:editor` and `--open=scheduled:detail` pose the three
  views. What is deliberately not carried is declared in `Deltas/reference-surface-deltas.tsv`:
  the source and runs-on filters, Move to cloud, the watcher history, the per-task browser and
  tool approvals, project link/unlink, the templates' hover illustrations, and the cards-vs-list
  toggle — whose label, like the list's hidden-count line, is addressed by a short message id
  that resolves through a runtime catalogue this installation does not ship, so its text cannot
  be read off the bundle at all.
- **The browser engine** (`Services/ElectronRuntime.cs`, `Services/ElectronPaneHost.cs`,
  `Services/ElectronPaneSession.cs`, `Services/ElectronEngine.cs`,
  `Controls/ElectronPaneView.cs`, `Assets/ElectronHost/main.js`): every surface that
  renders web content runs on **Electron 42.10.0**, which is the build the reference
  desktop ships — read from its own `app/version`, and its Chromium is
  `148.0.7778.280` from electron/electron's `DEPS` at that tag, the same string that is
  in the reference's `claude.exe`. Three of the engine's data files
  (`v8_context_snapshot.bin`, `icudtl.dat`, `resources.pak`) are byte-identical between
  the two installs. The version is a **parity decision, not a maintenance one**: moving
  the pin moves what the pane renders with.
  `ElectronRuntime` fetches the archive once per machine-user into LocalApplicationData
  (not the roaming profile — it is 374 MB), checks it against the SHA-256 the release
  publishes, and only ever moves a complete, marked tree into place; each attempt stages
  under its own name, so two profiles installing at once cannot write over each other.
  `ElectronPaneHost` owns the process and speaks newline-delimited JSON over a **named
  pipe** — deliberately not the child's stdio, because an Electron main process on
  Windows sees stdin at EOF the moment it starts and a sidecar reading commands there
  exits before `app.whenReady()` fires, silently and with no output at all. Losing the
  pipe is the engine's shutdown signal, so no Chromium outlives the app. A request is
  bounded at 60s, above the 30s/45s the pane's tools race their own calls at, so a lost
  answer surfaces as an error rather than hanging the pane.
  The engine app holds **one window per surface** and one `WebContentsView` per tab —
  the architecture the reference uses for the same pane — and only the active view is
  given the window's rectangle, which is what lets a background tab keep its page.
  `ElectronEngine` hands each surface a session over one shared process; the ChatGPT
  session is the exception and gets its own, because it needs its own profile and its
  own Chromium switches, and a switch is set before the app is ready.
  `ElectronPaneView` is the WPF side: `HwndHost` has to hand WPF a handle synchronously
  and the engine's window arrives over a pipe later, so it hosts a plain Win32 container
  and reparents the engine's window into it (`SetParent` plus `WS_CHILD`). It sizes that
  window from the **WPF element**, not from the container's client rect, because
  `HwndHost` updates the container *after* the layout event that reports the new size —
  reading it there is one pass behind, and the first pass leaves the window at 0x0.
  Chromium does not merely hide a zero-sized window, it stops producing frames for it, so
  `Page.captureScreenshot` never answers and synthetic clicks land nowhere. For the same
  reason a window that is never shown does not paint at all, which is why the diagram
  renderer's view is 1x1 and visible rather than hidden.
  Assets a page needs are served at `jarvis-asset://{host}/` — a privileged scheme
  registered at startup, so the diagram page gets an origin the browser calls secure
  (its sandboxed mermaid iframe breaks on an opaque one); anything resolving outside the
  mapped folder is refused. A page answers back over a DevTools binding
  (`Runtime.addBinding`) where WebView2 had `window.chrome.webview`, and **`Runtime.enable`
  has to come before `addBinding`** or the page's call produces no event at all.

- **Voice input**: offline dictation via System.Speech on the composer mic button.
- **The app shell** (`Services/AppMenu.cs`, `Services/AppUpdates.cs`,
  `Services/AppUpdateService.cs`, `Services/DeepLinks.cs`, `Services/JumpListModel.cs`,
  `Services/WindowsIntegration.cs`, `Services/NotificationPolicy.cs`,
  `Services/TaskbarAttention.cs`, `Views/MainWindow.Shell.cs`, `Views/AboutWindow.xaml`,
  `Views/MessageDialog.xaml`): the OS-facing half, ported from the reference's main
  process (app.asar `.vite/build/index.chunk-DnlgCaT3.js`).
  **The application menu is popped from the title bar**, as the reference pops it on
  Windows (its `requestMainMenuPopup` reaching `Menu.getApplicationMenu().popup()`):
  File, Edit, View and Help in its order, with its rows and its accelerators
  (`Services/AppMenu.cs` is the model; `Accelerators.Display` turns Electron's
  "CmdOrCtrl+Plus" into "Ctrl++"). The two Open rows appear on the Code surface only —
  with the separator the reference draws only when they are there — the pane rows are
  checkboxes named by their pane rather than Show/Hide pairs (its `checkbox: true`),
  Actual Size is disabled at zoom 0, and the troubleshooting rows live in Help ›
  Troubleshooting. Rows the reference has and this build does not are declared in
  `Deltas/reference-surface-deltas.tsv`: its Developer menu, Enable Developer Mode,
  the five Cowork rows and Record Net Log (30s).
  **Generate Diagnostic Report is the reference's modal over this app's own sources**
  (`Services/DiagnosticReport.cs` + `Views/DiagnosticReportDialog.cs`, ported from
  the collector `EVn` in that same main-process chunk and the popup in ion-dist
  `cd53438bb-zMSmatZM.js`): the Help ▸ Troubleshooting row opens it under its
  packaging title over a spinner, "Collecting diagnostics…" and the step label
  ("This usually takes a few seconds." until the first section names itself), then
  settles into "Diagnostic Report" — "This report contains:" over the reference's
  five rows in its order, its scrub note, and the "Preview contents" disclosure
  carrying the bundle's size in its own base-1000 narrow units and the first 40
  lines (`wVn`) as `[{section}] {line}`. Export to file writes the zip under the
  reference's own name (`jarvis-diagnostic-{id8}-{yyyyMMdd-HH}.zip`, its `OVn` with
  this product's name in front) and the title becomes "Report exported" over Show
  in Explorer and Done. **The reference collects eighteen sections and shows five
  bullets describing them**, so the five bullets are the contract this port
  collects against, from this app's own sources: version/OS/render tier/browser engine,
  the settings file with every credential-shaped value replaced by `<redacted>`,
  a reachability probe over the hosts `Services/FirewallAllowlist.cs` names plus
  the connected remote MCP servers (run together under the reference's one 15s
  budget), the diagnostic log's tail beside the calls `Services/ModelTrafficLog.cs`
  holds — status, model and duration only, never a body — and the crash dumps
  Windows wrote. Every line goes through `LineScrubber`, a port of the reference's
  `Vu` composed of `Bu`/`Hu`/`Xje`/`eMe`/`zu` in its order, with its placeholder
  vocabulary (`<userinfo>`, `<home>`, `<user>`, `<drv>:`, `<unc>`, `<email>`,
  `<ip>`, `<token>`, `<jwt>`); what is carried is the four kinds the modal's own
  note promises, and the reference's remaining path rewrites name nix profiles,
  volumes and partial downloads this platform does not have. Its **Send to
  Anthropic** half is declared rather than built — there is no endpoint here, and
  the reference itself renders exactly this shape whenever its own `showSendUi` is
  false. `--open=diagnostics` poses the modal.
  **Auto-update is the reference's state machine over this repository's releases**
  (`Services/AppUpdates.cs` holds `UpdateStateMachine`, ported event for event from
  its `ZU`; `AppUpdateService` is the transport). Five states — idle, checking,
  downloading, ready, error — with its manual-check rule: a run already in flight is
  re-stamped rather than restarted, and a check asked for while an update is staged
  re-announces it. The schedule is its `gSn` loop: a tick every hour, a check every
  fourth tick, and a ten-minute tick once something is staged. The Help rows are its
  `bSn`, the sidebar banner its `auto_updater_banner` (spinner lines while it works,
  a card offering the relaunch with "v{n}" under it, and one saying the update did
  not complete), the three check dialogs its own, and the relaunch asks first when a
  turn is in flight ("Jarvis is still working", naming the one session or counting
  them). What differs is the feed: the reference reads a Squirrel/MSIX feed and this
  build reads the GitHub releases of `liquid8796/jarvis-code`, preferring an installer
  asset over an archive, staging under the profile's `updates` folder and unpacking an
  archive from a step that waits for this process to exit first.
  **`jarvis-code://` is registered where the reference registers `claude://`**
  (`Services/DeepLinks.cs` parses, `ProtocolRegistration` claims it under HKCU —
  on by default, with a Settings switch that takes it away — and the single-instance
  pipe carries a link to the running instance). The scheme is this app's own rather
  than the reference's rebranded `jarvis://`, because the session links "Copy session
  link" writes already shipped under it, and one registered protocol that opens
  everything beats two that each open half; `session/{id}` is therefore a route of
  the same parser. The other routes are its `claudeURLHandler`'s: `claude.ai/new?surface=chat`,
  `code/new` with `folder` and a prompt capped at its own 14336, `code/continue`
  taking `last` or a `local_` id, `code/needs-input` whose session is optional, and
  `resume?session=<uuid>`, which imports one Claude Code CLI transcript and says why
  when it cannot — its four resume toasts, one per cause. Its `cowork`, `login`,
  `preview`, `hotkey` and `debug-handoff` hosts are declared rather than routed.
  **The jump list is its model** (`Services/JumpListModel.cs`, ported from `_Zt`,
  `vZt`, `fZt`, `mZt` and `hZt` with the caps 60, 3 and 5): a Needs Your Input
  category ordered oldest-wait-first, the recent folders under the New Code Session
  label, then the Tasks section with New Chat, New Code Session and Continue "{title}".
  Labels run through the reference's own text sanitizer (`Services/DisplayText.cs`,
  its `ve`), so a folder named with a right-to-left override cannot reorder the row it
  lands in; that pass is a code-point scan rather than a regex because .NET's
  `\p{Cs}` matches each half of a surrogate pair and would delete every emoji, and it
  runs before the normalization because .NET's `Normalize` throws on a lone surrogate
  where JavaScript's tolerates one. Rows the user removed by hand are never re-added.
  Its `Other…` row is dock-only and is not built.
  **The taskbar badge is the half of the level policy nothing counted**
  (`Services/TaskbarAttention.cs` over `NotificationPolicy.CountsForBadge`): a type
  set to "Badge only" shows no banner and still has to be counted, so what is waiting
  is tracked per session — the reference counts blocking requests, not kinds, so two
  sessions waiting count as two — and drawn as a taskbar overlay, which coming back to
  the window clears for the two momentary types while a permission prompt keeps its
  place until it is answered. The sound setting rides the balloon; WinForms'
  NotifyIcon exposes no NIIF_NOSOUND, so "silent" drops the balloon's icon and leaves
  the sound to Windows.
  **The dialogs are the reference's**, in the shape its main process raises them —
  message, detail, buttons in its order, a default and a cancel index
  (`Views/MessageDialog.xaml`, since WPF's own MessageBox has none of that): Reset
  Application Data, Clear Cache and Restart, Restart Required, Link couldn't be
  opened (offering to copy it), Open email link?, Could not load app settings, the
  three update dialogs, Install Git with its Download Git / Not now, and the
  first-run "Auto mode is now Jarvis Code's default permission mode". Every unhandled
  failure — dispatcher, worker thread or dropped Task — reaches one of them with a
  Copy details button. **Reset App Data is deferred by one launch**
  (`Services/PendingAppDataReset.cs`): the profile cannot delete itself while this
  process holds its files open, so a marker beside it asks the next launch to do it.
  **About** (`Views/AboutWindow.xaml`) is its own window at its 320x428: the app mark
  at 84px, the name with an italic "for" before the platform, a version line that
  copies to the clipboard and says so for two seconds, then Help and Get support. The
  reference's line shows Electron's `process.version`; this one shows the app's own,
  which is what the label promises.
  **Find spans the window** — Ctrl+F, F3/Shift+F3 and Ctrl+G/Ctrl+Shift+G — and
  targets the Browser pane while it holds focus (the reference's `TIn` checks the
  same thing first), where it runs Chromium's own `findInPage`, which the engine
  exposes directly. The pane's context menu gains its link and image rows
  (Open Link in Default Browser, Copy Link Address, Copy Image, Copy Image Address);
  the spelling half of that menu is the composer's instead
  (`Views/ChatSurface.Spelling.cs` over WPF's own checker, with "Add to dictionary"
  writing the .lex file `Services/CustomDictionary.cs` keeps), because the engine
  reports no misspelled word.
  **Quick Entry carries screenshots**, which is what the reference's payload means by
  them (`Views/QuickEntryWindow.Images.cs`; its `images` array of base64 and mime
  type): a capture pasted with Ctrl+V or an image dropped on the pill becomes a chip
  and rides the message. A submission that cannot be acted on says so with its
  "Failed to process quick entry", on the window's own toast queue. The tray menu is
  its two rows — Show App and Exit — with the session entries moved to the jump list
  where Windows puts them. The Explorer verb reads "Open in Jarvis Code", its
  `lGSZOWCVYC`, with the MultiSelectModel value it writes.
  Deliberately absent beyond the menu rows above: its plan and usage tray rows and the
  cloud feed behind them, and "Feature of the week", a server-driven card with no feed
  here. `Services/GitBash.cs` honours `CLAUDE_CODE_GIT_BASH_PATH` under the
  reference's own name, which is what makes the Install Git dialog's sentence true
  here; Core's `ShellTool` still probes Program Files only, since Core is
  additive-only for this package.
- **Quick Entry**: global Ctrl+Alt+Space pill on the cursor's monitor. **Multi-profile**:
  `--profile=NAME` isolates everything in `%APPDATA%\JarvisCode-NAME`; `--toggle` reaches the
  running instance over a named pipe. The tray icon is created in the MainWindow
  constructor, not off `SourceInitialized`, so it is there for as long as the process is —
  a start-in-tray launch never shows the window; `App` calls `EnsureHandle()` on that path
  so the WndProc hook and the Quick Entry hotkey still get their window handle. Tray icon
  follows the accent color.
- Dev/verification flags (a `--screenshot` run never forwards to a running instance:
  it renders and exits, so two renders of one profile cannot end with the second
  exiting 0 and writing nothing): `--screenshot=PATH` (render + exit), `--window-size=WxH`
  (render at an explicit size, for exercising narrow layouts), `--open=` with
  `settings[:Group:Label]` / `themes` / `quickentry` / `customize[:page]` / `scheduled` /
  `chat:{welcome|greeting|incognito|waiting|compacting|error|queue|searches|empty-searches}`
  (the Chat surface's own screens: the first-chat onboarding, the ordinary empty screen
  a second chat reaches, an incognito chat, the
  waiting line, the compaction indicator, the unfinished-turn card, the queued stack and
  the activity drawer) /
  `settings[:Group:Label]` / `themes` / `quickentry` / `about` / `appmenu` (the
  title bar's application menu, which needs a real screen grab like the other
  popups) / `diagnostics` (Help > Troubleshooting > Generate Diagnostic Report,
  posed over a real packaging pass) /
  `update[:checking|:downloading|:failed]` (the sidebar's auto-updater banner, posed
  through the real state machine) / `customize[:page]` / `scheduled` /
  `artifact` / `slash[:query]` (a Code session with the command menu open, optionally
  posed mid-filter) / `mention` (a Code session in the cwd with the @-completion
  open — popups need a real screen grab, RenderTargetBitmap misses them) /
  `panes[:name]` (the tile mosaic - the terminal and the diff tiled beside the
  conversation, or the one pane the name asks for) /
  `sidebar[:showmore|:hints|:state]` (the Code sidebar's list seeded with a row of every
  kind - running, waiting, unread, nested, and a title too long to fit - or pushed past a
  bucket's twenty, or with its jump keycaps raised, or grouped by State) /

  `titlebar[:loading|:agent|:panes]` (the Code session's titlebar: the ordinary bar, its
  loading skeletons, its agent badge, or the two pane controls the host gates on -
  narrow the window with `--window-size` to watch the pill and the rail fold) /
  `permission[:escalated]` / `effort` (the composer's effort selector popover open) /
  `home[:clear]` (the Code home view with one row of every kind the reference lists, or the
  landing-clear state that is the only one showing the usage stats card) /
  `gitbar[:create|:draft|:commit|:view|:failing|:merged|:queued|:stacked]` (the PR bar in
  one mode; `:stacked` adds the related and stacked rows) /
  `branchswitch` (the dirty-tree dialog a branch pick raises — its own window, so it
  needs a real screen grab) /
  `sessionnotfound` (the card for a session whose transcript is gone) /
  `scheduled:approvals` (the routine detail page's Always-allowed chips) /
  `monitors` (a sample plugin's Monitors rows) /
  `emulator` (the Android Emulator pane attached to a booted emulator, or its attach
  prompt where none is running) /
  `issuepicker` (the Import GitHub issue picker — its own window, and it asks `gh`) /
  `model` (the composer's model menu open, for checking its provider groups — a real
  screen grab, like the other popups) /
  `teach` (the guided-tour overlay waiting on a sample step) /
  `glow` (the rim a driven desktop wears — a click-through window of its own, so it needs
  a real screen grab, and posing it is what makes it capturable at all) /
  `markdown[:code][:top|:mid|:mermaid]` (one answer per section of markdown
  constructs — headings and inline styles, lists and quotes,
  tables/fences/math/image, or the diagram fences — on whichever of the two
  renderer profiles the flag names; the mermaid pose waits for a real browser to
  render, and the diagram it draws is a bitmap, so `RenderTargetBitmap` captures
  it) /
  `comparison[:vote|:reason|:saved|:send]` (the side-by-side view empty, at one of
  its three vote states, or `:send`, which types a prompt and really runs both
  arms) /
  `transcript` (sample fences + expanded
  tool rows for the code renderer, including a running shell row with its
  "Run in background" action) /
  `widget` (a Code session whose transcript holds one rendered visualize widget —
  the row's settled label and the reference's runtime page inside it; the page is a
  real browser, so `RenderTargetBitmap` misses it and the pose is checked with a
  screen grab) / `thinking[:code]` (the thinking cells of either
  surface, posed in every state: untimed, clamped, unclamped, collapsed and
  streaming) / `browser` (a Code session with the Browser panel
  open) / `palette` (the command palette on its Quick actions group) / `shortcuts` (the
  keyboard-shortcuts sheet) / `toast` (one toast of each variant, including one carrying an
  action) / `errorcard[:kind]` (the API-error card posed for one category) / `backgroundtasks[:empty|:subagent|:subagent:failed]` (the Background tasks pane
  posed with one row of every kind and state, its empty state, or pushed onto a sample
  agent's own transcript — settled, or failed with its report in danger), `--subagent-selftest` (with `--screenshot`: drives the pane's subagent view on the real
  controls — an agent row presents exactly one control, that control is a button rather than
  a toggle and reads as one to automation, invoking it opens the view without expanding the
  row while an ordinary row keeps its toggle, the view is titled by the call's description,
  the real Back button pops it, and a call the transcript no longer holds does not push one
  — exit 0/2),
  `--titlebar-selftest` (with `--screenshot`: drives the Code session's titlebar on the
  real controls - the bar lays out at 32px, a press on its own background reaches the panel
  and a press on the title does not, a plain Enter opens no editor while BeginHeaderRename
  swaps in the bare outlined one, a right-click on the title opens the session menu, and
  narrowing the window to 520px collapses the pill to its icon and widening restores it -
  exit 0/2),
  `--sidebar-selftest` (with `--screenshot`: drives the Code sidebar's list on the real
  controls - one filter icon and one header menu in the whole list and no standing
  "Recents" row, a 30px row, twenty rows then "Show 10 more" which reveals the rest, the
  Last-activity submenu on exactly the State grouping, the bulk-older submenu, Output style
  between Transcript view and Export, the collapse caret folding the section, and a
  double-click swapping the row for its rename editor - exit 0/2),
  `--slash-selftest` (with `--screenshot`: drives the composer's command menu on the real

  controls — the menu is bounded 240..512 and never taller than 384, a row is one 32px line
  whose label carries no slash and no description, the highlighted row raises the card, the
  hint shows for a bare slash and not for a query, `/bg` resolves to `background (bg)`, the
  typed run is bolded, arrowing repaints both rows and moves the card, Escape keeps it shut
  until the command line is left, and Tab completes the composer — exit 0/2),
  `--pane-selftest` (with `--screenshot`: drives the Browser pane's own toolset —
  navigate, read_page, a ref click, console, REPL, screenshot, viewport emulation — against
  a local page end to end, exit 0/2), `--ui-selftest` (with `--screenshot`: measures the
  fourteen reference-pinned metrics on the laid-out visual tree — shell geometry plus the
  chat thinking cell's gutter, spacers, clamp and fade — and exits 0/2; the parity
  suite drives it), `--input-selftest` (with `--screenshot`: walks every editable field on
  twenty posed surfaces and checks the three things about a field a screenshot cannot
  settle — that its caret can be seen against what it blinks on, that its placeholder
  starts where `GetRectFromCharacterIndex(0)` says the first character will, and that
  neither the hint nor the first line overflows the field — exiting 0/2), `--e2e="prompt"` (run one real turn,
  exit 0/2), and `--e2e-switch="prompt"` (run a turn, start a second session answering the
  same prompt while the first still streams, come back — exit 0 if the first turn was still
  live and both sessions answered, 2 if the round trip lost either, 3 if the model was too
  fast to leave a live turn). Note
  `RenderTargetBitmap` cannot capture an engine view — verify artifacts with a real screen grab.

## The CLI (JarvisCode.Cli)

`jarvis.exe` — a terminal front-end at the **command-line surface of the reference CLI**
(measured against the standalone 2.1.251 on this machine), driving this repo's engine and
the app's own profile: settings, sessions, MCP servers, skills, hooks and memory are shared,
so a CLI session appears in the app's sidebar and a session started in the app resumes with
`--continue`. `AppServices` gained a `headless` flag (browser bridge on a process-private
pipe, no native-messaging manifest rewrite) and a `settingsFile` override; nothing else in
the App changed except `InternalsVisibleTo("jarvis")`.

- **Help and errors are the reference's own bytes.** `HelpTexts.cs` is generated from the
  installed CLI's real `--help` output with `claude`→`jarvis` swapped
  (`src/JarvisCode.Cli/gen-help-texts.py`; regenerate rather than hand-edit). The generator
  **walks the reference's own command tree** — it parses each `Commands:` listing and
  recurses — so the constants cover all **55** paths, leaves included (`mcp add`,
  `project purge`, `plugin marketplace add`), and it emits a `ByPath` table that
  `Subcommands.HelpForInvocation` resolves deepest-first, the way the reference documents
  every level separately. Every one of the 55 is diffed against the live binary by the
  parity suite below. `CommandLine.cs` is a commander-compatible parser:
  long/short options, `--opt=value`, optional values (`[value]` never eats the next
  option), variadic lists that stop at the next option and also split on commas, choice
  validation, and commander's wording verbatim — `error: unknown option '--x'`,
  `error: option '--model <model>' argument missing`, `error: option '--output-format
  <format>' argument 'bogus' is invalid. Allowed choices are text, json, stream-json.`
  Two precedence rules were measured, not assumed: an error never suppresses a later
  `-h`/`-v` (`claude --bogus --help` prints help, exit 0), while a *required* value
  swallows one (`--model --help` takes "--help" as the model name).
- **Print mode** (`-p`, or any redirected stdin): `text` prints the final answer, `json`
  emits the reference result object (is_error/duration_api_ms/num_turns/usage/modelUsage/
  permission_denials/terminal_reason/subtype/result…), `stream-json` emits `system:init` →
  `assistant` → `user` tool results → result, one JSON line each. Cost fields are zeros —
  this engine does not price turns. With no TTY the permission gate has no prompt callback,
  so unsettled calls are denied, like the reference. `--system-prompt-snapshot <on|off>` is
  the reference's: the conversation's system prompt is recorded once on the session
  (`Session.SystemPromptSnapshot`) and reused verbatim on every later request and resume, on
  by default and off when `--system-prompt` or `--append-system-prompt` supplies text that
  should apply fresh — the desktop does the same, standing down while an output style
  appends to the prompt. `--restricted` (or `CLAUDE_CODE_RESTRICTED=1`) is implemented rather
  than refused: Bash, PowerShell, Workflow, NotebookEdit and WebFetch are removed unless
  `--tools` names them, the file tools stay inside the working directory (a path outside is
  denied, not asked about), `bypassPermissions` is refused with "bypassPermissions not
  supported in restricted mode", and no user, project or local settings file contributes
  rules or hooks. `/effort <level> s` in the REPL changes the effort for the session only,
  as the reference's `s` does, and a level picked without it is saved per model
  (`AppSettings.EffortByModel`), so switching models restores each one's own.
- **Interactive mode is the reference's own session**, ported onto a hand-rolled console
  renderer (`Repl/`, measured against CLI 2.1.257). The transcript is printed into the
  terminal's scrollback and everything under it — the spinner row, the prompt box, the
  completion popup, the footer, a dialog — is a **live region** erased and redrawn on every
  change (`Repl/Render/Screen.cs`), which is the model the reference's Ink app draws with.
  The whole thing runs against an `IConsole` (`Repl/Terminal/IConsole.cs`), so the loop, the
  key dispatch, the command table and the renderer are driven end to end by a scripted
  console in `tests/JarvisCode.Cli.Tests/ReplLoopTests.cs` with no terminal attached.
  **The key layer is the reference's table, not a hand-picked set**
  (`Repl/Keys/KeyBindings.cs`, its `MG` array read out of the binary): all 23 contexts, all
  its actions, and the two platform choices it makes on Windows — image paste on `alt+v`
  (`lNo`) and mode cycling on `shift+tab` (`qNn`). `keybindings.json` is loaded from the same
  profile file the desktop reads, with the reference's whole validation
  (`Repl/Keys/KeybindingsFile.cs`): the shape errors, unknown contexts, empty key parts,
  unknown actions with its Levenshtein suggestion, `command:` bindings outside Chat, the
  duplicate scan it runs over the *raw text* as well as the parsed blocks, and the reserved
  keys a terminal cannot deliver — each with its own sentence. JSON's duplicate-key rule is
  honoured by rebuilding the document (System.Text.Json throws where `JSON.parse` keeps the
  last), and the reserved-key scan runs over the **user's** bindings only, as its `bBe` does.
  Chords are spelled and rendered by its own formatter (`Repl/Keys/ChordFormat.cs`, its
  `cte`: three styles, the shared-modifier collapse, the arrow separator), and an action's
  hint chord is the **last unshadowed** binding, which is what its `kVt` returns — so the
  overlay offers `ctrl + shift + _ to undo`, the last of the four undo chords its Chat block
  binds. A two-chord sequence is held by `Repl/Keys/KeyRouter.cs`, and a press that continues
  nothing still gets its own chance rather than being swallowed; the same rule lets Enter
  submit while the completion popup is open, since the popup claims only its own four actions.
  The composer (`Repl/Input/`) carries the reference's editing: the readline kills with a
  kill ring and yank-pop, its debounced undo buffer, `\`+⏎ / ctrl+j newlines, vim mode behind
  `editorMode`, ↑/↓ history with the draft stashed and given back, ctrl+r reverse search with
  ctrl+s cycling its three scopes over a `history.jsonl` that lives beside the profile, and
  its paste collapsing — `[Pasted text #N +M lines]` past 800 characters or the box's line
  budget, `[Image #N]`, and "paste again to expand", which its `m4e` implements by expanding
  the newest placeholder. Three completions share one engine: `/` over the command table
  through the App's own `SlashMenu`/`FuzzySearch`, `@` over the working directory, and a path
  completion inside `!` shell mode.
  **The rendering is measured, not approximated**: the 186 spinner verbs are its `h` array
  (`Repl/Render/SpinnerVerbs.cs`), the frames its `Nkt` (`∴ ∷ ∵ ∷`), the row its `Wo` —
  verb, then a parenthesised cluster of elapsed · `↓N tokens` · thinking joined by `" · "` —
  and the retry ladder its `Oit`, down to "API error" until the third attempt and the
  rate-limit's own name after it. A tool result folds at three lines behind `⎿  ` with
  `… +N lines (ctrl+o to expand)`, including its rule that exactly one hidden line is shown
  rather than counted (`Repl/Render/ResultFold.cs`, its `Aun`/`LNo`/`bR`). The footer names
  the permission mode with its own symbol and wording, carries the context indicator's three
  sentences (`{n}% until auto-compact`, `{n}% context used`, `Context low (…) · Run /compact
  to compact & continue`) and ends in `? for shortcuts`; `?` opens the three-column overlay
  its `BHe` draws. Markdown is rendered through the engine's own CommonMark+GFM parser in the
  conversation dialect, a **block at a time** as the answer streams — a terminal cannot
  restyle what it has printed, so a paragraph is held until its blank line and a fence until
  its closing marker (`Repl/Render/StreamingMarkdown.cs`).
  **The four dialogs are the reference's** (`Repl/Dialogs/`): the permission prompt with its
  "Yes" / standing-permission / "No, and tell Jarvis what to do differently (esc)" rows and
  its mode labels; the plan approval with "Ready to code?", its three approving rows, the
  feedback row that shift+tab approves through, ctrl+g to edit the plan in `$EDITOR` and the
  200,000-character cap that withholds approval; the AskUserQuestion card with its checkbox
  tab strip, "Other" row and review step; and the workspace trust dialog, asked once per
  folder. They are wired into the turn through `CliTurnRunner`'s `AskUser`/`PlanApproval`/
  `Workers` seams, which a print run leaves empty — so plan approval, AskUserQuestion and
  background agents are live in the REPL and inert in `-p`, and a finished worker's report
  arrives as the reference's `<task-notification>` on a hidden user message.
  **The command surface is its own** (`Repl/ReplCommandTable.cs`): 76 entries with the
  reference's own descriptions and aliases (`/cost` and `/stats` on `/usage`, `/peers` on
  `/list-agents`, `/continue` on `/resume`), the session flows behind them — `/clear` starts
  a *new* session and leaves the old one resumable, `/compact <instructions>` passes them,
  `/resume` opens the picker or takes a search term, plus `/rename`, `/export`, `/rewind`,
  `/branch`, `/fork` and `/subtask` — and the account- and product-bound ones parsing and
  then refusing by name rather than reading as typos. An unknown name gets the reference's
  own line, including its two-edit "Did you mean" cap. `/batch`, `/debug` and
  `/release-notes` are real here as they are on the desktop, the last drawing the
  reference's own picker (`Repl/Dialogs/ReleaseNotesPicker.cs`); `--debug-file` is
  honoured rather than only parsed, which is what `/debug` reports as this session's
  log.
  Two of the brief's row wordings could **not** be found in CLI 2.1.257 in any encoding —
  `Running…` and `Waiting…` — so they are not shipped rather than invented; the row's shell
  command is still cut to its two lines and 160 characters.
- **One turn assembly for both**: `CliTurnRunner` reproduces the Code surface's outgoing
  message — gitStatus reminder on the first message, the budgeted skill listing, plan mode,
  brief/goal, ultrathink & ultracode keywords, memory recall — then runs `session_start` and
  `user_prompt_submit` hooks and the orchestrator, exactly as `ChatViewModel` does.
- **Subcommands**: `doctor`, `mcp` (list/get/add/add-json/remove/login/logout), `plugin`
  (list/marketplace/install/uninstall/validate), `project purge` (--all/--dry-run/-y),
  `auth status` (per-provider credential state, JSON or `--text`) run for real. Everything
  cloud- or binary-bound — `agents`/`attach`/`logs`/`stop`/`rm`/`respawn`, `install`,
  `update`, `setup-token`, `gateway`, `import`, `ultrareview`, `auto-mode` — parses and then
  refuses with the reason, and so do the flags in `RootOptions.Unsupported` (`--cloud`,
  `--teleport`, `--remote-control`, `--worktree`, `--bare`, `--safe-mode`, `--chrome`, …). Every reference flag still *parses*, so a script written against `claude`
  gets a precise refusal instead of "unknown option".
- `--effort` is **not** a commander choice: measured against the reference, an unknown value
  prints `Warning: Unknown --effort value 'x' — ignoring it and using the default effort.
  Valid values: …` on stderr and the run continues at the default, rather than failing the
  way an invalid choice does.
- **What a headless run sends** (measured by capturing `jarvis.exe -p` at the same listener
  the reference was captured at, and diffing request against request): no host append and
  no scratchpad block (the desktop's), a Shell line and a shell set that follow the launch
  shell (Bash alone from Git Bash), the routine and worktree tools the reference's `-p`
  list carries (`CronCreate`/`CronList`/`CronDelete`, `EnterWorktree`/`ExitWorktree`), and a
  print run without `AskUserQuestion`, `Monitor`, `EnterPlanMode` or `ExitPlanMode`. Three
  desktop tools stay off the CLI and are declared as such: `ListAgents`, `ReportFindings`
  and `ScheduleWakeup` render into the desktop's panes.
- Deliberate deltas: session ids are the engine's (`--session-id` still requires a UUID like
  the reference, and then uses it verbatim); `--input-format stream-json` is refused. A typed
  `/name` that is not a built-in now resolves against the session's skills and runs as one,
  so the reference's skill namespace reaches the REPL as well as the model-side skill tool.
  What the REPL deliberately does not draw is declared in
  `Deltas/reference-surface-deltas.tsv` as `component` rows: the reference's scrollable
  transcript pane and its show-all toggle (this front-end prints into the terminal's own
  scrollback, so ctrl+o toggles verbose output instead), its message-selector list (/rewind
  restores the last prompt rather than offering every earlier one), its diff dialog, its
  model picker and effort slider (both are commands here), the proactivity menu and
  push-to-talk (server-side and cloud-voice), and its session tab strip.
- `JARVISCODE_PROFILE=NAME` isolates the CLI the way `--profile=NAME` isolates the app —
  that is how the E2E runs seed a throwaway profile pointed at a capture listener.

## Parity verification (tests/JarvisCode.Parity.Tests)

Parity used to be an assertion in this file; this project makes it a check. It is separate
from the three unit suites because it **runs the installed reference** — the standalone CLI
and the packaged desktop app — and skips itself, with a reason, on a machine that has
neither (`ReferenceCliFact`/`ReferenceAppFact`; xunit 2.9 has no runtime `Assert.Skip`, so
the condition is evaluated in the attribute's constructor). 3,765 cases, ~4min, measured
against **CLI 2.1.257 and desktop 1.44121.2.0** — the desktop bundles 2.1.255 and 2.1.258
and runs the latter, but nothing here reads a bundled CLI, so the wire, prompt and
tool-doc recordings stay the standalone 2.1.257's:

- **CLI help**, all 55 command paths: runs `claude <path> --help` (six at a time, cached in
  a class fixture), applies the generator's brand swap and demands byte equality. A failure
  usually means the installed CLI moved, so the message names both versions and points at
  `gen-help-texts.py`. A second test walks the reference's own `Commands:` listing and
  fails if it declares a command we carry no help for.
- **CLI behaviour**: sixteen argument-handling command lines run through both binaries with
  stdout, stderr and exit code compared (argument handling only — no case starts a turn, so
  the suite spends no tokens and needs no network). Seven more assert the **deliberate
  deltas** — `--cloud`, `--teleport`, `--remote-control`, `--input-format stream-json`,
  `--session-id`, `ultrareview`, `install` — with their exact refusal sentences, so an
  intended difference cannot be "fixed" by accident.
- **UI strings**: each ported label is looked up by the reference **message id** it carries
  in `en-US.json` (`QhT5HdB9mD` = "Waiting for Claude…", `mOzN+6yGec` = "Almost done
  thinking…", …). Matching by id rather than by "some entry has this text" is what makes it
  meaningful in a 23k-entry catalogue. Thirty-five of those pin *behaviour* — the label a
  method returns for a given elapsed time — and the flat remainder rides
  `Manifest/ui-strings.tsv`: **491 strings** paired with their message id, asserted in both
  directions (the reference still says it under that id; the named source still renders it)
  plus a **completeness scan** that fails when the App starts rendering a catalogue string
  no row covers. `Manifest/ui-strings-ignored.tsv` holds the 37 matches that are not UI text
  — language ids, JSON keys, profile folder names — so an unrelated rewording upstream
  cannot fail the suite over a registry value. `JARVIS_APPROVE_UI_STRINGS=1` rewrites the
  manifest from the installed app.
  **The brand is the one word these rows do not share with the reference**
  (`UiBrand`): the reference names the assistant Claude and this app names it Jarvis, so a
  row stores what *this* app renders and matches when it equals the reference's text either
  verbatim or with the brand swapped ("Claude Code" → "Jarvis Code", then "Claude" →
  "Jarvis"). Both candidates are derived from what the reference says *now*, so a rewording
  still fails; and the scan indexes every catalogue string twice — verbatim first, in its
  own pass, so a rebranding can never take the slot a real entry owns — which is what keeps
  a rebranded string in the manifest instead of quietly dropping out of it. The rule is
  deliberately **not** `ReferenceInstall.Rebrand`, which the CLI help check applies to the
  reference's terminal output: that one must never learn this swap, because the help text
  says "Claude subscription", "Claude Desktop" and "Claude in Chrome" about products that
  keep their names here.
- **The brand** (`AssistantBrandTests` + `Manifest/brand-exceptions.tsv`): the check that
  the rebrand stays done, over **both front-ends** — it used to read only the App, which left
  the CLI's own user-facing text unwatched while the REPL grew into a surface of its own; the
  first thing the wider scan found was an interrupted row still naming the reference.
  Renaming the user-visible strings was a one-off edit, and the
  string checks above would accept a revert — they compare against the reference, and the
  reference says Claude. So this one reads the App's own sources and requires **every**
  place still spelling "Claude" to be declared with the reason it is not the assistant's
  name: 19 rows covering Anthropic's models, the reference product this app imports skills
  and sessions from, the `mcp__Claude_Browser__` wire prefix, the
  `X-Claude-Code-Session-Id` header, the theme tokens, and the four prompt files the model
  alone reads (those as whole-file rows, since `PortedTextParityTests` already pins them
  line by line). C# is read as literals, so a comment about the reference needs no row;
  **XAML is read line by line**, because its literal extractor knows six attribute names
  and "unseen" is the one answer this check may not give. Nothing here needs the reference
  installed — the rule is ours.
- **Ported prose** (`PortedTextParityTests`): the other half of the string check, and the
  one that scales. It reads the twenty-eight reference-derived source files, pulls every fixed run
  of prose ≥30 chars the program can actually emit — through a small C# tokenizer
  (`PortedText.cs`) that skips comments, understands verbatim and raw literals, splits at
  interpolation holes and **joins concatenation chains**, without which this port's wrapped
  sentences would read as drift — and requires the reference build to still contain it. What
  it does not contain lives in `Deltas/ported-text-deltas.tsv` (257 rows) with the reason it
  differs; a row whose text the reference *does* carry fails as stale, a row saying REVIEW
  fails as unexplained, and `JARVIS_APPROVE_PORTED_TEXT=1` regenerates the file with a first
  guess at the reasons. The CLI-derived files are searched in `claude.exe` and the
  desktop-derived ones in the packaged app — **`app.asar` plus the ion-dist chunks and the
  catalogue** (`ReferenceCorpus`, one flat byte array per build, searched in parallel) —
  because a string that moved from one product to the other is exactly the drift worth
  catching. A shipped build stores one string four ways, so `ReferenceCorpus.Contains`
  tries all four — UTF-8, UTF-16, JavaScript quote/backtick escaping, and every non-ASCII
  character written as a \uXXXX escape — or \xHH below U+0100, which is how a middle
  dot is carried — which is what a minifier emits for an em dash.
  The last two were added with this round and turned six false deltas back into matches.
- **The surface** (`Deltas/reference-surface-deltas.tsv`): the question the prose
  check cannot ask. Ported text is compared line by line, but a whole *feature* the
  reference has and this port does not leaves no line to compare, so the only record of it
  was prose in this file, and prose is not checkable. The manifest names every
  reference tool, hook event and slash command this build does not carry whole, with one of
  nine dispositions: `cloud` / `installer` / `surface` / `platform` / `retired` are the ones
  that cannot exist here and hold most of the rows; `portable` is the work list rather than
  a refusal; `partial` is carried with a named piece missing; `diverged` is the dangerous
  kind — the reference's name over a different source, where nothing looks wrong until
  somebody assumes the behaviour follows the name; `addition` is this build's own. The file
  is the count, so this line does not repeat one.
  `ReferenceSurfaceParityTests` is what makes it a check rather than a note. It reads the
  reference's own inventories out of the installed CLI — the hook-event array, found by two
  of its members and bounded by the brackets around them, and the slash commands, found by
  the `type:"local"`/`"local-jsx"`/`"prompt"`/`argumentHint`/`userFacingName` shapes and read
  from **every** `name:` in the window around each — then requires each entry to be answered
  by this port or declared with a reason. **Every, not the nearest**: reading only the
  nearest name looked tidier and silently lost five real commands (batch, debug, design-sync,
  sandbox, schedule), which in a checker is the worst kind of wrong, because it cannot fail
  for a command it never saw. Over-reading costs a few minified identifiers instead, and
  those are stable, so `Manifest/reference-surface.tsv` records the extraction as a baseline
  (**33 hook events, 111 commands**) and the suite compares against it in both directions:
  a reference release lands as one failure naming what moved, `JARVIS_APPROVE_REFERENCE_SURFACE=1`
  re-records it, and the checks then name whatever is newly unaccounted for. The port
  answers 76 of the 111 (75 in the desktop's command table, plus /cost in the CLI REPL) and
  declares the other 45. Two more tests keep the manifest honest: the disposition vocabulary
  and a real reason on every row, and — against the installed build — that no row describes
  something the reference no longer has, and that nothing declared as this build's own is
  in fact the reference's.
  **The manifest grew two kinds on 2026-09-01**, `mcpserver` and `mcptool`, because it had
  no vocabulary for a whole in-process MCP server: it could describe a missing CLI tool but
  not the ten servers the desktop shell proxies as `mcp__{server}__{tool}`, so those went
  unrecorded while the port registered their tools under bare names instead. A port could
  therefore pass every other check here and still share none of its tool names with the
  reference. `InternalMcpSurfaceParityTests` closes that: the reference's ten servers and
  74 tools are pinned as a table measured from a live ccd session and cross-checked against
  app.asar, and five tests require each one to be carried or declared, require every name
  this build carries to be one the reference has, require the shell's own server list to be
  spelled the reference's way *and* to recognise everything the shell composes (a server
  missing from it would be silently deferred behind tool_search), and require a legacy alias
  to resolve without ever being advertised. The mcpserver/mcptool rows are checked against
  the **desktop** corpus rather than the CLI binary, which is where those names live.
- **The wire** (`RequestWireParityTests`): what the reference *sends*, not what it contains.
  Sixteen fixtures under `Captures/` were recorded from CLI 2.1.257 by pointing its
  `ANTHROPIC_BASE_URL` at a local listener with a dummy key — no request left the machine,
  and the identity headers, `metadata` and prompt were dropped before writing — one per
  model class and effort level (opus-5 at high/low, fable-5-1, fable-5, opus-4-8, sonnet-5
  at low/high/xhigh/max, opus-4-6, opus-4-5, sonnet-4, sonnet-4-5, haiku-4-5, 3-7-sonnet,
  3-5-sonnet). Each is compared against what `AnthropicProvider.PreviewRequest` builds:
  path, model, max_tokens, stream, thinking field by field, output_config,
  context_management, and the beta list as a subset-plus-order check. `thinking.display`
  and `metadata` are declared deliberate omissions and asserted *absent*.
  `Captures/known-wire-gaps.tsv` is the place a measured difference the port has not
  closed goes, asserted to still be exactly that gap; it is empty since this round closed
  the four it held.
- **Tool docs as sent** (`ToolDocsParityTests` + `Captures/Tools/tools-cli-2.1.257.json`):
  the tools array of three reference requests (opus-5 lean, fable-5-1 lean, opus-4-5
  classic), verbatim. The Core tools that need nothing but themselves are wrapped the way
  the turn factory wraps them and compared field by field — description and input schema,
  both exact — and the generated `CapturedToolDocs` table is checked against the same
  recording, so a regeneration that drifted shows up here rather than on the wire.
- **Smoke self-tests**: `--pane-selftest` and `--subagent-selftest` now run from the suite
  beside `--ui-selftest`, in throwaway profiles; `--e2e` and `--e2e-switch` are wired behind
  `JARVIS_E2E=1` because they need a configured provider and spend tokens (the switch test
  accepts the app's exit 3, which is it reporting that the model answered too fast to prove
  anything).
- **The prompt as sent, not as stored** (`RenderedPromptParityTests` +
  `Captures/rendered-system-prompt-cli-2.1.257.txt` and its three per-model siblings,
  with `ClassicPromptParityTests` on the two classic recordings and
  `SubagentPromptParityTests` on the six built-in agent types): every other check here searches the
  reference's *bytes*, and the build stores prompts as **template literals** — it holds
  `- You must ${Tn} the file …` where the sent prompt reads "You must Read the file …". A byte
  search therefore cannot tell a section this port never carried from one it words differently,
  which is how nine sections went missing while 1,265 tests stayed green. This one renders both
  sides and diffs them: every line of the recorded CLI prompt must appear in what
  `ReferencePromptBuilder` produces. One-directional, because this port legitimately adds
  sections the CLI has no equivalent for, and the lines the environment decides are dropped by
  name with the reason. The reference side is a **recording**, the choice the wire fixtures
  already make: spawning the CLI from a test host does not work here — with no console attached
  it produces no request and no output at all, while the same command from a shell captures
  fine. The refresh recipe is in the file.
- **The desktop's prompt appendices, enumerated** (`HostPromptAppendixParityTests`): the byte
  checks can only compare text this port already emits, so they cannot notice a section the
  reference appends that was never ported — which is how four went unnoticed. This reads the
  reference's own declarations instead: the appendices sit in `app.asar` as adjacent literals
  that all open with a blank line, so one window around a known member yields the set, and each
  must be carried by `HostPromptSections` or declared as a `prompt` row. The auto-fix-PR block is
  the one accounted for by declaration alone, which is asserted, so the declaration path stays
  live rather than the check always passing.
- **Interpolation-tolerant matching** (`ReferenceCorpus.ContainsAllowingInterpolation`): the
  build stores `via the ${Tn} tool` where the sent text reads "via the Agent tool", so a
  whole-line search reports a line as missing when only the hole differs. When the whole line is
  absent this probes the line's two ends — not every substring, since searching a quarter of a
  gigabyte is not free — and a line with holes in **both** ends is reported missing rather than
  guessed at, which is the safe direction for a check whose job is to notice drift.
- **Provenance on the ported sources** (`PortedSourceProvenanceTests`, `PortedSource.MeasuredAgainst`):
  a delta row records *why* a line differs but not *when* it was checked, so after a reference
  release every mismatch looked alike. A source may now name the build its text was read from,
  and the failure message prints it beside the installed version. The field is optional —
  inventing a provenance for a file nobody measured would be worse than leaving it blank — so
  what is enforced is that a stamp, once given, names a real build and matches the side the file
  is checked against.
- **Our own documentation** (`ProjectDocClaimTests`): not a parity check — it needs nothing
  installed — but the one that catches a claim with nothing behind it. CLAUDE.md said the system
  prompt carried `<browser_surfaces>` while the string was in no source file, and no test could
  catch that, because there is nothing to write a test against for something that does not
  exist. Every backticked path into this repo's own source must resolve (112 today; the
  project shorthand this file writes — a leading Core/, App/, Cli/, Host/ or Providers/ — is
  expanded to its real directory first), and every backticked prompt
  tag whose name carries a separator must appear in `src` — searched by the bare name, since a
  tag is usually built from a constant, and by separator because a bare `<env>` would match
  anywhere and prove nothing.
- **Line endings** (`LineEndingParityTests`): C# raw string literals keep the line endings of
  the file they are written in, so a CRLF checkout puts CRLF on the wire where the reference
  sends LF — the same text, different bytes, and invisible to every other check here because
  they all compare text. The suite fails on any multi-line literal carrying a carriage
  return (escaped `\r` in code is fine — only literals that span lines count) and on a
  missing `.gitattributes`, which pins `eol=lf` so a fresh clone on Windows cannot introduce
  them.
- **The harness blocks as rendered** (`HarnessBlockParityTests`): the ported-text sweep
  asks whether every sentence is still in the reference; this one asks whether the block
  this port *renders* reads the way the reference renders it — the deferred-tools delta's
  paragraph order and blank-line separators, the task-notification envelope (including
  that wrapping twice is a no-op and that a closing tag inside the payload is escaped),
  the `<persisted-output>` block with the reference's size formatting, the four
  background-command openings, and the async-agent launch result in both its forms. The
  sentences that carry an interpolation hole in the build — every one that names
  ToolSearch, and both notification paragraphs, whose first line is the header
  interpolated in — are compared against the corpus as fragments, since a whole-line
  search there proves nothing.
- **Timing**: the constants that decide *when* the status line acts, each carrying the
  evidence it was read from — label dwell 650ms (`Yp(label, 650)`), morph 180ms with
  `cubic-bezier(.2,0,0,1)` (`Dd=[.2,0,0,1]`), cluster fade 150ms, shimmer `2s ease-in-out
  3s infinite` over opacity 1→.75→1, stats visible after 2s (`f>=2`), token count-up 400ms.
  Three of them are additionally **re-derived from the installed bundle** at test time, so
  the reference moving is caught rather than assumed away.
- **UI geometry**: drives the app's own `--ui-selftest`, which measures the
  reference-pinned metrics (sidebar 288, title bar 36, session header 48, epitaxy titlebar
  32 — the window's own header row and the Code session's titlebar are two different
  elements — settings nav 192, theme picker 920×660, quick entry 606, tile padding 8, tile
  gap 12) on the **laid-out visual tree** — a style change
  can move a control without touching the literal it was declared with. A metric whose
  element never appeared is reported as such, because every comparison against `NaN` is
  false and would otherwise pass silently.
- **CLI literals**: every string ported out of the CLI binary — the teammate naming
  refusals, the Team Coordination reminder, the task-board docs and result lines, the
  workflow determinism guards and refusals, the background-agent banners, and the whole of
  the interactive REPL (its keybinding validation, spinner and retry rows, footer, context
  indicator, ? overlay, four dialogs, resume picker, command descriptions and startup tips)
  — is asserted to still exist verbatim in the installed CLI. The desktop catalogue cannot
  cover these: they live only inside the binary, so the check reads its bytes.
- **UI golden images**: renders `--open=settings` and `--open=themes` in a fixed profile,
  theme and window size and compares to approved baselines under `Baselines/` (per-channel
  tolerance 8, at most 0.2% of pixels differing; a failing render is kept as
  `<surface>.actual.png`, which `.gitignore` excludes). This is regression, not parity —
  two different apps showing two different sessions never match pixel for pixel, so layout
  parity is asserted numerically by `--ui-selftest` instead. A missing baseline is written
  and the test still **fails**, because a run that only wrote a baseline proved nothing.

## Rules

- **Do not modify existing Core/Providers/Host behavior.** Additive upgrades are fine; the UI
  consumes the engine through `AgentOrchestrator.RunTurnAsync(AgentTurnContext)`
  (`IAsyncEnumerable<AgentEvent>`), its own `IPermissionGate`, and path-parameterized stores.
- `AppSettings` intentionally carries no window/layout/theme fields and must not grow any —
  presentation state lives in the App's own `ui-settings.json` (`Services/UiSettings.cs`).
- Do not restore, copy, or adapt UI code from git history predating the App project; the old
  deleted UI is not a starting point.
- **In front of the user the assistant is Jarvis, not Claude.** A string the user reads —
  a label, a status, a notice, a settings description, a slash-command summary, a tray
  balloon — says Jarvis, and this app says Jarvis Code, however the reference words it. The
  name stays "Claude" only where it is not this assistant's: Anthropic's models on the
  provider cards, the reference product whose skills and sessions this app imports, wire
  names and headers, theme tokens, and the prompt text the model alone reads (where the
  model *is* Claude, and where the harness prompt is pinned verbatim). New text has to be
  declared in `tests/JarvisCode.Parity.Tests/Manifest/brand-exceptions.tsv` to keep the
  name, so the exception is a decision rather than an oversight.
