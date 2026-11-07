#!/bin/bash
# Rebuild the looping README previews from the reviewed 86-second launch film.
# Needs ffmpeg and libwebp (img2webp).
# Usage: scripts/readme-media.sh path/to/winpalm-launch.mp4
set -euo pipefail

if [[ $# -ne 1 || ! -f "$1" ]]; then
  echo "usage: $0 <WinPalm launch film.mp4>" >&2
  exit 1
fi
for tool in ffmpeg img2webp; do
  command -v "$tool" >/dev/null || { echo "error: $tool not found" >&2; exit 1; }
done
SRC="$(cd "$(dirname "$1")" && pwd -P)/$(basename "$1")"
ROOT="$(cd "$(dirname "$0")/.." && pwd -P)"
OUT="$ROOT/docs/media"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
mkdir -p "$OUT"

encode() {
  img2webp -loop 0 -min_size -lossy -q "$2" -m 6 -d 83 "$WORK/$1"/f*.png -o "$OUT/$1.webp" >/dev/null 2>&1
  echo "$1.webp: $(wc -c < "$OUT/$1.webp") bytes"
}

# A short four-feature montage at the same size/frame rate as CmdPalm's hero.
mkdir -p "$WORK/hero"
ffmpeg -nostdin -v error -y \
  -ss 12.6 -t 4.4 -i "$SRC" \
  -ss 22.6 -t 3.2 -i "$SRC" \
  -ss 36.6 -t 3.2 -i "$SRC" \
  -ss 50.6 -t 3.4 -i "$SRC" \
  -filter_complex "[0:v][1:v][2:v][3:v]concat=n=4:v=1:a=0,fps=12,scale=800:-2:flags=lanczos[v]" \
  -map '[v]' -threads 2 "$WORK/hero/f%04d.png"
encode hero 52

# Keep each loop inside its chapter, clear of the next scene's transition.
while read -r name start duration; do
  mkdir -p "$WORK/$name"
  ffmpeg -nostdin -v error -y -ss "$start" -t "$duration" -i "$SRC" \
    -vf 'fps=12,scale=480:-2:flags=lanczos' -threads 2 "$WORK/$name/f%04d.png"
  encode "$name" 55
done <<'CLIPS'
trackpad 12.4 9
keyboard 22.4 7
custom 30.4 5
deck 36.4 7
apps-media 44.4 5
voice 50.4 5
CLIPS
