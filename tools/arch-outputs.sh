#!/usr/bin/env bash
# Every detector's scene list and per-frame stats on the sample clips, through both decoders, into one folder:
# ci.yml's arch job runs it on x64 and on ARM and diffs the two folders (the same clips must give the same files on
# both), and the frames' checksums (tools/arch-summary.py compares them). In-process decoding uses the FFmpeg libraries in <libs> (our build for the platform).
# Usage: tools/arch-outputs.sh <shotdetect> <libs> <out-dir> <clip>...
set -euo pipefail
exe=$1 libs=$2 out=$3
shift 3
mkdir -p "$out"
for clip in "$@"; do
  name=$(basename "$clip")
  # The frames themselves, without ShotDetector: decoded, converted to BGR as both decoders do, and with +bitexact.
  md5() { "$libs/ffmpeg" -v error -i "$clip" "$@" -fflags +bitexact -f framemd5 -; }
  sha256sum < "$clip" | cut -c1-64 > "$out/$name.file.md5"
  md5 -pix_fmt yuv420p > "$out/$name.yuv420p.md5"
  md5 -threads 1 -pix_fmt yuv420p > "$out/$name.yuv420p-1thread.md5"
  md5 -pix_fmt bgr24 > "$out/$name.bgr24.md5"
  md5 -sws_flags bicubic+bitexact -pix_fmt bgr24 > "$out/$name.bgr24-bitexact.md5"
  for detector in adaptive content threshold hist hash; do
    for decoder in inprocess process; do
      base="$out/$name.$detector.$decoder"
      "$exe" -i "$clip" -d "$detector" --decoder "$decoder" --ffmpeg-dir "$libs" --csv "$base.scenes.csv" --stats "$base.stats.csv" > /dev/null
    done
  done
done
echo "$(ls "$out" | wc -l) files in $out"
