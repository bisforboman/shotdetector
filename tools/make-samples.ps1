# Recreates samples/ (gitignored): a synthetic clip, Blender open-movie trailers (CC-BY, ~45 MB
# total from download.blender.org) and frame-rate variants. Results: docs/verification-table.md.
# Stop at the first failing ffmpeg, rather than leaving a sample out and failing later on a missing file.
$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $true
$out = Join-Path $PSScriptRoot "../samples"
New-Item -ItemType Directory -Force $out | Out-Null
Push-Location $out
try {
    # 7 shots of ffmpeg test sources joined by hard cuts at frames 90, 180, 240, 315, 327 (a 12-frame
    # shot, shorter than min-scene-len) and 387. 29.97 fps.
    $s = "s=640x360:r=30000/1001"
    ffmpeg -v error -y `
      -f lavfi -i "testsrc2=${s}:d=3" `
      -f lavfi -i "mandelbrot=${s}:end_scale=0.001" `
      -f lavfi -i "smptehdbars=${s}:d=2" `
      -f lavfi -i "life=${s}:mold=10:ratio=0.1:death_color=#C83232:life_color=#00ff00" `
      -f lavfi -i "cellauto=${s}:rule=110" `
      -f lavfi -i "testsrc=${s}:d=0.4" `
      -f lavfi -i "rgbtestsrc=${s}:d=2" `
      -filter_complex "[1]trim=duration=3,setpts=PTS-STARTPTS,format=yuv420p[m];[3]trim=duration=2.5,setpts=PTS-STARTPTS,format=yuv420p[l];[4]trim=duration=2,setpts=PTS-STARTPTS,format=yuv420p[c];[0][m][2][l][5][c][6]concat=n=7:v=1:a=0,format=yuv420p" `
      -c:v libx264 -crf 20 synthetic.mp4

    $downloads = @{
        "sintel_trailer-480p.mp4"       = "https://download.blender.org/durian/trailer/sintel_trailer-480p.mp4"
        "sintel_trailer-1080p.mp4"      = "https://download.blender.org/durian/trailer/sintel_trailer-1080p.mp4"
        "sintel_trailer1_480p_divx.mkv" = "https://download.blender.org/durian/trailer/Sintel_Trailer1.480p.DivX_Plus_HD.mkv"
        "bbb_trailer_480p.mov"          = "https://download.blender.org/peach/trailer/trailer_480p.mov"
        "bbb_trailer_400p.ogg"          = "https://download.blender.org/peach/trailer/trailer_400p.ogg"
        "bbb_trailer_iphone.m4v"        = "https://download.blender.org/peach/trailer/trailer_iphone.m4v"
    }
    foreach ($name in $downloads.Keys) {
        if (-not (Test-Path $name)) { Invoke-WebRequest $downloads[$name] -OutFile $name -UseBasicParsing }
    }

    # Same frames at other frame rates, to exercise min-scene-len rounding and timecodes.
    foreach ($fps in "24000/1001", "30000/1001", "60") {
        $name = "sintel_retimed_" + ($fps -replace "/", "_") + ".mp4"
        ffmpeg -v error -y -i sintel_trailer-480p.mp4 -an -vf "setpts=N/($fps)/TB" -r $fps -c:v libx264 -crf 18 $name
    }

    # Variable frame rate: 24 fps for 400 frames then 48 fps, and phone-like jittery timestamps (~25 fps).
    ffmpeg -v error -y -i sintel_trailer-480p.mp4 -an -vf "settb=1/1200,setpts='if(lt(N,400),N*50,20000+(N-400)*25)'" `
      -fps_mode passthrough -enc_time_base 1/1200 -c:v libx264 -crf 18 vfr_24_48.mp4
    ffmpeg -v error -y -i sintel_trailer-480p.mp4 -an -vf "settb=1/1000,setpts='N*40+mod(N*7,13)'" `
      -fps_mode passthrough -enc_time_base 1/1000 -c:v libx264 -crf 18 vfr_jitter.mp4

    # Phone-style rotation tags (pixels stored sideways or upside down; decoders rotate them upright).
    ffmpeg -v error -y -display_rotation 90 -i sintel_trailer-480p.mp4 -c copy sintel_rotated.mp4
    ffmpeg -v error -y -display_rotation 180 -i sintel_trailer-480p.mp4 -c copy sintel_rotated180.mp4
    # Rotated where turning before or after converting differs (OpenCV turns the converted image): 10-bit, and 4:2:2 at 90.
    ffmpeg -v error -y -i sintel_trailer-480p.mp4 -an -t 20 -c:v libx264 -pix_fmt yuv420p10le -crf 18 rot_10bit.mp4
    ffmpeg -v error -y -display_rotation 90 -i rot_10bit.mp4 -c copy sintel_rotated_10bit.mp4
    ffmpeg -v error -y -i sintel_trailer-480p.mp4 -an -t 20 -c:v libx264 -pix_fmt yuv422p -crf 18 rot_422.mp4
    ffmpeg -v error -y -display_rotation -90 -i rot_422.mp4 -c copy sintel_rotated_422.mp4
    Remove-Item rot_10bit.mp4, rot_422.mp4
    # 4:2:2 unturned: FastYuv converts it as x86's swscale does, so ARM with FastYuv must equal x64 on it (arch-diff).
    ffmpeg -v error -y -i sintel_trailer-480p.mp4 -an -t 20 -c:v libx264 -pix_fmt yuv422p -crf 18 sintel_422.mp4

    # MPEG-2 in a program stream: most packets carry no pts (timestamps come from a decoding pass).
    ffmpeg -v error -y -i sintel_trailer-480p.mp4 -an -c:v mpeg2video -q:v 3 sintel_mpeg2.mpg
    # MPEG-1 in a system stream, the same way: it decodes with MPEG-2's IDCT (the C one everywhere, VideoReader.IdctOptions).
    ffmpeg -v error -y -i sintel_trailer-480p.mp4 -an -c:v mpeg1video -q:v 3 -f mpeg sintel_mpeg1.mpg

    # 10-bit h264: its conversion interpolates chroma, from where the stream says the samples sit (left for h264), as
    # OpenCV does; compared exactly. (Informational until 2026-10-11: ffmpeg's command line drops that siting.)
    ffmpeg -v error -y -i sintel_trailer-480p.mp4 -an -c:v libx264 -pix_fmt yuv420p10le -crf 18 sintel_10bit.mp4
    # ... with top-left chroma in the stream (h264 VUI) and left in Matroska's header: OpenCV converts with the frame's
    # (the bitstream's) siting, ffprobe's stream reports the container's.
    ffmpeg -v error -y -i sintel_trailer-480p.mp4 -an -t 20 -c:v libx264 -pix_fmt yuv420p10le -crf 18 `
      -chroma_sample_location left -x264-params chromaloc=2 sintel_10bit_topleft.mkv

    # MPEG-4 Part 2 (the DivX/Xvid era), from FFmpeg's encoder and from Xvid: FFmpeg's decoder takes the Xvid IDCT for
    # Xvid's (by its signature) and the platform's for the rest, which we replace with the C IDCT (Mpeg4Idct), so both.
    ffmpeg -v error -y -i sintel_trailer-480p.mp4 -an -c:v mpeg4 -q:v 3 sintel_mpeg4.avi
    ffmpeg -v error -y -i sintel_trailer-480p.mp4 -an -c:v libxvid -q:v 3 sintel_xvid.avi
    New-Item -ItemType Directory -Force informational | Out-Null
    # An odd width (853x480; VP9, as libx264 refuses odd widths in subsampled formats): x86's swscale converts it as even
    # widths. Reported on ARM until its first table is read (docs/decisions/2026-10-18-fastyuv-422.md).
    ffmpeg -v error -y -i sintel_trailer-480p.mp4 -an -t 20 -vf scale=853:480 -c:v libvpx-vp9 -crf 32 -b:v 0 -row-mt 1 `
      -cpu-used 4 informational/sintel_oddwidth.mkv
    # Colour tags that change midway: two h264 halves joined, each SPS with its own (a spliced or concatenated stream).
    # OpenCV converts every frame with its own, and so does in-process decoding and FastYuv through the executable
    # (docs/decisions/2026-10-24-per-frame-tags-fastyuv.md, 2026-10-27-per-frame-tags-executable.md); through the
    # executable, the default path takes the first frame's chroma siting for the whole video
    # (docs/decisions/2026-10-19-per-frame-tags.md): its stats differ after the change, cuts don't. 8-bit with the
    # matrix changing (BT.601 to BT.709), 10-bit with the siting (left to top left).
    foreach ($clip in @(
            @{ Name = "sintel_matrix_change.mkv"; Format = "yuv420p"; First = "colormatrix=bt470bg"; Second = "colormatrix=bt709" },
            @{ Name = "sintel_10bit_siting_change.mkv"; Format = "yuv420p10le"; First = "chromaloc=0"; Second = "chromaloc=2" })) {
        ffmpeg -v error -y -i sintel_trailer-480p.mp4 -an -t 10 -c:v libx264 -pix_fmt $clip.Format -crf 18 `
          -x264-params $clip.First -f mpegts tags_first.ts
        ffmpeg -v error -y -ss 10 -i sintel_trailer-480p.mp4 -an -t 10 -c:v libx264 -pix_fmt $clip.Format -crf 18 `
          -x264-params $clip.Second -output_ts_offset 10 -f mpegts tags_second.ts
        ffmpeg -v error -y -i "concat:tags_first.ts|tags_second.ts" -c copy (Join-Path informational $clip.Name)
        Remove-Item tags_first.ts, tags_second.ts
    }

    # Interlaced broadcast video (issue #42): 10 s of 1080i25, top field first, made from the 1080p trailer
    # (50p woven into fields). XDCAM HD (MPEG-2 4:2:2, 50 Mbit/s, in MXF) is compared exactly (samples/broadcast/);
    # AVC-Intra 100 and ProRes 422 are informational (deinterlacing off: OpenCV can't convert interlaced 10-bit frames).
    New-Item -ItemType Directory -Force broadcast | Out-Null
    # The interlace filter marks the frames top field first (FFmpeg 9 dropped the -top encoder option).
    $i25 = "fps=50,scale=1920:1080,setsar=1,interlace=scan=tff"
    ffmpeg -v error -y -i sintel_trailer-1080p.mp4 -an -t 10 -vf "$i25,format=yuv422p" -c:v mpeg2video -b:v 50M `
      -minrate 50M -maxrate 50M -bufsize 17825792 -g 12 -flags +ildct+ilme -f mxf broadcast/xdcam_hd422_1080i25.mxf
    ffmpeg -v error -y -i sintel_trailer-1080p.mp4 -an -t 10 -vf "$i25,format=yuv422p10le" -c:v libx264 `
      -avcintra-class 100 -flags +ildct+ilme -f mxf informational/avcintra100_1080i25.mxf
    ffmpeg -v error -y -i sintel_trailer-1080p.mp4 -an -t 10 -vf "$i25,format=yuv422p10le" -c:v prores_ks -profile:v 2 `
      -flags +ildct informational/prores422_1080i25.mov

    # Colour handling: full-range yuv420p (tagged "pc"), full-range MJPEG (yuvj420p) and 4:4:4.
    ffmpeg -v error -y -i sintel_trailer-480p.mp4 -an -vf "scale=out_range=full" -color_range pc -c:v libx264 -crf 18 -pix_fmt yuv420p sintel_fullrange.mp4
    ffmpeg -v error -y -i sintel_trailer-480p.mp4 -an -c:v mjpeg -q:v 4 -pix_fmt yuvj420p sintel_mjpeg_yuvj420p.avi
    ffmpeg -v error -y -i sintel_trailer-480p.mp4 -an -c:v libx264 -crf 18 -pix_fmt yuv444p sintel_yuv444p.mp4
}
finally { Pop-Location }
