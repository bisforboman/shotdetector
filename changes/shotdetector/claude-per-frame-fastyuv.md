### Fixed

- ShotDetector.FastYuv, decoding in-process: a video whose colour matrix, range or (10-bit) chroma siting changes midway is now converted with each frame's own tags, as OpenCV does, not the first frame's for the whole video, so its per-frame stats match scenedetect's after the change too.
