"""The ideal true peak of WAV files, for LoudnessMeterTests' reference numbers (issue #646).

Each file is resampled 32 times with a long Kaiser window (scipy's resample_poly, beta 12, zeros outside the samples),
an ideal reconstruction, and its largest magnitude over all channels printed in dBTP. Make the clips with FFmpeg 8.1's
ffmpeg from the sources in tests/FrameReader.Tests/LoudnessMeterTests.cs (`-c:a pcm_f32le`), then:

    pip install scipy numpy
    python tools/true_peak_reference.py clip.wav ...
"""

import sys

import numpy as np
from scipy.io import wavfile
from scipy.signal import resample_poly


def true_peak(path: str) -> float:
    _, data = wavfile.read(path)
    x = data.astype(np.float64)
    if x.ndim == 1:
        x = x[:, None]
    if np.issubdtype(data.dtype, np.integer):
        x /= float(np.iinfo(data.dtype).max) + 1
    peak = 0.0
    for c in range(x.shape[1]):
        y = resample_poly(x[:, c], 32, 1, window=("kaiser", 12.0))
        peak = max(peak, float(np.max(np.abs(y))))
    return 20 * np.log10(peak) if peak > 0 else float("-inf")


if __name__ == "__main__":
    for p in sys.argv[1:]:
        print(f"{p}\t{true_peak(p):.3f}")
