# Blender upstream parity — Jarvis 1.0.99

## Reviewed baseline

Reviewed on 2026-09-28 against `ahujasid/mcp-for-blender`, commit
`41a184322db3ccdcb2fdbc1f6994afe71bf9163c` (2026-09-27), package `2.1.1`,
addon protocol `11`. Source: https://github.com/ahujasid/mcp-for-blender/tree/41a184322db3ccdcb2fdbc1f6994afe71bf9163c

The previous configured runtime was `mcp-for-blender 2.0.0` with 31 tools.
The reviewed runtime has 36 downstream tools. Jarvis now has seven Blender
bridge routes (the original six plus `blender_get_capabilities`), bringing the
standard Agent catalog from 102 to 103. These are different layers, not 43 new
modeling tools. Discover through `blender_list_tools` and call exact schemas
through `blender_call_tool`.

The audit compared decorated tool definitions and changed functions, then read
server/transport, addon dispatcher, Poly Haven search/import/material paths,
Premium generation, addon manager, telemetry/consent/decorators, Safe Mode,
packaging and regression fixtures. Geometry/provider implementations come from
the reviewed package rather than a second hand-maintained C# copy.

## Capability comparison

| Group | Tools in reviewed runtime | Change from configured baseline |
|---|---|---|
| Core (10) | `get_addon_status`, `disable_telemetry`, `get_scene_info`, `get_object_info`, `get_viewport_screenshot`, `execute_blender_code`, `describe_node_type`, `bpy_api_lookup`, `export_scene`, `record_trajectory_feedback` | Retain functionality; improve error propagation, audit and privacy. |
| Poly Haven (6) | `get_polyhaven_status`, `get_polyhaven_categories`, `search_polyhaven_assets`, **`get_polyhaven_asset_preview`**, `download_polyhaven_asset`, `set_texture` | Add image preview; adopt ranked query, taxonomy/attribute/physical-size filters and revised model/material import. |
| Sketchfab (4) | `get_sketchfab_status`, `search_sketchfab_models`, `get_sketchfab_model_preview`, `download_sketchfab_model` | Retain existing provider and image-result forwarding. |
| Poly Pizza (3) | `get_polypizza_status`, `search_polypizza_models`, `download_polypizza_model` | Retain existing search/import. |
| Hyper3D (5) | `get_hyper3d_status`, `generate_hyper3d_model_via_text`, `generate_hyper3d_model_via_images`, `poll_rodin_job_status`, `import_generated_asset` | Adopt image-flow fixes and Premium-mode behavior; retain own-key operation. |
| Hunyuan3D (4) | `get_hunyuan3d_status`, `generate_hunyuan3d_model`, `poll_hunyuan_job_status`, `import_generated_asset_hunyuan` | Add `quality` support for protocol-11 Premium. Own-key mode ignores this option. |
| Tripo (4) | **`get_tripo_status`**, **`generate_tripo_model`**, **`poll_tripo_job_status`**, **`import_generated_asset_tripo`** | All four are new. Generation requires upstream Premium; discovery does not verify a subscription, credits or readiness. |

Five tool names are new; other improvements strengthen existing implementations
or schemas. Poly Haven imports native `.blend` assets, selects an asset/LOD0
collection where available, checks file-version compatibility and reports glTF
fallback. Material handling selects usable maps, connects normal/displacement
data, packs referenced images and records attribution/physical dimensions. These
require the matched addon, not just newer server-side tool names.

### Breaking schemas and workflow guidance

Poly Haven search now accepts `query`, `category`, `attributes`, `min_size_m` and
`limit`; the old `categories` parameter is not equivalent. Jarvis checks that
legacy argument against the live schema and rejects it before dispatch when no
longer supported. It never silently drops a filter or guesses a taxonomy mapping.
Rediscover schemas after upgrading. Model download behavior also changed; prefer
documented defaults instead of carrying over old format arguments.

Upstream moved general workflow guidance from a user-invoked prompt into server
instructions. Jarvis descriptions independently remind callers to inspect the
scene, look up RNA/node schemas instead of guessing, preview before importing,
and check provider readiness separately from catalog availability.

## Jarvis-specific improvements

`blender_get_capabilities({"probeAddon": false})` reports catalog/schema parity
without a tool-level addon probe. The trusted MCP process may perform its own
read-only startup handshake. `probeAddon: true` additionally calls
`get_addon_status` and reports compatibility separately. Credentials, checkboxes,
network access and credits remain untested. The audit neither enables providers
nor generates assets.

Known upstream string/JSON failures now propagate as errors, including Safe Mode
rejection, export/import failures and provider envelopes. Original text/images
are retained and uncertain mutations are never replayed. Disabled integrations
reported by status queries remain successful observations. The bounded
JSON-prefix parser handles status replies followed by optional guidance.
Unity's default error behavior is unchanged.

Extensions retain local approval, Arm/Pause, per-session client lifetime,
cross-session Blender serialization, cancellation and output limits. The new
diagnostic is a sensitive approved bridge operation, not an authorization bypass.

## Side-by-side installation

The immutable source/archive/addon hashes are in
`scripts/blender/upstream.lock.json`. The installer validates member paths,
symlinks, entry count and expanded size. It refuses existing unmanaged or edited
environments instead of overwriting local changes. Completed unchanged receipts
are idempotent.

```powershell
# Use a trusted Python 3.11+ executable.
.\scripts\Install-BlenderMcpRuntime.ps1 -PythonExe 'C:\path\to\python.exe'
$runtime = Join-Path $env:LOCALAPPDATA 'JarvisAgent\blender-runtimes\2.1.1-jarvis.1'
```

Optional `-ArchivePath` accepts a local archive with the exact reviewed hash.
Installation creates a new venv, runs `pip check`, compiles Python, copies the MIT
license and writes a receipt with installed hashes. `requirements-resolved.txt`
records resolved dependencies; this is not yet a fully hash-locked dependency
graph. Installation does not switch a shared profile, write Blender preferences,
start/stop Blender or restart/enroll an Agent.

### Privacy and Safe Mode

Jarvis keeps Safe Mode and telemetry opt-out environment variables enabled.
Hash-gated patches touch only reviewed `telemetry.py`, `consent_prompt.py` and
`server.py`. Opt-out avoids importing the omitted telemetry config, creating a
persistent telemetry UUID, starting a sender, querying consent or showing a
consent dialog. Transport logging omits command parameters and malformed response
bodies. Provider implementations and Safe Mode validation are not weakened.
Normal local tool input/output still exists; this does not promise empty logs.

The new upstream checks for extension installation and script-directory
persistence are retained. AST Safe Mode is defense in depth, not an OS sandbox
or a guarantee that modeling code cannot damage an open scene.

## Isolated verification

Use a separate config and unused loopback port, not a user's current scene:

```powershell
.\scripts\Configure-BlenderMcp.ps1 `
  -PythonExe "$runtime\Scripts\python.exe" -Port 19876 `
  -ConfigPath 'D:\path\to\test-mcp.json'
```

`Start-BlenderMcp.ps1` uses factory startup, disables file auto-execution, records
its PID and validates that this exact process owns the loopback listener. It now
waits for the child by default: an exited supervised launcher may have its child
tree cleaned up. It never escapes supervision or creates a scheduled task/service.

When testing under an exclusive workspace execution lease, run launch, smoke and
cleanup in ONE owning shell. Call the launcher with `-WaitForExit:$false` within
that shell, retain its returned PID, run the smoke before the shell exits, and
then stop only the process whose identity matches the launch receipt. Leaving a
waiting launcher in one shell can block another workspace-mutating tool; it is
not a detached persistent service.

The real smoke entry point is:

```powershell
dotnet run --project .\tests\BlenderMcp.Smoke -- `
  'D:\path\to\test-mcp.json' 'D:\path\to\smoke-output'
```

Append `--public-assets` to opt into free public Poly Haven metadata and thumbnail
requests in that disposable scene. It does not request paid model generation.

It checks all 36 tools, handshake, audit, RNA/node lookup, scene/object inspection,
Python execution, image forwarding, GLB/FBX export, denied imports/persistence and
semantic errors. Its object has a unique name and exact-name cleanup. Tripo is
queried only for status; no paid generation is made. Provider network tests are
separate. Never stop another Blender window to clean up a test.

## Activation and rollback

Installing this runtime and building Agent 1.0.99 do not hot-upgrade a running
Agent or shared scene. Coordinate with active users/sessions, save work and pair
the new runtime with its matching addon before production activation. Do not
silently replace a user's active scene with a new blank test instance. Review
custom Blender environment values before replacing its profile entry; the
configure script backs up the old profile and preserves other MCP server entries.

Deploy the Agent, reconnect and refresh discovery to expose the diagnostic.
Existing server dynamic-manifest routing already supports the new descriptor;
this adds no server endpoint and needs no server deployment merely to route it.
New capabilities remain subject to local permission/approval policy.

Rollback restores the previous backed-up profile and matching addon/runtime pair.
Both environments remain side by side. Never assume a listed tool works with an
old addon, and never retry uncertain imports/generations without inspecting the
scene or job status first.

## Evidence

Receipts/logs are under `artifacts/blender-parity-20260928/`. The upstream
mocked-network suite completed with **269 passed, 1 skipped**, with four upstream
datetime deprecation warnings. The fresh continuation in `resumed/` passed all
**639 maintained .NET tests**, **20 Python tests**, and a **22-operation** real
Blender smoke including public search/preview and GLB/FBX export. Published
Desktop/CLI outputs and assembly/file **1.0.99.0** were verified. The final
post-launcher receipt confirms the disposable Blender process and port are gone.
Full details and the explicit non-activated live state are recorded in
`docs/BUILD-STATUS.md`. Paid provider operation is not certified by discovery or
mocked tests.
