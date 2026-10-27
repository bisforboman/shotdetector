### Fixed

- ShotDetector.FastYuv through the ffmpeg executable (`--decoder process --fast-yuv`, or a converter passed with the executable decoding): a video whose colour matrix, range or (10-bit) chroma siting changes midway is now converted with each frame's own tags, as OpenCV does and as in-process decoding already did, not the first frame's for the whole video, so its per-frame stats match scenedetect's after the change too.
