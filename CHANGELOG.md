# Changelog

Versions are git tags (`vX.Y.Z`); each one publishes the NuGet packages and a GitHub release. From 1.0,
semantic versioning (README, "Versions"); before it, minor versions could change the public API.
What's merged but not released yet is in changes/ (one file per pull request).

## 1.5.0 – 2026-10-11

FrameReader is stable and a ShotDetector dependency; what issue #81 needs to move its tone job in-process (a
limited tone search, checks for a system FFmpeg's encoders and filters); audio from a Stream as ffmpeg reads a
pipe; and `--probe` in the CLI.

### New

- **FrameReader is stable** (1.5.0): under semantic versioning with package validation, like ShotDetector, and
  ShotDetector depends on the `FrameReader` package instead of carrying its own copy of FrameReader.dll, so its XML
  documentation (IntelliSense) comes with it (issue #81).
- CLI `--probe` and `--probe-json`: describe the input (container, duration, the video's codec, size, frame rate,
  pixel format, field order, rotation; every stream's codec, size or sample rate and layout, language, title, bit
  rate, flags) as text or JSON, then stop. The library's `ShotDetection.Probe` with `VideoInfo.Streams`, in the CLI.
- `ReferenceToneOptions.SearchDuration`, `DetectionOptions.ReferenceToneSearchDuration` and the CLI's `--tone-search`:
  search only the first minutes for reference tone, then stop decoding (issue #81).
- `FFmpegLibraries.HasEncoder`, `HasFilter` and `HasMuxer`: check a system FFmpeg build at startup; FrameReader's
  README lists what each reader and writer needs from one (issue #81).

### Fixed

- FrameReader, audio from a `Stream` (read as ffmpeg reads `-i -`): MP3 kept the encoder's end padding (a few hundred
  samples more than ffmpeg gives; the input reported no size, which the mp3 demuxer took for a concatenated file),
  and `AudioReader.Seek` on a Stream threw instead of decoding on and trimming as ffmpeg's `-ss` does on a pipe.

## 1.4.1 – 2026-10-10

VideoWriter with ShotDetector.Native.Gpl's libraries (issue #127). APIs unchanged.

### Fixed

- `VideoWriter` with ShotDetector.Native.Gpl's libraries: every writer failed with "Filter not found" (issue #127). The
  build lacked the `format`, `null` and `scale` filters its graph to the encoder's pixel format uses; CI now writes
  with that package's own libraries, and the tests' x264 check no longer took that failure for a missing libx264.

## 1.4.0 – 2026-10-10

FrameReader's first package (`FrameReader` 1.4.0-preview), its API reviewed before it, and AudioReader's seek matching
ffmpeg on Matroska/WebM. ShotDetector's own API is unchanged.

### Breaking changes (FrameReader only; it isn't under semantic versioning yet, ShotDetector's API is unchanged)

FrameReader's API before its own package (docs/frame-reader-api-review.md):
- The libraries load in one place: `FFmpegLibraries.Load(folder)`. `LibraryDirectory` is gone from every options
  record (the libraries load once per process, so only the first folder ever counted).
- Names: `FrameReaderOptions` is `VideoFrameReaderOptions`; the pixel layout is `FrameFormat` (`VideoFrameReaderOptions`,
  `VideoFrame`), the muxer `Container` (`AudioWriterOptions`, `VideoWriterOptions`, `RemuxOptions`).
- Time: `FrameDecoder.TimeBase` and `VideoFrameReader.TimeBase` are `Rational`; `MediaInfo.Duration` is a `TimeSpan?`
  (was `DurationMicroseconds`); `StreamInfo` and `VideoStreamInfo` add `Duration`.
- Errors: `FrameReaderError.WriteFailed` for the writers and `Remux`; unknown encoder options are an
  `ArgumentException`; `FFmpegLibraries.ErrorMessage` is internal.
- `AudioChunk.Length` is `SampleCount`; `WaveformData.Save` takes `WaveformBits`.

### New

- A `CancellationToken` on `Remux.Copy`, `WaveformData.Read` and `VideoFrameReader.ReadAt`.
- **FrameReader as its own NuGet package** (`FrameReader`, MIT), a preview: released with ShotDetector, its version is
  ShotDetector's with `-preview` until its API is declared stable. ShotDetector's package keeps its own copy of
  FrameReader.dll until then (no prerelease dependency for ShotDetector's users).

### Fixed

- FrameReader's `AudioReader.Seek` on Matroska/WebM with video (Opus there): its millisecond timestamps put frames up to
  a millisecond off a sample, so after a seek the samples started up to 1 ms away from ffmpeg's `-ss` (24 samples on
  Elephants Dream's webm). Timestamps now follow ffmpeg's decoder (`audio_ts_process`), and the seek takes ffmpeg's
  margin for files with B-frames. Found by the new real-world audio check (realworld.yml), which covers AAC, MP3 and Opus.

## 1.3.0 – 2026-10-10

Audio and H.264 writing and stream copy in FrameReader, the reference tone, and the GPL native packages with x264
(the same as 1.3.0-preview.1).

### New

- Reference tone (1 kHz line-up tone, "bars and tone"): `ShotDetection.FindReferenceTone(path)` finds its runs in a
  video's or an audio-only file's audio, and `DetectionOptions.TrimReferenceTone` leaves a leading and a trailing run
  out of detection (`DetectionResult.ReferenceTone` lists them); CLI `--find-tone`, `--trim-tone`,
  `--tone-min-duration`. Interrupted line-up (EBU, GLITS) is one run; beeps and music don't count. Opt-in: nothing
  changes with it off. In FrameReader: `ReferenceTone.Find(audioReader)`.
- FrameReader (inside the ShotDetector package; its API isn't under semantic versioning yet):
  - `AudioWriter`: float32 samples to MP3 (LAME), AAC or WAV, the file ffmpeg writes from the same samples.
  - `Remux.Copy`: streams copied into another container without re-encoding (`ffmpeg -map ... -c copy`), e.g. the
    video alone into an .mp4 and the audio into an .m4a.
- ShotDetector.Native and the CLI's FFmpeg libraries add the AAC, MP3 (LAME 3.100, LGPL, linked in) and 16-bit PCM
  encoders and common muxers (mp4, m4a, mkv, mka, webm, adts, mp3, wav, flac, ogg, opus, mpegts, srt, webvtt, ass),
  about 0.5 MB per platform.
- H.264 encoding, opt-in (issue #81):
  - FrameReader `VideoWriter`: frames (BGR, RGB, gray or yuv420p) in, H.264 in .mp4/.mkv/.mov out, with an optional
    AAC track; pixel format, CRF or bit rate, preset, GOP, keyframes forced every N seconds (IDR), x264 params. The
    file `ffmpeg -c:v libx264` writes from the same frames with the same FFmpeg build.
  - `ShotDetector.Native.Gpl.<rid>`: ShotDetector.Native's libraries with x264, used instead of ShotDetector.Native.
    **GPL-2.0-or-later**: an app that ships with it falls under the GPL. H.264 is covered by Via LA's AVC patents
    either way (docs/encoding-package.md).

## 1.2.0 – 2026-10-09

Waveform peaks and audio filter graphs in FrameReader (issue #81's last items), and the native libraries' filters for
them. ShotDetector's own API is unchanged.

### New

- FrameReader (inside the ShotDetector package; its API isn't under semantic versioning yet):
  - `WaveformData`: BBC audiowaveform's min/max peaks from an `AudioReader` (samples per point or points per second,
    channels mixed or split) and its binary .dat (`Save`, 8 or 16 bits), byte for byte audiowaveform's for PCM input.
  - `AudioFilter`: an ffmpeg audio filter graph over an `AudioReader`, the samples `ffmpeg -af GRAPH -f f32le` gives,
    with ffmpeg's `enable=` time windows and analysis filters' results in `Metadata`.
- ShotDetector.Native and the CLI's FFmpeg libraries carry the audio filters for it: volume, equalizer, bass, treble,
  highpass, lowpass, bandpass, bandreject, afade, pan, acompressor, alimiter, dynaudnorm, agate, ebur128, loudnorm,
  silencedetect, astats (and aformat, aresample).

## 1.1.0 – 2026-10-09

Every stream of a file or URL in `VideoInfo.Streams`, newer zlib and dav1d in the native libraries, and a
file left open after a failed in-process decode closed.

### New

- `VideoInfo.Streams` (`ShotDetection.Probe`, `DetectionResult.Video.Info`): every stream of the file (video, audio,
  subtitle, data, attachment) with its index, type, codec, codec tag (hvc1/hev1...), language and title tags,
  default and forced flags, bit rate, duration, and for audio the sample rate, channels, channel layout and sample
  format: the values ffprobe's `-show_streams` gives, read in-process or from ffprobe for URLs (issue #81).
  `HasAudio` now comes from it. `FrameRate` stays the average frame rate, as OpenCV reports it, and `Container` is
  ffprobe's full format name. For a URL both are looked up when first read (one more request); for a `Stream` input
  they stay null.

### Fixed

- ShotDetector.Native and the CLI's FFmpeg libraries link zlib 1.3.2 and dav1d 1.5.4 (were 1.3.1 and 1.5.1): their
  security and decoder fixes (issues #91, #92).
- In-process decoding kept the file open when it failed after opening it (no video stream, no decoder for the codec,
  an unsupported rotation), until the process ended: a file without video couldn't be deleted after the error.

## 1.0.0 – 2026-10-08

The first stable release: identical results to scenedetect 0.7.1, the public API frozen under semantic versioning,
and CI failing any breaking change to it. Compared with 0.10.0, a few types change shape so that adding to them later
isn't a breaking change, and the decoding internals stop being public (docs/decisions.md).

### Breaking changes

- `VideoInfo`, `DetectionResult` and `DetectionProgress` are no longer positional: the same properties (init-only),
  without the public constructors and `Deconstruct`, so new properties after 1.0 are additions. They come from
  `ShotDetection` (`with` expressions still work).
- `VideoReader` is the read-only view of a video in a result (`DetectionResult.Video`): its constructors and
  `Frames()` are internal. `Position()`, `Info` and the properties stay.
- `Stats` is read-only for callers: `Set` and the constructor are internal.

### New

- Breaking changes to the libraries' API fail the pack (the SDK's package validation), against the last stable
  release from 1.0 on (`ApiBaselineVersion` in Directory.Build.props).

## 0.10.0 – 2026-10-08

In-process for nearly everything, and faster: rotated video, image sequences, AV1 and streams now decode with
FFmpeg's libraries (only URLs and the exports still need the ffmpeg executable), deinterlacing computes only the rows
it uses, and with all CPUs in-process detection is faster than scenedetect on every benchmark film, on x64 and ARM.

### New

- `DetectionOptions.Yadif` (`IYadif`), with `Yadif` in ShotDetector.FastYuv (LGPL, a port of FFmpeg's yadif):
  in-process deinterlacing computes only the rows the resize reads, in parallel, instead of FFmpeg's yadif on whole
  frames. Identical results for 8-bit video. For 9 to 16 bits it follows FFmpeg's C code, since FFmpeg 8.1's x86
  SIMD yadif gives a different result on every run there.
- More in-process, with identical per-frame stats to the executable: rotated and flipped video (the display matrix
  applied as ffmpeg's autorotate does), image sequences (`%04d.png`, with `FrameRate`), and AV1. Our FFmpeg build
  (ShotDetector.Native, the CLI) now links in zlib (PNG) and dav1d (AV1, BSD-2-Clause). Codecs the loaded libraries
  can't decode are checked up front and go to the executable, also with other FFmpeg builds.
- Streamed mp4s without "faststart" (headers last, as most mp4s are) work: `Detect(Stream)`, `DetectStreamAsync`
  and CLI `-i -` copy them to a temporary file, deleted afterwards, and read that as a file, with the same results
  as the file's path. Other streams still stream (issue #7).

### Faster

- Streams (`Detect(Stream)`, `DetectStreamAsync`, CLI `-i -`) decode in-process when FFmpeg's libraries load, read
  through a custom AVIOContext, with the executable's results (frame times from the stream's start). A piped 480p
  film takes 1.3 s, as its file path does, instead of 1.6-2.0 s through ffmpeg (issue #7: 3.8 s vs 1.4 s on 2 CPUs).
  A Stream can't be read twice, so one the libraries fail on no longer falls back to the executable.
- In-process decoding and conversion run on their own thread, overlapped with the resize and scoring, as the
  executable path overlaps ffmpeg with them. With all CPUs, small films were slower than scenedetect and now aren't:
  Big Buck Bunny 360p 5.5 s instead of 8.4 s on a 4-CPU ARM runner (scenedetect 7.9 s), 6.4 s instead of 8.1 s on
  x64 (7.4 s); Elephants Dream 240p 5.5 s instead of 8.2 s on ARM (6.7 s).
- Deinterlacing with FastYuv's `Yadif`: an 80 s 1080i25 MPEG-2 4:2:2 MXF takes 7.5 s instead of 11.6 s with 2 CPUs,
  and 5.0 s instead of 5.5 s with all of them (using a third less CPU time); 5.1 s and 4.0 s without deinterlacing.

## 0.9.0 – 2026-10-08

Broadcast and ARM: deinterlacing in-process, which decoder ran and a startup check, more in the probe (issue #42),
NEON on ARM, and the scenedetect 0.6.4 mode removed. CI now checks interlaced 1080i XDCAM HD, AVC-Intra and ProRes.
Note for interlaced video: scenedetect's current OpenCV can't read it (one black frame throughout), so compare with
deinterlacing on (README, "Where this differs").

### Breaking changes

- `VideoDecoder.InProcess` without loadable FFmpeg libraries throws with `Reason = ShotDetectionError.FfmpegLibrariesNotFound`
  (new), no longer `FfmpegNotFound`, which now means the executable only (issue #42).
- `VideoInfo` has two more positional members, `Container` and `HasAudio` (issue #42).
- The scenedetect 0.6.4 compatibility mode is gone: `PySceneDetectVersion.V0_6_4` and CLI `--compat 0.6.4`. 0.7.1
  stays the default and only mode (`PySceneDetectVersion` remains, for the next scenedetect release); use
  ShotDetector 0.8 to reproduce 0.6.4.

### New

- Deinterlacing in-process: our FFmpeg build (ShotDetector.Native, the CLI) adds libavfilter with only yadif, and
  the in-process decoder runs it as ffmpeg's `-vf yadif` does, so deinterlaced detection no longer needs the ffmpeg
  executable. Identical results (tested against the executable, from the start and after a seek, and against a
  lossless yadif copy). Not faster: on an 80 s 1080i25 MPEG-2 4:2:2 MXF with 2 CPUs, 13.3 s in-process vs 13.4 s
  with the executable, yadif itself being most of the cost (issue #42).
- Which decoder a run uses: `DetectionProgress.DecodesInProcess` and `.Pipeline`, in every report from a first one
  sent before any frame, so `DetectStreamAsync` callers see it too (issue #42).
- `ShotDetection.CanDecodeInProcess(ffmpegDirectory)`: whether FFmpeg's libraries load, for a worker's startup check.
- `VideoInfo.Container` (FFmpeg's demuxer name) and `HasAudio` (null for streamed input), so a probe can replace a
  separate ffprobe call (issue #42).

### Faster

- ARM (Apple Silicon, Graviton, Ampere): the HSV conversion has a NEON path and the resize's vertical pass and the
  frame differences fall back to 128-bit vectors, where they ran scalar. On a 2-CPU Neoverse-N2, in-process
  decoding is now faster than scenedetect on all four benchmark films (Big Buck Bunny 0.95x, was 1.16x) with less
  CPU. CI runs the whole test suite on ARM, and the benchmark covers ARM too.

### Fixed

- In-process decoding with a start time on MPEG program streams (packets without timestamps) began up to a GOP late:
  libavformat's seek lands after the wanted frame there. Such files now decode from the start and drop the frames
  before it, as the executable's results (since 0.7.0).

## 0.8.0 – 2026-10-07

Automatic deinterlacing and a public probe (issue #35). Results are unchanged unless you turn deinterlacing on.

### Breaking changes

- `DetectionOptions.Deinterlace` is a `DeinterlaceMode` (`Off`, `On`, `Auto`) instead of a `bool`: `Deinterlace = true`
  becomes `Deinterlace = DeinterlaceMode.On`. The default is still off.

### New

- `DeinterlaceMode.Auto` (CLI `--deinterlace auto`): deinterlace when the video stream is flagged interlaced (its
  field order), so a worker no longer needs ffprobe to decide (issue #35).
- `ShotDetection.Probe` / `ProbeAsync`: a video's properties from its headers (size, frame rate, frame count,
  duration, codec, pixel format, field order, rotation) as a `VideoInfo`, in milliseconds, in-process when FFmpeg's
  libraries load. `VideoReader` has `FieldOrder`, `Codec`, `Duration`, `Deinterlaces` and `Info` too (issue #35).

## 0.7.0 – 2026-10-07

ShotDetector now decodes in-process by default and needs no ffmpeg installed for video files: the CLI bundles
FFmpeg's libraries, and the new `ShotDetector.Native.<rid>` packages carry them for library users. On 2 CPUs it is
faster than scenedetect on every benchmark film, with less CPU and memory (README "Performance"). Results are
unchanged.

### Changed

- In-process decoding is the default when FFmpeg 8's shared libraries load (`VideoDecoder.Auto`, the new default;
  CLI `--decoder auto`), else the ffmpeg executable as before. Same results either way. `--decoder process` keeps
  the executable. FFmpeg's libraries load once per process: a later `FfmpegDirectory` no longer errors, the first
  copy is used.

### New

- `ShotDetector.Native.<rid>` packages (LGPL) for win-x64, linux-x64, linux-arm64, linux-musl-x64 and osx-arm64:
  FFmpeg 8.1.3's libraries built by us for decoding only (7-10 MB per platform). The shotdetect tool and binaries
  bundle them. Byte-identical stats to the FFmpeg 8.1 executable on H.264, HEVC and VP9.
- Probing in-process: with the libraries available, a video file's properties and packet timestamps (and MPEG-PS
  frame timestamps) are read with them instead of ffprobe, identical by test. With ShotDetector.Native or the CLI,
  detecting shots in a file needs no ffmpeg at all (streams, URLs, image sequences, rotated video, deinterlacing,
  AV1 and the image/clip exports still use the executable).
- The libraries are looked for next to the app first (`runtimes/<rid>/native/`, then the app's folder), then in
  the system's usual places. In `Auto`, a codec they can't decode (AV1: no software decoder in our build) is
  decoded by the ffmpeg executable instead. FFmpeg's log is quiet in-process. The CLI summary says how it decoded.
- Benchmark (`benchmark.yml`, `tools/bench.py`): the default and executable modes, CPU time and the whole process
  tree's memory, on four films including Sintel at 1080p; the README's performance tables come from it.

## 0.6.1 – 2026-10-07

Speed only: every result is unchanged (byte-identical stats on Sintel, Big Buck Bunny and Tears of Steel).
In-process decoding (`--decoder inprocess`, FFmpeg 8.1's libraries) is now faster than scenedetect on most
films; the default ffmpeg-executable path is up to 41% faster on small video.

### Faster

- In-process decoding, on Linux with 2 CPUs (wall time against scenedetect 0.7.1): Sintel 1080p 4.6 s vs
  5.1 s, Tears of Steel 32.1 s vs 33.2 s, Big Buck Bunny within 4% (was 1.7x), at the same or less CPU (issue #12):
  - it converts only the row slices the resize reads, set up from the frame's colour tags (tested exact for
    BT.709, full range, 4:2:2, 4:4:4, 10-bit and odd heights);
  - the exact resize (every pipeline) has no bounds checks in the horizontal pass and a 256-bit vertical pass;
    stage timing showed it cost more than decoding at 640x360;
  - HSV conversion runs 8 pixels at a time (AVX2; tested on all 2^24 colours).
- Default path (ffmpeg executable), small video: ffmpeg sends whole frames when the resize reads at least a
  quarter of the pixels, instead of filtering them (remap): on Linux with 2 CPUs, 37% faster on 640x360 and 41% on
  426x240, with less memory; 1080p keeps the filter.

## 0.6.0 – 2026-10-07

Every result is still identical to scenedetect's; in-process decoding and deinterlacing are tested equal to the
ffmpeg executable and to a lossless yadif copy.

### Changed

- ffmpeg, ffprobe and input failures throw `ShotDetectionException` (with a `Reason`: FfmpegNotFound,
  InvalidInput, DecodeFailed, ExportFailed) instead of a plain `InvalidOperationException`. It derives from
  `InvalidOperationException`, so `catch` blocks still work; only exact type checks (such as xunit's
  `Assert.Throws<InvalidOperationException>`) see the difference. A file without a video stream now says so
  (issue #17).
- Default decoder threads: one fewer than the CPUs available (1 in a 2-CPU container, where it used the same wall
  time as more for ~9% less CPU), still at most 4, or 8 on the yuv420p path.

### New

- In-process decoding, opt-in: `DetectionOptions.Decoder = VideoDecoder.InProcess` / `--decoder inprocess`
  decodes with FFmpeg 8.1's shared libraries (FFmpeg.AutoGen bindings, MIT) instead of the ffmpeg executable.
  Same frames and results. On Linux with 2 CPUs: 1.2-1.5x faster than the ffmpeg executable for 9-33% less
  CPU; it decodes with all the CPUs, as OpenCV does (`--threads 1` uses the least CPU) (issue #12). Streams, URLs, image
  sequences, rotated video and deinterlacing still use the executable.
- `Deinterlace` / `--deinterlace`: ffmpeg's yadif before analysis, for interlaced sources; results equal
  scenedetect's on a lossless `-vf yadif` copy, seeking included (issue #16).
- Tracing: `ShotDetection.ActivitySourceName`, one span per detection with the video's size, frame rate,
  pipeline, decoder, frames and shots (issue #19).
- Packages: SourceLink, symbol packages (.snupkg), deterministic CI builds; release packages and binaries carry
  GitHub build attestations (`gh attestation verify <file> --repo bisforboman/shotdetector`, issue #18).

### Faster

- Scoring: frames as small as scenedetect's (at most 256 wide) no longer split across threads, which cost 3-4x
  their CPU; the resize reads the sampled rows directly; difference sums use 256-bit vectors. Same results.
  On Linux with 2 CPUs: 17% faster, 9% less CPU than 0.5.0 (issue #12).

### Fixed

- Correction to 0.5.0: "on 2 CPUs it now uses less CPU than scenedetect" holds on the Windows machine it was
  measured on (ffmpeg 7.1), not on Linux: there, with 2 CPUs, ShotDetector uses ~1.5x scenedetect's CPU and
  ~1.6x its wall time (issue #12). The README has both measurements.

## 0.5.0 – 2026-10-06

Every new output and option below is byte-identical to scenedetect 0.7.1's (checked in CI).

### Breaking changes

- API review toward 1.0: the detector classes, `IDetector`, `ContentScorer` and `EdgeDetector` are internal
  (use `ShotDetection` with `DetectorKind`); `VideoReader.FrameAt`, `SeekFrame`, `PositionAfterDecoding` and
  `FrameCountHint` are internal; `FrameTime`'s raw fields and `FrameTime.Pts` are internal and `ToString()` is
  the timecode; `SaveImages`/`SplitVideo` return `IReadOnlyList<string>`.
- `SplitVideo` takes `SplitOptions` before the `CancellationToken` (pass the token by name).
- `DecodeThreads` is nullable; the default (null) is 8 decoder threads on the yuv420p fast path (~13% faster
  on HD, ~70 MB more at 1080p) and 4 elsewhere.
- The shot list CSV starts with scenedetect's `Timecode List:` row (an empty row when there are no cuts), as
  scenedetect's does; `Shots.Csv(shots, includeCutList: false)` / CLI `--skip-cuts` leaves it out.
- `DetectionResult` has a new `Cuts` parameter (scenedetect's cut list, which keeps the cuts of dropped or merged
  shots).

### New

- Timeline exports: `Timeline.Edl`, `Fcpx`, `Fcp7`, `Otio` and `Qp` / CLI `--save-edl`, `--save-fcp`,
  `--save-otio`, `--save-qp` (scenedetect's save-edl, save-fcp, save-otio, save-qp).
- `Shots.Html` / CLI `--save-html`: scenedetect's save-html (export-html) page, with thumbnails.
- Several detectors in one run (`DetectionOptions.Detectors` with per-detector `DetectorSettings`; CLI repeated
  `-d`, with `-t`/`-m`/`--filter-mode` per detector), `DropShortScenes`, `MergeLastScene`, `FilterMode`
  (suppress), `Downscale`.
- `ShotDetection.LoadScenes` / CLI `--load-scenes`: shots from a scene list CSV, as scenedetect's load-scenes.
- CLI `--config <file>`: scenedetect's config file (scenedetect.cfg) as defaults; unknown keys are listed.
  `DetectionOptions.AddLastScene` (threshold detector; on by default, as in scenedetect).
- File name templates: `ImageOptions.FileName`, `SplitOptions.FileName` (CLI `--image-filename`,
  `--split-filename`); CLI `-o/--output` and `$VIDEO_NAME` in output paths.
- `SplitOptions` (CLI `--split-copy`, `--split-high-quality`, `--split-crf`, `--split-preset`, `--split-args`,
  `--split-expand`): scenedetect's split-video options.
- `DetectionOptions.FrameRate` / CLI `--frame-rate`: scenedetect's `-f/--frame-rate`, including the rate of
  image sequences (`frames/%04d.png`).
- `MinSceneLength` takes every scenedetect format: frames, `0.6s`, `0.6` and `HH:MM:SS.mmm`.
- `ShotDetection.DetectAsync`; CLI `-q/--quiet`.

### Faster

- The MIT core pipes only the pixels the exact resize reads (ffmpeg's remap filter after its own BGR conversion;
  `FramePipeline.SampledBgr`): identical results, 0.44 MB instead of 6.2 MB per 1080p frame. On 2 CPUs it now uses
  less CPU than scenedetect (was 2.1x) and is on par in wall time; faster than scenedetect with more CPUs (#7).
  *Correction: measured on Windows with ffmpeg 7.1 only; on Linux with 2 CPUs it uses ~1.5x scenedetect's CPU
  (issue #12, see the README).*

### Fixed

- MPEG program streams (`.mpg`, MPEG-2) gave shifted cuts: packets without a pts were dropped from the frame
  timestamps. Per-frame timestamps now come from a decoding pass for such files, as OpenCV sees them.
- When the decoder drops frames (damaged video), later frames got the wrong times and frame numbers; each frame
  now takes its own time, as in scenedetect.
- A streamed input that doesn't start at 0 (e.g. a TS starting at 1.4 s) had every time shifted by its start.
- split-video now always drops subtitle streams (`-sn`), as scenedetect 0.7.1 does; only the 0.6.4 mode did.

### Repository

- Real-world (whole films, nightly) and mutation checks in CI; a weekly benchmark; `main` is protected, with
  17 required checks.

## 0.4.0 – 2026-10-06

- Streamed input: `Detect(Stream)`, `DetectStreamAsync(Stream)`, URLs, `DetectionOptions.Streaming`
  and `ProbeBytes`; the CLI reads standard input with `-i -`. `DetectionResult.VideoPath` is null for
  a `Stream`.
- Time range (`StartTime`, `EndTime`, `Duration`), `FrameSkip` and `Crop`, matching scenedetect's
  `time`, `--frame-skip` and `--crop`; `ImageOptions` for `SaveImages` (size, scale, format).
- API: test-only members are internal; `VideoReader.Pipeline` is the `FramePipeline` enum;
  `DetectionResult.MinSceneLength` is `MinSceneLengthFrames`; `SaveImages` takes `ImageOptions`.
- Package icon, changelog, Dependabot.

## 0.3.0 – 2026-10-05

- All five scenedetect detectors: `Histogram` (`detect-hist`, bit-exact) and `Hash` (`detect-hash`,
  exact except on flat frames where the hash is rounding noise) join adaptive, content and threshold.
- `ShotDetection.DetectStreamAsync`: shots as an `IAsyncEnumerable<Shot>` while the video decodes.
- `CancellationToken` on `Detect`, `SaveImages` and `SplitVideo`; cancelling kills ffmpeg.
- `DetectionOptions.Progress` (frames done / expected) and `FfmpegDirectory`; a clear error when
  ffmpeg is missing.
- PySceneDetect 0.6.4 compatibility mode (`Compatibility = PySceneDetectVersion.V0_6_4`).
- Fix: video with a rotation tag (portrait phone clips) was read with swapped dimensions.
- Runs on Alpine (musl); tested in CI.
- The `shotdetect` CLI ships as the `ShotDetector.Cli` dotnet tool and as Native AOT binaries for
  Linux, Alpine, Windows and macOS on the GitHub release.

## 0.2.0 – 2026-10-05

- Colour conversion follows the video's colour tags (BT.709, BT.2020, full range), as scenedetect
  does on Linux with FFmpeg 8; `ShotDetector.FastYuv` handles every matrix and range.
- Fix: scene list timecodes now match scenedetect digit for digit (OpenCV's millisecond positions,
  Python's rounding).

## 0.1.0 – 2026-10-05

First release: `ShotDetector` (MIT) with the adaptive, content and threshold detectors, identical
to scenedetect 0.7.1 on every clip in CI, and `ShotDetector.FastYuv` (LGPL-2.1-or-later), the
optional yuv420p fast path ported from FFmpeg's swscale.
