# MCP Tool Metadata Spec

## Goal

Make Jarvis MCP advertise human-readable tool titles and standard MCP annotations in the same native metadata path used by mature MCP servers such as Desktop Commander, so ChatGPT can choose its compact/collapsible action rendering without any browser DOM injection.

## Requirements

- Every tool returned by `tools/list` must include a non-empty top-level `title`.
- Every tool returned by `tools/list` must include `annotations.title`, matching the top-level `title` for compatibility across MCP clients.
- Existing `readOnlyHint`, `destructiveHint`, and `openWorldHint` semantics must remain unchanged in this patch.
- Dynamic Jarvis agent tools must receive a deterministic human-readable title derived from their public MCP name, splitting namespace separators, underscores and Pascal/camel-case boundaries while preserving acronym runs. Examples: `filesystem__Read` -> `Filesystem Read`, `browser__read_page` -> `Browser Read Page`, `workflow__AskUserQuestion` -> `Workflow Ask User Question`, `session__open` -> `Session Open`.
- The six `agent_task_*` tools must use curated titles: `Create Agent Task`, `Plan Agent Task`, `Get Agent Task`, `Read Task Artifacts`, `Cancel Agent Task`, and `List Agent Task Tools`.
- Add regression coverage at the MCP JSON-RPC boundary, not only unit coverage of a helper.
- Update the existing changelog to document the metadata behavior and explicitly note that ChatGPT owns the final collapse/default-open presentation.
- Bump package version to `1.0.66` and assembly/file versions to `1.0.66.0`, including the root `VERSION` file.
- Commit and push to `master` with Conventional Commit format `type(scope): message`.

## Non-goals

- No ChatGPT DOM injection or browser-extension UI patch.
- No fake rewrite of ChatGPT's `Version name: dev mode` field.
- No change to tool permission policy, local approval gates, schemas, routing, session ownership, or tool execution behavior.
- No inference-based changes to `openWorldHint`, `destructiveHint`, or `readOnlyHint` beyond their current values.
