# LoadScenes for frames from the caller (2026-10-31)

Issue #647, for shotdetector-web#90 (correcting cuts on the page, then exporting from the edited list).
`ShotDetection.LoadScenes(videoPath, sceneListCsv, column, options)` takes the video and the scene list as file paths:
it probes the video for its frame rate, length and positions, and reads the CSV from disk. A browser host has
neither, though a `YuvVideo` carries everything the probe gives.

## Question

How should frames from the caller get LoadScenes?

## Choices

1. **`YuvVideo` + CSV text**: `LoadScenes(YuvVideo video, TextReader sceneList, string column = "Start Frame",
   DetectionOptions? options = null)`, with the same parsing, time range, DropShortScenes and MergeLastScene as the
   path version. The page writes its edited list as CSV text (`Shots.Csv`) and passes a `StringReader`.
2. **From a result plus a cut list**: `ShotDetection.FromCuts(DetectionResult detected, IEnumerable<long> cutFrames,
   DetectionOptions? options = null)`, re-cutting an earlier result (either source) with no CSV parsing: closest to the
   page's in-memory edit, but a new concept beside scenedetect's load-scenes.
3. **Both**.

## Answer

**`YuvVideo` + CSV text** (the owner, 2026-10-31).

## How

- Both overloads share the parsing and decisions (`Loaded`). The frame-source one makes its reader from `YuvVideo`
  with no frames and without the conversion's checks (`converts: false`): no converter is needed, and a pixel format
  or odd height a converter wouldn't take is fine, since nothing is converted.
- Positions come from `YuvVideo.FrameTimestamps` as a file's come from its packets (without them, frame n is at
  n / rate, as for Detect on frames). There is no `VideoPath`, so the exports that cut the file aren't available.
- Tests: `FrameInputTests.LoadScenesFromTextIsTheFiles` (an edited list, by frame, timecode and seconds, with a time
  range, DropShortScenes and MergeLastScene, on the mpeg4 clip and H.264 with B-frames) gives `LoadScenes(path)`'s
  shots, cuts, positions, scene CSV and EDL, QP, keyframes and shot-change exports byte for byte;
  `FrameRangeTests.LoadScenesTakesTheVideosPropertiesAndAListAsText`.
