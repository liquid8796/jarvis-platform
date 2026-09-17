# MCP Tool Metadata Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Publish human-readable MCP tool titles and matching annotation titles for all Jarvis actions while preserving existing safety hints and behavior.

**Architecture:** Add one small transport-layer title formatter for dynamically published agent tools, and set both `Tool.Title` and `ToolAnnotations.Title` at the two MCP tool construction points (`McpGateway.PublicTool` and `AgentTaskMcpTools.List`). Verify behavior through the existing JSON-RPC `tools/list` integration harness so serialized metadata, not only in-memory objects, is covered.

**Tech Stack:** .NET 10, C# 14, ModelContextProtocol.AspNetCore 2.2.0, xUnit.

**Spec:** `docs/superpowers/specs/2026-09-17-mcp-tool-metadata.md`

## Global Constraints

- Preserve current `readOnlyHint`, `destructiveHint`, and `openWorldHint` values.
- Do not patch ChatGPT DOM/UI or fake the ChatGPT app-version label.
- Bump package version to `1.0.66` and assembly/file versions to `1.0.66.0`.
- Update existing `CHANGELOG.md`.
- Final commit must use `type(scope): message` and be pushed to `master`.

---

### Task 1: Lock MCP title behavior with a failing integration regression

**Files:**
- Modify: `tests/Jarvis.Server.Tests/AgentTaskMcpTests.cs`

**Interfaces:**
- Consumes: existing `RpcAsync` helper and OAuth-bound `tools/list` result.
- Produces: JSON assertions requiring top-level `title` and `annotations.title` for task, session, and dynamic agent tools while checking existing hint values remain unchanged.

- [ ] **Step 1: Write the failing test assertions**

Extend `OAuth_client_discovers_creates_and_reads_tasks_without_forging_device` immediately after `tools/list` to cover task/session metadata plus dynamic snake_case and PascalCase names (`process__read`, fixture-backed `workflow__AskUserQuestion`), then assert:

```csharp
AssertToolMetadata(listed, "agent_task_artifacts", "Read Task Artifacts", readOnly: true, destructive: false, openWorld: true);
AssertToolMetadata(listed, "session__open", "Session Open", readOnly: false, destructive: true, openWorld: true);
AssertToolMetadata(listed, "process__read", "Process Read", readOnly: true, destructive: false, openWorld: true);
AssertToolMetadata(listed, "workflow__AskUserQuestion", "Workflow Ask User Question", readOnly: true, destructive: false, openWorld: true);
```

Add this helper near the other test helpers:

```csharp
private static void AssertToolMetadata(JsonElement listed, string name, string title,
    bool readOnly, bool destructive, bool openWorld)
{
    var tool = listed.GetProperty("result").GetProperty("tools").EnumerateArray()
        .Single(t => t.GetProperty("name").GetString() == name);
    Assert.Equal(title, tool.GetProperty("title").GetString());
    var annotations = tool.GetProperty("annotations");
    Assert.Equal(title, annotations.GetProperty("title").GetString());
    Assert.Equal(readOnly, annotations.GetProperty("readOnlyHint").GetBoolean());
    Assert.Equal(destructive, annotations.GetProperty("destructiveHint").GetBoolean());
    Assert.Equal(openWorld, annotations.GetProperty("openWorldHint").GetBoolean());
}
```

- [ ] **Step 2: Run the focused test and verify RED**

Run:

```powershell
dotnet test tests/Jarvis.Server.Tests/Jarvis.Server.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~AgentTaskMcpTests.OAuth_client_discovers_creates_and_reads_tasks_without_forging_device"
```

Expected: FAIL because `title` / `annotations.title` are absent.

### Task 2: Publish Desktop-Commander-style title metadata

**Files:**
- Create: `jarvis-mcp-server/src/Jarvis.McpServer/Transport/McpToolMetadata.cs`
- Modify: `jarvis-mcp-server/src/Jarvis.McpServer/Transport/McpGateway.cs`
- Modify: `jarvis-mcp-server/src/Jarvis.McpServer/Transport/AgentTaskMcpTools.cs`

**Interfaces:**
- Consumes: public MCP tool names such as `filesystem__Read` and `session__open`.
- Produces: `McpToolMetadata.TitleFor(string name) -> string`, plus curated task-operation titles.

- [ ] **Step 1: Implement the smallest deterministic formatter**

Create:

```csharp
namespace Jarvis.McpServer.Transport;

internal static class McpToolMetadata
{
    public static string TitleFor(string name)
    {
        var normalized = name.Replace("__", "_", StringComparison.Ordinal).Replace('.', '_');
        return string.Join(' ', normalized.Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(SplitIdentifierWord));
    }

    private static string SplitIdentifierWord(string word)
    {
        var splitAcronym = System.Text.RegularExpressions.Regex.Replace(word, "([A-Z]+)([A-Z][a-z])", "$1 $2");
        var splitCase = System.Text.RegularExpressions.Regex.Replace(splitAcronym, "([a-z0-9])([A-Z])", "$1 $2");
        return splitCase.Length == 0 ? splitCase : char.ToUpperInvariant(splitCase[0]) + splitCase[1..];
    }
}
```

- [ ] **Step 2: Apply title metadata to dynamic tools**

In `McpGateway.PublicTool`, calculate `var title = McpToolMetadata.TitleFor(name);`, then set:

```csharp
Title = title,
Annotations = new ToolAnnotations
{
    Title = title,
    ReadOnlyHint = descriptor.ReadOnly,
    DestructiveHint = !descriptor.ReadOnly,
    OpenWorldHint = true
}
```

Do not alter schemas, descriptions, routing, or existing hint values.

- [ ] **Step 3: Apply curated titles to task tools**

In `AgentTaskMcpTools.List`, map operations to:

```csharp
"create" => "Create Agent Task",
"plan" => "Plan Agent Task",
"get" => "Get Agent Task",
"artifacts" => "Read Task Artifacts",
"cancel" => "Cancel Agent Task",
_ => "List Agent Task Tools"
```

Set both `Tool.Title` and `ToolAnnotations.Title` to that value while leaving existing hint expressions unchanged.

- [ ] **Step 4: Run focused test and verify GREEN**

Run the same filtered `dotnet test` command from Task 1.

Expected: PASS.

- [ ] **Step 5: Run the complete server test suite**

Run:

```powershell
dotnet test tests/Jarvis.Server.Tests/Jarvis.Server.Tests.csproj -c Release --no-restore
```

Expected: all tests pass. Baseline observation on 2026-09-17: the unfiltered suite stalls after test discovery even before this patch; if that persists, record it as an existing test-harness limitation and verify this change with the complete `AgentTaskMcpTests` group plus a Release build of `Jarvis.McpServer`.

### Task 3: Document and version the patch

**Files:**
- Modify: `CHANGELOG.md`
- Modify: `Directory.Build.props`
- Modify: `VERSION`

**Interfaces:**
- Consumes: completed metadata behavior from Task 2.
- Produces: release metadata `1.0.66` / `1.0.66.0` and changelog entry.

- [ ] **Step 1: Add the 1.0.66 changelog entry**

Insert above 1.0.65:

```markdown
## 1.0.66 - 2026-09-17

- Publish human-readable MCP `title` plus matching `annotations.title` for every dynamic Jarvis action and all six `agent_task_*` tools, following the metadata pattern used by mature MCP servers such as Desktop Commander.
- Preserve existing read-only/destructive/open-world hints, schemas, permissions, routing and execution behavior; ChatGPT remains responsible for the final compact/collapse presentation.
- Add JSON-RPC `tools/list` regression coverage for task, session, dynamic process actions and PascalCase tool names.
- Bump package/assembly/file versions to 1.0.66 / 1.0.66.0. Production deployment and ChatGPT tool-definition refresh remain separate steps.
```

- [ ] **Step 2: Bump version files**

Set `Directory.Build.props` to `Version=1.0.66`, `AssemblyVersion=1.0.66.0`, `FileVersion=1.0.66.0`, and set `VERSION` to `1.0.66`.

- [ ] **Step 3: Verify versions and tests**

Run:

```powershell
dotnet test tests/Jarvis.Server.Tests/Jarvis.Server.Tests.csproj -c Release --no-restore
```

Then verify no stale 1.0.65 remains in `Directory.Build.props` or `VERSION`.

### Task 4: Review, commit, and push master

**Files:**
- Review all modified files.

**Interfaces:**
- Consumes: passing test suite and clean intended diff.
- Produces: one master commit published to origin.

- [ ] **Step 1: Inspect status and diff**

Run `git status` and review the full diff. Confirm no unrelated files are included.

- [ ] **Step 2: Commit**

```bash
git add CHANGELOG.md Directory.Build.props VERSION docs/superpowers/specs/2026-09-17-mcp-tool-metadata.md docs/superpowers/plans/2026-09-17-mcp-tool-metadata.md tests/Jarvis.Server.Tests/AgentTaskMcpTests.cs jarvis-mcp-server/src/Jarvis.McpServer/Transport/McpToolMetadata.cs jarvis-mcp-server/src/Jarvis.McpServer/Transport/McpGateway.cs jarvis-mcp-server/src/Jarvis.McpServer/Transport/AgentTaskMcpTools.cs
git commit -m "feat(mcp): publish human-readable tool metadata"
```

- [ ] **Step 3: Verify commit and push**

Run the final verification suite once more against the committed tree, then:

```bash
git push origin master
```

Confirm local `master` and `origin/master` point at the new commit.
