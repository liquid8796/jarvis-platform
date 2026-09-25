# Thread, automation and asynchronous input

Version 1.0.95 extends Jarvis Agent's append-only SQLite thread runtime with three related capabilities:

1. bounded thread discovery and cursor waits;
2. deterministic local schedules that enqueue future thread work;
3. durable structured questions that can be answered later.

These capabilities are local Agent infrastructure. They do not themselves create a language-model turn, spend provider credits, call arbitrary tools in the background, or promise execution while the Agent machine is offline.

## Public tools

### Thread discovery and wait

| Public name | Canonical ID | Purpose |
|---|---|---|
| `thread__list` | `thread.list` | List bounded durable thread headers in a selected project |
| `thread__wait` | `thread.wait` | Wait for events after an event cursor |

`thread__wait` is a read-only composite tool. It polls the durable `thread_events` journal and returns:

```json
{
  "threadId": "0123456789abcdef0123456789abcdef",
  "events": [],
  "nextCursor": 42,
  "hasMore": false,
  "timedOut": true
}
```

The cursor is the last observed event ID. Reusing it is safe because thread history is append-only. Results are bounded to 200 events. Waiting does not reserve the normal tool execution slot, but cancellation and request deadlines still apply.

### Automation

| Public name | Canonical ID | Purpose |
|---|---|---|
| `automation_create` | `automation.create` | Create a one-time or interval schedule |
| `automation_update` | `automation.update` | Edit, pause, resume, or convert a schedule using its revision |
| `automation_get` | `automation.get` | Read one owned schedule |
| `automation_list` | `automation.list` | List bounded owned schedules |
| `automation_cancel` | `automation.cancel` | Cancel at an expected revision |
| `automation_run_due` | `automation.run_due` | Explicitly materialize due owned schedules now |

Example one-time schedule:

```json
{
  "threadId": "0123456789abcdef0123456789abcdef",
  "name": "verify release",
  "payloadJson": "{\"task\":\"verify\",\"release\":\"1.0.95\"}",
  "nextDueUtc": "2026-09-27T09:00:00+07:00"
}
```

Add `intervalSeconds` for a recurring schedule. Production intervals are bounded to 60 seconds through one year. Update/delete operations require `expectedRevision`; stale writers receive `AUTOMATION_REVISION_CONFLICT` instead of overwriting newer state.

When a schedule becomes due, Jarvis performs one deterministic transaction:

1. append a `thread_queue` record whose payload identifies the automation, scheduled time, actual fire time, skipped occurrences, and user payload;
2. append an `automation.fired` thread event;
3. increment revision and run count;
4. mark a one-time schedule `completed`, or advance a recurring schedule to its first due time after now.

The scheduler deliberately does **not** run a model or nested Agent tool. The queued record is ordinary durable work for a later explicit processing turn. This avoids surprise side effects and provider charges.

If the Agent was offline or paused past several recurring occurrences, catch-up is coalesced into one queue record. `skippedOccurrences` reports how many additional intervals elapsed, and `nextDueAt` moves into the future. The runtime never emits an unbounded burst.

The Desktop/CLI Agent hosts a five-second local pump while its process is alive. On startup, the first pump tick catches up due rows. `automation_run_due` allows a model/operator to request a bounded immediate pass for the current session. This is local process scheduling, not cloud background execution.

### Asynchronous input

| Public name | Canonical ID | Purpose |
|---|---|---|
| `async_input_request` | `async_input.request` | Create a durable structured question |
| `async_input_respond` | `async_input.respond` | Submit a schema-validated answer at an expected revision |
| `async_input_wait` | `async_input.wait` | Wait for answered/cancelled/expired state |
| `async_input_list` | `async_input.list` | List bounded owned requests |
| `async_input_cancel` | `async_input.cancel` | Cancel a pending request at an expected revision |

Example request:

```json
{
  "threadId": "0123456789abcdef0123456789abcdef",
  "prompt": "Approve the release?",
  "schemaJson": "{\"type\":\"object\",\"properties\":{\"approved\":{\"type\":\"boolean\"}},\"required\":[\"approved\"],\"additionalProperties\":false}",
  "expiresInSeconds": 86400
}
```

Later response:

```json
{
  "inputId": "input_0123456789abcdef0123456789abcdef",
  "expectedRevision": 1,
  "answerJson": "{\"approved\":true}"
}
```

Jarvis compiles the bounded schema with the existing local `SchemaGuard`, which rejects external references and unsupported recursion. A mismatched answer returns `ASYNC_INPUT_SCHEMA_MISMATCH` and leaves the request pending. Successful answers, cancellation, and expiration increment the revision and append thread events. `async_input_wait` is a read-only composite wait and returns the durable record when it reaches a terminal state or the wait window expires.

This runtime supplies the durable protocol and tools; it does not automatically create a special operating-system dialog. A connected client, artifact, or later workflow may choose how to present pending requests.

## Ownership and workspace boundary

Threads remain project-scoped records in `thread-runtime.db`. Automation and async-input rows additionally store:

- authenticated owner ID;
- enrolled Agent device ID;
- explicit Jarvis session ID;
- referenced thread ID.

Every tool call verifies all four. An opaque automation/input ID copied to another session or device is not sufficient authority. The referenced thread's project must be inside the current session's selected primary or additional workspace roots. `thread__list` also resolves its project through the same workspace containment rules.

All tools require an explicit `js_...` session. On destructive session close, active/paused automations and pending inputs owned by that session are marked `cancelled`, revisions advance, and cancellation events are journaled. Ordinary stop-work remains resumable and does not destroy durable records.

## Storage and limits

The new tables share the existing `thread-runtime.db`, SQLite WAL mode, foreign keys, and busy timeout:

- `thread_automations`
- `thread_async_inputs`

Default bounds include 128 active/paused automations per session, 128 pending input requests, 200 list rows, 100,000-character automation payloads, 64,000-character schemas, 100,000-character answers, seven-day maximum request expiry, and 200 firings per pump/call. Tool schemas apply equivalent input limits before execution.

Thread events remain the audit/recovery timeline. Queue insertion, schedule transitions, answer transitions, expiration, and session-close cancellation are committed transactionally with the associated journal event whenever they modify state.

## Failure and retry model

- Create/update/cancel/respond operations use optimistic revisions; read again after a conflict.
- Firing is transactional and revision-checked. A failed transaction creates neither a queue record nor a successful state advance.
- One-time completed and cancelled automations are terminal.
- Answered, cancelled, and expired input requests are terminal.
- A wait timeout is not failure and does not change durable state.
- Automatic pump errors are contained and retried on the next tick; no model/tool action is replayed because the pump never performs one.
