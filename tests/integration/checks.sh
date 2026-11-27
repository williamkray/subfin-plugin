#!/usr/bin/env bash
# Integration checks against a Jellyfin prepared by setup-jellyfin.sh (fixture library).
# Usage: checks.sh [env-file]   (default /tmp/subfin-it/env)
# Exits non-zero if any check fails. Requires curl, jq, ffmpeg.
set -uo pipefail

ENV_FILE="${1:-/tmp/subfin-it/env}"
# shellcheck source=/dev/null
. "$ENV_FILE"
R="$BASE/rest"
OQ="u=owner&p=$OWNER_P&v=1.16.1&c=it&f=json"
RQ="u=restricted&p=$RESTRICTED_P&v=1.16.1&c=it&f=json"
AH="Authorization: MediaBrowser Token=\"$ADMIN_TOKEN\""
TMP=$(mktemp -d); trap 'rm -rf "$TMP"' EXIT

PASS=0; FAIL=0
pass() { printf '  ok    %s\n' "$1"; PASS=$((PASS+1)); }
fail() { printf '  FAIL  %s  (got: %s)\n' "$1" "$2"; FAIL=$((FAIL+1)); }
# expect <name> <actual> <expected>
expect() { if [ "$2" = "$3" ]; then pass "$1"; else fail "$1" "$2"; fi; }
api()  { curl -s "$R/$1.view?$2&${3:-}"; }                       # api <method> <authq> [params]
sr()   { jq -c "$1" <<< "$2" 2>/dev/null; }                       # select from a response
code() { api "$1" "$2" "${3:-}" | jq -r '."subsonic-response".error.code // "ok"'; }
http() { curl -s -o /dev/null -w '%{http_code}' "$@"; }
seconds() { ffmpeg -v error -i "$1" -f null - -stats 2>&1 | tr '\r' '\n' | grep -o 'time=[0-9:.]*' | tail -1 \
            | awk -F'[=:]' '{printf "%d", $2*3600 + $3*60 + $4 + 0.5}'; }
item() { curl -s "$BASE/Items?Recursive=true&IncludeItemTypes=$1&SearchTerm=$(jq -rn --arg s "$2" '$s|@uri')" -H "$AH" | jq -r '.Items[0].Id'; }

DOUBLE=$(item MusicAlbum "Double Album"); SINGLE=$(item MusicAlbum "Single Album"); OTHER=$(item MusicAlbum "Other Album")
LOOSE=$(item Audio "Loose Track 1"); SONG1=$(item Audio "Single 1"); SONG2=$(item Audio "Single 2"); OTHERSONG=$(item Audio "Other 1")
# Fail fast on a missed lookup: an id of "null" would make the "denied"/"not found" checks pass vacuously
for v in DOUBLE SINGLE OTHER LOOSE SONG1 SONG2 OTHERSONG; do
  [[ "${!v}" =~ ^[0-9a-f]{32}$ ]] || { echo "fixture lookup failed: $v='${!v}'" >&2; exit 1; }
done

echo "== endpoint sweep (owner)"
PL=$(api createPlaylist "$OQ" "name=it-sweep&songId=$SONG1" | jq -r '."subsonic-response".playlist.id')
ARTIST=$(api getAlbum "$OQ" "id=$SINGLE" | jq -r '."subsonic-response".album.artistId')
[[ "$PL" == pl-* && "$ARTIST" =~ ^[0-9a-f]{32}$ ]] || { echo "setup failed: PL='$PL' ARTIST='$ARTIST'" >&2; exit 1; }
while read -r m params; do
  expect "$m" "$(code "$m" "$OQ" "$params")" ok
done <<EOF
getLicense
getOpenSubsonicExtensions
getMusicFolders
getArtists
getIndexes
getUser
getScanStatus
getArtist id=$ARTIST
getAlbum id=$SINGLE
getSong id=$SONG1
getMusicDirectory id=$ARTIST
getAlbumList type=newest&size=5
getAlbumList2 type=alphabeticalByName
getRandomSongs size=5
getGenres
getSongsByGenre genre=Rock
getPlaylists
getPlaylist id=$PL
search3 query=Single
getStarred
getStarred2
getArtistInfo id=$ARTIST
getArtistInfo2 id=$ARTIST
getAlbumInfo id=$SINGLE
getAlbumInfo2 id=$SINGLE
getSimilarSongs id=$SONG1
getSimilarSongs2 id=$SONG1
getTopSongs artist=Fixture%20Artist
getLyricsBySongId id=$SONG1
getNowPlaying
getPlayQueue
getShares
EOF

echo "== response shapes"
expect "getAlbumInfo2 wraps in albumInfo (GH #6)" "$(api getAlbumInfo2 "$OQ" "id=$SINGLE" | jq -r '."subsonic-response" | has("albumInfo")')" true
expect "getArtistInfo2 keeps artistInfo2" "$(api getArtistInfo2 "$OQ" "id=$ARTIST" | jq -r '."subsonic-response" | has("artistInfo2")')" true
expect "album artistId is a getArtists id" \
  "$(api getArtists "$OQ" | jq --arg a "$ARTIST" '[."subsonic-response".artists.index[].artist[].id] | index($a) != null')" true

echo "== library model (GH #4)"
expect "multi-disc getAlbum has all tracks" "$(api getAlbum "$OQ" "id=$DOUBLE" | jq '."subsonic-response".album.song | length')" 4
expect "multi-disc getMusicDirectory has all tracks" "$(api getMusicDirectory "$OQ" "id=$DOUBLE" | jq '."subsonic-response".directory.child | length')" 4
expect "disc-folder song reports the real album" \
  "$(api search3 "$OQ" "query=Disc%201%20Track%201" | jq -r --arg d "$DOUBLE" '."subsonic-response".searchResult3.song[0].albumId == $d')" true
LCOVER=$(api getSong "$OQ" "id=$LOOSE" | jq -r '."subsonic-response".song.coverArt')
expect "loose song uses its own cover id" "$LCOVER" "$LOOSE"
expect "loose song cover resolves" "$(http -L "$R/getCoverArt.view?$OQ&id=$LCOVER")" 200

echo "== streaming (GH #5)"
curl -s -o "$TMP/full.mp3" "$R/stream.view?$OQ&id=$SONG1&format=mp3"
curl -s -o "$TMP/seek.mp3" "$R/stream.view?$OQ&id=$SONG1&format=mp3&timeOffset=12"
curl -s -o "$TMP/br.mp3" "$R/stream.view?$OQ&id=$SONG1&format=mp3&maxBitRate=64"
expect "transcode full length (20s)" "$(seconds "$TMP/full.mp3")" 20
expect "timeOffset=12 after a prior transcode → 8s" "$(seconds "$TMP/seek.mp3")" 8
BR=$(ffprobe -v error -show_entries format=bit_rate -of csv=p=0 "$TMP/br.mp3"); BR=$(( ${BR:-0} / 1000 ))
expect "maxBitRate=64 honoured" "$([ "$BR" -ge 60 ] && [ "$BR" -le 70 ] && echo yes || echo "$BR kbps")" yes

echo "== share credentials are scoped"
SHARE=$(api createShare "$OQ" "id=$SONG1")
SURL=$(sr '."subsonic-response".shares.share[0].url' "$SHARE" | tr -d '"')
SUID=$(sr '."subsonic-response".shares.share[0].id' "$SHARE" | tr -d '"')
SQ="u=share_$SUID&p=${SURL#*secret=}&v=1.16.1&c=it&f=json"
expect "share login: getAlbumList2 denied" "$(code getAlbumList2 "$SQ" "type=random")" 50
expect "share login: createPlaylist denied" "$(code createPlaylist "$SQ" "name=x")" 50
expect "share login: stream shared song" "$(http "$R/stream.view?$SQ&id=$SONG1")" 200
expect "share login: stream other song" "$(http "$R/stream.view?$SQ&id=$SONG2")" 403
expect "share login: download other song" "$(http "$R/download.view?$SQ&id=$SONG2")" 403
expect "share page" "$(http "$SURL")" 200
expect "share page, bad secret" "$(http "$BASE/subfin/share/$SUID?secret=nope")" 401
api updateShare "$OQ" "id=$SUID&expires=$(( ($(date +%s) - 60) * 1000 ))" >/dev/null
expect "expired share: stream" "$(api stream "$SQ" "id=$SONG1" | jq -r '."subsonic-response".error.code')" 40
expect "expired share: page" "$(http "$SURL")" 410

echo "== library access + ownership (restricted user: lib1 only)"
expect "restricted: getMusicFolders lists only lib1" "$(api getMusicFolders "$RQ" | jq -c '[."subsonic-response".musicFolders.musicFolder[].name]')" '["lib1"]'
expect "restricted: getAlbum in lib2" "$(code getAlbum "$RQ" "id=$OTHER")" 70
expect "restricted: getSong in lib2" "$(code getSong "$RQ" "id=$OTHERSONG")" 70
expect "restricted: download from lib2" "$(http "$R/download.view?$RQ&id=$OTHERSONG")" 404
expect "restricted: createShare of lib2 song" "$(code createShare "$RQ" "id=$OTHERSONG")" 70
expect "restricted: getAlbum in lib1" "$(code getAlbum "$RQ" "id=$SINGLE")" ok
expect "restricted: getPlaylist (owner's private)" "$(code getPlaylist "$RQ" "id=$PL")" 70
expect "restricted: updatePlaylist (owner's private)" "$(code updatePlaylist "$RQ" "playlistId=$PL&name=pwned")" 70
expect "restricted: deletePlaylist (owner's private)" "$(code deletePlaylist "$RQ" "id=$PL")" 70
expect "restricted: deleteShare (owner's)" "$(code deleteShare "$RQ" "id=$SUID")" 70
expect "owner: playlist intact" "$(api getPlaylist "$OQ" "id=$PL" | jq -r '."subsonic-response".playlist.name')" it-sweep
expect "owner: deleteShare" "$(code deleteShare "$OQ" "id=$SUID")" ok
expect "owner: deletePlaylist" "$(code deletePlaylist "$OQ" "id=$PL")" ok

echo ""
echo "[checks] $PASS passed, $FAIL failed"
[ "$FAIL" -eq 0 ]
