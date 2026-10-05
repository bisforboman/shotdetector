# ShotDetector.FastYuv

Optional fast path for [ShotDetector](https://www.nuget.org/packages/ShotDetector): with it, ffmpeg
sends raw yuv420p and ShotDetector converts only the ~7% of pixels its resize reads, instead of
ffmpeg converting every full frame to BGR. Results are identical; on 1080p video detection takes
about 10 s instead of 17 s for 5000 frames.

```csharp
using ShotDetector;
using ShotDetector.FastYuv;

var result = ShotDetection.Detect("video.mp4", new DetectionOptions { Yuv420Converter = new SwscaleYuv420() });
```

The converter is a bit-exact port of FFmpeg libswscale's yuv420p → BGR24 conversion (verified for
all 2^24 Y/U/V values), so this package is licensed under the **GNU LGPL 2.1 or later**
(COPYING.LGPL). The ShotDetector package itself is MIT. Video that isn't 8-bit yuv420p with an
even height uses the normal path anyway.

Copyright (C) 2001-2007 Michael Niedermayer, (C) 2009-2010 Konstantin Shishkov (FFmpeg);
C# port (C) 2026 Jakob Boman.
