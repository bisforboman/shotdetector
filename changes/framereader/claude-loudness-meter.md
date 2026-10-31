### New

- `LoudnessMeter`: EBU R128 loudness of samples you decode yourself (interleaved float, a chunk at a time with `Add`,
  then `Finish`), with nothing of FFmpeg, so it runs in a browser too (issue #646). It gives `LoudnessMeasures`: the
  `LoudnessResult` `Loudness.Measure` gives, and each channel's `ChannelPeaks` (sample peak, true peak, samples at full
  scale). Integrated loudness, loudness range and its ends are ebur128's arithmetic (its 0.01 LU histogram included),
  so they are the same doubles as `Loudness.Measure` on the same samples, and so is the sample peak. The true peak
  follows BS.1770-4 Annex 2 (4 times oversampled below 192 kHz). On programme audio it agrees with ebur128's within
  0.1 dB. On audio with energy near Nyquist, ebur128's resampler reads up to about 0.8 dB higher (README, "Loudness
  without FFmpeg").
- `ChannelWeights`: BS.1770-4's channel weights as ebur128 takes them from a layout (`Default(channels)`,
  `ForLayout("5.1(side)")`), or your own.
