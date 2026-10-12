"""Writes the cases PropertyTests.SameAsScenedetect checks, each with scenedetect's answer (0.7.1 and 0.7.2 give the
same file) or Python's:

  python tools/oracle.py tests/ShotDetector.Tests/Data/oracle.txt

Rerun it (and commit the file) when scenedetect's pinned version moves or the cases below change. A fixed seed, so
the same scenedetect gives the same file. One case per line, question TAB answer:

  t TIME FRAMES   frame_num, seconds, get_timecode(), f"{seconds:.3f}", then the same three of TIME + FRAMES
  m TIME TIME     frame_num, seconds, get_timecode() of TIME - TIME
  c TIME TIME     sign of the comparison, ==
  r BITS          repr(float)

TIME is KIND VALUE TB_DEN NUM DEN: f (frame number, TB_DEN 0), p (pts in units of 1/TB_DEN s) or s (seconds, TB_DEN
-2), as FrameTime holds them. Doubles are written as their 64-bit patterns. An exception answers "! Name".
"""
import math
import random
import struct
import sys
from fractions import Fraction

from scenedetect.common import FrameTimecode, Timecode

R = random.Random(20261011)
RATES = [Fraction(n, d) for n, d in [(24, 1), (25, 1), (30, 1), (50, 1), (60, 1), (1, 1), (24000, 1001), (30000, 1001),
                                     (60000, 1001), (15, 2)]]
TIME_BASES = [1000, 90000, 12800, 15360, 30000, 24000, 1_000_000, 25, 2, 16, 1024]  # 16, 1024: exact ms ties


def double(bits):
    return struct.unpack("<d", struct.pack("<q", int(bits)))[0]


def bits(x):
    return struct.unpack("<q", struct.pack("<d", x))[0]


def near(x):
    """x, or a double up to 2 ulps either side."""
    k = R.randint(-2, 2)
    for _ in range(abs(k)):
        x = math.nextafter(x, math.copysign(math.inf, k))
    return x


def rate():
    return R.choice(RATES) if R.random() < 0.5 else Fraction(R.randint(1, 240_000), R.randint(1, 1001))


def time():
    fps, tb = rate(), R.choice(TIME_BASES) if R.random() < 0.5 else R.randint(1, 1_000_000)
    n = R.randint(0, 3_600_000) if R.random() < 0.5 else R.choice([59_999, 3_599_999, 86_399_999])  # minute/hour rollover
    wiggle = R.randint(-2, 2)
    kind = R.randint(0, 5)
    if kind == 0:
        return ["f", n, 0, fps]
    if kind == 1:
        return ["p", R.randint(0, 2**31 - 1), tb, fps]
    if kind == 2:  # half a frame
        return ["p", max(0, (2 * n + 1) * tb * fps.denominator // (2 * fps.numerator) + wiggle), tb, fps]
    if kind == 3:  # half a millisecond
        return ["p", max(0, ((2 * n + 1) * tb + 1000) // 2000 + wiggle), tb, fps]
    secs = (n + 0.5) / float(fps) if kind == 4 else (n + 0.5) / 1000
    return ["s", bits(max(0.0, near(secs))), -2, fps]


def pair(op):
    """Two times at one rate (FrameTimecode subtracts only those), or for comparisons sometimes two; b often a few
    ticks from a, in half a's time base: equal or nearly."""
    a, b = time(), time()
    if a[0] == "p" and R.random() < 0.5:
        b = ["p", max(0, a[1] + R.randint(-2, 2)) * 2, a[2] * 2, b[3]]
    if op == "m" or R.random() < 2 / 3:
        b[3] = a[3]
    return a, b


def a_double():
    kind = R.randint(0, 4)
    if kind == 0:  # any bit pattern
        while not math.isfinite(d := double(R.randint(-(2**63), 2**63 - 1))):
            pass
        return d
    if kind == 1:  # subnormals
        return double(R.randint(0, 2**31 - 1))
    if kind == 2:  # around the switches to exponent notation (1e-05, 1e+16)
        return near(float(f"{R.randint(-99_999, 99_999)}e{R.randint(-10, 22)}"))
    if kind == 3:  # a stats file's usual values
        return R.randint(0, 1_000_000) / 1000
    return float(10 ** R.randint(0, 20))  # whole numbers


def frame_time(kind, value, tb, fps):
    if kind == "f":
        return FrameTimecode(value, fps)
    if kind == "s":
        return FrameTimecode(double(value), fps)
    return FrameTimecode(Timecode(pts=value, time_base=Fraction(1, tb)), fps)


def text(t):
    kind, value, tb, fps = t
    return f"{kind} {value} {tb} {fps.numerator} {fps.denominator}"


def describe(t):
    return f"{t.frame_num} {bits(t.seconds)} {t.get_timecode()}"


def answer(op, x, y):
    if op == "r":
        return repr(x)
    if op == "t":
        t = frame_time(*x)
        return f"{describe(t)} {t.seconds:.3f} {describe(t + y)}"
    a, b = frame_time(*x), frame_time(*y)
    if op == "m":
        return describe(a - b)
    return f"{(a > b) - (a < b)} {a == b}"


cases = ([("t", time(), R.randint(-1000, 1000)) for _ in range(1500)]
         + [("m", *pair("m")) for _ in range(1000)]
         + [("c", *pair("c")) for _ in range(1000)]
         + [("r", a_double(), None) for _ in range(1500)])
with open(sys.argv[1], "w", newline="\n") as out:
    for op, x, y in cases:
        question = (f"r {bits(x)}" if op == "r" else f"t {text(x)} {y}" if op == "t" else f"{op} {text(x)} {text(y)}")
        try:
            result = answer(op, x, y)
        except Exception as e:
            result = f"! {type(e).__name__}"
        out.write(f"{question}\t{result}\n")
