#!/usr/bin/env bash
# Bump plugin version in meta.json and .csproj (single source of truth).
#
# Version scheme: JFMAJOR.JFMINOR.PLUGIN_MAJOR.PLUGIN_MINOR
#   Part 1 (Major)    — Jellyfin major version targeted (e.g. 10)
#   Part 2 (Minor)    — Jellyfin minor version targeted (e.g. 11)
#   Part 3 (Build)    — Plugin feature/major version
#   Part 4 (Revision) — Plugin patch version
#
# Example: 10.11.3.0 → targets Jellyfin 10.11.x, plugin release 3.0
# serverVersion seen by Subsonic clients: "{Build}.{Revision}.0" (e.g. "3.0.0")
#
# Usage: ./scripts/bump-version.sh <NEW_VERSION>
# Example: ./scripts/bump-version.sh 10.11.3.0

set -euo pipefail

NEW_VERSION="${1:-}"
if [ -z "$NEW_VERSION" ]; then
    echo "Usage: $0 <version>  (e.g. 0.2.1.0)" >&2
    exit 1
fi

if [[ ! "$NEW_VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
    echo "Error: version must be four-part (e.g. 0.2.1.0)" >&2
    exit 1
fi

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
META_SRC="$REPO_ROOT/meta.json"
CSPROJ="$REPO_ROOT/Jellyfin.Plugin.Subsonic/Jellyfin.Plugin.Subsonic.csproj"

OLD_VERSION=$(jq -r '.version' "$META_SRC")

# Parse new version parts
IFS='.' read -r JF_MAJOR JF_MINOR PLUGIN_MAJOR PLUGIN_MINOR <<< "$NEW_VERSION"
SERVER_VERSION="${PLUGIN_MAJOR}.${PLUGIN_MINOR}.0"

# Check targetAbi consistency
TARGET_ABI=$(jq -r '.targetAbi' "$META_SRC")
ABI_MAJOR=$(echo "$TARGET_ABI" | cut -d. -f1)
ABI_MINOR=$(echo "$TARGET_ABI" | cut -d. -f2)
if [ "$JF_MAJOR" != "$ABI_MAJOR" ] || [ "$JF_MINOR" != "$ABI_MINOR" ]; then
    echo "WARNING: new version implies Jellyfin ${JF_MAJOR}.${JF_MINOR}.x but targetAbi is ${TARGET_ABI}" >&2
    echo "  Update targetAbi in meta.json manually if targeting a different Jellyfin version." >&2
fi

# Update meta.json
tmp=$(mktemp)
jq --arg v "$NEW_VERSION" '.version = $v' "$META_SRC" > "$tmp" && mv "$tmp" "$META_SRC"

# Update .csproj (AssemblyVersion and FileVersion)
sed -i "s|<AssemblyVersion>[^<]*</AssemblyVersion>|<AssemblyVersion>${NEW_VERSION}</AssemblyVersion>|" "$CSPROJ"
sed -i "s|<FileVersion>[^<]*</FileVersion>|<FileVersion>${NEW_VERSION}</FileVersion>|" "$CSPROJ"

echo "Bumped ${OLD_VERSION} → ${NEW_VERSION}"
echo "  meta.json:       $(jq -r '.version' "$META_SRC")"
echo "  serverVersion:   ${SERVER_VERSION}  (what Subsonic clients see)"
echo "  Plugin dir:      Subfin_${NEW_VERSION}"
echo "  Run: ./scripts/deploy-dev.sh"
