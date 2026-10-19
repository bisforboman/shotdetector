### Fixed

- With ShotDetector.FastYuv and the ffmpeg executable, a video whose colour matrix or range changes midway (two streams
  joined, a spliced broadcast) could give other cuts than PySceneDetect: ffmpeg converted the frames after the change
  before FastYuv read them. They now reach FastYuv as decoded, and the cuts are exact. Stats after such a change can
  still differ slightly, because FastYuv converts every frame with the first frame's colours.
