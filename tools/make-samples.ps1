# Recreates samples/ (gitignored): a synthetic clip, Blender open-movie trailers (CC-BY, ~45 MB
# total from download.blender.org) and frame-rate variants. Results: docs/verification-table.md.
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
}
finally { Pop-Location }
