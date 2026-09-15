# SDK subprocess verification

This check uses the real Python Claude Agent SDK against a built Jarvis CLI and a loopback HTTP fixture. It makes no external model request and uses only synthetic prompts, dummy keys, an isolated profile and a temporary workspace.

The validated SDK version is `claude-agent-sdk==0.2.152`. Use a Python environment with that package installed, then run:

```powershell
python scripts/verify-sdk-subprocess.py --cli artifacts/parity-verification/build/bin/JarvisCode.Cli/debug/jarvis.exe --artifacts artifacts/sdk-subprocess
```

The CLI must be built first; use the executable path in the verification runner's summary. The script tests two SDK turns, structured output, partial events, a real SDK-hosted MCP echo tool, a denied call, rewritten input, session permission updates, and exact single model-switch hook callbacks. It verifies that the MCP instructions and explicitly selected tool schema reach the model request. It also checks context-usage totals, rewind by replayed user UUID, unknown-task rejection, and detached session start/list/attach/stop/remove. Exactly seven HTTP calls reach its local fixture. The second SDK turn explicitly selects Auto to exercise the stored allow rule; Manual/default continues asking before mutating calls.

The evidence JSON records the SDK version and executable/assembly hashes. A successful run exits zero. The script cleans up its own background host and profile on failure. This rewind case has no changed files; real file restoration is covered by the CLI checkpoint tests.

This is process-boundary coverage for those flows. It is not proof that every current SDK method is implemented. Jarvis's product version remains its own; the SDK's minimum-Claude-version warning is retained rather than hidden or replaced with the installed Claude version.
