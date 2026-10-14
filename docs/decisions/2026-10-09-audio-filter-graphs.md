# Audio filter graphs (2026-10-09)

Issue #81's last item. Asked the API: **an `AudioFilter` class over an `AudioReader` with TryRead (recommended)**, not
an `AudioReaderOptions.Filter`; and which filters our native build carries: **all four groups**: EQ and level
(volume, equalizer, bass, treble, highpass, lowpass, bandpass, bandreject, afade, pan), dynamics (acompressor,
alimiter, dynaudnorm, agate), loudness (ebur128, loudnorm) and detection (silencedetect, astats). Time windows are
ffmpeg's own `enable=` option.
