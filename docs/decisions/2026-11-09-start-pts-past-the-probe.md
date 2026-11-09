# A video's start_pts past libavformat's probe (2026-11-09)

### Question

The clock audit (docs/backlog-done.md, 2026-11-08) found a Matroska file whose video stream FFmpeg gives start_pts 0
while its first packet and frame are 5 s in. The cause is libavformat's probe: `avformat_find_stream_info` reads about
5 s of the file. When the video's first packet comes after that (a video that starts more than about 5 s after the
audio: the test clips switch between a 5 s and a 5.5 s offset), the stream gets no start time of its own and takes
the format's. A bigger probe (`-analyzeduration`, `-probesize`) gives the right one.

OpenCV probes with the same defaults, so its start_time is 0 there too:

- Its frame times count from start_time. scenedetect's labels and cuts on such a file are file times (the first
  frame at 5 s is frame 50). ShotDetector already matched them: every detector, scene list and stats exact.
- Its seek counts frames from `first_frame_number`, its first frame's number from start_time (`dts_to_frame_number`:
  seconds times the rate, plus 0.5, truncated). scenedetect's `time -s 12.5` on the 5 s clip lands on frame 125
  after the first, at 17.5 s. ShotDetector's `SeekFrame` measured from start_pts, so its constant-rate check failed,
  and its variable-rate fallback landed at 12.5 s: six scene list cells differed.
- `TrimReferenceTone` is ShotDetector's own. Its start seeks and its end compares frame times, so a fix to the seek
  has to move the start as well.

Choices put to the owner: fix both (seek as OpenCV does, and move the tone trim with it); fix the tone only and
document the seek as a difference; document only; look for real files that do this first.

### Answer

The owner, 2026-11-09: **fix both.**

- `VideoReader.FirstFrameNumber` is OpenCV's `first_frame_number`, from `Pts[0]` (the first frame's pts) against
  start_pts. `SeekFrame` measures its estimate and its fallback's target from it. It is 0 on every file whose
  start_pts is its first frame's (every sample in tools/make-samples.ps1), so nothing else moves.
- `TrimmedToTone` sets the start that much earlier (`VideoReader.SeekShift`); the end keeps start_pts as its origin.
  Before this change, the trim's start was right on such a file only because the seek went by frame times.
- Test: `FrameInputTests.SameAsTheFileTrimmedToTone("late6")`, a tone clip with the video 6 s late: the trim on both
  decoders, and `StartTime` 7.2 s landing where scenedetect 0.7.2's does (frame 331, 13.2 s).
- Frames from the caller keep their seek (`FramesSeekFrame`): the caller states StartPts. On such a file the probe
  has no pixel format either, so a host describes the frames from the first decoded one.
