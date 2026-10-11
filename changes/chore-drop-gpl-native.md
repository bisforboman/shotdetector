### Breaking changes

- `ShotDetector.Native.Gpl.<rid>` (our FFmpeg build with x264, GPL) is no longer built or published; earlier versions
  stay on nuget.org. FrameReader's `VideoWriter` is unchanged and still encodes H.264 byte for byte as
  `ffmpeg -c:v libx264`, but needs FFmpeg 8 libraries with libx264 of your own, such as a "gpl" shared build, loaded
  with `FFmpegLibraries.Load(folder)`. `ShotDetector.Native.<rid>` (LGPL) is unchanged.
