### New

- `ShotDetection.Detect(YuvVideo, IEnumerable<YuvFrame>, options)`: shot detection on frames another decoder decoded,
  without FFmpeg (nothing on this path loads FFmpeg's libraries or starts ffmpeg, so it can run in the browser with a
  managed decoder). It takes 8-bit 4:2:0 planes with their strides and pts, and the video's properties as FFmpeg reports
  them, and gives the file's result byte for byte with the same converter, which is required: `Yuv420Converter = new
  SwscaleYuv420()` from ShotDetector.FastYuv. Constant frame rate (checked), one size, no rotation; the time range,
  crop, downscale, frame skip and stats apply, deinterlacing and `FfmpegResize` don't.
