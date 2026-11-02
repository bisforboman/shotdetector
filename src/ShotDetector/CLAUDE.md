# src/ShotDetector: notes beside the code

Read with the library's decoding code (`VideoReader*.cs`, `ShotDetection*.cs`, `InProcess*.cs`, `FrameWriter.cs`).

## How it reads video

- `VideoReader`: ffprobe + ffmpeg rawvideo over a pipe (a named pipe on Windows); streaming mode parses `showinfo`
  for timestamps. In-process, the same frames come through FrameReader: `FrameWriter` builds the pipelines' layouts on
  FrameReader's `BgrConverter`, `InProcessProbe` formats `MediaProbe` as ffprobe text, `InProcess.Map` maps
  `FrameReaderException`.
- IDCTs: MPEG-1/2, MJPEG and MPEG-4 Part 2 decode with FFmpeg's C IDCT (`VideoReader.IdctOptions`; `-idct simple`,
  and FrameReader's `CodecOptions` in-process, so FrameReader alone keeps FFmpeg's default), except MPEG-4 that FFmpeg
  gives the Xvid IDCT (`Mpeg4Idct`: signature, else FourCC). docs/decisions/2026-10-18-mpeg4-xvid-idct.md.

## Gotchas (each cost time once)

- ffmpeg `-ss` lands on different frames per codec; only the `-copyts` + `select=gte(pts,P)` seek is exact (`-ss` two
  frames early; VideoReader).
- Crop: ffmpeg must crop *after* `scale,format=bgr24`; scenedetect's downscale factor uses crop size + 1 (both dims).
- End of a time range is compared as frame numbers (`Position(i).PlusFrames(1).FrameNum >= end`), with
  `-e`/`-d` rounded to frames at the average rate; frame skip reads ahead before checking.
- MPEG-PS packets mostly lack a pts: OpenCV uses the frame's dts, so the reader takes per-frame timestamps
  from a decoding pass when any packet has none (`FrameTimestamps`); in-process they come with the frames instead
  (`TimestampsArrive`, `PacketOfArrived`: `PacketOf` over those so far, the pass where they don't settle a frame or
  something needs them all first; docs/decisions/2026-10-27-pre-scan.md); through the executable ffprobe's pass runs
  alongside the read and labels frames as its timestamps arrive (`PacketOfFromPass`; showinfo's own times are ffmpeg's
  best-effort ones, which differ: docs/decisions/2026-10-29-mpeg-ps-pass-executable.md).
- 10-bit (any format without swscale's unscaled path to BGR): chroma is interpolated from where the samples sit.
  OpenCV's `sws_scale_frame` takes that from the frame (h264/HEVC 4:2:0: left); ffmpeg's command line drops it before
  its filters (center), so the executable path passes `in_chroma_loc` (the first frame's, read with ffprobe or
  FrameDecoder: a container's header can contradict the stream's own, as can its colour matrix and range, which
  FastYuv takes from there too; a file is read again from each frame whose siting differs, with its own, a stream
  keeps the first frame's and warns: docs/decisions/2026-10-27-per-frame-tags-executable.md) and BgrConverter's row
  context sets swscale's position.
- Linux OpenCV can't decode AV1 at all.
- Frame labels (0.7.1): each decoded frame's showinfo time is matched to its packet (`PacketOf`), so frames
  the decoder drops make numbers skip as in OpenCV. Files and streams both run with `-copyts` (absolute times; streams
  since docs/decisions/2026-10-29-stream-timestamp-reset.md): a stream's showinfo times have the input's start
  subtracted as they are read (`InputStart`, from ffmpeg's "Input #0" Duration line, the connection being read), so
  ffprobe's `_startPts` must not be subtracted there too.
- The core path's frames: ffmpeg converts to BGR, then `remap` (two looping 16-bit PGM maps, x and y) keeps only
  `CvResize.SourceCols` x `SourceRows`; `ResizeSampled` resizes from that. Equivalent to the full frames by test
  (`SampledFramesAreTheFullFramesResized`); `DetectionOptions.FullFrames` (internal) brings the old path back.
- Streamed input needs the container headers first (mkv/webm/ts/mov or faststart mp4); ffmpeg reports a non-faststart
  mp4 on a pipe as "moov atom not found" or, when the whole file fit the probe prefix, "partial file". ShotDetection
  spills those to a temp file first (`VideoReader.HeadersLast`: mdat before moov in the first bytes).
- Windows named pipe path is `\\.\pipe\…`; stdout's 4 KB pipe is far slower. Kill ffmpeg race-free (`Kill` helper).
