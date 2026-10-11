"""Debug only (debug/tenbit): where OpenCV's BGR for 10-bit video differs from ffmpeg's, and the YUV there.
Usage: python tools/tenbit_probe.py clip.mp4 width height"""
import subprocess
import sys

import cv2
import numpy as np

path, w, h = sys.argv[1], int(sys.argv[2]), int(sys.argv[3])
print(cv2.getBuildInformation().split("Video I/O:")[1].split("Parallel framework")[0])


def ffmpeg(*args):
    return subprocess.run(["ffmpeg", "-v", "error", "-i", path, *args, "-f", "rawvideo", "-"], check=True,
                          capture_output=True).stdout


cap = cv2.VideoCapture(path)
cv = []
while True:
    ok, f = cap.read()
    if not ok:
        break
    cv.append(f)
cv = np.array(cv)
n = len(cv)
print("frames", n, cv.shape)


def bgr(*flags):
    return np.frombuffer(ffmpeg(*flags, "-pix_fmt", "bgr24"), np.uint8).reshape(-1, h, w, 3)[:n]


yuv = np.frombuffer(ffmpeg("-pix_fmt", "yuv420p10le"), np.uint16).reshape(-1, h * w * 3 // 2)[:n]
Y = yuv[:, :h * w].reshape(n, h, w).astype(np.int64)
U = yuv[:, h * w:h * w * 5 // 4].reshape(n, h // 2, w // 2).astype(np.int64)
V = yuv[:, h * w * 5 // 4:].reshape(n, h // 2, w // 2).astype(np.int64)

for label, flags in [("default", []), ("bicubic", ["-sws_flags", "bicubic"]),
                     ("bicubic+accurate_rnd", ["-sws_flags", "bicubic+accurate_rnd"]),
                     ("bicubic+full_chroma_int", ["-sws_flags", "bicubic+full_chroma_int"]),
                     ("bicubic+accurate_rnd+full_chroma_int", ["-sws_flags", "bicubic+accurate_rnd+full_chroma_int"]),
                     ("bilinear", ["-sws_flags", "bilinear"]), ("cpuflags0", ["-cpuflags", "0"]),
                     ("cpuflags0 bicubic+accurate_rnd", ["-cpuflags", "0", "-sws_flags", "bicubic+accurate_rnd"])]:
    ff = bgr(*flags).astype(np.int64)
    d = cv.astype(np.int64) - ff
    print(f"{label:40s} differing B/G/R: {[int((d[..., c] != 0).sum()) for c in range(3)]} "
          f"of {d[..., 0].size}, values {sorted(set(np.unique(d).tolist()))}")

# Detail for the default: per differing pixel, its Y and its chroma sample's U and V (nearest, top-left).
ff = bgr().astype(np.int64)
d = cv.astype(np.int64) - ff
idx = np.argwhere((d != 0).any(axis=-1))
print("examples (frame, y, x: Y U V | ffmpeg BGR | opencv BGR):")
for k, (f, y, x) in enumerate(idx[:: max(1, len(idx) // 40)][:40]):
    print(f, y, x, ":", Y[f, y, x], U[f, y // 2, x // 2], V[f, y // 2, x // 2], "|", ff[f, y, x].tolist(), "|",
          cv[f, y, x].tolist())
# Is the difference a function of (Y, U, V) at the pixel, or of position (chroma interpolation)?
if len(idx):
    flat = (d != 0).any(axis=-1)
    even = flat[:, 0::2, 0::2].mean(); odd = flat[:, 1::2, 1::2].mean(); mix = flat[:, 0::2, 1::2].mean()
    print(f"differing fraction at chroma-sited (even,even) {even:.4f}, (odd,odd) {odd:.4f}, (even,odd) {mix:.4f}")
np.savez_compressed("tenbit.npz", cv=cv, ff=ff, Y=Y, U=U, V=V)
