# MCP Output Schemas Implementation Plan

> **For agentic workers:** Use superpowers:executing-plans to implement this approved plan task-by-task. Steps use checkbox syntax for tracking.

**Goal:** Publish truthful output schemas and matching structured results for every advertised Jarvis MCP tool without breaking existing text/image consumers.

**Architecture:** Keep the change in the MCP transport layer. Dynamic installed tools expose a stable `{text, isError}` envelope; the six task tools expose the existing task reply shapes or a `{tools: [...]}` descriptor wrapper. Continue returning the exact legacy content blocks and error flag.

**Tech Stack:** C#/.NET 10, ModelContextProtocol.AspNetCore 2.2.0, System.Text.Json, JsonSchema.Net 9.4.0, xUnit and the existing OAuth/WebSocket integration fixtures.

**Spec:** The user approved all requirements in the preceding design on 2026-09-16; the accepted scope and constraints are recorded below.

## Global Constraints / Approved Design

- Add `outputSchema` in `tools/list` and `structuredContent` in `tools/call` for every dynamically listed tool and all six `agent_task_*` operations.
- Keep root schemas/results object-shaped for compatibility with the existing MCP 2025-11-25 integration flow.
- Preserve input schemas, names, OAuth owner/device isolation, local approvals, Arm/Pause, WebSocket contracts, legacy text and image blocks. Do not duplicate image base64 into structured results or publish local widget HTML.
- Model omitted nullable properties using the existing `WireJson.Options` (`WhenWritingNull`); schema-required fields must really be serialized.
- Task artifacts describe sequence, stepId, stage, toolId, attempt, success, output, truncated, optional exitCode/error, createdAt and optional nextOffset. Allow negative process exit codes and adaptive attempt counts.
- `agent_task_tools` retains its legacy JSON-array text; only structured content wraps descriptors in an object. Exceptions also return a schema-compatible error object.
- Update existing Markdown, bump package 1.0.61 to 1.0.62 and assembly/file to 1.0.62.0, verify, commit `feat(mcp): add output schemas and structured tool results`, and push master without force.
- Work in the clean master checkout explicitly authorized by the user. Use isolated build output if necessary; do not stop the running agent/native-messaging bridge or deploy/restart production during this patch.

## Task 1: Contract regression coverage (RED)

**Files:** Modify `tests/Jarvis.Server.Tests/AgentTaskMcpTests.cs`; create `tests/Jarvis.Server.Tests/AgentTaskMcpOutputTests.cs` as another part of the same test class, reusing its private OAuth/RPC helpers.

**Interfaces:** `AssertOutputMatches(JsonElement listed, string name, JsonElement result)` compiles the actual advertised output schema and validates actual HTTP structured content. A test-only `IAgentTool` supplies controlled text/error/image replies through the real agent connection.

- [x] Run the clean baseline: `dotnet test tests/Jarvis.Server.Tests/Jarvis.Server.Tests.csproj --no-restore --verbosity minimal`.
- [x] Assert schema existence and root `type` for every listed tool; extend the existing OAuth task lifecycle assertions to validate create/plan/get/artifacts/cancel/tools and both remote/local error paths.
- [x] Add ordinary reply, image preservation, invalid-input/local-denial, all six task validation errors, task descriptor wrapping, task-artifact pagination and omitted-field coverage. Validate malformed payload rejection, not merely schema presence.

```csharp
Assert.True(tool.TryGetProperty("outputSchema", out var schema));
Assert.Equal("object", schema.GetProperty("type").GetString());
Assert.True(result.TryGetProperty("structuredContent", out var structured));
Assert.True(SchemaGuard.Matches(SchemaGuard.Compile(schema), structured));
```

- [x] Run `dotnet test tests/Jarvis.Server.Tests/Jarvis.Server.Tests.csproj --no-restore --filter FullyQualifiedName~AgentTaskMcp --verbosity minimal`; confirm failures are missing outputSchema/structuredContent, not compilation or fixture failures.

## Task 2: MCP transport implementation (GREEN)

**Files:** Create `jarvis-mcp-server/src/Jarvis.McpServer/Transport/McpOutputSchemas.cs`; modify `McpGateway.cs` and `AgentTaskMcpTools.cs` in the same directory.

**Interfaces:** `McpOutputSchemas.ToolReply` is a cached JsonElement; `McpOutputSchemas.ForTask(string operation)` selects the cached task, artifact or tool-list schema. Each schema has property types/descriptions and success/error alternatives where appropriate.

- [x] Publish `OutputSchema = McpOutputSchemas.ToolReply` in the dynamic registry and `OutputSchema = McpOutputSchemas.ForTask(operation)` in task tool discovery.
- [x] Add the stable ordinary reply envelope for success and every existing `Error` return:

```csharp
StructuredContent = WireJson.Element(new { text = reply.Text, isError = reply.IsError })
```

- [x] Add `StructuredContent = WireJson.Element(reply)` for RemoteTaskReply and `StructuredContent = WireJson.Element(new { tools = value })` for tool descriptors. Add `{error: message}` structured content for task exceptions while preserving their legacy text.
- [x] Model actual snapshot/artifact/descriptor fields with required non-null members and optional omitted fields. Keep schemas local (no external schema references or new dependencies).
- [x] Run the targeted contract tests and the full Server suite. Investigate each mismatch before changing schemas or assertions.

## Task 3: Release evidence and publication

**Files:** Update `VERSION`, `Directory.Build.props`, root `README.md`, `CHANGELOG.md`, `jarvis-mcp-server/README.md`, `docs/AGENT-TASK-GATEWAY.md`, `docs/BUILD-STATUS.md`, this plan and `COMMIT_MESSAGE.txt`.

- [x] Bump 1.0.62 / 1.0.62.0 and document schema contracts, legacy compatibility, server deployment and client refresh requirements (source push does not update a live plugin).
- [x] Run `python scripts/Verify-CurrentVersion.py` and Release tests for Server, Core and Windows suites. Build `Jarvis.slnx` with isolated output if the active bridge locks normal output.
- [x] Publish the server into a versioned artifact directory and verify assembly metadata. Record only observed outcomes in BUILD-STATUS.md.
- [ ] Review full diff and whitespace, stage only this patch, commit with the approved Conventional Commit message, and push master without force.
- [ ] Verify local/remote HEAD agreement and a clean working tree. Report commit, versions, tests and the live-deployment/refresh boundary.

## Execution checkpoint before source publication

Implementation and packaging are verified. Targeted contract tests: 17/17; full Release: Core 150, Windows 29, Server 95, total 274 with no failed/skipped tests. Full solution build: zero errors and 39 warnings from unchanged vendor sources. Server publish and archive validation passed with assembly/file 1.0.62.0; see BUILD-STATUS.md for the SHA-256 and evidence paths.

The image round-trip regression uncovered the existing SDK image-data adapter defect; `ImageContentBlock.FromBytes` now preserves base64 correctly. The denial fixture was made sensitive because ordinary read-only tools intentionally do not prompt. No production permission policy was altered. The remaining publication checklist is checked against Git after committing this document; it is not predeclared complete inside the commit itself.