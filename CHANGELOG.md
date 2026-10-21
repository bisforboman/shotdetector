# Changelog

Versions are git tags (`vX.Y.Z`); each one publishes the NuGet packages and a GitHub release. From 1.0,
semantic versioning (README, "Versions"); before it, minor versions could change the public API.
What's merged but not released yet is in changes/shotdetector/ (one file per pull request).
From 2.1.0 on, FrameReader has its own versions and src/FrameReader/CHANGELOG.md; until then its changes are here,
marked "FrameReader:".

## 3.12.0 – 2026-10-21

FastYuv's row deinterlacing with its converter up to 1.5x faster, and more decoder threads for parallel h264 and HEVC.

### New

- `IYuvChromaRows`: a YUV converter can say which chroma rows converting a row reads; decoding in-process, ShotDetector
  then takes only the rows it converts from the decoder. ShotDetector.FastYuv's converters implement it.

### Faster

- Deinterlacing with FastYuv's `Yadif` together with FastYuv's converter (`Yadif = new Yadif()` and
  `Yuv420Converter = new SwscaleYuv420()`, in-process) now deinterlaces and copies only the rows the conversion reads
  (about 3 in 10 at 1080), not whole frames. That pairing was slower than FFmpeg's own yadif and than `Yadif` with the
  default converter; on 150 s of 1080i25 MPEG-2 (4 vCPUs) it now takes 17.2 s instead of 24.1 s for 4:2:2 MXF and 11.8 s
  instead of 16.0 s for 4:2:0 MPEG-TS, a third less CPU time, and 1.35-1.54x as fast in parallel mode. Results are
  unchanged, byte for byte.
- FastYuv in-process copies only the rows it converts for progressive video too (10-bit h264: 1.04-1.09x).
- Parallel detection gives h264, HEVC and other frame-threaded decoders twice the CPUs' decoder threads (at most 16),
  shared among the chunks, for frames up to 1920 x 1080: each chunk's decoder now has up to the CPUs' count. On 4 vCPUs,
  150 s of 1080p h264 took 15.1 s instead of 16.8 s (8-bit) and 19.5 s instead of 23.8 s (10-bit), in the same CPU time
  and about 30 MB more memory. A sequential run, larger frames and `DecodeThreads` you set are unchanged.

## 3.11.0 – 2026-10-20

The CLI prints warnings as they happen, has --version, and comes as a Linux ARM64 binary.

### New

- `shotdetect --version` prints the version.
- `shotdetect` as a Native AOT binary for `linux-arm64` (Graviton, Raspberry Pi 5) on each GitHub release, beside
  linux-x64, linux-musl-x64, win-x64 and osx-arm64. The README's Install section now says how to download, unpack
  and run the binaries on each system, and what still needs the ffmpeg executable.
- `shotdetect` prints each warning on stderr as soon as it is known (why `--parallel` reads sequentially, an HLS
  playlist read by the ffmpeg executable) instead of after detection, and still once.

## 3.10.1 – 2026-10-20

Fixes from the review of 3.10.0: file names with a %, and Auto no longer hands an http(s) URL the libraries refused to the executable.

### Fixed

- A video file with a `%` in its name that isn't an image sequence's number (a URL-encoded name such as
  `My%20Video.mp4`) failed with `--frame-rate` / `FrameRate` in 3.10.0 ("No demuxer or protocol option "framerate""),
  and always failed through the ffmpeg executable ("Option framerate not found"). Only a pattern FFmpeg reads as an
  image sequence (`frames/%04d.png`: one `%d`, with optional digits; `%%` is a percent sign) is now read as one.
- `Decoder = Auto` (the default) no longer hands an http or https URL that FFmpeg's libraries failed to open to the
  ffmpeg executable: it now refuses it, with the libraries' error and why. The executable fetches the https segments,
  keys and variants an HLS playlist names without checking their certificates, so a playlist whose segments the
  libraries refused (a certificate nobody vouches for) was decoded anyway. `--decoder process`
  (`Decoder = VideoDecoder.FfmpegProcess`) still uses the executable, with its warning. This also applies to DASH over
  http(s) with the FrameReader.Native libraries, which have no DASH demuxer: use `--decoder process` for it. Files and
  other URLs fall back to the executable as before.
- The warning for an http or https HLS playlist decoded by the ffmpeg executable now says it fetches "any https
  segments, keys and variants" without checking their certificates (it is given without reading the playlist).

## 3.10.0 – 2026-10-19

Colour tags from the decoded frame, warnings as they happen, and parallel chunks that don't probe again.

### New

- `DetectionOptions.Warnings`: an `IProgress<string>` told each warning as soon as it is known (an http or https HLS playlist
  read by the ffmpeg executable, why `Mode = Parallel` read sequentially, a fallback from chunks), so
  `DetectStreamAsync`, which has no result, gets them too. Detect still returns them in `DetectionResult.Warnings`.

### Faster

- Parallel detection probes the file once: each chunk's reader takes the first reader's stream properties and packet
  timestamps instead of reading them again. Most noticeable on MPEG-TS, where reading every packet's timestamp is slow:
  a 4-minute, 280 MB clip went from 6.7 s to 5.8 s in 2 chunks on 4 CPUs (and from 21 s to 17 s of CPU time in 4).

### Fixed

- The warning that the ffmpeg executable fetches an HLS playlist's https segments without checking their certificates
  now covers playlists opened over http too, whose https segments, keys and variants went unchecked without a word.
- With ShotDetector.FastYuv and the ffmpeg executable, a video whose colour matrix or range changes midway (two streams
  joined, a spliced broadcast) could give other cuts than PySceneDetect: ffmpeg converted the frames after the change
  before FastYuv read them. They now reach FastYuv as decoded, and the cuts are exact. Stats after such a change can
  still differ slightly, because FastYuv converts every frame with the first frame's colours.
- Videos whose container says something else than the video stream about where the chroma samples sit or about the
  colours (for example an mkv whose header says "left" or BT.709 while its h264 stream says "top left" or BT.601):
  the ffmpeg executable path (10-bit video) and ShotDetector.FastYuv (both decoders) converted with the container's
  values and gave other stats than PySceneDetect, which converts with the stream's. They now take the first frame's,
  as the in-process default path already did.

## 3.9.0 – 2026-10-19

FastYuv's 10-bit conversion a frame at a time, and parallel detection with less memory.

### New

- `IYuv420P10FrameConverter`: a 10-bit converter that converts all of a frame's pixels the resize reads in one call
  (`FrameToBgr`), beside `IYuv420P10Converter`; the pipelines use it when the converter `ForVideo` returns implements
  it. ShotDetector.FastYuv's 10-bit converters do.

### Faster

- ShotDetector.FastYuv on 10-bit 4:2:0 video: each chroma row is filtered horizontally once per frame (it was filtered
  again for every row that reads it), the vertical filter runs four chroma samples at a time, and saturation no longer
  branches. On 4 shared x64 vCPUs, 30 s of 854x480 10-bit h264 took 3.9 s instead of 5.8 s through the ffmpeg
  executable and 3.8 s instead of 5.1 s in-process (the default path: 3.5 s and 3.5 s); 1080p 10.3 s instead of 12.3 s
  in-process (default 12.1 s). The same results, byte for byte.
- Parallel detection uses less memory: the chunks share a sequential run's 8 frame buffers (4 each in 2 chunks, 2 in 3
  or 4) instead of keeping 8 each, at the same speed. Peak memory in 2 / 4 chunks, measured on 4 CPUs: 1080p 187 to
  175 / 259 to 218 MB; with ShotDetector.FastYuv, 1080p 202 to 178 / 292 to 221 MB and 2160p 553 to 458 / 868 to 583
  MB.

## 3.8.0 – 2026-10-18

FastYuv for 10-bit video, parallel detection by default from 4 CPUs, and why a parallel run fell back.

### New

- ShotDetector.FastYuv converts 10-bit 4:2:0 video (yuv420p10le, e.g. h264 High 10) too, with x86's exact bytes:
  `SwscaleYuv420` implements the new `IYuv420P10Converter`, a port of the path x86's swscale takes for it (the
  bicubic chroma filters as initFilter sets them up, the MMXEXT vertical scaler and output, the C output for the
  last two rows), with the chroma sited where the stream says. So on ARM, ShotDetector with FastYuv gives x64's
  results for 10-bit as it does for 8-bit. Even widths, any height, turned or not; odd widths keep the default path.
- On x64, FastYuv isn't needed for exactness on 10-bit: it gives the same bytes as the default path and is slower on
  small video (854x480: about 2.5-3.5x the time; 1080p about even), since each chroma row is filtered again for
  every row that reads it. A converter you set is used as set (docs/decisions/2026-10-18-fastyuv-10bit-x64.md); a
  faster 10-bit pass is on the backlog.
- `DetectedVideo.ChunkFallback`: why scoring in chunks fell back to a sequential read (a chunk's frame at another
  position, overlap scores that differ, ...), also a `DetectionResult.Warnings` entry that the CLI prints. Before, a
  fallback was silent and looked like a run that never chunked.
- `Mode = DetectionMode.Parallel` (CLI `--parallel`, `--chunks`) warns when it reads the file sequentially all the
  same, and why: fewer than 4 CPUs, shorter than two minutes, a time range, frame skip, the ffmpeg executable, a
  stream, MPEG-PS without timestamps.

### Faster

- Parallel detection is now the default from 4 CPUs on (was 8), still for frames up to 3840x2160: on GitHub's 4-CPU
  x64 and ARM runners 2 chunks were 1.07x to 1.8x faster on every film and clip measured, 1080p included, for 10 to
  54 MB more memory and, on 2-core SMT machines, up to 8% more CPU time (17% at low resolution with stats). On 4 to 7
  CPUs that is 2 or 3 chunks. `Mode = DetectionMode.Sequential` (CLI `--sequential`) opts out.

### Fixed

- Parallel detection: decoder threads that don't divide evenly among the chunks are no longer lost (`--threads 5` in 2
  chunks decoded with 2 + 2, now 3 + 2), and `DecodeThreads = 0` (FFmpeg's choice) shares out the default count
  instead of giving each chunk 1 thread.

## 3.7.0 – 2026-10-18

Parallel detection by default on 8+ CPUs, FastYuv for 4:2:2, ARM decodes MPEG-1/2, MJPEG and MPEG-4 as x86, and library warnings.

### New

- **Parallel detection is now the default** for `Detect`/`DetectAsync` and `shotdetect` on machines with 8 CPUs or
  more. A local file of two minutes or more, decoded in-process (no time range, frame skip or MPEG-PS), with frames up
  to 3840x2160, is scored in up to 4 chunks at once (half the CPUs), with exactly the sequential result. More cores are
  busy at once, for about the same total CPU time. Memory grows by one decoder and its frame buffers per chunk. If a
  chunk can't reproduce the file, it is read sequentially instead, and progress starts over. Services that run several
  detections at once can set `Mode = DetectionMode.Sequential` (CLI `--sequential`). `DetectStreamAsync` stays
  sequential.
- `DetectionOptions.Mode` (`DetectionMode`): `Auto`, the default, as above; `Sequential` never chunks; `Parallel`
  chunks from 4 CPUs on and at any frame size, `DetectStreamAsync` too (its shots then all come at the end). Chunks
  never outnumber the decoder threads you set (`DecodeThreads`, `--threads`).
- `shotdetect --sequential` scores the file in one pass; `--parallel` is `Mode = DetectionMode.Parallel`.
- `DetectionOptions.Parallel` is obsolete (use `Mode = DetectionMode.Parallel`). It still works, but it raises warning
  CS0618, which breaks builds that treat warnings as errors.
- ShotDetector.FastYuv converts 4:2:2 (yuv422p, yuvj422p) too, through the new `IYuv422Converter` interface, which
  `SwscaleYuv420` implements: 8-bit 4:2:2 video takes the fast path, and on ARM gives x86's results as 4:2:0 does.
- `DetectionResult.Warnings`: what the caller should know about how the video was read; `shotdetect` prints them on
  stderr. So far one: an https HLS playlist decoded by the ffmpeg executable, whose segment downloads don't check
  certificates (in-process decoding checks them).

### Fixed

- ARM: MPEG-1/2 and MJPEG video decode as on x86 (FFmpeg's C IDCT; ARM's own rounds differently), through the
  ffmpeg executable too, so with ShotDetector.FastYuv their results equal x86's. Unchanged on x86.
- ARM: MPEG-4 Part 2 video not from Xvid (FFmpeg's encoder, DivX, ...) now decodes as on x86 (FFmpeg's C IDCT), through
  both decoders. Xvid video already did: FFmpeg decodes it with the Xvid IDCT on every platform, and which streams are
  Xvid follows FFmpeg's own rule (the encoder's signature, else the FourCC). Unchanged on x86.
- FastYuv on rotated video converts the frames before turning them, as OpenCV does: turned 4:2:2 takes the fast path,
  and turned 4:2:0 with an odd width or height gives the default path's results (turned 4:2:0 whose width is odd, an
  odd height once turned, used to differ).
- FastYuv: video whose pixel format changes part-way (4:2:0 frames in a 4:2:2 stream, or the reverse) gives the default
  path's results, through both decoders: those frames are converted from their own format, as OpenCV does. Before,
  they were converted as the stream's format, so their colours were wrong. (A stream read through the ffmpeg
  executable still converts them as the stream's format: it can't be read again.)
- In-process decoding from a start time inside a part of the video at another size (a resolution change) failed with
  "Specified argument was out of the range of valid values": the frames are now scaled to the stream's size, as the
  ffmpeg executable path does.

## 3.6.0 – 2026-10-17

Faster edge detection on ARM.

### Faster

- Edge detection on ARM (Linux arm64, Apple Silicon): the non-maximum suppression works on 4 pixels at a time with
  128-bit vectors where it used to run one by one; the edge detector takes 0.72-0.88x the time on film frames on
  GitHub's ARM runners, the edges the same byte for byte.

## 3.5.0 – 2026-10-17

Faster --stats runs (edge detection), and hardening: a protocol whitelist for Stream input, a frame pipe only the user can open.

### Faster

- `--stats` runs (and edge weights in the content and adaptive detectors) take 0.68-0.74x the time of 3.4.0 on an
  8-core desktop: edge detection's non-maximum suppression works on 8 pixels at a time, the hysteresis needs no
  border checks, and the edge map is written 16 bytes at a time. The edge maps and stats are the same, byte for byte.

### Fixed

- A `Stream` input (and the CLI's `-i -`) reads with FFmpeg's protocol whitelist set to `pipe`, as a file reads only as
  a file, so nothing in the piped data can open another protocol.
- Windows: the named pipe the ffmpeg executable writes frames to accepts only the current user's connections, so
  another local user can't connect to it first and break the run.

## 3.4.0 – 2026-10-16

Faster in-process detection: the resize with SSE2 on the scoring thread, and edge detection about 3.8x faster.

### Faster

- Edge detection (`--stats`, and edge weights in the content and adaptive detectors) is about 3.8x faster: Sobel and
  the dilation work on 8 and 16 pixels at a time with the same integer arithmetic. `--stats` runs took 0.51-0.60x the
  wall time on an 8-core desktop; the edge maps and stats are the same, byte for byte.
- In-process detection on x64 is faster where decoding isn't the limit: on an 8-core desktop, wall time 0.74x
  (Elephants Dream 426x240), 0.64x (Big Buck Bunny 640x360), 0.86x (Tears of Steel 1280x534), 0.97x (Elephants Dream
  1080p VP9, decode-bound). The resize's horizontal pass uses SSE2, a pixel's three channels at once with the same
  integer arithmetic, and the resize runs on the thread that scores frames, which used to wait for the decoding thread
  most of the time. The results are the same, byte for byte.

## 3.3.0 – 2026-10-16

https in-process on Linux and macOS, HLS segments checked, fixes from a security review (two crashes on crafted files among them) and a faster resize.

### New

- https URLs decode in-process on Linux and macOS too, with FrameReader.Native's https (OpenSSL on Linux, the
  system's TLS on macOS); before, they went through the ffmpeg executable there.

### Faster

- The resize to scenedetect's size resizes each source row once, as OpenCV does: about 15% less resize time per
  frame for videos up to twice the processed height (426x240, for one), and about 40% when upscaling.

### Fixed

- In-process, the https segments an HLS playlist names are checked too (FFmpeg didn't pass the check on to them); the
  ffmpeg executable still doesn't.
- A rotated video whose frames got larger mid-stream (a larger encode joined on) read past a buffer in-process and
  could crash the process; every frame is now turned at the first frame's size, as through the ffmpeg executable.
- ffprobe's timestamp pass (MPEG-PS without packet timestamps, through the executable) read without the input's
  protocol whitelist, timeout, certificate check and frame size cap, so a crafted playlist could reach other
  protocols or addresses there; it reads as the other passes do now.
- ffmpeg and ffprobe are started by full path, next to the app or from PATH: by bare name, Windows and .NET on Unix
  also looked in the current folder, so an `ffmpeg` someone put there could run.
- A video claiming frames larger than 8192 x 8192 fails at the probe, before buffers are sized from it (the FastYuv
  path allocated them first).
- A spilled stream (an mp4 without faststart read from a `Stream`) goes in a temp folder of its own, readable only by
  the user on Linux and macOS; it was readable by every local user.
- ffprobe takes the input with `-i`, so a file named like one of its options (`-report`) is read as a file.

## 3.2.0 – 2026-10-16

Parallel detection of long files (opt-in), URLs decoded in-process, inputs that read only what they need, a frame size cap and an MPEG-TS start-time fix.

### New

- Parallel detection of long files: `DetectionOptions.Parallel` (CLI `--parallel`) scores chunks of a local file on
  several cores at once, with exactly the sequential result: the same cuts, scene list, stats and timecodes. Half the
  CPUs, at most 4 chunks, from 4 CPUs on and for files of two minutes or more. Measured on films: 1.5-2.3x faster on
  16 threads, 1.1-1.6x on 4-CPU runners, with the same CPU time. Anything a chunk can't reproduce (a seek that lands
  late, dropped frames, a size change) reads the file sequentially instead; `DetectedVideo.Chunks` says which
  ran. In-process decoding of files only; streams, URLs, a time range, frame skip and MPEG-PS read sequentially.
- URLs decode in-process when FFmpeg's libraries have the URL's protocol (`Auto`, or `InProcess`): with
  FrameReader.Native, http everywhere and https on Windows. Faster, and no ffmpeg executable needed for them; other URLs
  use the executable as before. The results are the same either way.
- URL inputs read only their own family of protocols, with the executable and in-process alike (http(s) URLs: http,
  https, tcp, tls, crypto; rtsp: rtsp, rtp, udp, tcp, tls): a second layer over FFmpeg's own checks, which already keep
  a remote playlist from reaching local files.

### Fixed

- An input path reads as a file only (FFmpeg's `protocol_whitelist=file,crypto`, with the executable and in-process): an
  FFmpeg pseudo-protocol string passed as a path, such as `concat:a.mp4|b.mp4` or `subfile:...`, is refused rather than
  interpreted. Network URLs (`scheme://host`) are unaffected.
- A start time (`StartTime`, `-s`) in an MPEG-TS file lost frames: the seek, which in a file without an index can
  land after the wanted frame (at the next keyframe, up to a GOP late, or past the last one: no frames at all), now
  starts over from further back when it does, on both decoders. The results match scenedetect's again.
- A video whose frames are larger than 8192 x 8192 pixels fails to decode (`InvalidInput`/`DecodeFailed`), with the
  executable and in-process, so a crafted file can't exhaust memory. No real video comes near it.
- An http(s) URL that stops sending fails after 30 s instead of waiting forever (FFmpeg's `rw_timeout`, with the
  executable and in-process), and cancellation stops an in-process read that waits on the network.
- An https URL's server certificate is checked, in-process and with the ffmpeg executable (`-tls_verify 1`): FFmpeg
  8 doesn't by default, so a self-signed or forged one was accepted. The segments an HLS playlist names are still
  opened without the check (FFmpeg doesn't pass the option on).

## 3.1.0 – 2026-10-15

save-images quality and compression, the crop as corners, and the time options as TimeSpans.

### New

- `DetectedVideo.Crop`: the analysed region as `DetectionOptions.Crop` takes it, inclusive top-left and bottom-right
  corners (the option's corners ordered and clamped to the frame), beside `CropRegion`'s X, Y, width and height.
- save-images' quality and compression, as scenedetect's `--quality` and `--compression`: `ImageOptions.Quality` (JPEG,
  WebP; 0-100) and `ImageOptions.Compression` (PNG; 0-9), and the CLI's `--image-quality` and `--image-compression`
  (and `quality`, `compression` under `[save-images]` in a config file). PNG's and WebP's mean what OpenCV's do;
  JPEG's is mapped onto ffmpeg's `-q:v`. The defaults are unchanged.
- The time options as `TimeSpan`s, beside scenedetect's strings: `DetectionOptions.Start`, `End`, `Duration`,
  `MinSceneDuration` and `MinOutDuration` (for `StartTime`, `EndTime`, `RangeDuration`, `MinSceneLength`,
  `MinOutLength`), and `DetectorSettings.MinSceneDuration`/`MinOutDuration`. Set one form of each option; both is an
  `ArgumentException`.

## 3.0.0 – 2026-10-14

ShotDetectionException under FrameReader's MediaException (issue #239), dependencies bounded below the next major, and no more ShotDetector.Native aliases.

### Breaking changes

- The `ShotDetector.Native` and `ShotDetector.Native.<rid>` packages aren't published any more: reference
  `FrameReader.Native` or `FrameReader.Native.<rid>` (the same libraries; the 2.x aliases stay on nuget.org).
- ShotDetector depends on FrameReader `[2.3.0, 3.0.0)` and FFmpeg.AutoGen `[8.1.0, 9.0.0)`, FastYuv on ShotDetector up
  to its next major: a later major of either can't replace the version a package was built against.

### New

- `ShotDetectionException` derives from FrameReader's `MediaException` (still an `InvalidOperationException`), as
  `FrameReaderException` does: one `catch (MediaException)` for both packages (issue #239).

## 2.2.1 – 2026-10-14

The ShotDetector.Native aliases and the CLI's Alpine binary take FrameReader.Native 2.2.1's fixed musl libraries.

### Fixed

- `ShotDetector.Native.<rid>` and `ShotDetector.Native` depend on FrameReader.Native 2.2.1 or newer, whose Alpine
  (linux-musl-x64) libraries load without `LD_LIBRARY_PATH` (issue #278); the shotdetect CLI's Alpine binary bundles
  them, so it decodes in-process there again instead of falling back to the ffmpeg executable.

## 2.2.0 – 2026-10-14

ShotDetector.Native now forwards to FrameReader.Native, and ShotDetector depends on FrameReader 2.2.0.

### New

- `ShotDetector.Native.<rid>` and `ShotDetector.Native` now only depend on `FrameReader.Native.<rid>` and
  `FrameReader.Native` (the same libraries), so existing references keep working; new projects reference
  FrameReader.Native.

## 2.1.0 – 2026-10-13

FrameReader: TestMedia, Video.Transcode, Remux.CanCopy, MissingFilters, progress and async overloads for the whole-file jobs, a ShotDetector.Native meta-package; ShotDetector: ProgressStep; a frame-count fix for deinterlaced frame-rate reading (issues #236-#244).

### New

- FrameReader: `FFmpegLibraries.MissingFilters(filterGraph)`, the filters a graph of your own uses that the libraries
  lack, in `Missing`'s format (`"filter equalizer"`), so a startup check concatenates the two lists instead of keeping
  a hand list; `"filter graph"` when the graph doesn't parse. The graph is only parsed, nothing is created
  (issue #236).
- `ShotDetector.Native`: a meta-package depending on every platform's `ShotDetector.Native.<rid>` at its version, so
  one reference covers Windows, Linux (x64, ARM64, musl) and macOS; a publish for one runtime still copies only its
  libraries (issue #244).
- `DetectionOptions.ProgressStep` (e.g. `0.1`): progress is reported only when the fraction done crosses a step, besides
  the first report and the last, so a service log gets one line per 10 % without a throttle of its own (issue #238).
- FrameReader: `Remux.CanCopy(input, output, options)`, whether `Remux.Copy` can stream-copy the selected streams into
  that container (DASH included) and why not, per stream: FFmpeg's own answer from the muxer, nothing written, instead
  of a list of rules that drifts from FFmpeg (issue #241).
- FrameReader: `TestMedia.Write(path, options)` writes synthetic test media in-process from FFmpeg's own sources
  (test patterns, colour bars, tones) and encoders: video sources concatenated in order (cuts at known frames),
  progressive or interlaced, an audio source, mpeg2video/mpeg4/aac/PCM. Tests of media code no longer need the
  ffmpeg executable to make their clips (issue #237).
- ShotDetector.Native: the source filters (testsrc2, smptebars, mandelbrot, color, sine, anullsrc), concat, setfield,
  setsar, setparams, trim and atrim; the mpeg2video, mpeg4, pcm_s24le, pcm_s32le and pcm_f32le encoders; the avi muxer.
- FrameReader: `Video.Transcode(input, output, options)` (and `TranscodeAsync`), a whole file re-encoded in one call
  as `ffmpeg -i IN [-vf yadif] -c:v E -pix_fmt yuv420p -g G -force_key_frames ... OUT`: deinterlaced when the stream
  is flagged interlaced, frames upright, the first audio stream re-encoded and interleaved, keyframes every
  `KeyframeEvery`. With whatever video encoder your libraries have (`libx264`, `libopenh264`) (issue #240).
- FrameReader: progress for the whole-file jobs, through `AudioConvertOptions`, `RemuxOptions`, `WaveformOptions`
  and `ReferenceToneOptions`'s new `Progress`: one `MediaProgress` (position, duration, fraction) for all, about 10
  times a second and once at the end (issue #242).
- FrameReader: async overloads `Audio.ConvertAsync`, `Remux.CopyAsync`, `WaveformData.ReadAsync`,
  `ReferenceTone.FindAsync` and `MediaProbe.ProbeJsonAsync`, off the calling thread and cancellable (issue #243).

### Fixed

- FrameReader: `VideoFrameReader` with both deinterlacing and `FrameRate` gave one frame more at the end than ffmpeg's
  `-vf yadif,fps=R`: yadif's frames carry their duration in its doubled time base, which the fps filter read as twice
  as long. It now gets the duration in the stream's time base, and the frames equal ffmpeg's, byte for byte.

## 2.0.0 – 2026-10-13

ShotDetector and FrameReader share their types: one StreamInfo, StreamType and FieldOrder (issue #194), IRowDeinterlacer and ToneSegment; DetectionResult.Video is a DetectedVideo; names that meant two things renamed. Detection results are unchanged; the breaking changes below say what to change.

### Breaking changes

- `DetectionResult.Video` is a `DetectedVideo`: the video as the detection read it, immutable, with no hidden
  probing. `VideoReader` is internal. Renamed on the way: `Width`/`Height` (the analysed size) are
  `ProcessedWidth`/`ProcessedHeight`, and `SourceWidth`/`SourceHeight` are `Width`/`Height`, so `Width` always means
  the video's own; `Fps` is `FrameRate`; `Deinterlaces`, `DecodesInProcess` and `Streaming` are `Deinterlaced`,
  `DecodedInProcess` and `Streamed`. `Info`, `Streams`, `HasAudio` and `Pipeline` are gone: `ShotDetection.Probe` (or
  FrameReader's `MediaProbe.Probe`) gives the streams.
- `FramePipeline` is internal, and `DetectionProgress.Pipeline` is gone: how frames reach the detectors is internal
  plumbing (the `shotdetector.pipeline` ActivitySource tag still records it). `DetectionProgress.DecodesInProcess` stays.
- `IYadif` is gone: `DetectionOptions.Yadif` takes FrameReader's `IRowDeinterlacer`, which ShotDetector.FastYuv's
  `Yadif` implements (so a FrameReader `FrameDecoder` takes it too). `Yadif = new Yadif()` reads as before.
- `ReferenceToneSegment` is gone: `FindReferenceTone` and `DetectionResult.ReferenceTone` give FrameReader's
  `ToneSegment` (the same fields). `ReferenceToneMinDuration` and `ReferenceToneSearchDuration` are
  `DetectionOptions.ReferenceTone`, FrameReader's `ReferenceToneOptions` (the same defaults, with `Frequency` and
  `MaxGap` too): `ReferenceTone = new() { SearchDuration = TimeSpan.FromMinutes(3) }`.
- Renamed where one name meant two things: `VideoInfo.FrameCount` and `DetectedVideo.FrameCount` (OpenCV's
  CAP_PROP_FRAME_COUNT) are `OpenCvFrameCount`, apart from FrameReader's container count; `DetectionOptions.Duration`
  (a scenedetect time, "30s" or frames) is `RangeDuration`, apart from `VideoInfo.Duration`; and
  `ShotDetectionError.FfmpegLibrariesNotFound` is `LibrariesNotFound`, as FrameReader's. The CLI's options and output
  are unchanged.
- ShotDetector's `StreamInfo`, `StreamType` and `FieldOrder` are gone: `VideoInfo` and `VideoReader` use
  FrameReader's types of the same names, so code using both packages is no longer ambiguous (CS0104, issue #194).
  `StreamInfo.Duration` is FrameReader's exact value (`DurationPts` in `TimeBase`), no longer rounded to ffprobe's
  six decimals.

### New

- FrameReader: `StreamInfo` has a public constructor, for filling one from another source.

## 1.13.1 – 2026-10-13

Audio.Convert and AudioFilter downmix AC-3, E-AC-3 and DTS with the stream's own levels, as ffmpeg (issue #211).

### Fixed

- FrameReader: `Audio.Convert` and `AudioFilter` downmix AC-3, E-AC-3 and DTS with the stream's own centre,
  surround and LFE levels, as the ffmpeg command line does: 5.1 AC-3 to MP3 was louder than ffmpeg's and is now the
  same, byte for byte (issue #211).

## 1.13.0 – 2026-10-13

FastYuv exact on resolution changes through the ffmpeg executable too, in-process decoding about 16% lighter on SD video, Fps converting to and from FrameReader's Rational.

### New

- `Fps` converts implicitly to and from FrameReader's `Rational`, so a rate from `MediaProbe` works where
  ShotDetector takes an `Fps`, and the other way round.

### Faster

- In-process decoding resizes straight from the converted rows instead of copying the pixels it reads first:
  about 16% less CPU on SD video and 5% on 1080p (content detector, pinned to 2 CPUs). Results are unchanged.

### Fixed

- ShotDetector.FastYuv on video that changes resolution mid-stream through the ffmpeg executable (in-process was
  fixed in 1.12.0): once a frame of another size turns up, the file is read again on the exact pipeline, so the
  results equal the core path's and scenedetect's. Only such files are slower.

## 1.12.0 – 2026-10-13

FrameReader: ffmpeg's audio conversion and ffprobe's JSON in one call, DASH names per content type, a check for what FFmpeg builds lack; FastYuv exact on resolution changes in-process (issues #191-#195).

### New

- ShotDetector's `StreamInfo`, `StreamType` and `FieldOrder` are `[Obsolete]`: ShotDetector 2.0 uses FrameReader's
  types of the same names instead, since both packages defining them makes code using both ambiguous (CS0104,
  issue #194). Until then, `FrameReader.MediaProbe.Probe(path)` gives FrameReader's, or alias them
  (`using StreamInfo = FrameReader.StreamInfo;`).
- FrameReader: `Audio.Convert(input, output, options)`, the file `ffmpeg -i IN -vn [-af G] [-c:a E] [-b:a B] OUT`
  writes, byte for byte: the decoder's own samples through the filter graph to the encoder, converted to its nearest
  format, rate and channel layout as the command line does (a downmix of integer samples normalized, of float ones
  not), with the input's metadata and chapters (issue #192).
- FrameReader: DASH segment names with `$ContentType$` (`video`, `audio`, `text`) and folders, for Shaka Packager's
  `video/` and `audio/` layout: `Remux.Copy` creates the folders and puts the content type into the files' names and
  the MPD's templates, which FFmpeg's dash muxer can't (issue #191). The files are otherwise ffmpeg's, byte for byte.
- FrameReader: `FFmpegLibraries.Missing(Capability...)` lists what loaded FFmpeg libraries lack for FrameReader's
  features (`"filter yadif"`, `"encoder libmp3lame"`, `"muxer dash"`), empty when they have everything: a startup
  check that needn't track what each feature uses (issue #195).
- FrameReader: `MediaProbe.ProbeJson(path)`, ffprobe's `-print_format json -show_format -show_streams` (ported from
  ffprobe 8.1): the same keys and values, value types included, with every field ffprobe prints (profile, level,
  aspect ratios, colour, field order, disposition, tags, side data, decoder options, ...) and unknown values left out
  as ffprobe's JSON does (issue #193).

### Fixed

- `AudioWriter` converts to the encoder's channel layouts too, as ffmpeg does: 5.1 into MP3 is downmixed to stereo
  instead of failing.
- ShotDetector.FastYuv on video that changes resolution mid-stream (two encodes joined), decoding in-process: the
  new size's frames are now converted as OpenCV converts them (scaled to the first size in the one conversion to
  BGR), so the results equal the core path's and scenedetect's. Only those frames are slower.

## 1.11.0 – 2026-10-12

Widevine pssh data from the caller: EncryptionOptions.WidevinePsshData (issue #185).

### New

- FrameReader: `EncryptionOptions.WidevinePsshData`, the Widevine pssh data as a DRM provider supplies it (with its
  provider, content ID and so on), written into the pssh box unchanged, the box
  `pssh-box.py --widevine-system-id --pssh-data` gives (issue #185). Null: built from the key ID as before.

## 1.10.0 – 2026-10-12

Follows scenedetect 0.7.2 by default (its new detect-hist and detect-hash defaults, frame-count minimum lengths,
detect-threshold's min-out-length; issue #177), with 0.7.1 still selectable; save-keyframes.

### New

- scenedetect 0.7.2 (issue #177), now the default compatibility mode (`PySceneDetectVersion.V0_7_2`); 0.7.1 stays
  selectable (`V0_7_1`, `--compat 0.7.1`). Following a new scenedetect release changes default results, as README's
  "Versions" describes. Each mode gives its release's cuts, scene lists and stats (CI checks both):
  - detect-hist defaults to threshold 0.20 and 128 bins, detect-hash to 0.35 and size 8 (0.7.1: 0.05/256 and
    0.395/16). `DetectionOptions.Bins` and `HashSize` default to 0 now, meaning the release's default.
  - A minimum length given in frames (as the CLI passes it) is compared as a frame count in the content detector's
    flash filter, so an exact gap of that many frames counts (scenedetect #577).
  - The adaptive detector measures the minimum length from the frame it cuts at (scenedetect #408).
  - detect-threshold ignores fade-outs shorter than `MinOutLength` (new; CLI `--min-out-length`), 0.1 s by default
    as in scenedetect's CLI.
- `Timeline.Keyframes` and CLI `--save-keyframes <file>`: scenedetect 0.7.2's save-keyframes (Aegisub's keyframe
  format v1).

## 1.9.0 – 2026-10-12

CENC encryption and Widevine signalling for DASH and .mp4 output (RemuxOptions.Encryption; issue #171).

### New

- FrameReader: `RemuxOptions.Encryption`, Common Encryption (CENC, AES-CTR) of .mp4 and DASH output with a key ID
  and key, the files ffmpeg's `-encryption_scheme cenc-aes-ctr` gives. `Widevine = true` adds, for DASH, Widevine's
  `pssh` box to the init segments (`pssh-box.py`'s) and `ContentProtection` to the MPD, as Shaka Packager writes them
  (issue #171).

## 1.8.0 – 2026-10-12

DASH output: Remux takes muxer options, and the dash muxer writes fMP4 segments and a static MPD as ffmpeg does
(issue #171); stats values from 1e16 up are written as Python writes them.

### New

- FrameReader: `RemuxOptions.MuxerOptions`, FFmpeg muxer options as ffmpeg's output options (e.g.
  `movflags=+faststart`). With `Container = "dash"`: MPEG-DASH output, fMP4 segments and a static MPD, the files
  `ffmpeg -c copy -f dash` writes with the same options (issue #171). ShotDetector.Native has the dash muxer now.

### Fixed

- `Remux.Copy` to an .mpd on Windows wrote the segments into the working directory when the path used `\`
  (FFmpeg's dash muxer looks for `/`); they now go next to the manifest.
- Stats files: values from 1e16 to 1e21 are written like Python's `repr` (`1e+16`, not `10000000000000000.0`).

## 1.7.0 – 2026-10-11

Joined encodes (timestamps starting over, a resolution change) now give scenedetect's results, with its stats
where a time repeats; damaged files decode with OpenCV's thread count, so concealed frames match; the
ShotDetector.Native.Gpl packages are gone.

### Breaking changes (packages only: no API changes)

- `ShotDetector.Native.Gpl.<rid>` (our FFmpeg build with x264, GPL) is no longer built or published; earlier versions
  stay on nuget.org. FrameReader's `VideoWriter` is unchanged and still encodes H.264 byte for byte as
  `ffmpeg -c:v libx264`, but needs FFmpeg 8 libraries with libx264 of your own, such as a "gpl" shared build, loaded
  with `FFmpegLibraries.Load(folder)`. `ShotDetector.Native.<rid>` (LGPL) is unchanged.

### New

- FrameReader: `BgrConverter.KeepFirstSize`, to scale frames of another size to the first one's.

### Fixed

- Damaged video: the default decoder threads are OpenCV's (the CPUs, at most 16) on both paths, since error
  concealment depends on the count; truncated files now give scenedetect's stats exactly (they differed a
  little), and a corrupt TS as closely as scenedetect's own runs agree with each other. `DecodeThreads` / `--threads` still override it.
- Two encodes joined (timestamps that start over, a resolution change): ShotDetector now gives scenedetect's results
  (it found 2 cuts where scenedetect finds 7 on the test file, and in-process decoding failed). Frames are labelled by
  their time as scenedetect labels them, so cuts that come out of order are sorted and a later frame's stats replace
  an earlier one's at the same time, metric by metric; frames of a new size are scaled to the stream's size in the one
  conversion, as OpenCV does. Damaged files are now compared with scenedetect in CI.
- Stats where a timestamp repeats (two encodes joined, duplicated timestamps): the threshold detector keeps the
  average already stored at that time, for its stats and its cuts, as scenedetect's does when writing stats.
- FrameReader's `SpectralStats` places each window where ffmpeg's `aspectralstats` does when the stream's timestamps
  have a gap (Ogg Vorbis's first pages can): it counted samples from the start, so times after the gap were early
  (10 ms on a Commons recording). The real-world audio check now covers Vorbis too.

## 1.6.0 – 2026-10-11

Rotated and 10-bit video now give scenedetect's results exactly (OpenCV's chroma siting and its turn of the
converted image, both found by probing OpenCV on Linux); the CLI does #81's audio job (`--audio-filter`,
`--audio-out`) and writes waveforms; DASH input is tested.

### New

- CLI `--audio-filter "<graph>"` and `--audio-out <file>`: an ffmpeg `-af` graph over the audio, with analysis
  filters' results (silencedetect, ebur128, astats) printed with their times, and the filtered audio written
  (encoded by the extension). E.g. line-up tone notched out and the result as MP3, the same audio ffmpeg gives:
  `shotdetect -i in.mxf --audio-filter "equalizer=f=1000:t=q:w=8:g=-40:enable='between(t,0,60)'" --audio-out out.mp3`.
- CLI `--waveform <file.dat>`: the audio's waveform peaks as BBC audiowaveform's .dat (for peaks.js and other
  viewers), with `--waveform-zoom`, `--waveform-pixels-per-second`, `--waveform-bits` and
  `--waveform-split-channels` (audiowaveform's `-z`, `--pixels-per-second`, `-b`, `--split-channels`); byte for byte
  audiowaveform's for PCM input (CI compares them).
- FrameReader: `FrameDecoderOptions.Autorotate` (default true): false gives the frames as coded, as `-noautorotate`.

### Faster

- Rotated 4:2:2 and rotated, deinterlaced video decode in-process (they needed the ffmpeg executable).

### Fixed

- Rotated video turns as OpenCV turns it: the converted BGR image, by the display matrix's angle (flips aside), not
  ffmpeg's autorotate, which turns the planes before converting. Rotated 10-bit video and 4:2:2 at 90 or 270 degrees
  now give scenedetect's results (they differed on both decoding paths); checked against OpenCV on Linux for 8- and
  10-bit 4:2:0 and 4:2:2 at 90 and 180 degrees.
- 10-bit video now gives scenedetect's results exactly on Linux (it was ±1, and up to ±100 on sharp colour edges, in
  red and green). Converting it to BGR interpolates chroma from where the stream says the samples sit (left for
  h264), which OpenCV's converter takes from the frame and ffmpeg's command line drops; both decoding paths now use
  it. FrameReader's `VideoStreamInfo.ChromaLocation` reports it.

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
