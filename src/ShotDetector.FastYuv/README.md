# ShotDetector.FastYuv

Optional fast path for [ShotDetector](https://www.nuget.org/packages/ShotDetector): with it, ffmpeg
sends raw yuv420p (or yuv422p) and ShotDetector converts only the ~7% of pixels its resize reads, instead of
ffmpeg converting every full frame to BGR. Results are identical, and large video is faster (the current
numbers: ShotDetector's [weekly benchmarks](https://github.com/bisforboman/shotdetector/actions/workflows/benchmark.yml)).

```csharp
using ShotDetector;
using ShotDetector.FastYuv;

var result = ShotDetection.Detect("video.mp4", new DetectionOptions { Yuv420Converter = new SwscaleYuv420() });
```

It also has `Yadif`, a port of FFmpeg's yadif deinterlacer, row by row: with in-process decoding and
deinterlacing on, only the rows the resize reads are deinterlaced (identical results, and only those rows'
work). It is a FrameReader `IRowDeinterlacer`, so a `FrameDecoder` takes
it too (`FrameDecoderOptions.RowDeinterlacer`).

```csharp
var result = ShotDetection.Detect("broadcast.mxf", new DetectionOptions { Deinterlace = DeinterlaceMode.Auto, Yadif = new Yadif() });
```

The converter is a bit-exact port of FFmpeg libswscale's yuv420p → BGR24 conversion as x86 runs it (verified for
all 2^24 Y/U/V values), so this package is licensed under the **GNU LGPL 2.1 or later**
(COPYING.LGPL). The ShotDetector package itself is MIT. It converts yuv422p (and yuvj422p) too: swscale runs the same
converter for it, with a chroma row for every row (`IYuv422Converter`, which `SwscaleYuv420` implements). Video that
isn't 8-bit yuv420p or yuv422p with an even decoded height uses the normal path anyway. Rotated video is converted
before it is turned, as OpenCV does, so turned 4:2:0 and 4:2:2 take the fast path too. On ARM, where FFmpeg's own
conversion rounds differently, it gives x86's results.

Copyright (C) 2001-2007 Michael Niedermayer, (C) 2009-2010 Konstantin Shishkov (FFmpeg); yadif (C) 2006-2011
Michael Niedermayer, 2010 James Darnley (FFmpeg);
C# port (C) 2026 Jakob Boman.
