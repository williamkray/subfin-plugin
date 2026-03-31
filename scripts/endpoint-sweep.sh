#!/usr/bin/env bash
# endpoint-sweep.sh — Hit all Subsonic REST endpoints and report ok/failed status.
#
# Usage:
#   ./scripts/endpoint-sweep.sh                         # uses get-creds.js output
#   ./scripts/endpoint-sweep.sh user1 mypassword        # explicit credentials
#   BASE=http://localhost:8096/rest ./scripts/endpoint-sweep.sh user1 pass
#
# Requires the plugin to be running (deploy-dev.sh first).
# Output: one line per endpoint; exits non-zero if any fail.

set -euo pipefail
REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"

BASE="${BASE:-http://localhost:8096/rest}"
V="1.16.1"
C="endpoint-sweep"

# Resolve credentials
if [ $# -ge 2 ]; then
  U="$1"; P="$2"
else
  echo "[sweep] Extracting credentials from plugin DB..."
  CREDS=$(node "$REPO_ROOT/scripts/get-creds.js" 2>/dev/null | grep "^  id=" | head -1)
  U=$(echo "$CREDS" | sed 's/.*u=\([^ ]*\).*/\1/')
  P=$(echo "$CREDS" | sed 's/.*p=\([^ ]*\).*/\1/' | sed 's/ .*//')
fi

if [ -z "$U" ] || [ -z "$P" ]; then
  echo "[sweep] ERROR: could not resolve credentials. Run deploy-dev.sh and link a device first." >&2
  exit 1
fi

echo "[sweep] Testing $BASE as user '$U'"
echo ""

# Pick a real artist/album/song ID from the library for parameterised tests
echo "[sweep] Resolving test IDs from getAlbumList2..."
ALBUM_RESP=$(curl -sf "$BASE/getAlbumList2.view?u=$U&p=$P&v=$V&c=$C&f=json&type=newest&size=1")
ALBUM_ID=$(echo "$ALBUM_RESP" | python3 -c "import json,sys; a=json.load(sys.stdin)['subsonic-response'].get('albumList2',{}).get('album',[]); print(a[0]['id'] if a else '')" 2>/dev/null || true)

SONG_ID=""
ARTIST_ID=""
if [ -n "$ALBUM_ID" ]; then
  ALBUM_DETAIL=$(curl -sf "$BASE/getAlbum.view?u=$U&p=$P&v=$V&c=$C&f=json&id=$ALBUM_ID")
  SONG_ID=$(echo "$ALBUM_DETAIL" | python3 -c "import json,sys; s=json.load(sys.stdin)['subsonic-response'].get('album',{}).get('song',[]); print(s[0]['id'] if s else '')" 2>/dev/null || true)
  ARTIST_ID=$(echo "$ALBUM_DETAIL" | python3 -c "import json,sys; print(json.load(sys.stdin)['subsonic-response'].get('album',{}).get('artistId',''))" 2>/dev/null || true)
fi

PLAYLIST_ID=$(curl -sf "$BASE/getPlaylists.view?u=$U&p=$P&v=$V&c=$C&f=json" | python3 -c "
import json,sys
pls=json.load(sys.stdin)['subsonic-response'].get('playlists',{}).get('playlist',[])
print(pls[0]['id'] if pls else '')
" 2>/dev/null || true)

echo "[sweep] album=$ALBUM_ID  song=$SONG_ID  artist=$ARTIST_ID  playlist=$PLAYLIST_ID"
echo ""

PASS=0; FAIL=0
run() {
  local name="$1" params="${2:-}"
  local result status
  result=$(curl -sf "$BASE/${name}.view?u=$U&p=$P&v=$V&c=$C&f=json&$params" 2>&1) || { printf "  %-35s CURL_ERROR\n" "$name"; FAIL=$((FAIL+1)); return; }
  status=$(echo "$result" | python3 -c "import json,sys; print(json.load(sys.stdin).get('subsonic-response',{}).get('status','?'))" 2>/dev/null || echo "PARSE_ERROR")
  if [ "$status" = "ok" ]; then
    printf "  %-35s ok\n" "$name"
    PASS=$((PASS+1))
  else
    local err
    err=$(echo "$result" | python3 -c "import json,sys; print(json.load(sys.stdin).get('subsonic-response',{}).get('error',{}).get('message','?'))" 2>/dev/null || echo "?")
    printf "  %-35s FAILED  (%s)\n" "$name" "$err"
    FAIL=$((FAIL+1))
  fi
}

# Core
run ping
run getLicense
run getOpenSubsonicExtensions
run getMusicFolders
run getArtists
run getIndexes
run getUser
run getScanStatus

# Item lookup (parameterised)
[ -n "$ARTIST_ID" ] && run getArtist    "id=$ARTIST_ID"    || run getArtist    "id=missing"
[ -n "$ALBUM_ID"  ] && run getAlbum     "id=$ALBUM_ID"     || run getAlbum     "id=missing"
[ -n "$SONG_ID"   ] && run getSong      "id=$SONG_ID"      || run getSong      "id=missing"
[ -n "$ARTIST_ID" ] && run getMusicDirectory "id=${ARTIST_ID#ar-}" || run getMusicDirectory "id=missing"

# Lists
run getAlbumList   "type=newest&size=5"
run getAlbumList2  "type=newest&size=5"
run getRandomSongs "size=5"
run getGenres
run getSongsByGenre "genre=Rock&count=5"

# Playlists
run getPlaylists
[ -n "$PLAYLIST_ID" ] && run getPlaylist "id=$PLAYLIST_ID" || run getPlaylist "id=missing"

# Search
run search3 "query=a&songCount=3&albumCount=3&artistCount=3"

# Favourites
run getStarred
run getStarred2

# Artist/Album info (stubs)
[ -n "$ARTIST_ID" ] && run getArtistInfo  "id=$ARTIST_ID" || run getArtistInfo  ""
[ -n "$ARTIST_ID" ] && run getArtistInfo2 "id=$ARTIST_ID" || run getArtistInfo2 ""
[ -n "$ALBUM_ID"  ] && run getAlbumInfo   "id=$ALBUM_ID"  || run getAlbumInfo   ""
[ -n "$ALBUM_ID"  ] && run getAlbumInfo2  "id=$ALBUM_ID"  || run getAlbumInfo2  ""
[ -n "$SONG_ID"   ] && run getSimilarSongs  "id=$SONG_ID"   || run getSimilarSongs  ""
[ -n "$SONG_ID"   ] && run getSimilarSongs2 "id=$SONG_ID"   || run getSimilarSongs2 ""
run getTopSongs
[ -n "$SONG_ID"   ] && run getLyrics          "id=$SONG_ID"   || run getLyrics          ""
[ -n "$SONG_ID"   ] && run getLyricsBySongId  "id=$SONG_ID"   || run getLyricsBySongId  ""

# Playback state
run getNowPlaying
run getPlayQueue

# Shares
run getShares

echo ""
echo "[sweep] $PASS passed, $FAIL failed"
[ "$FAIL" -eq 0 ] && exit 0 || exit 1
