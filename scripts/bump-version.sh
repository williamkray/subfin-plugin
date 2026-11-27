#!/usr/bin/env bash
# Bump the plugin version (single source of truth: <SubfinVersion> in the .csproj).
#
# The plugin is built for several Jellyfin lines from the same source. Each build's
# four-part version is <Jellyfin ABI major.minor>.<SubfinVersion>:
#   SubfinVersion 5.8 → 10.11.5.8 (Jellyfin 10.11.x) and 12.0.5.8 (Jellyfin 12.x)
# serverVersion seen by Subsonic clients: "<SubfinVersion>.0" (e.g. "5.8.0").
# meta.json "version" tracks the 10.11 line (used by deploy-dev and as the release tag).
#
# Usage: ./scripts/bump-version.sh <PLUGIN_VERSION>
#   ./scripts/bump-version.sh 5.8          # plugin major.minor
#   ./scripts/bump-version.sh 10.11.5.8    # legacy four-part form: last two parts are used

set -euo pipefail

ARG="${1:-}"
if [[ "$ARG" =~ ^[0-9]+\.[0-9]+$ ]]; then
    SUBFIN_VERSION="$ARG"
elif [[ "$ARG" =~ ^[0-9]+\.[0-9]+\.([0-9]+\.[0-9]+)$ ]]; then
    SUBFIN_VERSION="${BASH_REMATCH[1]}"
else
    echo "Usage: $0 <plugin-version>  (e.g. 5.8, or legacy 10.11.5.8)" >&2
    exit 1
fi

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
META_SRC="$REPO_ROOT/meta.json"
CSPROJ="$REPO_ROOT/Jellyfin.Plugin.Subsonic/Jellyfin.Plugin.Subsonic.csproj"

OLD=$(sed -n 's|.*<SubfinVersion>\(.*\)</SubfinVersion>.*|\1|p' "$CSPROJ")
sed -i "s|<SubfinVersion>[^<]*</SubfinVersion>|<SubfinVersion>${SUBFIN_VERSION}</SubfinVersion>|" "$CSPROJ"

tmp=$(mktemp)
jq --arg v "10.11.${SUBFIN_VERSION}" '.version = $v | .targetAbi = "10.11.0.0"' "$META_SRC" > "$tmp" && mv "$tmp" "$META_SRC"

echo "Bumped plugin version ${OLD} → ${SUBFIN_VERSION}"
echo "  Jellyfin 10.11 build: 10.11.${SUBFIN_VERSION}   (meta.json, release tag v10.11.${SUBFIN_VERSION})"
echo "  Jellyfin 12.x build:  12.0.${SUBFIN_VERSION}"
echo "  serverVersion:        ${SUBFIN_VERSION}.0"
