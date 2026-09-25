# Artifact Runtime

Version 1.0.94 adds a durable local document runtime to Jarvis Agent. It complements one-shot visualization tools with revisioned content that can be read, updated, shown again, or deleted later in the same explicit Jarvis session.

## Public tools

| Public name | Canonical ID | Purpose |
|---|---|---|
| `artifact_create` | `artifact.create` | Create a bounded artifact, optionally render it immediately |
| `artifact_update` | `artifact.update` | Replace selected fields at an expected revision |
| `artifact_get` | `artifact.get` | Read one artifact in the current session |
| `artifact_list` | `artifact.list` | List bounded metadata, newest update first |
| `artifact_show` | `artifact.show` | Render and display the current or expected revision |
| `artifact_delete` | `artifact.delete` | Soft-delete at an expected revision |

Every operation requires an explicit `js_...` session. Artifact identity is the tuple `(owner, enrolled device, Jarvis session, artifact_id)`; knowing an ID from another session or device does not grant access.

## Supported content

The `kind` field is one of:

- `html`
- `markdown`
- `svg`
- `json`
- `text`

Example creation:

```json
{
  "title": "Release verification",
  "kind": "markdown",
  "content": "# Results\nAll focused tests passed.",
  "metadataJson": "{\"source\":\"ci\"}",
  "show": true
}
```

The response includes an opaque ID such as `artifact_0123...`, revision `1`, content SHA-256, timestamps, and a stable local URI:

```text
jarvis-artifact://artifact_0123456789abcdef0123456789abcdef?revision=1
```

The URI is an identifier, not a network endpoint or ambient authorization token.

## Revision safety

Updates and deletes require `expectedRevision`:

```json
{
  "artifactId": "artifact_0123456789abcdef0123456789abcdef",
  "expectedRevision": 1,
  "content": "# Results\nUpdated after review.",
  "show": true
}
```

If another call already changed the artifact, Jarvis returns `ARTIFACT_REVISION_CONFLICT`. Read the current revision, reconcile changes, and submit a new update. The runtime does not implement last-writer-wins and does not replay a failed mutation automatically.

`artifact_show` accepts an optional expected revision when the caller must prove it is rendering exactly the content it previously inspected.

## Storage

Desktop and CLI store artifacts in the Agent settings root as `artifact-runtime.db`. SQLite uses WAL mode, foreign keys, and a busy timeout. The runtime records:

- owner, device, and session scope;
- title and content kind;
- content and normalized metadata JSON;
- revision and SHA-256 content digest;
- created/updated timestamps and deleted state;
- append-only create/update/delete event rows.

Defaults are 128 active artifacts per session, title length 240, content length 2,000,000 characters, metadata length 64,000 characters, and 200 list rows. These are application bounds, not a claim that rendering very large interactive documents is inexpensive.

Soft deletion increments the revision and clears content/metadata from the active document row. Ordinary get/list calls exclude deleted documents. An explicit `includeDeleted` read returns tombstone metadata without the prior content.

## Rendering boundary

The artifact renderer returns a `WidgetArtifact` and, in the Desktop/CLI runtime, sends it to the existing local artifact sink. It does not create an externally hosted page or upload the document.

HTML processing:

- removes `<base>` tags;
- removes HTTP refresh meta tags;
- injects a strict content-security policy;
- sets `referrer=no-referrer`;
- denies network connections, frames, objects, forms, remote fonts, and default external resources;
- permits inline styles/scripts for local interactive artifacts and `data:`/`blob:` images/media only.

Other formats avoid raw-markup execution:

- Markdown uses a bounded basic renderer and HTML-escapes source text.
- Text is rendered inside an escaped `<pre>` block.
- SVG source is encoded into a `data:image/svg+xml;base64,...` image instead of inserted as active page markup.
- JSON must parse successfully and is pretty-printed into escaped text.

The CSP is defense in depth inside the existing local WebView artifact host; it is not an operating-system sandbox. Do not place secrets in an artifact that an authorized session should not be able to read.

## Lifecycle and coordination

Artifacts are durable across Agent restarts but remain inaccessible outside their original scope. Artifact mutation tools participate in the same session coordination resource used by other stateful workflow tools, preventing overlapping writes inside one scope. They still pass normal catalog, schema, Arm/Pause, permission, approval, ownership, cancellation, and audit gates.

Deleting or closing a Jarvis session does not currently purge its historical database rows automatically. They remain scope-inaccessible and can be removed by deleting the local artifact database while the Agent is stopped. Production retention/backup policy should be selected by the operator rather than inferred from soft deletion.
