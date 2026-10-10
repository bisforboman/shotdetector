# ShotDetector.Native.Gpl.&lt;rid&gt;

[ShotDetector.Native](https://www.nuget.org/packages/ShotDetector.Native.linux-x64)'s FFmpeg libraries with
**x264** added, so FrameReader's `VideoWriter` (the FrameReader package, a ShotDetector dependency) encodes H.264 in-process. One package
per platform: `win-x64`, `linux-x64`, `linux-arm64`, `linux-musl-x64`, `osx-arm64`. Use it **instead of**
ShotDetector.Native.&lt;rid&gt; (the same file names), not next to it.

```
dotnet add package ShotDetector.Native.Gpl.linux-x64
```

```csharp
using var writer = new VideoWriter("out.mp4", new()
{
    Width = 1280, Height = 720, FrameRate = new(25, 1), FrameFormat = FrameFormat.Bgr24,
    PixelFormat = "yuv420p", Crf = 23, Preset = "medium",
    Gop = 50, KeyintMin = 50, KeyframeEvery = TimeSpan.FromSeconds(2), X264Params = "scenecut=0",
    Audio = new() { SampleRate = 48000, Channels = 2 },   // optional AAC track: writer.WriteAudio(samples)
});
foreach (var frame in frames)
    writer.Write(frame);   // Width x Height x 3 bytes, rows packed
```

The file is the one `ffmpeg -c:v libx264` (with the same options) writes from the same frames with the same FFmpeg
build, byte for byte when both set bitexact.

## Licence: read this first

- **GPL.** x264 is under the GNU General Public License 2 or later, and building FFmpeg with it makes these libraries
  GPL-2.0-or-later (`COPYING.GPL`). In the Free Software Foundation's view, an application that loads them is a
  combined work: if you distribute your application with this package, you have to distribute it under the GPL, with
  its source. ShotDetector itself stays MIT, and ShotDetector.Native (without x264) LGPL. x264 LLC sells a
  commercial x264 licence if the GPL doesn't fit.
- **Patents.** H.264 is covered by Via LA's AVC patent pool. Whoever distributes an H.264 encoder is a licensee
  (encoders are royalty-free up to 100,000 units a year, but the licence has to be signed); free distribution isn't
  exempt. This package doesn't change that, whichever licence you use.

## What's inside

Everything in ShotDetector.Native (FFmpeg 8.1.3's decoders and demuxers, the AAC, MP3 and PCM encoders, common
muxers, the filters FrameReader uses; zlib, dav1d and LAME linked in), plus x264 from its stable branch, statically
linked, as FFmpeg's `libx264` encoder. `configure.txt` has the source URLs, the x264 commit and the exact configure
line; the build script is `tools/native/build-ffmpeg.sh <rid> <dir> gpl` in the repository.
