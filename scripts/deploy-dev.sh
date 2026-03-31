#!/usr/bin/env bash
# Deploy subfin-plugin to localenv Jellyfin via direct copy-in-place.
# Workflow: dotnet publish → copy DLL + meta.json → restart → verify.
# Usage: ./scripts/deploy-dev.sh

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
LOCALENV="$REPO_ROOT/../subfin/localenv"
META_SRC="$REPO_ROOT/meta.json"

VERSION=$(jq -r '.version' "$META_SRC")
PLUGIN_DIR="$LOCALENV/jellyfin-data/config/plugins/Subfin_${VERSION}.0"

# 1. Build
echo "[deploy] Building Release v${VERSION}..."
dotnet publish -c Release "$REPO_ROOT/Jellyfin.Plugin.Subsonic" --nologo -v quiet
PUBLISH="$REPO_ROOT/Jellyfin.Plugin.Subsonic/bin/Release/net9.0/publish"

# 2. Remove stale version dirs to avoid Jellyfin loading multiple versions
for stale in "$LOCALENV/jellyfin-data/config/plugins/Subfin_"*/; do
    [ -d "$stale" ] && [ "${stale%/}" != "$PLUGIN_DIR" ] && rm -rf "$stale" && echo "[deploy] Removed stale dir: $stale"
done
# Also clean up any old Subsonic_* dirs left from before the rename
for stale in "$LOCALENV/jellyfin-data/config/plugins/Subsonic_"*/; do
    [ -d "$stale" ] && rm -rf "$stale" && echo "[deploy] Removed legacy Subsonic dir: $stale"
done

# 3. Copy DLL and meta.json into plugin dir
echo "[deploy] Installing to $PLUGIN_DIR..."
mkdir -p "$PLUGIN_DIR"
cp "$PUBLISH/Jellyfin.Plugin.Subsonic.dll" "$PLUGIN_DIR/"
# Write meta.json with required status/assemblies fields
jq '. + {status: "Active", autoUpdate: true, assemblies: ["Jellyfin.Plugin.Subsonic.dll"]}' "$META_SRC" \
  > "$PLUGIN_DIR/meta.json"

# 4. Restart Jellyfin
echo "[deploy] Restarting Jellyfin..."
cd "$LOCALENV"
docker compose restart jellyfin

# 5. Clear derived_cache so post-deploy testing isn't masked by stale artist index data
DB="$LOCALENV/jellyfin-data/config/data/SubfinPlugin/subsonic.db"
if [ -f "$DB" ]; then
    sqlite3 "$DB" "DELETE FROM derived_cache;" 2>/dev/null && echo "[deploy] Cleared derived_cache"
fi

# 6. Wait and verify — check only logs from the last 30 seconds to avoid stale history
sleep 8
RECENT=$(docker logs --since 30s jellyfin 2>&1)
if echo "$RECENT" | grep -q "Loaded plugin: Subfin"; then
    echo "[deploy] OK — plugin loaded v${VERSION}"
    echo "[deploy] Smoke test:"
    curl -s "http://localhost:8096/rest/ping.view?u=x&p=x&v=1.16.1&c=deploy-check"
    echo
else
    echo "[deploy] FAIL — plugin not loaded. Recent logs:"
    echo "$RECENT" | grep -E "Subfin|ERR|error" | tail -10
    exit 1
fi
