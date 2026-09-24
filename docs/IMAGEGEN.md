# ChatGPT Web ImageGen through the existing browser

Applies to Jarvis Agent **1.0.87**, assembly/file **1.0.87.0**, and Jarvis Agent Browser extension **1.4.0**. This is an extension-backed integration, not Codex runtime parity and not a public OpenAI Image API client. Execution evidence and live acceptance scope are recorded in [BUILD-STATUS.md](BUILD-STATUS.md).

## Architecture and boundaries

An authorized MCP client calls the local Agent's `image_gen.*` tools. The Agent creates a durable, explicit-session job and asks its existing browser service to contact **one exact extension instance**. That extension operates a dedicated ChatGPT tab in an already open normal Chrome/Edge window. It uses ChatGPT's composer, attachment input, Send and Save controls. Browser-native downloads become immutable local original artifacts, with separate bounded PNG previews returned through existing MCP image content blocks.

The ImageGen backend never constructs `ChatGptWebViewTransport`, launches a browser executable, creates an embedded Chromium profile, imports/exports cookies, reads an API key, or calls public Images/Responses APIs. No failure path changes those rules. Existing unrelated Jarvis providers and the dev-browser tools are not removed or modified by this integration.

Native messaging still uses the small `jarvis-browser-host` relay and browser-service process; these are **not another browser**. No browser identity is inferred from the default/first active Chrome connection. A locally selected `extensionInstanceId` is stable across extension reconnects; temporary connection IDs are resolved afresh and never used as persistent identity. Missing or duplicate selected instances fail closed. Generic browser tools keep their previous routing behavior; ImageGen has a separate exact-instance route.

Chrome/Edge retains its normal authenticated session. The page adapter checks the account identity through the page's same-origin session read and only returns a SHA-256 identity fingerprint, never cookies, access tokens, email addresses or the full authentication response. Account fingerprints are pinned per image session/job. Challenges and expired sessions must be handled by the user in the selected browser. There is no challenge or rate-limit bypass. ChatGPT account plan/limits still apply; this module does not turn a subscription into unlimited generation.

## Activation

1. Build/publish and start the **1.0.87 Agent**, reconnect it to Jarvis Control. The source build/server deployment does not replace a running old Agent automatically.
2. Use Connection center's browser integration setup to copy/register the updated extension assets. Reload the unpacked **Jarvis Agent Browser 1.4.0** extension in the **existing** Chrome/Edge profile. The new Downloads permission may require browser approval. Do not create another profile or import cookies.
3. Open ChatGPT in that profile and sign in normally. Use **ImageGen > Refresh connections > Use selected browser**, or the extension popup's **Use this browser for ImageGen**. Merely discovering a connection never binds it. A popup reports that a selection was *requested*; the Agent settings show the saved result.
4. Import/publish the Agent's four `image_gen` descriptors in Jarvis Control and configure normal local tool approvals. Image generation, recovery and cancellation are sensitive operations; this release does not automatically arm control or grant Full permissions.
5. Open an explicit Jarvis session and retain its own `_jarvis.sessionHandle`. Stateful image references are never shared via the sessionless owner/device scope.

The extension normally creates one inactive ChatGPT tab in an existing normal window for each Jarvis session. It does not commandeer the tab controlling Jarvis or an arbitrary personal conversation. The first successful submission creates the remote conversation; subsequent jobs keep its ID. Separate tabs/conversations isolate work, **not account quota**.

### Optional existing-tab assignment

Call `image_gen__get_state` once so the session appears in the extension, then open the intended image-only ChatGPT tab. In the extension popup select the Jarvis session, confirm that this is an image-only conversation (not the controller chat), and click **Assign current tab**. A web page cannot call this configuration action: the extension accepts it only from its packaged popup. Tabs or conversation IDs already owned by another session are rejected. An unfinished request cannot be moved to another tab. User-adopted tabs are preserved when a session closes; ChatGPT conversations are never deleted by teardown.

A full browser restart loses the browser's session-scoped live tab ownership. Durable jobs are retained, but tab IDs are not guessed or adopted automatically. Re-establish tab ownership explicitly and inspect the original conversation before cancelling/replacing an uncertain request. Rebinding to another account/profile does not migrate existing jobs.

## Tool contract

Canonical IDs are `image_gen.imagegen`, `image_gen.read`, `image_gen.cancel`, and `image_gen.get_state`; default MCP names use `image_gen__`.

### Start: image_gen__imagegen

```json
{
  "prompt": "Create a game inventory icon with a transparent background.",
  "request_id": "inventory-icon-v1"
}
```

An accepted start returns a `jobId` immediately. `request_id` is an optional idempotency key, not an image-model parameter. The same key and identical normalized request return the original job; the same key with different input is rejected. When omitted, the call ID supplies one-call idempotency only. After a lost acknowledgement, inspect `get_state` rather than issuing a new unidentified request.

For local edits:

```json
{
  "prompt": "Keep the object unchanged and make the background transparent.",
  "referenced_image_paths": ["D:\\Project\\art\\source.png"],
  "request_id": "transparent-edit-v1"
}
```

For recent generated artifacts:

```json
{
  "prompt": "Keep the composition and change the background to night.",
  "num_last_images_to_include": 1,
  "request_id": "night-edit-v1"
}
```

`prompt` is nonblank, maximum 16,000 characters. Supply **either** 1..5 local paths **or** a recent count 1..5, never both. A new image omits both. Local paths are resolved against the accepted Jarvis workspace and snapshotted before upload; files outside that workspace still require the normal local tool approval. The combined reference size is at most 10 MiB. Image bytes must decode successfully. No required input is silently dropped. Upload starts only from a composer with no draft or leftover attachments, and Send is blocked until every staged filename is present and upload activity has finished.

Recent references come only from completed output artifacts of the same owner/device/explicit session. Client-chat attachments are not magically transferred by an ID: they must first be available as local files. No code searches for the most recent Downloads file or borrows another chat's image.

The adapter appends a visible, unique request-tracking reference and asks ChatGPT not to render that reference. This correlates the original user message to the image response; it is not a local execution instruction. Model-returned scripts/tool envelopes are not executed by this backend.

### Read: image_gen__read

```json
{"jobId":"ig_0123456789abcdef0123456789abcdef","preview":true,"resume":false}
```

Returns status, bounded metadata and, on completion, up to five separate PNG preview image blocks. The original artifact local paths, byte sizes, dimensions, MIME, SHA-256 and parent IDs are retained. `preview:false` is suitable for polling without repeatedly transferring images.

`resume:true` explicitly reconciles an uncertain job in **its original browser instance**. It can observe and download the original result, but never calls prepare/upload/submit again. An explicit local rebind to the same instance may refresh its binding revision; a different instance/account is rejected. Browser restart/lost ownership may require manual recovery before reconciliation can proceed.

### Cancel: image_gen__cancel

```json
{"jobId":"ig_0123456789abcdef0123456789abcdef"}
```

Requests local cancellation and best-effort Stop on the exact tracked ChatGPT turn. Only owned download IDs may be cancelled. Explicit cancellation can abandon an uncertain local job so a later request can be accepted; it does **not** undo a remote image, delete history, guarantee server cancellation or restore quota. Read the job after cancellation. Already completed jobs remain completed.

### State: image_gen__get_state

Takes no arguments. Reports the saved browser binding, exact-instance connectivity, the session's tab state and its latest 25 jobs. It does not launch a browser or submit a prompt. An empty browser/tab state is not proof of a usable ChatGPT login; runtime account/identity checks run before image operations.

## State and failure semantics

Normal states:

```text
queued -> binding_browser -> opening_chatgpt_tab -> uploading_inputs (edits only)
       -> submitting_prompt -> waiting_for_generation -> downloading_result -> completed
```

A send intent is persisted before the browser is allowed to click Send. A lost acknowledgement after that intent produces `completion_unknown`, not an automatic retry. Failures include disconnected/binding-changed browser, signed-out/challenged account, incomplete upload, unsupported page, changed conversation, quota/refusal, unverified/failed download, invalid image format and local storage failure. `errorCode` supplies a stable machine-readable explanation while user messages avoid token/payload dumps.

One unresolved image job per Jarvis session is admitted. The Agent bounds active jobs at 16, retained jobs at 256 per session, and each job at 20 minutes of local execution. Extension history is bounded at 1,000 jobs. Capacity errors preserve old data instead of deleting history. Polling commands remain short relative to the MCP gateway timeout; no gateway timeout increase is needed. Jobs retain their session/browser resource lease; `read`, `cancel`, Pause and session stop remain available.

Pause/disconnect stops local jobs. After submission, transport/session cancellation retains uncertainty unless the user explicitly cancels the local job. Restart does not resume or resend jobs automatically. Journal reads and atomic replacement writes are synchronized to avoid Windows sharing races; recovery re-reads the newest journal before classifying an interrupted job.

## Result verification and storage

The page adapter accepts only loaded generated-image cards after the uniquely correlated latest user message, with stable message/conversation identity and recognized Save/Download controls. It waits for generation to stop and the image set to remain stable across three polls. Unsupported or ambiguous page structures fail closed. A prose statement that an image exists, an input thumbnail, an arbitrary markdown image, an HTML download, or a screenshot is not an image result.

The extension downloads from the selected card's Save target. Allowed direct download origins are ChatGPT's backend file path and OpenAI's image-file CDN; URLs are not supplied by the remote caller. For a Save button, the extension correlates the download using the exact conversation referrer, allowed image URL, start window, and a single unambiguous candidate. Only its matching filename event is renamed. Oversized owned downloads are stopped on the next status check; unrelated downloads are not cancelled. If the browser does not provide enough attribution, the result is `DOWNLOAD_UNVERIFIED`, not a guessed file. Other downloads are untouched.

Browser downloads stay under `Downloads/JarvisImageGen/<jobId>/result-*`; the actual Downloads root may be customized by the browser. Browser safety checks must pass; Jarvis does not accept a dangerous download for the user. Local import rechecks file location, no links/junctions, byte size, file magic, successful decode and dimension limits. It copies originals to:

```text
%LOCALAPPDATA%/JarvisAgent/imagegen/<owner-device-session-hash>/<jobId>/
    job.json
    inputs/     immutable source snapshots
    outputs/    immutable generated originals
    previews/   separate bounded PNG previews
```

Per result: at most 24 MiB, 8,192 pixels on either dimension, and 32 million decoded pixels; all results together at most 64 MiB. PNG/JPEG/GIF are supported by Windows decoding; WebP additionally depends on an installed compatible Windows codec and otherwise produces an explicit error. GIF originals are preserved, but previewing uses the first frame. SVG/HTML are not accepted. Deterministic output names permit retrying an import only when bytes match; an existing different artifact is never overwritten.

At most five previews, each 384 KiB before base64, keep the complete MCP result below the existing 8 MiB wire cap. PNG previews preserve alpha; original bytes are never transcoded for transport. Download source URLs and browser auth metadata stay out of tool summaries. Local paths are not public download URLs: a remote client gets previews and metadata, not a new public artifact server.

## Files and deployment scope

Core owns request validation, tool descriptors, journal/job lifecycle and session ownership under `Jarvis.Agent.Core/ImageGeneration`. Windows owns exact-instance extension transport, local binding settings and raster artifact storage under `Jarvis.Agent.Windows/ImageGeneration`. Browser service dispatches a constrained `imagegen.*` command family; the vendor bridge adds exact-instance dispatch and a local popup-origin binding event. The extension's `imagegen-page.js`, `imagegen.js` and packaged popup implement only this workflow. Desktop adds the sixth **ImageGen** settings destination.

The MCP wire protocol and gateway image-block handling are unchanged. The server release is still rebuilt/deployed when requested to keep the platform version aligned. Production promotion uses the existing service upgrade path, preserves server secrets/data and retains rollback. Do not replace production with the first-install script.

## Verification and known acceptance boundaries

Run `dotnet test` for Core/Windows/Server, `node --test tests/browser-session-isolation.test.cjs tests/imagegen-extension.test.cjs`, the repository Python checks, published-output checks and WPF UiSmoke. Tests use synthetic image files and deterministic browser fixtures: they must not require an API key or consume a ChatGPT account quota.

A live read-only Chrome probe can verify current composer/input selectors, but does not prove generation/edit/download end-to-end through the new extension. Full live acceptance requires the updated Agent, extension permission/reload, explicit user browser selection and real create/edit jobs. ChatGPT's private page structure and same-origin session response are not stable public integration contracts. Any unverified live requirement must remain explicitly recorded in BUILD-STATUS rather than being inferred from passing mocks.
