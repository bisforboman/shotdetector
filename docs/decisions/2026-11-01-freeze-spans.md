# Freeze spans on the detection pass (2026-11-01)

Beside black spans (docs/decisions/2026-11-01-black-spans.md), the owner put freeze detection in scope from the start,
for `shotdetect qc`: a managed measure in FrameReader beside PictureMeter, exact against `ffmpeg -vf freezedetect`
(its mafd arithmetic, defaults n=0.001, d=2), exposed as `DetectionOptions.FreezeDetect` and
`DetectionResult.FreezeSpans`.

What scoping found (FFmpeg 8.1's libavfilter/vf_freezedetect.c): unlike blackdetect, freezedetect isn't a judgement of
each frame alone. Each frame is compared with a reference, the last frame that differed from it (the still picture's
first), however far back. Its comparison is scene_sad over every plane, an exact integer sum on every CPU (the SIMD
versions finish the rows' tails in C), over the samples and 2^depth. Its durations are pts rescaled to microseconds
(av_rescale_q, rounded). A still running at the end gets no freeze_end: the filter has no end-of-stream handling.

## Questions

1. With parallel reading: a chunk can't judge its first frames without the previous chunk's reference picture. How
   should `FreezeDetect` work with chunks?
2. A still picture lasting to the end: freezedetect prints freeze_start but never freeze_end. What should
   `FreezeSpan`'s End be then?

## Choices

1. **Read sequentially** (recommended): with `FreezeDetect` set, Detect reads in one pass, and Warnings says why when
   Parallel was asked for. **Chunks with a boundary re-read**: re-read from a chunk's reference frame where a still
   crosses a boundary, until the comparison chains agree (exact, parallel, more code and a partial extra decode).
   **Chunks, approximate**: each chunk compares from its own first frame (not exact at a boundary).
2. **Null, as freezedetect** (recommended): `FreezeSpan(StartFrame, int? EndFrame, double Start, double? End)`, End,
   EndFrame and Duration null for a freeze still running at the end. **The last frame, like blackdetect**: End at
   the last frame with a flag saying ffmpeg wouldn't print it.

## Answer

**Read sequentially** and **null, as freezedetect** (the owner, 2026-11-01).

## How

- FrameReader: `FreezeDetector`, the filter's state machine and arithmetic (plane sizes from av_image_get_linesize, the
  chroma's height shifted down; mafd = sad / samples / 2^depth at most n; d in whole microseconds; a frame with no pts or
  an earlier one than the reference has a duration from the frame rate, or 0 without one, as freezedetect's), and
  `Finish(frames)`, the spans as freezedetect reports them after so many frames (the in-process reader decodes ahead of
  the frames detection uses, and a run can stop at its end time). A class of its own beside `PictureMeter`: the meter's
  figures are each frame's alone and its measures join across ranges, where the detector holds a reference picture.
- Executable: `freezedetect=n:d` just before showinfo, beside blackdetect, as detection reads the frames (after yadif
  when deinterlacing). Its lines for a frame come before the frame's showinfo line; freeze_start's value, the still's
  first frame's time, is matched to the frames given by FFmpeg's time string for each frame's pts.
- In-process: FrameReader's detector on each decoded frame (deinterlaced when deinterlacing); a pixel format freezedetect
  takes only after a conversion is refused with a message to use the executable. Frames from the caller: the detector
  on the given planes (deinterlaced whole when deinterlacing). Ranges refuse `FreezeDetect`.
- What is compared, as for black spans: whole frames, every frame, within the time range (a still the start cuts into
  starts at the first frame read), times from the video stream's start. freezedetect's frame-rate fallback is left out
  in-process (no frame rate given: a duration without pts is 0), so a still across a frame without pts can differ from
  ffmpeg's there.
- Printing: `FreezeSpan.ToString()` is freezedetect's three log lines' values on one line (`freeze_start:1.2
  freeze_duration:3.04 freeze_end:4.24`), the start alone for a still at the end, with FFmpeg's time strings.

## Checks

- FrameReader `FreezeDetectorTests`: freezedetect's lines on clips with a held still, a still under light noise and a
  still to the end, at five n and d pairs, in yuv420p, yuv422p10le, gray, NV12 and x264 (B-frames), odd sizes; and
  av_rescale_q's rounding.
- `FreezeDetectTests.SameAsFreezedetect`: the spans' text equals `ffmpeg -vf freezedetect`'s on the executable and
  in-process, FastYuv, a start time, deinterlaced (against `yadif,freezedetect`), a Stream, 10-bit, frame skip, and
  Parallel asked for (read in one pass, the warning given).
- `FrameInputTests.SameAsTheFileWithFreezeSpans`: frames from the caller give the file path's spans.
