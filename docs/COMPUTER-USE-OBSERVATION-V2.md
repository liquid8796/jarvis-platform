# Computer Use Observation V2

Version 1.0.92 makes desktop input depend on the exact visual/accessibility state that justified it. The public tool remains `computer_use` with canonical ID `computer_use.computer_use`.

## Observe

Use `get_window_state` after choosing exactly one window from `list_windows` or `list_apps`:

```json
{
  "action": "get_window_state",
  "window": { "app": "notepad", "id": 12345 },
  "include_screenshot": true,
  "include_text": true,
  "max_nodes": 500
}
```

The response includes:

- `observation_id`: opaque owner/device/session/window-bound state ID;
- `generation` and `observed_at`;
- the returned window and exact screen bounds;
- optional `screenshot_id`, image metadata and displayed PNG;
- optional accessibility element tree with stable indexes for this observation only;
- `focused_element`, `selected_text`, `selected_elements`, and bounded `document_text` when available;
- `truncated` when the node budget ended traversal early.

`include_screenshot` defaults to true. `include_text` defaults to false because full UI Automation traversal is more expensive. Request text before using `element_index`.

Screenshots first use `PrintWindow` with `PW_RENDERFULLCONTENT`, which can render many covered windows without reading pixels from the foreground desktop. If Windows reports failure or returns a blank frame, Jarvis uses desktop copy after activating the target. The response identifies the selected `capture_backend`.

## Act

Every input action requires the current observation ID:

```json
{
  "action": "click",
  "window": { "app": "notepad", "id": 12345 },
  "observation_id": "obs_0123456789abcdef0123456789abcdef",
  "element_index": 17
}
```

Coordinate-backed input also requires the screenshot from that same observation:

```json
{
  "action": "click",
  "window": { "app": "notepad", "id": 12345 },
  "observation_id": "obs_0123456789abcdef0123456789abcdef",
  "screenshot_id": "shot_0123456789abcdef0123456789abcdef",
  "coordinate": [420, 260]
}
```

The same screenshot requirement applies to `scroll` and `drag`. Scroll now requires an explicit coordinate inside the observed window rather than falling back to the current cursor.

Observation validation checks:

- authenticated isolation scope;
- native window handle;
- current observation generation;
- exact window bounds;
- two-minute lifetime;
- screenshot identity for coordinate actions;
- element index membership and live UI Automation element availability.

The first action attempt consumes the observation before input dispatch. This is deliberate: once activation or input begins, the outcome can be uncertain even when a later step fails. Reusing the same ID returns `STALE_OBSERVATION`; capture new state before deciding whether to retry.

## Post-action state

Input actions accept `return_state`:

| Value | New observation |
|---|---|
| `none` | Return only the action result. |
| `accessibility` | Return fresh accessibility state without a screenshot. |
| `screenshot` | Return and display a fresh screenshot without traversing accessibility text. |
| `full` | Return both screenshot and accessibility context. |

Example:

```json
{
  "action": "type_text",
  "window": { "app": "notepad", "id": 12345 },
  "observation_id": "obs_0123456789abcdef0123456789abcdef",
  "text": "Hello",
  "return_state": "full"
}
```

The response contains `action_result` and `state`. The returned state has a new observation ID and replaces the consumed observation.

## Security and local control

Observation binding does not grant access. Before observing or acting, Jarvis still enforces:

- Arm/Pause and normal tool approval;
- per-application grants and denied-app policy;
- read/click/full grant tiers;
- Windows integrity/UIPI restrictions;
- owner/device/session isolation;
- desktop execution-resource serialization;
- cancellation and audit behavior.

Window objects must come from current discovery results. Do not construct handles, reuse IDs after lifecycle changes, or infer that a failed action had no effect. Observe again after any input, modal, focus change, window movement, resize, application navigation, or uncertain result.
