#!/usr/bin/env bash
# Bump plugin version in meta.json and .csproj (single source of truth).
# Usage: ./scripts/bump-version.sh <NEW_VERSION>
# Example: ./scripts/bump-version.sh 0.2.1.0

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

# Update meta.json
tmp=$(mktemp)
jq --arg v "$NEW_VERSION" '.version = $v' "$META_SRC" > "$tmp" && mv "$tmp" "$META_SRC"

# Update .csproj (AssemblyVersion and FileVersion)
sed -i "s|<AssemblyVersion>[^<]*</AssemblyVersion>|<AssemblyVersion>${NEW_VERSION}</AssemblyVersion>|" "$CSPROJ"
sed -i "s|<FileVersion>[^<]*</FileVersion>|<FileVersion>${NEW_VERSION}</FileVersion>|" "$CSPROJ"

echo "Bumped ${OLD_VERSION} → ${NEW_VERSION}"
echo "  meta.json:       $(jq -r '.version' "$META_SRC")"
echo "  Plugin dir:      Subsonic_${NEW_VERSION}"
echo "  Run: ./scripts/deploy-dev.sh"
