# Edited cut lists that keep a run's frame times (2026-11-02)

shotdetector-web#90 lets a viewer correct cuts on the page and then export from the edited list, through
`ShotDetection.LoadScenes(YuvVideo, TextReader, column, options)` (docs/decisions/2026-10-31-load-scenes-frame-source.md).
Its session found that reading back an unedited list doesn't give the run's result on two samples. The FFmpeg path
(`LoadScenes(path)`) does the same:

- **Variable frame rate** (shotdetector-web's `samples/vfr-320x240.mp4`): `Detect` puts the cuts at the frames' times,
  00:00:02.000 and 00:00:05.600. `LoadScenes` of the same Start Frame list (1, 33, 90) puts them at frame / average
  rate, 00:00:02.022 and 00:00:05.625.
- **An edit list that ends before the media** (`samples/edit-end-352x288-25.mp4`, 75 frames played, nb_frames 114):
  `Detect`'s last shot ends at frame 75 (3.000 s), `LoadScenes`'s at 114 (4.560 s, OpenCV's frame count).
- **The length in general**: `LoadScenes` ends at nb_frames, not at the frames FFmpeg plays, also on fragmented MP4
  (`fragmented-320x240-25.mp4`: 114 played, 122 counted). Where the `moov` lists only the first fragment's samples
  (`fragmented-moov-320x240-25.mp4`, nb_frames 38 of 114), every cut after frame 38 is dropped.

scenedetect 0.7.2's own `detect-content` and then `load-scenes` on both samples give exactly these numbers (checked
2026-11-02), so `LoadScenes` is faithful. For the page, though, fixing one cut on a VFR film would move every other cut.

## Choices

1. Add an overload that keeps the run's positions: it takes a `DetectionResult` and an edited cut list (frame numbers)
   and keeps that run's frame times and length. `LoadScenes` stays scenedetect's (recommended).
2. Leave it as scenedetect's: the page exports an edited list through `LoadScenes`, and the difference is documented
   in docs/frame-source-parity.md.
3. Change `LoadScenes` itself to use frame timestamps and the decoded length, as a documented difference from
   scenedetect.

## Answer

**1, an overload that keeps the run's positions.** It goes in the backlog's Next, and shotdetector-web adopts it once
it ships.

## The overload's shape (2026-11-02, while building it)

- **Frames as decoded, not scenedetect's frame numbers.** The cuts are 0-based frames counted from the video's first
  as decoded, what `DetectedVideo.Position(int)` takes. A scene list's Start Frame is a time × the average rate: on a
  variable frame rate several frames share one (frames 49 and 50 of the VFR sample both round to the same number),
  so an editor couldn't name the frame it means. `DetectionResult.CutFrames` gives a run's cuts as frames and
  `DetectionResult.FrameOf` any position's.
- **A run's cut is kept as it was.** A frame the run cut at gives the run's cut, not the frame's position: the
  threshold detector's cuts are bare frame numbers, which print otherwise. That makes the round trip exact.
- **The run's range.** The shots start and end where the run's did (the run already applied its time range);
  StartTime, EndTime and RangeDuration are refused rather than narrowing it, as narrowing would need the run's seek
  rules again on frames it read. Frames outside the run are left out. MinSceneLength, DropShortScenes and
  MergeLastScene apply in frames at the run's rate, as Detect's.
- Stats, black and freeze spans, warnings and the video are the run's: they don't depend on the cuts.
