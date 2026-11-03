### New

- `ShotDetection.LoadScenes(DetectionResult, cutFrames, options)`: an edited cut list at a detection's own frame times
  and length, for exporting after correcting cuts. The cuts are frames (0-based, as decoded; a run's own are
  `DetectionResult.CutFrames`, a position's frame `DetectionResult.FrameOf`); each cut is where the run had that frame,
  and the last shot ends where the run's did. Unlike scenedetect's load-scenes (`LoadScenes` of a scene list, which
  stays scenedetect's), fixing one cut on a variable frame rate, after an edit list that ends early or on fragmented MP4
  moves no other, and a run's own cuts given back give exactly its shots, scene list and exports. MinSceneLength,
  DropShortScenes and MergeLastScene apply; the time range is the run's. Nothing is decoded.
