#!/usr/bin/env bash
# Builds a long broadcast-like 1080i25 MPEG-TS the way #452's measurement did (2026-10-21), whose parallel run fell back
# to reading sequentially: "chunk 1: frame 9000 at 359.968 s, expected 359.928 s".
#
#   tools/make-interlaced-ts.sh [out.ts] [scene seconds (20)] [copies (6)] [join seconds (the base's audio length)]
#
# 1. A base: six lavfi scenes (testsrc2, smptehdbars, zoneplate, cellauto, gradients, life) of <scene seconds> each,
#    made at 1080p50 with temporal noise and woven to 1080i25 top field first, as mpeg2video 4:2:2 50 Mb/s in MXF with
#    PCM, then re-encoded to MPEG-TS: mpeg2video 4:2:0 ~18 Mb/s, interlaced DCT, GOP 12 with 2 B-frames, MP2 256k.
# 2. <copies> copies of the base joined by ffmpeg's concat demuxer with -c copy (6 x 2 min = 12 min, about 1.6 GB),
#    each copy starting <join seconds> after the previous. The original's joins were 0.024 s shorter than the video
#    (frame 9000, three joins in, sat at 359.928 s, not 360): the audio's length, 119.976 s for a 120 s base. With that,
#    each copy's first frame (a keyframe) comes 0.016 s after the previous copy's last frame, under half a frame at
#    25 fps, and a seek for that last frame lands on the keyframe. The concat demuxer's own choice here (the format
#    duration, 120.010 s with FFmpeg 8.1) leaves 0.05 s between the copies, and nothing falls back; neither does a
#    -stream_loop copy nor one 12-minute encode, which have no joins.
#
# The fallback needs a chunk to start right after a join: 2 chunks (4 CPUs) meet at 9000 with 6 copies; any even
# number of copies, or `shotdetect --chunks <copies>`, puts a boundary there. Neither the interlacing nor the audio
# matters: two copies of a 2 s 320x240 progressive mpeg2video TS joined 0.024 s short do the same (the tests'
# RangeTests.JoinedTransportStream).
#
# FFMPEG (default: ffmpeg on PATH) runs everything; the MXF is deleted afterwards. 2-minute base: about 4 minutes on
# 4 CPUs, 0.8 GB of MXF on the way.
set -euo pipefail
out=${1:-interlaced.ts}
scene=${2:-20}
copies=${3:-6}
ffmpeg=${FFMPEG:-ffmpeg}
ffprobe=${FFPROBE:-$(dirname "$(command -v "$ffmpeg")")/ffprobe}
dir=$(dirname "$out")
base="$dir/$(basename "$out" .ts)-base"
length=$((6 * scene))
size=1920x1080

# The rate control's underflow warnings (the noise is costly at a fixed rate) say nothing about the result.
encode() { "$ffmpeg" -hide_banner -y -loglevel error "$@" 2> >(grep -v -e "rc buffer underflow" -e "max bitrate possibly too small" >&2); }

encode \
  -f lavfi -i "testsrc2=s=$size:r=50:d=$scene" \
  -f lavfi -i "smptehdbars=s=$size:r=50:d=$scene,hue=H=t" \
  -f lavfi -i "zoneplate=s=$size:r=50:d=$scene:kt2=2:ky2=1:kx2=1" \
  -f lavfi -i "cellauto=s=$size:r=50:rule=110,trim=duration=$scene" \
  -f lavfi -i "gradients=s=$size:r=50:d=$scene:speed=0.05" \
  -f lavfi -i "life=s=$size:r=50:mold=10:ratio=0.2:death_color=#C83232:life_color=#00ff00,trim=duration=$scene" \
  -f lavfi -i "sine=f=440:r=48000:d=$length" -f lavfi -i "anoisesrc=r=48000:a=0.1:c=pink:d=$length" \
  -filter_complex "[0][1][2][3][4][5]concat=n=6:v=1:a=0,format=yuv422p,noise=alls=12:allf=t,tinterlace=mode=interleave_top,setfield=tff[v];[6][7]amix=inputs=2,aformat=channel_layouts=stereo[a]" \
  -map "[v]" -map "[a]" -c:v mpeg2video -pix_fmt yuv422p -flags +ildct+ilme -top 1 -b:v 50M -maxrate 50M -minrate 50M -bufsize 17M -g 12 -bf 2 \
  -c:a pcm_s24le -f mxf "$base.mxf"
encode -i "$base.mxf" -map 0 \
  -c:v mpeg2video -pix_fmt yuv420p -flags +ildct+ilme -top 1 -b:v 20M -maxrate 25M -bufsize 10M -g 12 -bf 2 \
  -c:a mp2 -b:a 256k -f mpegts "$base.ts"
rm "$base.mxf"

join=${4:-$("$ffprobe" -v error -select_streams a -show_entries stream=duration -of csv=p=0 "$base.ts")}
join=${join%%$'\n'*} # the program's copy of the stream follows
list="$base.txt"
: > "$list"
for ((i = 0; i < copies; i++)); do
  printf "file '%s'\nduration %s\n" "$(basename "$base.ts")" "$join" >> "$list"
done
"$ffmpeg" -hide_banner -y -loglevel error -f concat -safe 0 -i "$list" -map 0 -c copy -f mpegts "$out"
rm "$list" "$base.ts"
echo "$out: $copies x $length s, joins every $join s"
