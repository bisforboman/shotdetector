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

    # MPEG-2 in a program stream: most packets carry no pts (timestamps come from a decoding pass).
    ffmpeg -v error -y -i sintel_trailer-480p.mp4 -an -c:v mpeg2video -q:v 3 sintel_mpeg2.mpg

    # Known differences, compared informationally (samples/informational/): 10-bit h264. Its conversion
    # to 8-bit BGR differs by +-1 in red and green on some pixels between OpenCV's bundled FFmpeg on
    # Linux and the ffmpeg CLI (any version, flags or dithering); cuts stay the same. See README.
    New-Item -ItemType Directory -Force informational | Out-Null
    ffmpeg -v error -y -i sintel_trailer-480p.mp4 -an -c:v libx264 -pix_fmt yuv420p10le -crf 18 informational/sintel_10bit.mp4

    # Interlaced broadcast video (issue #42): 10 s of 1080i25, top field first, made from the 1080p trailer
    # (50p woven into fields). XDCAM HD (MPEG-2 4:2:2, 50 Mbit/s, in MXF) is compared exactly (samples/broadcast/);
    # AVC-Intra 100 and ProRes 422 are 10-bit, so informational like the 10-bit h264 above.
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
