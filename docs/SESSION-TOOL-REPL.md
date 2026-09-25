# Session Tool REPL

Version 1.0.91 adds a persistent JavaScript orchestration runtime to each authenticated Jarvis application session.

## Public tools

| Public name | Canonical ID | Purpose |
|---|---|---|
| `exec` | `tool_repl.exec` | Start a JavaScript cell and wait briefly for output or completion. |
| `wait` | `tool_repl.wait` | Read unread output/images, wait for a cell change, or terminate it. |
| `sleep` | `tool_repl.sleep` | Perform a bounded session-aware delay without holding a normal execution slot. |
| `repl_reset` | `tool_repl.reset` | Cancel active cells and discard cell history and persistent JavaScript state for this session only. |

All four tools require an explicit `session__open` handle. REPL state is keyed by authenticated owner, enrolled device and Jarvis session ID. It is never shared between chats or sessionless calls.

## Runtime model

`exec` runs code inside a persistent Jint engine. Successful cells preserve values stored on `globalThis`:

```javascript
globalThis.builds = (globalThis.builds ?? 0) + 1;
text({ builds: globalThis.builds });
return globalThis.builds;
```

The current Agent catalog is projected before every cell as a `tools` object. Public names become JavaScript properties:

```javascript
const [status, tests] = await Promise.all([
  tools.git__git_status({ workingDirectory: "D:\\Project\\tools\\Jarvis\\jarvis-platform" }),
  tools.developer__test({ project: "tests/Jarvis.Core.Tests/Jarvis.Core.Tests.csproj" })
]);

table({ status, tests });
```

Only unique, non-composite, non-session-control tools are projected. The REPL tools, `tool_script.run`, `tool_program.run`, and session/workspace lifecycle tools cannot recursively invoke themselves through `tools`.

## Output and long-running cells

`text(value)` emits plain text. `table(value)` emits formatted JSON. Images/widgets produced by nested tools are carried in the cell result. `image(value)` is available as a parity marker while nested image payloads are forwarded automatically.

`exec` waits up to `yield_time_ms`. A cell that is still queued or running returns a `cell_id`. Continue with:

```json
{
  "cell_id": "cell_0123456789abcdef0123456789abcdef",
  "yield_time_ms": 30000,
  "max_output_tokens": 10000
}
```

`wait` returns only output and images not consumed by an earlier `exec`/`wait` response. `terminate: true` cancels that cell. Cell states are `QUEUED`, `RUNNING`, `SUCCEEDED`, `FAILED`, and `CANCELLED`.

## Security contract

The JavaScript engine has no direct Node, CLR, filesystem, process, environment, browser or network APIs. Its only effectful bridge is the generated `tools` object. Every nested call returns to `AgentConnection.InvokeInstalledToolAsync`, so it retains:

- local schema validation;
- Arm/Pause checks;
- exact tool permission and approval checks;
- capability leases and Full permission rules;
- workspace and additional-directory containment;
- application grants and Windows integrity restrictions;
- fair execution slots and resource locks;
- owner/device/session isolation;
- cancellation, deadline and audit behavior.

A failed or denied nested request fails the cell even when JavaScript catches the promise rejection. This prevents a script from masking a denied mutation as success. A failed, cancelled or timed-out cell discards its Jint engine because pending promises or partially mutated JavaScript state cannot be proven safe to reuse.

## Bounds and lifecycle

Defaults are intentionally bounded: 100,000 code characters, 64 sessions, 64 cells per session, 64 nested calls per cell, 256,000 text-output characters, 8 images, 32 MiB of encoded image data, 100,000 JavaScript statements, 32 MiB engine memory and a 10-minute cell deadline.

Pause, permission revocation, session stop/close, Agent disposal and `repl_reset` cancel owned REPL work. Reset affects only the current authenticated Jarvis session. Successful state is in-memory and is not restored after Agent restart.
