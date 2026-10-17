#!/usr/bin/env bash
# Every detector's scene list and per-frame stats on the sample clips, through both decoders, into one folder:
# ci.yml's arch job runs it on x64 and on ARM and diffs the two folders (the same clips must give the same files on
# both), with the frames' checksums alongside (tools/arch-summary.py compares them). In-process decoding uses the
# FFmpeg libraries in <libs> (our build for the platform); FASTYUV, when set, is a shotdetect built with
# -p:WithFastYuv=true, run with --fast-yuv too.
# Usage: tools/arch-outputs.sh <shotdetect> <libs> <out-dir> <clip>...
set -euo pipefail
exe=$1 libs=$2 out=$3
shift 3
mkdir -p "$out"
for clip in "$@"; do
  name=$(basename "$clip")
  # The video frames themselves, without ShotDetector (options before -- go before -i): decoded in their own format,
  # single-threaded, with the C IDCT (-idct simple), converted to BGR as both decoders do, with swscale's C code only, and with +bitexact.
  frames() {
    local pre=()
    while [ "$1" != -- ]; do pre+=("$1"); shift; done
    shift
    "$libs/ffmpeg" -v error "${pre[@]}" -i "$clip" -map 0:v:0 "$@" -fflags +bitexact -f framemd5 -
  }
  sha256sum < "$clip" | cut -c1-64 > "$out/$name.file.md5"
  "$libs/ffprobe" -v error -select_streams v:0 -show_entries stream=codec_name,pix_fmt -of csv=p=0 "$clip" > "$out/$name.codec.md5"
  frames -- > "$out/$name.decoded.md5"
  frames -threads 1 -- > "$out/$name.decoded-1thread.md5"
  frames -idct simple -- > "$out/$name.decoded-idctsimple.md5"
  frames -- -pix_fmt bgr24 > "$out/$name.bgr24.md5"
  frames -cpuflags 0 -- -pix_fmt bgr24 > "$out/$name.bgr24-c.md5"
  frames -- -sws_flags bicubic+bitexact -pix_fmt bgr24 > "$out/$name.bgr24-bitexact.md5"
  for detector in adaptive content threshold hist hash; do
    for decoder in inprocess process; do
      base="$out/$name.$detector.$decoder"
      "$exe" -i "$clip" -d "$detector" --decoder "$decoder" --ffmpeg-dir "$libs" --csv "$base.scenes.csv" --stats "$base.stats.csv" > /dev/null
      if [ -n "${FASTYUV:-}" ]; then
        "$FASTYUV" -i "$clip" -d "$detector" --decoder "$decoder" --ffmpeg-dir "$libs" --fast-yuv \
          --csv "$base.fastyuv.scenes.csv" --stats "$base.fastyuv.stats.csv" > /dev/null
      fi
    done
  done
done
echo "$(ls "$out" | wc -l) files in $out"
