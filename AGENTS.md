# Jarvis repository change and release workflow

Apply these rules to every patch in this repository, including UI, tests and documentation changes.

1. After each patch, bump the patch version (or the appropriate higher semantic version when required) in both `VERSION` and `Directory.Build.props` (`Version`, `AssemblyVersion`, `FileVersion`). Keep all three synchronized and update the top of `CHANGELOG.md`.
2. Run relevant tests and build/smoke verification for the changed component; report failures rather than claim success. Do not replace running Agent binaries while a session is active.
3. Review the diff, then commit the patch to `master` with the Conventional Commit format `type(scope): message` (for example, `fix(agent): restore Unicode labels`). Push to `origin/master`. If the branch diverged, reconcile safely; never force-push or silently discard others' changes. Report a failed push explicitly.
4. Deploy the MCP server to its configured OCI VM **only when the patch requires server changes** (server code, shared server-facing protocol, migrations, server configuration, or another server-affecting change). Use the repository's deployment scripts and perform validation/health checks after deployment. For Agent-only/UI-only patches, do not redeploy the MCP server. Never commit deployment credentials or production config.
5. In the final report include the version, commit ID/push status, validation results, and either the OCI deployment verification or why deployment was unnecessary. Do not claim deployment unless it actually succeeded.
