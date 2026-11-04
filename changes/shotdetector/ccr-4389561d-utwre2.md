### Faster

- Sequential runs give frame-threaded decoders (h264, HEVC, VP9, ProRes) up to 1920 x 1080 twice the CPUs as decoder
  threads (at most 16), as chunked runs already did: OpenCV's count left the cores waiting on frames in flight. Clean
  video decodes the same; set `DetectionOptions.DecodeThreads` (`--threads`) for another count.
