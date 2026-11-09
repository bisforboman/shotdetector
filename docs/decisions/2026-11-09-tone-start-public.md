# The tone trim's first frame, public (2026-11-09)

### Question

shotdetector-web, a session working on its backlog row "The reference tone left out of the detection", sent this
with the owner's agreement. A frame-source run with `TrimReferenceTone` that is stopped while still in the leading tone
has no frames of its own, so `ShotDetection.Merge` throws `ArgumentException` "StartTime is after the last frame (128
frames)". To reproduce: tone-silence-320x240-25.mp4 (tone 0-6 s), adaptive, one range from frame 0 stopped at frame 128.
`Detect` on a file that ends inside its tone does the same, so the FFmpeg path agrees. But a host that stops runs (the
page's Stop, split runs whose workers stop) has nothing to show and nothing kept to resume. shotdetector-web estimated
the start itself: the first frame at or after the leading segment's end, from `FileStartPts`, plus two frames.

Choices:

1. Make `ShotDetection.ToneStart(YuvVideo, DetectionOptions)` public: the frame a trimmed run starts at, which the page
   holds a stop until. Additive API, Merge unchanged. *Recommended.*
2. Make Merge of ranges that stop before the run's start give a stopped result with no shots (and a frame count), so
   the run can be kept and resumed. Changes what Merge does, and away from Detect's.
3. Both.
4. Neither for now: shotdetector-web keeps its estimate.

### Answer

The owner, 2026-11-09: **1, the public ToneStart.**

`ShotDetection.ToneStart(YuvVideo, DetectionOptions?)` checks the options as `ScoreRange` does (`TrimReferenceTone`
without `YuvVideo.ReferenceTone` is an `ArgumentException`) and returns the frame `ScoreRange` and `Merge` start the run
at: Detect's seek estimate, with no frame read and nothing of FFmpeg loaded; 0 without the option or a leading tone.
Merge still refuses a run stopped before it, as Detect does. Test: `FrameInputTests.RangesTrimmedToToneAreTheFiles`
(ToneStart is the trimmed result's first frame, 150; stopped at 128 refused; one frame past it merges to Detect's).
