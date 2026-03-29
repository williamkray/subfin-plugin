# deploy-localenv

Build and deploy the plugin to the local Jellyfin dev environment, then verify it loaded.

## When to invoke

- User asks to deploy, install, test in localenv, or "try it out"
- **After any code change** — always deploy + sweep before considering a fix complete. Do not wait for the user to ask.

## Steps

1. Run the deploy script (handles build + copy + meta.json reset + restart + smoke test):
   ```bash
   ./scripts/deploy-dev.sh
   ```
2. Report the smoke-test output. If the ping response shows `status="ok"`, deployment succeeded.
3. Run the endpoint sweep to catch regressions:
   ```bash
   ./scripts/endpoint-sweep.sh
   ```
   Report the `N passed, M failed` summary. The `getPlaylist` failure is a known pre-existing test-data gap (no playlists exist for the test user) — not a regression.
4. If the script fails, check `docker logs jellyfin 2>&1 | tail -30` for the error.

## Post-fix checklist

After any code change + deploy + sweep:
- [ ] Sweep shows no new failures (compare against pre-change baseline)
- [ ] Ping response shows expected `serverVersion` (it's dynamic — reflects loaded plugin version)
- [ ] Update `current-plan.md` to reflect what was fixed or partially addressed
- [ ] Update the relevant `issues/*.md` file with what was done and what remains

## Publishing for external prod testing

**Always bump the version before publishing** — without a version change, there's no way to confirm which code is running on prod:

```bash
./scripts/bump-version.sh 0.2.1.0  # four-part version required (validated by script)
./scripts/deploy-dev.sh             # verify serverVersion in smoke test output
./scripts/endpoint-sweep.sh         # confirm no regressions
./scripts/publish.sh                # then publish
```

On prod after install+restart, confirm with:
```bash
curl "http://your-jellyfin/rest/ping.view?u=...&p=...&v=1.16.1&c=test"
# Must show: serverVersion="0.2.1.0" (or whatever was bumped to)
```

If `serverVersion` still shows the old value, the old plugin is still loaded — do not proceed to test behavior.

## Web UI smoke test

After changes to `index.html`, `share.html`, or `config.html`, verify the pages load (not just the REST endpoint):

```bash
# index.html — must return HTML, not a redirect or error
curl -s -o /dev/null -w "%{http_code}" http://localhost:8096/subfin/

# share page (requires a valid share uid + secret from the DB)
# config page is served by Jellyfin at:
#   http://localhost:8096/web/index.html#!/configurationpage?name=SubfinAdmin
```

If `index.html` shows a broken layout or scripts don't run, check that the HTML still has the correct `localStorage` token read pattern — see CLAUDE.md §Web UI auth pattern.

If `config.html` shows static "Loading…" text that never changes, the inline script may not be executing. See CLAUDE.md §Jellyfin plugin config page patterns for diagnosis steps.

## Artist index validation

After any change to artist-related code, verify `getArtists` → `getArtist` roundtrip:

```bash
P=$(node scripts/get-creds.js | grep 'u=user1' | head -1 | sed 's/.*p=\([^ ]*\).*/\1/')
BASE="http://localhost:8096/rest"; AUTH="u=user1&p=$P&v=1.16.1&c=test&f=json"

ARTIST=$(curl -s "$BASE/getArtists.view?$AUTH" | python3 -c "
import json,sys; d=json.load(sys.stdin)['subsonic-response']
a=d['artists']['index'][0]['artist'][0]; print(a['id'], a['name'], a['albumCount'])")
echo "getArtists: $ARTIST"

ID=$(echo $ARTIST | awk '{print $1}')
curl -s "$BASE/getArtist.view?$AUTH&id=ar-$ID" | python3 -c "
import json,sys; d=json.load(sys.stdin)['subsonic-response']['artist']
print('getArtist: ', d['name'], d['albumCount'])"
```

Name must match and albumCount must be > 0. The deploy script now clears `derived_cache` automatically — no manual cache clearing needed.

## Proxying to Jellyfin internal endpoints

When adding code that proxies to a Jellyfin internal endpoint (`/Audio/*`, `/Items/*`, etc.):

1. **Probe the endpoint first** — use `curl` directly against localenv with the API key to verify behavior before implementing:
   ```bash
   APIKEY=$(sqlite3 ../subfin/localenv/jellyfin-data/config/data/SubfinPlugin/subsonic.db \
     "SELECT value_json FROM derived_cache WHERE cache_key='plugin-api-key'")
   curl -v -H "Authorization: MediaBrowser Token=\"$APIKEY\"" \
     "http://localhost:8096/Audio/{id}/stream.webm?audioCodec=opus&static=false&audioBitRate=64000&userId=..."
   ```
2. **Check redirects** — use `--max-redirs 0` to detect if Jellyfin redirects the URL elsewhere
3. **Add diagnostic logging from the start** — log the proxy URL and response status so prod issues are diagnosable without another deploy cycle:
   ```csharp
   _logger.LogInformation("[Subfin] stream proxy → {Url}", url);
   ```

**Known Jellyfin API pitfall:** `/Audio/{id}/universal?container=webm&audioCodec=opus` silently ignores `transcodingContainer` and builds an invalid `mp3+opus` transcoding profile. Use `/Audio/{id}/stream.{container}` (container in path) instead — it takes the output format literally and produces a valid ffmpeg command.

## Common failures

| Symptom | Cause | Fix |
|---------|-------|-----|
| `Skipping disabled plugin` | Jellyfin cached `status=NotSupported` in meta.json from a previous bad load | Script resets meta.json; re-run script |
| `Failed to load assembly … incompatible version` | DLL targets wrong Jellyfin ABI | Check `targetAbi` in meta.json matches running Jellyfin version (`docker logs jellyfin \| grep "Jellyfin version"`) |
| `Could not load file or assembly 'SomeLib'` at request time | Third-party dep not in plugin folder or not in `assemblies[]` | Run `dotnet publish`, copy the DLL, add it to `assemblies[]` in deploy script — see CLAUDE.md §Third-party dep bundling |
| `docker: No such container: jellyfin` | localenv not running | `cd ../subfin/localenv && docker compose up -d jellyfin` |
| Build error `CS0234 … Entities` | Jellyfin API namespace changed (see CLAUDE.md §10.11.6) | Fix usings per CLAUDE.md |
| Frontend calls `.../undefined` | JSON field case mismatch — record properties are PascalCase, anonymous objects are camelCase | See CLAUDE.md §JSON casing gotcha |
