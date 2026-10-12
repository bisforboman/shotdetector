### Fixed

- ShotDetector.FastYuv on video that changes resolution mid-stream (two encodes joined), decoding in-process: the
  new size's frames are now converted as OpenCV converts them (scaled to the first size in the one conversion to
  BGR), so the results equal the core path's and scenedetect's. Only those frames are slower.
