# jarvis-mcp-server

See [root README](../README.md), [OAuth](../docs/OAUTH-MCP.md), [task gateway](../docs/AGENT-TASK-GATEWAY.md) and [OCI deployment](../docs/DEPLOYMENT-OCI.md). Current package: **1.0.62**, assembly/file: **1.0.62.0**. Source publication and local packaging do not deploy or restart the live service.

## MCP output contracts (1.0.62)

Every tool advertised by `tools/list` now includes an object-root `outputSchema`. Every tool-level result includes matching `structuredContent`, including validation, local permission and execution errors. HTTP/OAuth or JSON-RPC protocol failures remain protocol failures, not fabricated successful tool results.

- Ordinary installed tools expose `{ "text": "...", "isError": false }`. Text remains opaque; existing text blocks and image blocks stay in `content`. Image data is encoded through the SDK's `ImageContentBlock.FromBytes` factory and is not duplicated in structured output. Local widget HTML is not exposed.
- `agent_task_create`, `agent_task_plan`, `agent_task_get` and `agent_task_cancel` expose the existing `RemoteTaskReply` with a typed task snapshot or error. A task whose status is FAILED can still be read successfully; only an operation-level error sets the MCP error flag.
- `agent_task_artifacts` describes the task snapshot, artifact array, optional `nextOffset`, and error fields. Null wire members are omitted. Artifact exit codes can be negative; adaptive attempts are not incorrectly capped at the per-step retry limit.
- `agent_task_tools` exposes `{ "tools": [...] }` while its legacy text remains the original descriptor array. All task operations also support `{ "error": "..." }` for local validation/exception results.

Schemas are cached, self-contained, use JSON Schema 2020-12 vocabulary and are validated against real OAuth/WebSocket responses in `AgentTaskMcpTests`. Input schemas, tool names, owner/device authorization, local approval/Arm/Pause policy and agent wire contracts are unchanged. Object roots intentionally retain compatibility with the tested MCP 2025-11-25 protocol even though newer revisions support broader JSON shapes.

After deployment, refresh/review and publish the updated tool definitions in the client's app-management flow where supported. Some ChatGPT plans require recreating and republishing the app instead. Do not assume that pushing Git updates a connected app's cached tool definitions or removes its output-schema warning immediately.

Protocol reference: https://modelcontextprotocol.io/specification/2025-11-25/server/tools

ChatGPT app updates: https://help.openai.com/en/articles/12584461-developer-mode-and-mcp-apps-in-chatgpt

Executed build/test evidence: [BUILD-STATUS.md](../docs/BUILD-STATUS.md).
