# Generates samples/synthetic.mp4: 7 shots of ffmpeg test sources joined by hard cuts at
# frames 90, 180, 240, 315, 327 (a 12-frame shot, shorter than min-scene-len) and 387. 29.97 fps.
New-Item -ItemType Directory -Force "$PSScriptRoot/../samples" | Out-Null
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
  -c:v libx264 -crf 20 "$PSScriptRoot/../samples/synthetic.mp4"
