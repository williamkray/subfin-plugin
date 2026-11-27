#!/usr/bin/env bash
# Cut a release: bump version, commit, tag, push → GitHub Actions handles the rest.
# Usage: ./scripts/release.sh <plugin-version>
# Example: ./scripts/release.sh 5.8   (legacy four-part 10.11.5.8 also accepted)
# Tags v10.11.<plugin-version>; the workflow builds the 10.11 and 12.x lines from that tag.

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
VERSIONED_FILES=(meta.json Jellyfin.Plugin.Subsonic/Jellyfin.Plugin.Subsonic.csproj)

ARG="${1:-}"
if [[ "$ARG" =~ ^[0-9]+\.[0-9]+$ ]]; then
  SUBFIN_VERSION="$ARG"
elif [[ "$ARG" =~ ^[0-9]+\.[0-9]+\.([0-9]+\.[0-9]+)$ ]]; then
  SUBFIN_VERSION="${BASH_REMATCH[1]}"
else
  echo "Usage: $0 <plugin-version>   (e.g. 5.8)"
  exit 1
fi
VERSION="10.11.${SUBFIN_VERSION}"

CURRENT_BRANCH=$(git -C "$REPO_ROOT" rev-parse --abbrev-ref HEAD)
if [ "$CURRENT_BRANCH" != "main" ]; then
  echo "ERROR: Must be on 'main' branch to release (currently on '$CURRENT_BRANCH')"
  echo "Merge your changes to main first, then re-run."
  exit 1
fi
if [ -n "$(git -C "$REPO_ROOT" status --porcelain -- "${VERSIONED_FILES[@]}")" ]; then
  echo "ERROR: ${VERSIONED_FILES[*]} have uncommitted changes"
  exit 1
fi

echo "[release] Bumping plugin version to $SUBFIN_VERSION..."
"$REPO_ROOT/scripts/bump-version.sh" "$SUBFIN_VERSION"

echo "[release] Committing version bump..."
git -C "$REPO_ROOT" add -- "${VERSIONED_FILES[@]}"
git -C "$REPO_ROOT" commit -m "release: bump to v${VERSION}"

echo "[release] Pushing main..."
git -C "$REPO_ROOT" push origin main

echo "[release] Tagging v${VERSION}..."
git -C "$REPO_ROOT" tag -a "v${VERSION}" -m "Release $VERSION (Jellyfin 10.11: ${VERSION}, Jellyfin 12.x: 12.0.${SUBFIN_VERSION})"
git -C "$REPO_ROOT" push origin "v${VERSION}"

REPO_SLUG=$(git -C "$REPO_ROOT" remote get-url origin | sed -E 's#(git@github.com:|https://github.com/)##; s#\.git$##')
echo ""
echo "[release] Done. GitHub Actions will build both Jellyfin lines, package, and update the manifest."
echo "  Watch: https://github.com/${REPO_SLUG}/actions"
echo "  Manifest: https://raw.githubusercontent.com/${REPO_SLUG}/main/jellyfin-plugin-subfin-manifest.json"
