# jarvis-mcp-server

See [root README](../README.md), [OAuth](../docs/OAUTH-MCP.md), [task gateway](../docs/AGENT-TASK-GATEWAY.md) and [OCI deployment](../docs/DEPLOYMENT-OCI.md). Current package: **1.0.88**, assembly/file: **1.0.88.0**. Source publication and local packaging do not deploy or restart the live service.

## Live dynamic tool surface (1.0.88)

The selected OAuth-bound device's live capability manifest is the runtime source of truth. Missing policy rows mean **Auto**, so newly added Agent tools become discoverable without an Import/Enable pass. **Published** retains an explicit public name/description; **Hidden** blocks direct discovery, live search, generic invocation and task-plan use.

Initialize-based MCP clients run statefully, advertise `tools.listChanged=true`, and receive `notifications/tools/list_changed` after Agent catalog or admin-policy changes. Every client also gets permanent `jarvis__tool_search` and `jarvis__tool_call` tools. They resolve canonical IDs and schemas from the selected device at call time, so a chat with a stale direct tool list can still discover and invoke a tool added after the chat started.

The generic fallback does not grant authority. Calls still pass device routing, current Agent catalog generation/digest checks, Arm/Pause, schema validation, local permissions/approval, session ownership and execution-resource scheduling. Database schema v2 is additive: it keeps the legacy `Enabled` column as a compatibility mirror and adds `PublicationMode`; no database recreation is performed.

## MCP output contracts (1.0.62)

Every tool advertised by `tools/list` now includes an object-root `outputSchema`. Every tool-level result includes matching `structuredContent`, including validation, local permission and execution errors. HTTP/OAuth or JSON-RPC protocol failures remain protocol failures, not fabricated successful tool results.

- Ordinary installed tools and `jarvis__tool_call` expose `{ "text": "...", "isError": false }`. `jarvis__tool_search` exposes a typed `{ "tools": [...] }` result. Text remains opaque; existing text blocks and image blocks stay in `content`. Image data is encoded through the SDK's `ImageContentBlock.FromBytes` factory and is not duplicated in structured output. Local widget HTML is not exposed.
- `agent_task_create`, `agent_task_plan`, `agent_task_get` and `agent_task_cancel` expose the existing `RemoteTaskReply` with a typed task snapshot or error. A task whose status is FAILED can still be read successfully; only an operation-level error sets the MCP error flag.
- `agent_task_artifacts` describes the task snapshot, artifact array, optional `nextOffset`, and error fields. Null wire members are omitted. Artifact exit codes can be negative; adaptive attempts are not incorrectly capped at the per-step retry limit.
- `agent_task_tools` exposes `{ "tools": [...] }` while its legacy text remains the original descriptor array. All task operations also support `{ "error": "..." }` for local validation/exception results.

Schemas are cached, self-contained, use JSON Schema 2020-12 vocabulary and are validated against real OAuth/WebSocket responses in `AgentTaskMcpTests`. Input schemas, tool names, owner/device authorization, local approval/Arm/Pause policy and agent wire contracts are unchanged. Object roots intentionally retain compatibility with the tested MCP 2025-11-25 protocol even though newer revisions support broader JSON shapes.

After deployment, new Agent capabilities default to Auto and do not require a manual Import/Enable pass. Stateful MCP clients are asked to refresh direct definitions with `tools/list_changed`; clients that do not refresh still retain the permanent live search/call fallback. A source push alone does not upgrade a running server or Agent.

Protocol reference: https://modelcontextprotocol.io/specification/2025-11-25/server/tools

ChatGPT app updates: https://help.openai.com/en/articles/12584461-developer-mode-and-mcp-apps-in-chatgpt

Executed build/test evidence: [BUILD-STATUS.md](../docs/BUILD-STATUS.md).
