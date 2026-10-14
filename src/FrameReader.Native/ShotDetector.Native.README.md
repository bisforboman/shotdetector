# ShotDetector.Native

FFmpeg's shared libraries for [ShotDetector](https://github.com/bisforboman/shotdetector) and FrameReader are now
`FrameReader.Native` and `FrameReader.Native.<rid>`. This package only depends on the matching one, so existing
references keep working; new projects reference FrameReader.Native directly:

```
dotnet add package FrameReader.Native.linux-x64
```

What's inside and the licence (LGPL-2.1-or-later, as FFmpeg): FrameReader.Native's README.
