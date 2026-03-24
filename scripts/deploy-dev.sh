#!/usr/bin/env bash
# Deploy subfin-plugin to localenv Jellyfin for development.
# Usage: ./scripts/deploy-dev.sh
# Assumes localenv Jellyfin is at ../subfin/localenv (relative to repo root).

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
LOCALENV="$REPO_ROOT/../subfin/localenv"
PLUGIN_DIR="$LOCALENV/jellyfin-data/config/plugins/Subsonic_0.1.0.0"
META_SRC="$REPO_ROOT/meta.json"

# 1. Build
echo "[deploy] Building Release..."
dotnet build -c Release "$REPO_ROOT" --nologo -v quiet

DLL="$REPO_ROOT/Jellyfin.Plugin.Subsonic/bin/Release/net9.0/Jellyfin.Plugin.Subsonic.dll"

# 2. Copy DLL
echo "[deploy] Copying DLL to $PLUGIN_DIR"
mkdir -p "$PLUGIN_DIR"
cp "$DLL" "$PLUGIN_DIR/"

# 3. Write meta.json — always restore to known-good state.
# Jellyfin overwrites meta.json with status=NotSupported/assemblies=[] on load failure;
# this script always resets it so the plugin isn't silently skipped.
VERSION=$(jq -r '.version' "$META_SRC")
GUID=$(jq -r '.guid' "$META_SRC")
cat > "$PLUGIN_DIR/meta.json" << EOF
{
  "category": "Music",
  "changelog": "$(jq -r '.changelog' "$META_SRC")",
  "description": "$(jq -r '.description' "$META_SRC")",
  "guid": "$GUID",
  "name": "Subsonic",
  "overview": "$(jq -r '.overview' "$META_SRC")",
  "owner": "subfin",
  "targetAbi": "$(jq -r '.targetAbi' "$META_SRC")",
  "timestamp": "$(jq -r '.timestamp' "$META_SRC")",
  "version": "$VERSION",
  "status": "Active",
  "autoUpdate": false,
  "assemblies": [
    "Jellyfin.Plugin.Subsonic.dll"
  ]
}
EOF

# 4. Restart Jellyfin
echo "[deploy] Restarting Jellyfin..."
cd "$LOCALENV"
docker compose restart jellyfin

# 5. Wait and verify — check only logs from the last 30 seconds to avoid stale history
sleep 8
RECENT=$(docker logs --since 30s jellyfin 2>&1)
if echo "$RECENT" | grep -q "Loaded plugin: Subsonic"; then
    echo "[deploy] OK — plugin loaded"
    echo "[deploy] Smoke test:"
    curl -s "http://localhost:8096/rest/ping.view?u=x&p=x&v=1.16.1&c=deploy-check"
    echo
else
    echo "[deploy] FAIL — plugin not loaded. Recent logs:"
    echo "$RECENT" | grep -E "Subsonic|ERR|error" | tail -10
    exit 1
fi
