"""Damaged variants of the Sintel trailer in samples/broken/ (run tools/make-samples.ps1 first), for checking
what ShotDetector and scenedetect make of broken files:  python tools/compare.py samples/broken/* --detector all"""
import pathlib, subprocess

ROOT = pathlib.Path(__file__).resolve().parent.parent
SRC = ROOT / "samples" / "sintel_trailer-480p.mp4"
OUT = ROOT / "samples" / "broken"
OUT.mkdir(exist_ok=True)


def ff(*args):
    subprocess.run(["ffmpeg", "-v", "error", "-y", *args], check=True)


# A clean 30 s base as faststart mp4 and as MPEG-TS.
base = OUT / "base.mp4"
ff("-i", str(SRC), "-an", "-t", "30", "-c:v", "libx264", "-crf", "20", "-g", "48", "-movflags", "+faststart", str(base))
ts = OUT / "base.ts"
ff("-i", str(base), "-c", "copy", str(ts))

# 1. Corrupted bytes: 20 runs of 2 KB garbage spread over the media data (headers left alone).
data = bytearray(base.read_bytes())
start = len(data) // 10
for k in range(20):
    pos = start + k * (len(data) - start) // 21
    data[pos:pos + 2048] = bytes((i * 37 + k) % 256 for i in range(2048))
(OUT / "corrupt.mp4").write_bytes(bytes(data))

# The same damage in a TS (a broadcast-style stream).
data = bytearray(ts.read_bytes())
start = len(data) // 10
for k in range(20):
    pos = start + k * (len(data) - start) // 21
    data[pos:pos + 2048] = bytes((i * 37 + k) % 256 for i in range(2048))
(OUT / "corrupt.ts").write_bytes(bytes(data))

# 2./3. Truncated at 70%: an interrupted download or recording.
(OUT / "truncated.mp4").write_bytes(base.read_bytes()[: len(base.read_bytes()) * 7 // 10])
(OUT / "truncated.ts").write_bytes(ts.read_bytes()[: len(ts.read_bytes()) * 7 // 10])

# 4. Duplicate timestamps: every 10th frame repeats the previous pts (mkv keeps them as written).
ff("-i", str(base), "-an", "-vf", "settb=1/1000,setpts='if(eq(mod(N,10),9),(N-1)*40,N*40)'", "-fps_mode", "passthrough",
   "-enc_time_base", "1/1000", "-c:v", "libx264", "-crf", "20", str(OUT / "dup_pts.mkv"))

# 5. Resolution change mid-stream: 15 s at 854x480 then 15 s at 640x360, concatenated as TS.
a, b = OUT / "part_a.ts", OUT / "part_b.ts"
ff("-i", str(base), "-t", "15", "-c:v", "libx264", "-crf", "20", "-bsf:v", "h264_mp4toannexb", str(a))
ff("-ss", "15", "-i", str(base), "-vf", "scale=640:360", "-c:v", "libx264", "-crf", "20", "-bsf:v", "h264_mp4toannexb", str(b))
(OUT / "res_change.ts").write_bytes(a.read_bytes() + b.read_bytes())
for f in (a, b):
    f.unlink()
print("\n".join(sorted(p.name for p in OUT.iterdir())))
