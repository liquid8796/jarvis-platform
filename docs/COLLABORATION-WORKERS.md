# Collaboration Workers

Version 1.0.93 adds a bounded collaboration-worker runtime to Jarvis Agent. It is intended for independent inspection, validation, aggregation, and other work that benefits from concurrency without pretending that Jarvis has started additional hidden language-model sessions.

## Public tools

| Public name | Canonical ID | Purpose |
|---|---|---|
| `worker_spawn` | `collaboration.worker_spawn` | Start one worker in the current explicit Jarvis session |
| `worker_send` | `collaboration.worker_send` | Send a bounded mailbox message to a running worker |
| `worker_wait` | `collaboration.worker_wait` | Read unread output or wait for the next worker state change |
| `worker_list` | `collaboration.worker_list` | List workers owned by the current session |
| `worker_stop` | `collaboration.worker_stop` | Cancel one worker without affecting its siblings |

An explicit `js_...` session is required. A worker ID is meaningful only to the authenticated owner, enrolled Agent device, and session that created it.

## Runtime model

A worker is a fresh Jint JavaScript engine running concurrently with other workers. The runtime exposes only:

```javascript
tools.<publicToolName>(arguments)
receive(timeoutMs)
text(value)
table(value)
sleep(milliseconds)
```

It does not inject Node.js, CLR interop, direct filesystem/process/environment/network APIs, browser credentials, or a model-provider client. The Agent does not spend API credits or call an LLM merely because a worker was started.

Example:

```json
{
  "label": "parallel test check",
  "code": "const result = await tools.developer__test({ project: 'tests/Jarvis.Core.Tests/Jarvis.Core.Tests.csproj' }); table(result); return { finished: true };",
  "yield_time_ms": 0
}
```

The call returns a `worker_id`. Poll or wait for output:

```json
{
  "worker_id": "worker_0123456789abcdef0123456789abcdef",
  "yield_time_ms": 5000,
  "max_output_tokens": 10000
}
```

`worker_wait` is cursor-like: each call returns only output, images, or a widget that has not already been delivered. A terminal status is `SUCCEEDED`, `FAILED`, or `CANCELLED`.

## Mailboxes

A worker can wait for later input:

```javascript
const message = await receive(300000);
if (!message) return { timedOut: true };
text(`received #${message.sequence}: ${message.message}`);
return { processed: true };
```

Send the input with `worker_send`. Mailboxes are bounded; a full mailbox fails explicitly rather than dropping an instruction. Messages are ordered per worker and include a sequence plus timestamp.

## Security and policy

Every `tools.x(...)` call re-enters the ordinary guarded invocation path. It therefore retains:

- live installed-tool and JSON-schema validation;
- Arm/Pause and local tool permission checks;
- interactive or standing approval rules;
- owner/device/session ownership;
- execution-resource scheduling and workspace/application restrictions;
- call cancellation, timeout, capability-lease, and audit behavior.

Workers cannot invoke another collaboration worker, Session Tool REPL, restricted script/program composite, session-control tool, or any other `ICompositeAgentTool`. This avoids recursive orchestration and nested scheduling deadlocks. If a nested call is rejected or fails, the complete worker fails even when JavaScript catches the Promise rejection.

## Bounds and lifecycle

Defaults are deliberately finite: eight active and 32 retained workers per session, 100,000 source characters, 128 queued messages, 64,000 characters per message, 256 nested calls, 256,000 output characters, eight images/32 MiB encoded image data, 250,000 statements, 32 MiB JavaScript memory, and 30 minutes elapsed time.

The Agent cancels workers on:

- `worker_stop`;
- local Pause/disarm;
- tool-permission or capability revocation;
- session stop or close;
- Agent shutdown/disposal;
- elapsed-time or request cancellation.

Stopping one worker does not stop siblings. Closing or stopping the owning Jarvis session removes the complete worker group. Completed workers are retained only for bounded unread output and are pruned after their result has been consumed.

## Operational notes

Workers do not automatically merge source-code changes or resolve resource conflicts. Use them primarily for independent reads, tests, checks, or explicitly partitioned work. Mutating nested tools still acquire the same resource claims and approvals as ordinary calls. A tool response lost after an uncertain side effect is never automatically replayed by the worker runtime.
