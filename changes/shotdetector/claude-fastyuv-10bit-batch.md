### New

- `IYuv420P10FrameConverter`: a 10-bit converter that converts all of a frame's pixels the resize reads in one call
  (`FrameToBgr`), beside `IYuv420P10Converter`; the pipelines use it when the converter `ForVideo` returns implements
  it. ShotDetector.FastYuv's 10-bit converters do.

### Faster

- ShotDetector.FastYuv on 10-bit 4:2:0 video: each chroma row is filtered horizontally once per frame (it was filtered
  again for every row that reads it), the vertical filter runs four chroma samples at a time, and saturation no longer
  branches. On 4 shared x64 vCPUs, 30 s of 854x480 10-bit h264 took 3.9 s instead of 5.8 s through the ffmpeg
  executable and 3.8 s instead of 5.1 s in-process (the default path: 3.5 s and 3.5 s); 1080p 10.3 s instead of 12.3 s
  in-process (default 12.1 s). The same results, byte for byte.
