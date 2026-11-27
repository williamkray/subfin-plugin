#!/usr/bin/env bash
# Start a throwaway Jellyfin with the plugin installed and a generated fixture library.
#
# Usage: setup-jellyfin.sh <jellyfin-image> <plugin-dll> <plugin-version> <target-abi> [port] [workdir]
#   e.g. setup-jellyfin.sh jellyfin/jellyfin:10.11.6 bin/.../Jellyfin.Plugin.Subsonic.dll 10.11.5.8 10.11.0.0
#
# Creates (fresh, under workdir, default /tmp/subfin-it):
#   - container "subfin-it" on <port> (default 8099) → 8096
#   - libraries lib1 + lib2 from make-fixtures.sh
#   - admin "owner" (all libraries) and "restricted" (lib1 only), password "password123"
#   - one linked Subsonic device each
# Writes <workdir>/env with BASE, OWNER_P, RESTRICTED_P, ADMIN_TOKEN, RESTRICTED_ID, LIB1_ID, LIB2_ID.
set -euo pipefail

IMAGE="$1"; DLL="$2"; VERSION="$3"; ABI="$4"; PORT="${5:-8099}"; WORK="${6:-/tmp/subfin-it}"
HERE="$(cd "$(dirname "$0")" && pwd)"
REPO="$(cd "$HERE/../.." && pwd)"
NAME=subfin-it
J="http://localhost:$PORT"

docker rm -f "$NAME" >/dev/null 2>&1 || true
rm -rf "$WORK"; mkdir -p "$WORK/config/plugins/Subfin_${VERSION}.0" "$WORK/cache"
"$HERE/make-fixtures.sh" "$WORK/media" >/dev/null
cp "$DLL" "$WORK/config/plugins/Subfin_${VERSION}.0/"
jq --arg v "$VERSION" --arg abi "$ABI" \
   '. + {version: $v, targetAbi: $abi, status: "Active", autoUpdate: false, assemblies: ["Jellyfin.Plugin.Subsonic.dll"]}' \
   "$REPO/meta.json" > "$WORK/config/plugins/Subfin_${VERSION}.0/meta.json"

docker run -d --name "$NAME" --user "$(id -u):$(id -g)" -p "$PORT:8096" \
  -v "$WORK/config:/config" -v "$WORK/cache:/cache" -v "$WORK/media:/media:ro" "$IMAGE" >/dev/null

wait_healthy() {
  for _ in $(seq 1 90); do [ "$(curl -s "$J/health")" = Healthy ] && return 0; sleep 2; done
  echo "Jellyfin did not become healthy" >&2; docker logs "$NAME" 2>&1 | tail -30 >&2; exit 1
}
wait_healthy
# Capture first: `docker logs | grep -q` trips pipefail via SIGPIPE when grep exits early
LOGS=$(docker logs "$NAME" 2>&1)
if ! grep -q "Loaded plugin: Subfin" <<< "$LOGS"; then
  echo "plugin not loaded" >&2; grep -i subfin <<< "$LOGS" >&2 || true; exit 1
fi

# Startup wizard
curl -sf -X POST "$J/Startup/Configuration" -H 'Content-Type: application/json' \
  -d '{"UICulture":"en-US","MetadataCountryCode":"US","PreferredMetadataLanguage":"en"}' >/dev/null
curl -sf "$J/Startup/User" >/dev/null
curl -sf -X POST "$J/Startup/User" -H 'Content-Type: application/json' -d '{"Name":"owner","Password":"password123"}' >/dev/null
curl -sf -X POST "$J/Startup/Complete" >/dev/null

login() {  # <user> → access token
  curl -sf -X POST "$J/Users/AuthenticateByName" -H 'Content-Type: application/json' \
    -H "Authorization: MediaBrowser Client=\"subfin-it\", Device=\"it\", DeviceId=\"it-$1\", Version=\"1\"" \
    -d "{\"Username\":\"$1\",\"Pw\":\"password123\"}" | jq -r .AccessToken
}
ADMIN_TOKEN=$(login owner)
AH="Authorization: MediaBrowser Token=\"$ADMIN_TOKEN\""

for lib in lib1 lib2; do
  curl -sf -X POST "$J/Library/VirtualFolders?name=$lib&collectionType=music&refreshLibrary=false" -H "$AH" \
    -H 'Content-Type: application/json' \
    -d "{\"LibraryOptions\":{\"PathInfos\":[{\"Path\":\"/media/$lib\"}]}}" >/dev/null
done
curl -sf -X POST "$J/Library/Refresh" -H "$AH" >/dev/null

# Wait for the scan: 9 tracks in lib1 + 2 in lib2
for _ in $(seq 1 90); do
  n=$(curl -s "$J/Items?Recursive=true&IncludeItemTypes=Audio&Limit=0" -H "$AH" | jq -r '.TotalRecordCount // 0')
  [ "$n" -ge 11 ] && break; sleep 2
done
[ "${n:-0}" -ge 11 ] || { echo "library scan incomplete ($n tracks)" >&2; exit 1; }
# ...and for tag extraction (every fixture track has a track number)
for _ in $(seq 1 60); do
  missing=$(curl -s "$J/Items?Recursive=true&IncludeItemTypes=Audio" -H "$AH" | jq '[.Items[] | select(.IndexNumber == null)] | length')
  [ "$missing" = 0 ] && break; sleep 2
done

LIBS=$(curl -sf "$J/Library/VirtualFolders" -H "$AH")
LIB1_ID=$(echo "$LIBS" | jq -r '.[] | select(.Name=="lib1") | .ItemId')
LIB2_ID=$(echo "$LIBS" | jq -r '.[] | select(.Name=="lib2") | .ItemId')

RESTRICTED_ID=$(curl -sf -X POST "$J/Users/New" -H "$AH" -H 'Content-Type: application/json' \
  -d '{"Name":"restricted","Password":"password123"}' | jq -r .Id)
curl -sf "$J/Users/$RESTRICTED_ID" -H "$AH" \
  | jq -c --arg l "$LIB1_ID" '.Policy | .EnableAllFolders=false | .EnabledFolders=[$l]' \
  | curl -sf -X POST "$J/Users/$RESTRICTED_ID/Policy" -H "$AH" -H 'Content-Type: application/json' -d @- >/dev/null
RESTRICTED_TOKEN=$(login restricted)

link() {  # <token> → app password
  curl -sf -X POST "$J/subfin/api/devices/link" -H "Authorization: MediaBrowser Token=\"$1\"" \
    -H 'Content-Type: application/json' -d '{"DeviceLabel":"it"}' | jq -r .password
}
OWNER_P=$(link "$ADMIN_TOKEN")
RESTRICTED_P=$(link "$RESTRICTED_TOKEN")

cat > "$WORK/env" <<ENV
BASE=$J
OWNER_P=$OWNER_P
RESTRICTED_P=$RESTRICTED_P
ADMIN_TOKEN=$ADMIN_TOKEN
RESTRICTED_ID=$RESTRICTED_ID
LIB1_ID=$LIB1_ID
LIB2_ID=$LIB2_ID
ENV
echo "[setup] Jellyfin $IMAGE ready at $J with Subfin $VERSION — env: $WORK/env"
