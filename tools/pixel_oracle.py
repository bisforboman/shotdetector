"""Writes the cases PixelTests.SameAsOpenCv checks: generated images through cv2 and scenedetect's pixel operations,
each with the result's SHA-256 (first 16 hex digits) or, for scores, the double's 64-bit pattern:

  python tools/pixel_oracle.py tests/ShotDetector.Tests/Data/pixels.txt

Rerun it (and commit the file) when the opencv-python or scenedetect pin moves or the cases below change. A fixed
seed, so the same versions give the same file. One case per line, question TAB answer:

  L SW SH DW DH IMG       cv2.resize(BGR, (DW, DH)), INTER_LINEAR (scenedetect's downscale)
  A SW SH DW DH IMG       cv2.resize(gray, (DW, DH), interpolation=INTER_AREA) (detect-hash)
  H W H IMG               cv2.cvtColor(BGR, COLOR_BGR2HSV), as the planes H, S, V one after the other
  G W H IMG               cv2.cvtColor(BGR, COLOR_BGR2GRAY) (detect-hash)
  E W H IMG               ContentDetector._detect_edges(gray): Canny from the median, dilated (kernel from the size)
  S W H IMG IMG           ContentDetector._calculate_frame_score of the second image after the first, all weights 1

IMG is how the pixels are made, the same in PixelTests: n SEED (noise), p SEED (only 0, 1, 127, 128, 254, 255),
g A B K (byte (x, y, c) = x*A + y*B + c*K, a gradient wrapping at 256) or f V0 V1 V2 (flat; a gray image takes V0).
Noise and palette bytes come from splitmix64 by index: byte i is mix(SEED + (i + 1) * 0x9E3779B97F4A7C15).
"""
import hashlib
import random
import struct
import sys

import cv2
import numpy as np
from scenedetect.detectors.content_detector import ContentDetector

GOLDEN = np.uint64(0x9E3779B97F4A7C15)
PALETTE = np.array([0, 1, 127, 128, 254, 255], np.uint8)
R = random.Random(20261013)


def mix(z):
    with np.errstate(over='ignore'):
        z = (z ^ (z >> np.uint64(30))) * np.uint64(0xBF58476D1CE4E5B9)
        z = (z ^ (z >> np.uint64(27))) * np.uint64(0x94D049BB133111EB)
        return z ^ (z >> np.uint64(31))


def image(spec, w, h, c):
    kind, *args = spec
    n = w * h * c
    if kind in 'np':
        with np.errstate(over='ignore'):
            v = mix(np.uint64(args[0]) + (np.arange(1, n + 1, dtype=np.uint64) * GOLDEN))
        flat = (v & np.uint64(0xFF)).astype(np.uint8) if kind == 'n' else PALETTE[(v % np.uint64(6)).astype(np.intp)]
    elif kind == 'g':
        a, b, k = args
        y, x, ch = np.meshgrid(np.arange(h), np.arange(w), np.arange(c), indexing='ij')
        flat = ((x * a + y * b + ch * k) & 0xFF).astype(np.uint8).ravel()
    else:
        flat = np.tile(np.array(args[:c], np.uint8), w * h)
    return flat.reshape((h, w, c)) if c == 3 else flat.reshape((h, w))


def spec():
    kind = R.choice('nnpgf')
    if kind in 'np':
        return (kind, R.getrandbits(64))
    if kind == 'g':
        return ('g', R.randrange(9), R.randrange(9), R.randrange(97))
    return ('f', R.randrange(256), R.randrange(256), R.randrange(256))


def text(s):
    return ' '.join(str(v) for v in s)


def size(big=0.05):
    if R.random() < big:
        return R.randint(100, 720), R.randint(60, 480)
    return R.randint(1, 80), R.randint(1, 80)


def digest(*arrays):
    h = hashlib.sha256()
    for a in arrays:
        h.update(np.ascontiguousarray(a).tobytes())
    return h.hexdigest()[:16]


def bits(x):
    return struct.unpack('<Q', struct.pack('<d', float(x)))[0]


def main(path):
    out = []
    for _ in range(400):
        sw, sh = size()
        dw, dh = R.randint(1, sw), R.randint(1, sh)
        s = spec()
        out.append((f'L {sw} {sh} {dw} {dh} {text(s)}',
                    digest(cv2.resize(image(s, sw, sh, 3), (dw, dh), interpolation=cv2.INTER_LINEAR))))
    for _ in range(300):
        sw, sh = size()
        dw, dh = R.randint(1, sw), R.randint(1, sh)
        s = spec()
        out.append((f'A {sw} {sh} {dw} {dh} {text(s)}',
                    digest(cv2.resize(image(s, sw, sh, 1), (dw, dh), interpolation=cv2.INTER_AREA))))
    for _ in range(300):
        w, h = size()
        s = spec()
        out.append((f'H {w} {h} {text(s)}', digest(*cv2.split(cv2.cvtColor(image(s, w, h, 3), cv2.COLOR_BGR2HSV)))))
    for _ in range(200):
        w, h = size()
        s = spec()
        out.append((f'G {w} {h} {text(s)}', digest(cv2.cvtColor(image(s, w, h, 3), cv2.COLOR_BGR2GRAY))))
    for _ in range(300):
        w, h = size(0.1)
        s = spec()
        out.append((f'E {w} {h} {text(s)}', digest(ContentDetector()._detect_edges(image(s, w, h, 1)))))
    for _ in range(300):
        w, h = size(0.1)
        s1, s2 = spec(), spec()
        det = ContentDetector(weights=ContentDetector.Components(1.0, 1.0, 1.0, 1.0))
        det._calculate_frame_score(None, image(s1, w, h, 3))
        out.append((f'S {w} {h} {text(s1)} {text(s2)}', str(bits(det._calculate_frame_score(None, image(s2, w, h, 3))))))
    with open(path, 'w', encoding='utf-8', newline='\n') as f:
        f.writelines(f'{q}\t{a}\n' for q, a in out)


if __name__ == '__main__':
    main(sys.argv[1])
