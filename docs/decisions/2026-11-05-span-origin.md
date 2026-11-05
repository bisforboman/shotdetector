# Black and freeze spans from the file's start (2026-11-05)

shotdetector-web found (ShotDetector 3.28.0) that `DetectionResult.BlackSpans` and `FreezeSpans` differ from
`ffmpeg -vf blackdetect` / `freezedetect` on Caminandes: Gran Dillama remuxed to a fragmented MP4: video start_pts
1000 in 1/24000 (B-frames, no edit list), audio at 0. ffmpeg printed black_start:0.0416667 black_end:2, ShotDetector
0 and 1.958333, a frame early on every path; the docs promised ffmpeg's times.

What ffmpeg does: without `-copyts` its command line subtracts the input's start time (AVFormatContext.start_time,
the earliest stream's; ts_offset, rescaled into each stream's time base) from every timestamp, so the filters print
times from the file's start. Checked with FFmpeg 8.1 on Matroska files whose video starts 0.5 s late: audio at 0
(printed 0.523, the video's first pts), audio after the video at 1 s and no audio (both printed 0, the file's start
being the video's), and the earlier vfr-fragmented-320x240.mp4 (video only, start 0.08, printed from 0.08). `-an`
and `-map 0:v` change nothing; `-copyts` prints the absolute times.

## Question

From which origin should the spans count, and how do frames from the caller give it?

## Choices

1. **The file's start, as ffmpeg prints it** (taken, the owner's "fix it now"): the executable from ffmpeg's own
   "Input #0" Duration line (`start:`, to the µs, rescaled to the stream's time base as ffmpeg does), in-process from
   FrameReader's `FrameDecoder.StartOffset` (ffmpeg's ts_offset already), frames from the caller from a new optional
   `YuvVideo.FileStartPts` (in TimeBase; null: the video stream's start, the old behaviour).
2. Keep the video stream's start and document the difference: the spans would then not be ffmpeg's on such files.

## Answer

1. A file whose video starts first, or that has no other stream, is unchanged. Streamed input was already timed from
   the input's start. `shotdetect qc`'s spans move with them (to the timeline its audio lanes already use).
   TrimReferenceTone's start, which takes a tone end on the file's timeline as a time on the video's, is a backlog
   item.
