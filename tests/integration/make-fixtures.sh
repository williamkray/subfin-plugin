#!/usr/bin/env bash
# Generate a small, copyright-free music library for integration tests.
#
# Layout (under $1, default ./fixtures):
#   lib1/Fixture Artist/Single Album/0N - Track N.mp3     normal album, 3 tracks
#   lib1/Fixture Artist/Double Album/CD 1/0N - ...mp3     multi-disc album in disc subfolders
#   lib1/Fixture Artist/Double Album/CD 2/0N - ...mp3
#   lib1/Loose Track N.mp3                                 loose files (no album), embedded cover
#   lib2/Other Artist/Other Album/0N - ...mp3              second library (access-restriction tests)
#
# Requires ffmpeg. Tracks are short sine tones with ID3 tags.
set -euo pipefail

OUT="${1:-./fixtures}"
mkdir -p "$OUT"
COVER="$OUT/.cover.png"
ffmpeg -v error -y -f lavfi -i "color=c=steelblue:s=300x300" -frames:v 1 "$COVER"

# track <path> <artist> <album> <title> <track> <disc> <freq> [cover]
track() {
  local path="$1" artist="$2" album="$3" title="$4" trackno="$5" disc="$6" freq="$7" cover="${8:-}"
  mkdir -p "$(dirname "$path")"
  local args=(-v error -y -f lavfi -i "sine=frequency=${freq}:duration=20")
  if [ -n "$cover" ]; then
    args+=(-i "$cover" -map 0:a -map 1:v -c:v png -disposition:v attached_pic -metadata:s:v title="Cover")
  fi
  args+=(-c:a libmp3lame -b:a 128k -id3v2_version 3
         -metadata artist="$artist" -metadata album_artist="$artist"
         -metadata title="$title" -metadata track="$trackno")
  [ -n "$album" ] && args+=(-metadata album="$album")
  [ -n "$disc" ] && args+=(-metadata disc="$disc")
  ffmpeg "${args[@]}" "$path"
}

A="Fixture Artist"
for n in 1 2 3; do
  track "$OUT/lib1/$A/Single Album/0$n - Single $n.mp3" "$A" "Single Album" "Single $n" "$n" "" $((300 + n * 20))
done
for d in 1 2; do
  for n in 1 2; do
    track "$OUT/lib1/$A/Double Album/CD $d/0$n - Disc $d Track $n.mp3" "$A" "Double Album" "Disc $d Track $n" "$n" "$d" $((400 + d * 40 + n * 10))
  done
done
for n in 1 2; do
  track "$OUT/lib1/Loose Track $n.mp3" "Loose Artist" "" "Loose Track $n" "$n" "" $((600 + n * 30)) "$COVER"
done
for n in 1 2; do
  track "$OUT/lib2/Other Artist/Other Album/0$n - Other $n.mp3" "Other Artist" "Other Album" "Other $n" "$n" "" $((700 + n * 20))
done
rm -f "$COVER"
echo "fixtures written to $OUT"
find "$OUT" -name '*.mp3' | sort
