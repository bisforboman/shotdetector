# Frame-source parity audit

What `ShotDetection.Detect(path)` (and the CLI's file options) handles or reports that the frame-source path
(`Detect`/`DetectAsync`/`ScoreRange(Async)`/`Merge` over `YuvVideo` and `YuvFrame`s) can't express or gives
differently, as of ShotDetector 3.23.0 (rotation #608, variable frame rate #609 and deinterlacing #614 already done).
One pass, 2026-10-31, from the backlog row "Frame-source parity audit".

How it was checked: the file path in-process with FastYuv's converter (`Decoder = InProcess`,
`Yuv420Converter = SwscaleYuv420`, FFmpeg 8.1.3 shared, BtbN's) against the same file's frames decoded with FrameReader
and given as `YuvVideo` + `YuvFrame`s, as `FrameInputTests.Decoded` builds them (the first frame's colour tags, the
file path's IDCT, ffprobe's values), on Linux x64. Equal means the scene CSV and the stats CSV are byte for byte the
same. The clips are the test fixture's three-shot clip (mpeg4, 320x240, 150 frames at 25 fps, cuts at 50 and 100) and
copies of it made with ffmpeg; the probes were a scratch test class, not committed (the commands are in each row).

Line numbers are at commit 15a2d89 (main after the 3.23.0 release).

## Gaps

| Feature | File path | Frame source | Status | Evidence |
|---|---|---|---|---|
| Frame size changing midway | A frame of another size is scaled to the stream's size in its conversion (bicubic), as OpenCV's `sws_scale_frame` does into its buffer | `YuvFrame.Width`/`Height` (0: the video's) say each frame's size; a frame of another size is refused (`ShotDetectionException`, `InvalidInput`) since #625, where it was read as the video's size (a larger frame's top-left corner) | **Gap: refused** (was silent; scaling it is a backlog row, docs/decisions/2026-10-31-frame-source-size-change.md) | `VideoReader.cs:1631-1634`, `FrameWriter.cs:12`, `VideoReader.InProcess.cs:230`; the check in `VideoReader.Frames.cs` `TakeFrame`. Probe: two libx264 segments, 320x240 then 352x288 (`-vf scale=352:288`), joined with the concat demuxer and `-c copy` in MPEG-TS: the file path's cuts are 50, 100; the frame source's were 50, 75, 100, 60 of 151 stats rows differing, no warning; now "Frame 75 is 352x288, not the video's 320x240" (`FrameInputTests.AResolutionChangeIsRefused`, `AFrameOfAnotherSizeIsRefused`) |
| Deinterlacing in ranges | `Detect(path)` chunks a deinterlaced file too: deinterlacing isn't one of `NotChunkable`'s reasons, and each chunk's decoder reads from before its start | `ScoreRange`/`Merge` refuse `Deinterlace` On, or Auto on an interlaced field order (yadif needs the frame before and after a range's edges). shotdetector-web then runs a deinterlaced video on one worker, with no split run and no resume across visits | **Gap: refused (ranges only)** | `ShotDetection.Ranges.cs:333-336`; `ShotDetection.cs:1498-1503`. Probe: `ScoreRange` with `Deinterlace = On` and `Yadif`: "Deinterlacing isn't available for ranges" |
| Timestamps that start over (two encodes joined, as the samples `sintel_matrix_change.mkv` and `sintel_10bit_siting_change.mkv`) | Taken: the frames are labelled by their packets, the shots told at the end (`TimestampsStartOver`, `TimestampResetTests`) | Refused: `YuvVideo.FrameTimestamps` must rise from frame to frame, and without them the frames must come at the constant rate from frame 0's pts ("Frame 240 is 9.917 s after the first, not 10 s"). A caller can leave `TimeBase` null (frames at n / rate), whose times can differ from the file's | **Gap: refused** (found while checking #627, 2026-10-31) | `VideoReader.Frames.cs` (the `FrameTimestamps` and constant-rate checks). Probe: both samples through `FrameInputTests.Decoded`, with and without `FrameTimestamps`: each throws |
| Odd frame height (8-bit formats) | Converted by FFmpeg (pipeline `SampledBgr`: FastYuv declines it) | Refused: "height must be even" (yuv420p10le of an odd height is taken since #627, as the file path's 10-bit pipeline takes it) | **Gap: refused** (rare) | `VideoReader.Frames.cs:57-58`. Probe: ffv1 at 320x239 (`-vf scale=320:239`): file path fine, frame source throws |
| Other pixel formats (yuv444p, gray, nv12, ...) | Converted by FFmpeg (pipeline `SampledBgr`) | Refused; no managed converter reproduces FFmpeg's for them | **Gap: refused** (rare in browsers' decoders) | `VideoReader.Frames.cs:53-54`. Probe: libx264 yuv444p, ffv1 gray: file path fine, frame source throws |
| Reference tone trim (`TrimReferenceTone`, CLI `--trim-reference-tone`) | Reads the audio with FrameReader's `AudioReader` (FFmpeg), finds the tone, sets the time range | Refused: "needs a file path". `FrameReader.ReferenceTone.Find` takes an `AudioReader` only, so a caller with its own decoded audio can't find the tone either; it can set `StartTime`/`EndTime` itself | **Gap: refused** | `ShotDetection.cs:1189-1194`, `:1435-1436`; `ReferenceTone.cs:54`; `RejectsWhatItCantReproduce("tone")` |

## By design, documented

| Feature | File path | Frame source | Status | Evidence |
|---|---|---|---|---|
| Exports that cut the file (save-images, split-video, HTML with images) | `Export.SaveImages`, `Export.SplitVideo` read `result.VideoPath` | No `VideoPath`: refused. The CSVs, timelines, stats and HTML without images work | By design | `Export.cs:173`, `:335`; `ShotDetection.cs:773` remarks |
| `FfmpegResize` | ffmpeg's scaler | Refused (no FFmpeg) | By design | `VideoReader.Frames.cs:51-52` |
| `DetectionMode.Parallel` (chunks) | Chunks on several cores | Sequential, with the warning "read sequentially, not in chunks: frames from the caller"; the host splits with `ScoreRange` + `Merge` instead | By design | `ShotDetection.cs:1301`, `:1309` |
| Time range, `FrameSkip`, `Parallel` in ranges | Taken | `ScoreRange` refuses each (the sequential frame-source `Detect` takes the first two) | By design (ranges score the whole video) | `ShotDetection.Ranges.cs:337-342`. Probe: each refused |
| A dropped frame inside a range | Taken as it comes | `Merge` refuses it (each frame must be at its `FrameTimestamps` entry); the sequential `Detect` takes it | By design | `ShotDetection.Ranges.cs:418`; `FrameRangeTests` (a range missing frame 60) |
| Decoder, FfmpegDirectory, DecodeThreads, Streaming, ProbeBytes | Used | Ignored (nothing decoded) | By design | `ShotDetection.cs:767-769` (remarks) |
| The converter | The core path (ffmpeg's BGR) by default; FastYuv's when given | Always `Yuv420Converter` (FastYuv's), required | By design: equal to the file path with the same converter; on ARM, FastYuv gives x86's results, the file's core path ARM FFmpeg's | `VideoReader.Frames.cs:39-43` |

## Supported (checked equal)

| Feature | Frame source | Evidence |
|---|---|---|
| All five detectors, several at once, stats, drop/merge short shots, min scene length | Same options | `FrameInputTests.SameAsTheFile` (adaptive, content, threshold, histogram, hash, drop) |
| Time range (`StartTime`, `EndTime`, `Duration`), frame skip | Same options; frames before the start enumerated, not converted | `SameAsTheFile` (start, end, skip); `VideoReader.Frames.cs:303` |
| Crop, downscale (`Downscale`, scenedetect's factor) | `AnalysedSize` as a file's | `VideoReader.Frames.cs:114`; `SameAsTheFile` (crop, crop-scale, scale) |
| Frame rate override (`FrameRate`) | As a file's | `VideoReader.Frames.cs:107-109` |
| Compatibility 0.7.1 | As a file's | `SameAsTheFile("v071")`, `SameAsTheFileAtAVariableRate("v071")`; probe on MPEG-PS with 0.7.1: equal |
| Average frame rate, frame count, duration (scenedetect's length) | `YuvVideo.FrameRate`, `FrameCount`, `Duration` | `VideoReader.Frames.cs:106-113` |
| Timestamps, start pts, edit lists | `YuvVideo.TimeBase`, `StartPts`, `YuvFrame.Pts` | `SameAsTheFileForH264WithBFrames` (mp4 with B-frames, MPEG-TS starting at 1.4 s) |
| Frames without timestamps | `TimeBase` null: frame n at n / rate | Probe: libx264 at 30000/1001 with B-frames, untimed: equal here (the API says the CSVs' times can differ in the last digits) |
| Variable frame rate, dropped frames (sequential) | `YuvVideo.FrameTimestamps` | #609; `SameAsTheFileAtAVariableRate`, `FrameTimestampsAreChecked` |
| MPEG-PS (packets mostly without pts) | The frames' own pts, `FrameTimestamps` left null | Probe: mpeg2video in VOB (15 of 150 packets without pts): equal at 0.7.2 and 0.7.1. Given `FrameTimestamps` from the packets (those without pts left out), frame 54 isn't at its entry and the run throws: for MPEG-PS, give the frames' times or none |
| Rotation | `YuvVideo.Rotation` | #608; `SameAsTheFileTurned` |
| Deinterlacing (sequential), field order | `Deinterlace` + `Yadif`, `YuvVideo.FieldOrder`, `YuvFrame.Interlaced`/`TopFieldFirst` | #614; `SameAsTheFileDeinterlaced` |
| Colour matrix and range of the first frame | `YuvVideo.ColorSpace`, `FullRange`, `PixelFormat` yuvj420p | `VideoReader.Frames.cs:116-123` (refused where FastYuv declines them: `RejectsWhatItCantReproduce("colour")`). Colour primaries and transfer: the file path doesn't use them either (`VideoReader.cs:767-773`) |
| Colour tags changing midway (a spliced stream), since #626 | `YuvFrame.ColorSpace`, `FullRange` (null: the video's): each frame converted with its own, through the file path's `FrameConversions`; a frame whose colours FastYuv declines is refused (the file path converts it with FFmpeg) | `FrameInputTests.SameAsTheFileWhereTheColourTagsChange` (libx264 BT.709 then SMPTE 170M via `h264_metadata=matrix_coefficients=6`, joined in MPEG-TS: content, histogram, adaptive stats equal; before #626, 52 of 151 content stats rows differed from frame 76 on), `EachFrameIsConvertedWithItsOwnColours` |
| Chroma location | `YuvVideo.ChromaLocation` (the first frame's), `YuvFrame.ChromaLocation` (each frame's, null: the video's), since #627; used for yuv420p10le only, as by the file path | `FrameInputTests.TenBitChromaIsSitedAsTheFramesSay`, `SameAsTheFileForEachFormat("h264-10-topleft")`; `VideoReader.cs:772` |
| 10-bit 4:2:0 (yuv420p10le), since #627 | `YuvVideo.PixelFormat` yuv420p10le, 16-bit little-endian samples in the byte planes (strides in bytes): FastYuv's 10-bit converter, as the file path's `Yuv420P10Sampled` | `FrameInputTests.SameAsTheFileForEachFormat` (libx264 and libx265 10-bit, sited left and top-left, resized and not, turned, deinterlaced); samples `sintel_10bit.mp4`, `sintel_10bit_topleft.mkv`, `sintel_rotated_10bit.mp4` (content, histogram, adaptive: CSVs equal, a scratch run, 2026-10-31) |
| 8-bit 4:2:2 (yuv422p, yuvj422p), since #627 | `YuvVideo.PixelFormat` yuv422p or yuvj422p, chroma planes of the full height: FastYuv's 4:2:2 converter, as the file path's `Yuv422Sampled` | `SameAsTheFileForEachFormat` (libx264 yuv422p, MJPEG yuvj422p, resized and not, turned, deinterlaced); samples `sintel_422.mp4`, `sintel_rotated_422.mp4`, `broadcast/xdcam_hd422_1080i25.mxf` deinterlaced (CSVs equal, scratch run) |
| Odd frame width | Taken | Probe: ffv1 at 319x240, downscaled and not (`Downscale = 1`, pipeline `FfmpegScale` on the file): equal |
| IDCT (MPEG-1/2, MPEG-4 Part 2, MJPEG) | The caller's decoder's business | Probe on x64: mpeg4 and mpeg2video decoded at FFmpeg's default IDCT (no `-idct`): equal to the file path. Not checked on ARM, where the file path's rule (`VideoReader.IdctOptions`, internal) matters (CLAUDE.md, ARM gotcha) |
| Shots as found, cancellation, progress, warnings | `OnShot`, `DetectAsync`, `Progress`, `Warnings` | `OnShotTests`, `FrameSourceAsyncTests` |
| Ranges with rotation, variable frame rate, resume | `ScoreRange(earlier, ...)`, `Merge` | `FrameInputTests.RangesFromKeyframesMergeIntoTheSequentialResult` (mpeg4-turned), `RangesAreAtTheirFrameTimestamps`, `FrameRangeTests` |

## DetectedVideo

| Field | Frame source |
|---|---|
| `Width`, `Height` (upright) | From `YuvVideo.Width`/`Height`, swapped by a quarter-turn `Rotation` (`VideoReader.Frames.cs:101-103`) |
| `ProcessedWidth`, `ProcessedHeight`, `CropRegion`, `Crop` | `AnalysedSize`, as a file's (`:114`) |
| `FrameRate`, `OpenCvFrameCount`, `Duration` | As a file's from `YuvVideo` (`:106-113`) |
| `Codec`, `Container` | Passed through from `YuvVideo.Codec`, `Container` (`:127-128`): empty unless the caller sets them |
| `PixelFormat` | `YuvVideo.PixelFormat` |
| `FieldOrder`, `Deinterlaced`, `Rotation`, `Compatibility` | As a file's: from `YuvVideo.FieldOrder`, `DeinterlacesFrames`, `YuvVideo.Rotation`, the option (`:124-125`, `:101`, `:82`) |
| `DecodedInProcess`, `Streamed` | False |
| `Chunks`, `ChunkFallback` | 1, null |
| `Position(frame)` | From the frame's pts, its `FrameTimestamps` entry, or its number (`VideoReader.Frames.cs` `FramePosition`) |
| `DetectionResult.VideoPath`, `ReferenceTone` | Null (see the gaps and exports above) |
| Warnings | The file path's HLS and chroma-siting warnings don't arise (no ffmpeg executable, no siting); a size change is refused on the frame source (#625); colour tags are followed per frame (#626) |

## Proposed issues

For the owner to pick from; not filed.

1. **Frame sources: a frame of another size** (high: silently other results; #625 refuses it, the owner's first step). `YuvFrame` carries no size, so a stream
   that changes resolution midway is read wrong (a larger frame as its top-left corner) or throws (a smaller one).
   Add the frame's size to `YuvFrame` (optional, defaulting to the video's) and either scale it to the video's size as
   the file path does (bicubic, as OpenCV) or, as a first step, refuse it with a clear error, so a result is never
   silently another file's.
2. **Frame sources: each frame's colour tags** (medium: silently other stats on spliced streams; done in #626). The file path
   converts each frame with its own matrix and range; the frame source with the video's. Optional per-frame
   `ColorSpace`/`FullRange` on `YuvFrame` (null: the video's), converted as `FrameConversions.For` does.
3. **Frame sources: 10-bit 4:2:0 and 8-bit 4:2:2** (medium: HEVC Main 10, 10-bit H.264, ProRes-like 4:2:2 refused; done in #627).
   FastYuv already has both converters for the file path; the frame source needs 16-bit planes in `YuvFrame` (or a
   variant), `YuvVideo.ChromaLocation` for the 10-bit interpolation, and the pixel-format check widened.
4. **Ranges: deinterlace** (medium, from shotdetector-web: a deinterlaced video runs on one worker, no split run, no
   resume). Let `ScoreRange` take the frame before its first and after its last (or say how many it was given) so
   yadif sees the same neighbours as the sequential run, and `Merge` accept deinterlaced ranges.
5. **Reference tone without a file** (low). `FrameReader.ReferenceTone.Find` over samples the caller decoded (an
   overload on PCM), so a frame-source host can find the tone and set the time range; or `TrimReferenceTone` taking
   given tone segments.
6. **Frame sources: odd heights and other pixel formats** (low, rare in practice): FastYuv declines odd heights and
   has no converter for 4:4:4 or gray; the file path falls back to FFmpeg's conversion, which the frame source can't.
   Only worth it if a caller meets them.

Not proposed: the IDCT rule on ARM (unverified here; a browser host decodes with portable code). If a native ARM host
decodes MPEG-1/2/4 or MJPEG itself, check it then; the rule would need a public form (FrameReader or `YuvVideo`).
