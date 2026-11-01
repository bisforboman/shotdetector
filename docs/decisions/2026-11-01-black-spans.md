# Black spans on the detection pass (2026-11-01)

The Jellyfin plugin chose to take black spans from ShotDetector's own pass (docs/decisions/2026-11-01-jellyfin-black-spans.md),
and `shotdetect qc` needs them too. The owner's direction, through the coordinator: "both paths, one API"; equal to
`ffmpeg -vf blackdetect`; the executable path by blackdetect in VideoReader's graph, in-process and frames from the
caller by FrameReader; working with parallel chunks; `DetectionOptions.BlackDetect` with blackdetect's d, pic_th and
pix_th at its defaults. This file records the API question put to the owner and the choices made building it.

## Question

What should `DetectionResult.BlackSpans` hold?

## Choices

1. **Frames and blackdetect's seconds** (recommended): `BlackSpan(int StartFrame, int EndFrame, double Start, double End)`,
   the frame indices to line up with shots and the times exactly as blackdetect computes them (pts times the time
   base; End is black_end: the first frame that isn't black, or the last frame at the end), printing as its log line.
2. **FrameTime, like shots**: `BlackSpan(FrameTime Start, FrameTime End)` on scenedetect's positions; times can differ
   from blackdetect's in the last digits.
3. **FrameReader's FrameSpan** as `PictureMeasures.BlackSpans` gives it, with the time base on the result.

## Answer

**1, frames and blackdetect's seconds** (the owner, 2026-11-01).

## How (choices made building it)

- **Options**: `BlackDetectOptions { MinDuration = 2 s, PictureThreshold = 0.98, PixelThreshold = 0.10 }`, blackdetect's
  `d`, `pic_th`, `pix_th` and their defaults; `DetectionOptions.BlackDetect` null (the default) is off.
- **One judgement per frame, spans once at the end.** blackdetect's judgement of a frame depends on that frame alone;
  only the runs and their ends depend on neighbours. So every path records each frame's judgement and pts, chunks read
  in parallel join by concatenation (each chunk without its overlap frame), and the spans are worked out once over the
  whole run as blackdetect does (`BlackFrames.Spans`): a run from a black frame to the next that isn't, or to the last
  frame's own pts at the end (FFmpeg's FIXME), kept when black_end - black_start in time-base units reaches
  `d / av_q2d(tb)` truncated to an integer (check_black_end).
- **Executable**: `blackdetect=d=0:pic_th:pix_th,metadata=mode=print` just before showinfo in VideoReader's graph. At
  d=0 every run is marked; metadata=print logs each mark (lavfi.black_start, lavfi.black_end) with its frame's pts, and
  each frame showinfo reports is black when the last mark at or before its pts was a start.
- **In-process and frames from the caller**: FrameReader's new `BlackPixels` (blackdetect's threshold and count on the
  luma plane), not the whole `PictureMeter`: the meter also measures signalstats, cropdetect and bars on every frame,
  which black spans don't need, and doesn't take gray or NV12, which blackdetect does. `PictureMeter` uses the same
  threshold (`BlackPixels.Threshold`) and gained blackdetect's pix_th and pic_th, so its spans match for any thresholds
  too. In-process, a pixel format blackdetect takes only after ffmpeg converts it (RGB, packed YUV) is refused with a
  message to read with the executable.
- **What is measured: the frames detection reads, as decoded.** Whole frames (the crop is detection's, not the
  video's); every frame (frame skip skips only the detectors' frames); within `StartTime`/`EndTime` when set (a run
  the start cuts into starts at the first frame read; one still black at the end of the range ends at its last frame);
  rotation doesn't change a count.
- **Deinterlacing**: after yadif, as detection reads the frames (equal to `ffmpeg -vf yadif,blackdetect`, in yadif's
  doubled pts and halved time base, which the minimum duration is counted in). FrameReader's decoder deinterlaces inside
  (filter or row-wise yadif), so in-process the frames before yadif aren't at hand; the executable measures the same
  frames to agree.
- **Times from the video stream's start**, as the shots' times (pts minus the stream's start pts; a streamed input's
  from its first frame). That is `ffmpeg -vf blackdetect`'s output whenever the container starts with the video stream
  (the CLI shifts by the container's start time).
- **Ranges** (`ScoreRange`, `Merge`) refuse BlackDetect for now: `RangeScores` would have to carry each frame's
  judgement. A sequential `Detect` on frames from the caller takes it.
- **Printing**: blackdetect's log line, `black_start:2.92 black_end:5.64 black_duration:2.72`, with FFmpeg 8.1's time
  strings (`av_ts_make_time_string2`: six decimals, more under a second, trailing zeros dropped) and black_duration from
  the pts difference, as FFmpeg prints it.

## Checks

- `BlackDetectTests.SameAsBlackdetect`: the spans' text equals `ffmpeg -vf blackdetect`'s log lines on a clip fading
  through black, with a short dip and black to the end: executable and in-process, FastYuv, parallel chunks, a start
  time, deinterlaced (against `yadif,blackdetect`), 10-bit, full range, a Stream, frame skip, other thresholds, pic_th 1.
- `FrameInputTests.SameAsTheFileWithBlackSpans`: frames from the caller give the file path's spans (8-bit, 10-bit,
  deinterlaced).
- The Sintel trailer (480p, 9 spans at d=0.1) equal on the executable, in-process, in 4 chunks and with FastYuv in 3.
  The cost on the pass: about 6% more wall time on the executable path, none measurable in-process (a second
  blackdetect pass costs about 60%).
