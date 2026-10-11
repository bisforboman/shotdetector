"""The Python side of tests/ShotDetector.Tests/PropertyTests.cs: answers one case per line on stdin with what
scenedetect 0.7.1's FrameTimecode or Python's repr(float) gives. Doubles go both ways as their 64-bit patterns.

  t KIND VALUE TB_DEN NUM DEN FRAMES   frame_num, seconds, get_timecode(), f"{seconds:.3f}", then the same three of + FRAMES
  m TIME TIME                          frame_num, seconds, get_timecode() of TIME - TIME
  c TIME TIME                          sign of the comparison, ==
  r BITS                               repr(float)

TIME is KIND VALUE TB_DEN NUM DEN; KIND is f (frame number), p (pts in units of 1/TB_DEN s) or s (seconds, VALUE a
double's bits). An exception answers "! Name".
"""
import struct
import sys
from fractions import Fraction

from scenedetect.common import FrameTimecode, Timecode


def double(bits):
    return struct.unpack("<d", struct.pack("<q", int(bits)))[0]


def bits(x):
    return struct.unpack("<q", struct.pack("<d", x))[0]


def frame_time(kind, value, tb_den, num, den):
    fps = Fraction(int(num), int(den))
    if kind == "f":
        return FrameTimecode(int(value), fps)
    if kind == "s":
        return FrameTimecode(double(value), fps)
    return FrameTimecode(Timecode(pts=int(value), time_base=Fraction(1, int(tb_den))), fps)


def describe(t):
    return f"{t.frame_num} {bits(t.seconds)} {t.get_timecode()}"


def answer(op, *a):
    if op == "r":
        return repr(double(a[0]))
    if op == "t":
        t = frame_time(*a[:5])
        return f"{describe(t)} {t.seconds:.3f} {describe(t + int(a[5]))}"
    x, y = frame_time(*a[:5]), frame_time(*a[5:])
    if op == "m":
        return describe(x - y)
    return f"{(x > y) - (x < y)} {x == y}"


for line in sys.stdin:
    try:
        print(answer(*line.split()), flush=True)
    except Exception as e:
        print("!", type(e).__name__, flush=True)
