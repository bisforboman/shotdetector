# FrameReader

Fast video frame reading for .NET, in-process through FFmpeg's libraries: no ffmpeg process per call, no pipes, no
allocation per frame. Frames come at the size and pixel format you ask for, and the pixels are exactly the bytes
ffmpeg's command line gives (`-vf scale=W:H:flags=bicubic,format=F -f rawvideo`), so you can check them against it.

Extracted from [ShotDetector](../../README.md)'s in-process decoding. Internal to this repository for now: not a
NuGet package of its own yet (ShotDetector's package carries `FrameReader.dll`), and its API may still change.
Plan and status: [docs/frame-reader-library.md](../../docs/frame-reader-library.md).

## Quick start

```csharp
using FrameReader;

using var reader = new VideoFrameReader("video.mp4", new FrameReaderOptions
{
    Width = 160, Height = 90,          // both or neither (neither: the frame's own size)
    Format = FrameFormat.Gray8,        // Bgr24 (default), Rgb24, Bgra32, Rgba32, Gray8, Yuv420p
});
while (reader.TryRead(out var frame))
{
    // frame.Index, frame.Time (from the input's start, as ffmpeg gives it), frame.Pts
    ReadOnlySpan<byte> pixels = frame.Data; // Width x Height, rows packed
}
```

Audio: `AudioReader` reads the best audio stream as interleaved float32, at a chosen sample rate and channel count
(default: the stream's own), the samples `ffmpeg -vn -ar R -ac C -f f32le` gives; `Seek(time)` as `-ss`.

```csharp
using var audio = new AudioReader("video.mp4", new() { SampleRate = 16000, Channels = 1 }); // e.g. for speech models
while (audio.TryRead(out var chunk))
    Process(chunk.Samples); // ReadOnlySpan<float>, valid until the next read
```

`MediaProbe.Probe(path or Stream)` reads a file's properties without decoding (codec, size, pixel format, colour
tags, field order, frame rates, time base, duration, frame count, rotation, container, audio; optionally every
packet's timestamp). `FrameDecoder` is the lower level: decoded frames as FFmpeg holds them (`GetPlane`), with
seeking.

## Frame lifetime

`VideoFrame` is a `ref struct` over the reader's own buffer: `Data` is valid until the next `TryRead` (or
`Dispose`). Copy it (`frame.Data.ToArray()`) to keep a frame.

## What it handles

- Files, image sequence patterns (`frames/%04d.png`, with `InputOptions` `framerate`), and `Stream`s (headers first:
  mkv, webm, ts, mov or a faststart mp4).
- Deinterlacing (`FrameDecoderOptions.Deinterlace`, yadif as `-vf yadif`), the display matrix applied as ffmpeg's
  autorotate does, decoder threads.
- Errors as `FrameReaderException` with a `Reason`: the libraries missing, an input FFmpeg can't open (or without
  video), a decoding failure.

## FFmpeg's libraries

FFmpeg 8's shared libraries (libavcodec 62, libavformat 62, libswscale 9, libavutil 60; libavfilter 11 to
deinterlace). The ShotDetector.Native.&lt;rid&gt; packages carry a decode-only build for win-x64, linux-x64,
linux-arm64, linux-musl-x64 and osx-arm64; a "shared" FFmpeg 8 build or the system's (`apk add ffmpeg-libs`) works
too. They load once per process: from `FrameDecoderOptions.LibraryDirectory`, else next to the app, else the system's
places. FrameReader is MIT; FFmpeg's libraries are LGPL and loaded dynamically.
