# deploy-localenv

Build and deploy the plugin to the local Jellyfin dev environment, then verify it loaded.

## When to invoke

User asks to deploy, install, test in localenv, or "try it out".

## Steps

1. Run the deploy script (handles build + copy + meta.json reset + restart + smoke test):
   ```bash
   ./scripts/deploy-dev.sh
   ```
2. Report the smoke-test output. If the ping response shows `status="ok"`, deployment succeeded.
3. If the script fails, check `docker logs jellyfin 2>&1 | tail -30` for the error.

## Common failures

| Symptom | Cause | Fix |
|---------|-------|-----|
| `Skipping disabled plugin` | Jellyfin cached `status=NotSupported` in meta.json from a previous bad load | Script resets meta.json; re-run script |
| `Failed to load assembly … incompatible version` | DLL targets wrong Jellyfin ABI | Check `targetAbi` in meta.json matches running Jellyfin version (`docker logs jellyfin \| grep "Jellyfin version"`) |
| `docker: No such container: jellyfin` | localenv not running | `cd ../subfin/localenv && docker compose up -d jellyfin` |
| Build error `CS0234 … Entities` | Jellyfin API namespace changed (see CLAUDE.md §10.11.6) | Fix usings per CLAUDE.md |
