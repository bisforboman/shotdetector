#!/bin/bash
# A release archive works on its own: unpacked into an empty folder, its shotdetect prints the version and decodes a
# clip in-process with the FFmpeg libraries it carries, with no ffmpeg or ffprobe on PATH. release.yml's `binaries`
# job runs it on every archive before uploading it, and ci.yml's `aot` job on the same archive built the same way
# (tools/archive-binary.sh), so a pull request runs the release's check.
#   tools/check-binary.sh --clips <folder>                   make the clips (needs ffmpeg; on Linux, once per run)
#   tools/check-binary.sh <archive> <version> <clip folder>  check an archive
# Bash and coreutils only beyond that (Alpine's SDK image, Git Bash on Windows, macOS).
set -euo pipefail

if [ "${1:-}" = --clips ]; then
  # Two shots of 2 s at 25 fps (a cut at frame 50), and the same in Matroska for standard input.
  mkdir -p "$2"
  ffmpeg -v error -y -f lavfi -i testsrc2=s=320x240:r=25:d=2 -f lavfi -i smptebars=s=320x240:r=25:d=2 \
    -filter_complex "[0][1]concat=n=2:v=1:a=0,format=yuv420p" -r 25 -c:v mpeg4 "$2/clip.mp4"
  ffmpeg -v error -y -i "$2/clip.mp4" -c copy -f matroska "$2/clip.mkv"
  ls -l "$2"
  exit 0
fi

archive=$1 version=$2
clips=$(cd "$3" && pwd)
# Windows' 7z wants Windows paths; Git Bash converts the arguments of the binary itself.
native() { if command -v cygpath > /dev/null; then cygpath -w "$1"; else echo "$1"; fi; }

dir=$(mktemp -d)
case "$archive" in
  *.zip) 7z x -o"$(native "$dir")" "$(native "$archive")" > /dev/null ;;
  *.tar.gz) tar -xzf "$archive" -C "$dir" ;;
  *) echo "::error::Not a release archive: $archive"; exit 1 ;;
esac
echo "Unpacked $archive:"
ls -l "$dir"
bin=$dir/shotdetect
[ -f "$bin.exe" ] && bin=$bin.exe
[ -f "$bin" ] || { echo "::error::$archive has no shotdetect at its root"; exit 1; }

# PATH without any folder holding ffmpeg or ffprobe, for the binary only (the folder may also hold this script's tools).
clean=
IFS=: read -ra folders <<< "$PATH"
for folder in "${folders[@]}"; do
  [ -n "$folder" ] || continue
  if [ -e "$folder/ffmpeg" ] || [ -e "$folder/ffmpeg.exe" ] || [ -e "$folder/ffprobe" ] || [ -e "$folder/ffprobe.exe" ]; then
    echo "Left off PATH (ffmpeg there): $folder"
    continue
  fi
  clean=${clean:+$clean:}$folder
done
if (PATH=$clean; hash -r; command -v ffmpeg || command -v ffprobe); then
  echo "::error::ffmpeg is still on PATH"; exit 1
fi
run() { PATH=$clean "$bin" "$@"; }
fail() { echo "::error::$1"; shift; for f in "$@"; do echo "== $f"; cat "$f"; done; exit 1; }

work=$dir/check
mkdir "$work" && cd "$work"
cp "$clips/clip.mp4" "$clips/clip.mkv" .

got=$(run --version | tr -d '\r')
echo "--version: $got"
[ "$got" = "$version" ] || fail "--version printed '$got', not $version"

# A file: two scenes, decoded with the bundled libraries.
run -i clip.mp4 -d content --csv shots.csv 2> summary.txt || fail "shotdetect failed on clip.mp4" summary.txt
cat summary.txt
grep -q "decoded in-process" summary.txt || fail "clip.mp4 wasn't decoded in-process (bundled libraries not loaded?)" summary.txt
[ "$(tail -n +3 shots.csv | wc -l | tr -d ' ')" -eq 2 ] || fail "Expected 2 scenes in clip.mp4" shots.csv   # after "Timecode List:" and the header

# --probe-json (System.Text.Json's writer, under Native AOT too).
run -i clip.mp4 --probe-json > probe.json 2> probe.txt || fail "--probe-json failed" probe.txt
grep -q '"width": 320' probe.json && grep -q '"frameRate": "25/1"' probe.json || fail "--probe-json's output" probe.json

# Standard input: FFmpeg calls back into .NET to read it (a custom AVIOContext).
run -i - -d content --csv piped.csv < clip.mkv 2> piped.txt || fail "shotdetect failed on standard input" piped.txt
cat piped.txt
grep -q "decoded in-process" piped.txt || fail "Standard input wasn't decoded in-process" piped.txt
[ "$(tail -n +3 piped.csv | wc -l | tr -d ' ')" -eq 2 ] || fail "Expected 2 scenes from standard input" piped.csv

cd / && rm -rf "$dir"
echo "$archive: $version, decodes in-process with no ffmpeg on PATH"
