# Reference tone and waveform from decoded samples (2026-10-24)

shotdetector-web decodes audio in the browser with ManagedCodecs' `AacDecoder`, with no FFmpeg there, and wants
FrameReader's reference tone and waveform from those samples: the same definition and code ShotDetector's
`FindReferenceTone` uses, not a copy (issue #494). `ReferenceTone.Find` and `WaveformData.Read` only read an
`AudioReader`, which is FFmpeg's.

## Question

What shape should the entries that take decoded samples have?

## Choices

1. **Push accumulators**: `ReferenceToneFinder` and `WaveformBuilder`, each built with the sample rate and channels,
   fed with `Add(ReadOnlySpan<float> interleaved, TimeSpan time)`, ended with `Finish()`. `Find` and `Read` become
   loops over them.
2. **Pull overloads**: `Find`/`Read` taking `IEnumerable<(float[] Samples, TimeSpan Time)>`, plus `IAsyncEnumerable`
   versions.
3. **Both.**

## Answer

**1. The accumulators** (the owner, 2026-10-24): the caller owns the loop, which suits a single-threaded browser
feeding one decoded packet at a time, and one code path serves both. Details settled while building them:

- **API:** `new ReferenceToneFinder(int sampleRate, int channels, ReferenceToneOptions? options = null, TimeSpan?
  duration = null)`, `bool Add(ReadOnlySpan<float> interleaved, TimeSpan time)`, `bool IsComplete`,
  `IReadOnlyList<ToneSegment> Finish()`; `new WaveformBuilder(int sampleRate, int channels, WaveformOptions? options =
  null, TimeSpan? duration = null, bool integerSamples = false)`, `void Add(ReadOnlySpan<float> interleaved, TimeSpan
  time)`, `WaveformData Finish()`.
- **Integer or float source (an addition to the shape):** `Read`'s 16-bit values depend on whether the decoder gave
  integers (shifted down, as libsndfile does) or floats (scaled by 32767 and truncated, as audiowaveform does), which
  `AudioReader` knows internally. The builder takes it as `integerSamples`, false by default (AAC and other lossy
  codecs decode to float); `Read` passes the decoder's.
- **Progress:** both take an optional `duration` (the input's length when known); `Find` and `Read` pass
  `AudioReader.Duration`, so their reports are unchanged. Without it the reports carry positions only (`Duration`
  null, `Fraction` null), except the tone's, whose `Duration` is still `SearchDuration` when set (the shorter of the
  two when both are known, as before). Positions: the tone's from the first `Add`'s time, the waveform's the `time`
  given, as before.
- **Times:** the first `Add`'s `time` is where the tone's segments count from; later times only move progress, since
  positions follow from the samples counted (as `Find` always did), so packet times that jitter don't matter.
- **SearchDuration stops consuming:** the finder's `Add` returns false once the search duration has been read (and
  `IsComplete` turns true), so the caller can stop decoding; samples past it are ignored, and later `Add`s return
  false. `Find` stops reading there as before. Without a `SearchDuration` it never completes. The waveform's `Add`
  returns nothing: a waveform always wants the whole input.
- **Cancellation:** the caller's own, between its `Add` calls; there are no threads and no tokens in the accumulators.
  `Find` and `Read` keep their tokens, checked before each chunk as before.
- **Refusals:** a non-positive rate or channel count is an `ArgumentOutOfRangeException` (an `ArgumentException`);
  a span that isn't a whole number of instants an `ArgumentException`; `Add` or `Finish` after `Finish` an
  `InvalidOperationException`. The options' own checks (`SearchDuration` positive, at least 2 samples per point, at
  most 24 channels, `PixelsPerSecond` positive) are made in the constructors, as `Find` and `Read` made them before
  reading.
- **Unchanged meanings:** `PixelsPerSecond` (the rate divided by it, rounded down) and `SplitChannels` (one waveform
  per channel, or their mean truncated toward zero).
- **Memory:** the finder holds the 20 ms window in progress and the segments found; the builder the point in progress
  and the points made (the result).
- **No FFmpeg:** neither type nor its members mention FFmpeg.AutoGen's; `tests/AudioWithoutFfmpeg` runs both in a
  process of its own and fails if FFmpeg.AutoGen or any FFmpeg library gets loaded.
- **Results unchanged:** `Find` and `Read` are loops over the accumulators; on the same clips (AAC, MP3, 16-bit and
  float WAV, 32-bit FLAC) the old and new builds gave the same segments (to the tick and the level's last bit) and the
  same .dat bytes, and `AudioFromSamplesTests` checks the accumulators against them however the chunks are cut.
