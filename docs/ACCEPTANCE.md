# Required acceptance before live use

This is a checklist, **not a record of passed tests**. C# tests and Windows runtime checks have not been executed in the authoring environment.

1. Restore and compile all solutions on .NET10; run Core and Server tests, publish both RIDs needed, collect TRX and compiler output. Exercise the existing baseline tool tests as well. Check transitive package vulnerabilities/licenses; create reproducible lockfiles after successful restore.
2. Start local server with a temporary DB/bootstrap. Check login/register/CSRF/lockout, pending approval, admin CRUD, last-admin restriction, concurrent revisions, own-device-only routes and secret-free API listing/logs. Test malformed/null/oversized bodies, schema errors, external references, and request throttling.
3. Run WPF on Windows10 and11 x64. Test tray restore/pause/exit, same-user single-instance, hotkey conflict, manual indefinite arming, Pause/Disconnect/Exit disarming, reconnect without action replay or loss of arm choice, deny/approve dialogs, DPI125/150/200%, multi-monitor coordinates, elevated-window refusal and clipboard. Verify Pause during a long shell command and during a delayed approval.
4. Install the derived browser integration under a standard account. Verify it does not change existing Jarvis Code host registration. Check extension origin, site approvals, tabs, navigate/find/read/JS/forms/uploads/network/console/screenshots/GIF/batch. Deny unsupported enterprise policy rather than bypassing it.
5. Confirm expected manifest counts (52 desktop /49 CLI) and each schema. Test original computer_batch mouse move/click/right/double/drag/scroll, keyboard, screenshot, app grants, displays and teach overlay on GUI. GUI must never operate its own local approval windows through computer tools.
6. Test visualize offline, malformed HTML, iframe isolation, denied network/download/host messages, WebView2 missing, cancellation, multiple windows and CLI HTML output. Do not treat CDN-dependent widgets as supported.
7. Test process__start/read/cancel, stdout+stderr, non-zero exit, log truncation/cursor, queue limits, timeout, whole-process-tree stop, agent exit and process-start/pause races. Confirm effects already performed are reported as possibly completed rather than retried.
8. Test real OAuth discovery, DCR allowed/denied redirects, PKCE S256/invalid/plain/missing verifier, token resource/audience, expired/reused/refresh/revoked grants, consent CSRF, disabled/deleted user/device and cross-tenant tool calls. Test complete ChatGPT initialize → tools/list → tools/call with an isolated test workspace, not production secrets.
9. Break WSS during execution, rotate token, duplicate invocation, replace connection for same device and cancel HTTP request. Validate completion-unknown semantics, no replay, clock drift rejection, heartbeat/reconnect and local lease reset. Test registry disablement while client has stale tools.
10. Rehearse install/health rollback/backups on a staging VM with unrelated sample services. Confirm existing listeners/units/proxy hashes remain unchanged. Check TLS chain, resource pressure, systemd lingering and native dependencies before production deployment.

No performance benchmark is included. Measure actual end-to-end latency using LAN/OCI RTT, image payloads, concurrency and tool duration rather than equating transport selection with a speed guarantee.

## Multi-directory acceptance - 1.0.23

Use Add folders and select multiple folders in one native dialog with Ctrl/Shift. Confirm the primary is preserved while additional folders are added without duplicates. Use Make primary and Remove on selected additional rows. Reconnect to apply folder changes and verify the saved list survives an application restart. Old profiles without additionalDirectories must still load. Use absolute paths for files in secondary folders. Relative paths use the primary folder. Existing guards still reject unselected file roots.

For process__start, set workingDirectory to an existing directory and verify the job starts there. Approve the action normally; the job remains owned and cancellable. Inspect the EXE/window/sidebar/tray icons. Full-permission configuration and removal of the workspace file guard are not implemented in this release and cannot pass acceptance yet.
