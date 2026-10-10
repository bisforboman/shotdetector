# FrameReader API review (2026-10-10)

Before FrameReader becomes a package of its own (backlog: "FrameReader as its own NuGet package"), a look at its
public API as a whole. It grew a piece at a time: frames, seeking, fps, audio, spectral stats, filter graphs,
waveforms, the BGR converter, the reference tone, and now writers and remux. Nothing here is under semantic
versioning yet (FrameReader ships inside ShotDetector's package), so changes are cheap now and expensive after the
first package. ShotDetector is the only known caller.

## What's good and stays

- **One shape for streaming results:** `TryRead(out X)` pull loops returning a `ref struct` valid until the next
  read (`VideoFrame`, `AudioChunk`, `SpectralWindow`), with readers that don't own their input (`AudioFilter`,
  `SpectralStats` over an `AudioReader`). Allocation-free, and the same everywhere.
- **Options as `init`-only records with nulls for "ffmpeg's default"**, and writers whose output equals ffmpeg's.
- **Errors as one exception type with a reason** (`FrameReaderException.Reason`).

## Proposed changes

### 1. Load the libraries in one place

Six options records carry a `LibraryDirectory` (`AudioReaderOptions`, `AudioWriterOptions`, `FrameDecoderOptions`,
`ProbeOptions`, `RemuxOptions`, `VideoWriterOptions`), but the libraries load once per process: only the first call's
folder counts, and later ones are silently ignored.

- **Change:** remove `LibraryDirectory` from the options; `FFmpegLibraries.Load(directory)` (or the default search
  next to the app) is the one way. Readers and writers load with the default when nothing was loaded yet.
- **Breaking:** yes, a property removed. ShotDetector passes its `FfmpegDirectory` through one call instead.

### 2. Consistent names for "the format"

`Format` means three things: the pixel layout in `FrameReaderOptions.Format` and `VideoFrame.Format`, the muxer in
`AudioWriterOptions.Format`, `RemuxOptions.Format` and `VideoWriterOptions.Format`. `VideoWriterOptions` has both
(`FrameFormat` and `Format`).

- **Change:** the pixel layout is `FrameFormat` everywhere (`FrameReaderOptions.FrameFormat`, `VideoFrame.FrameFormat`);
  the muxer is `Container` everywhere.
- Also: `FrameReaderOptions` becomes **`VideoFrameReaderOptions`** (it configures `VideoFrameReader`; every other
  reader's options carry their class's name).
- **Breaking:** renames only.

### 3. Time in one representation

Times come as `TimeSpan` in the high-level types (`VideoFrame.Time`, `AudioChunk.Time`, `Seek`), but elsewhere as raw
numbers: `FrameDecoder.TimeBase` and `VideoFrameReader.TimeBase` as `double`; `MediaInfo.DurationMicroseconds` as
`long`; `StreamInfo.DurationPts` with `TimeBase` as `Rational`.

- **Change:** every time base is a `Rational` (no `double`); every duration also as a `TimeSpan?` (`MediaInfo.Duration`,
  `StreamInfo.Duration`, `VideoStreamInfo.Duration`), keeping the pts values for exact work.
- **Breaking:** `TimeBase` changes type; `DurationMicroseconds` becomes `Duration`.

### 4. Errors that say what failed

`FrameReaderError` has `LibrariesNotFound`, `InvalidInput` and `DecodeFailed`, but the writers and remux report their
failures as `DecodeFailed`, and an unknown encoder option is `InvalidInput` while a bad size is an
`ArgumentException`.

- **Change:** add **`WriteFailed`** (encoding, muxing, the file); the rule written down: what the caller passed is an
  `ArgumentException` (sizes, rates, unknown options or encoder names); what the media or the libraries do is a
  `FrameReaderException`.
- `FFmpegLibraries.ErrorMessage(int)` (a raw FFmpeg error code) becomes internal: nothing public hands out such codes.
- **Breaking:** an enum value added, one exception type changed for unknown options, one method hidden.

### 5. Cancellation where work is long

Only `ReferenceTone.Find` takes a `CancellationToken`. `WaveformData.Read`, `Remux.Copy` and `VideoFrameReader.ReadAt`
can run for minutes on a film, with no way to stop them.

- **Change:** an optional `CancellationToken` on those three (the pull loops don't need one: the caller stops reading).
- **Breaking:** no (optional parameter).

### 6. The low-level layer, marked as such

`FrameDecoder` (decoded frames as FFmpeg holds them: `GetPlane`, `Pts`, `FramePts`, `FramePacketDts`, `StartOffset`,
`InputError`), `BgrConverter` and `IRowDeinterlacer` exist for ShotDetector's exact pipelines. They're useful, but
they're the part most likely to change.

- **Change:** keep them public, documented as the advanced layer in the README (and in their summaries), so the main
  API is `VideoFrameReader`, the audio readers, the writers, `Remux` and `MediaProbe`.
- **Breaking:** no.

### 7. Small ones

- `MediaInfo.HasAudio` duplicates `Streams` (keep it as a shortcut; documented as such).
- `AudioChunk.Length` is per channel: rename to **`SampleCount`**, documented "per channel".
- `WaveformData.Save(Stream, int bits)`: bits as an enum (`WaveformBits.Eight`, `Sixteen`) rather than a checked `int`.
- `VideoWriterOptions`' x264-named options (`Crf`, `Preset`, `X264Params`, `ForcedIdr`): keep, documented as libx264's
  (other encoders take `EncoderOptions`).

## Not proposed

- **Async APIs** (`IAsyncEnumerable` frames): the pull loops with `ref struct` results are the point (no allocation per
  frame), and async can't hand out a `Span`.
- **A different package name:** FrameReader now writes too, but the name is known and the reading is still the core.
  To decide with the package itself.

## Decided (2026-10-10)

The user picked changes 1 to 7, all made; the package itself comes later. In this order, each with its tests:
the libraries load in one place; `FrameFormat`/`Container`/`VideoFrameReaderOptions`; `Rational` time bases and
`TimeSpan` durations; `WriteFailed` and the argument rule, `ErrorMessage` internal; cancellation on `Remux.Copy`,
`WaveformData.Read`, `ReadAt`; the advanced layer marked; `SampleCount`, `WaveformBits`, the x264 options documented.

## Then

With the changes the user picks: ShotDetector updated to them, the FrameReader README and docs, and a prerelease
`FrameReader` package (`docs/frame-reader-library.md`'s last milestone), under semantic versioning from its first
stable version.
