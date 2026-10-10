# FrameReader

Fast video frame reading for .NET, in-process through FFmpeg's libraries: no ffmpeg process per call, no pipes, no
allocation per frame. Frames come at the size and pixel format you ask for, and the pixels are exactly the bytes
ffmpeg's command line gives (`-vf scale=W:H:flags=bicubic,format=F -f rawvideo`), so you can check them against it.

Extracted from [ShotDetector](https://github.com/bisforboman/shotdetector)'s in-process decoding. A **preview**
package: its API may still change until it's declared stable (versions end in `-preview`, released with
ShotDetector's). Plan and status:
[docs/frame-reader-library.md](https://github.com/bisforboman/shotdetector/blob/main/docs/frame-reader-library.md).

```
dotnet add package FrameReader --prerelease
dotnet add package ShotDetector.Native.linux-x64   # FFmpeg's libraries for your platform (or .Gpl. for H.264)
```

ShotDetector's own package carries a copy of FrameReader.dll; reference one or the other version consistently.

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

Seeking and thumbnails: `reader.Seek(time)` positions the reader so the next frame is the one `ffmpeg -ss T -i X`
gives (the first at or after the time: from the keyframe before it, decoding forward, as ffmpeg's accurate seek does;
forwards or backwards, any number of times), and `reader.TryReadAt(time, out frame)` seeks and reads one frame.

```csharp
using var thumbs = new VideoFrameReader("video.mp4", new() { Width = 320, Height = 180, Format = FrameFormat.Rgba32 });
foreach (var t in new[] { 10, 60, 120 })
    if (thumbs.TryReadAt(TimeSpan.FromSeconds(t), out var frame))
        Save(frame.Data.ToArray(), frame.Width, frame.Height);
```

A picture every second of a film: read forward instead of seeking. `TryReadForwardTo(time, out frame)` decodes on to
the first frame at or after the time and converts only that one, so the whole film is one pass (seeking to each
second re-decodes from a keyframe every time: on a 1080p trailer, 4x the CPU). When the next picture is further away
than a keyframe (at least a second ahead, per the container's index), it jumps instead of decoding the frames in
between, still exact. CPU for Tears of Steel (12 min, 1280x534), with keyframes every 0.75 s / every 4.4 s (x264):

| Pictures | Decodes | CPU |
|---|---|---|
| 1 per second | everything, one pass | 49 s / 43 s |
| 1 per 10 s | from each picture's keyframe | 6.2 s / 16 s |
| 1 per minute | from each picture's keyframe | 1.0 s / 2.5 s |

With spare cores, `VideoFrameReader.ReadAt(path, times, options, parallelism, (i, frame) => ...)` reads a list of
times with several readers at once (default: half the cores, at most 4), each a contiguous share: the same frames,
the handler called from several threads. On a busy 16-core desktop, a picture a second of the 12-minute film: 8.2 s
with one reader, 6.1 s with four; one per 10 s: 1.5 s, 0.6 s.

```csharp
using var sheet = new VideoFrameReader("film.mp4", new() { Width = 320, Height = 180, Format = FrameFormat.Rgba32 });
for (int second = 0; sheet.TryReadForwardTo(TimeSpan.FromSeconds(second), out var frame); second++)
    Save(frame.Data.ToArray(), frame.Width, frame.Height);
```

Or, as ffmpeg's `fps` filter does it (`-vf fps=1`): `FrameRate = new Rational(1, 1)` runs the decoded frames through
FFmpeg's own fps filter (nearest-frame rounding, duplicating or dropping), so you get the frames `-vf
fps=R,scale=W:H:flags=bicubic,format=F` gives, byte for byte; only those are converted. `1/10` for one every ten
seconds. The two rules can differ by a frame at the edges: reading forward takes the frame at or after each second,
fps rounds each frame onto its timeline (and decides whether a last frame fits).

```csharp
using var sheet = new VideoFrameReader("film.mp4", new() { Width = 320, Height = 180, Format = FrameFormat.Rgba32, FrameRate = new(1, 1) });
while (sheet.TryRead(out var frame))
    Save(frame.Data.ToArray(), frame.Width, frame.Height); // frame.Time: 0, 1, 2, ... s
```

On Windows, `Decoder = new() { HardwareDecoding = true }` decodes on the GPU (D3D11VA) and copies each frame back,
for 8-bit 4:2:0 video. On the desktop tested the pictures were identical to software decoding and it took about half
the CPU (24 s instead of 44 s for the 12-minute film), but 3.6x the wall time (20 s instead of 5.5 s): every frame
waits for the GPU. Use it when CPU is what's short, not for speed on a free machine. `FrameDecoder.UsesHardware` says
whether a GPU decoder took the stream.

For a preview where nearby frames will do, `Decoder = new() { KeyframesOnly = true }` decodes keyframes alone
(ffmpeg's `-skip_frame nokey`): a picture a second of the 12-minute film took 5.3 s / 1.8 s of CPU instead of 50 s /
46 s (keyframes every 0.75 s / 4.4 s), but each picture is the nearest keyframe, not the frame at that second.

Seeking is ffmpeg's `-ss` exactly, including its weakness: in open GOPs (MPEG-2, some H.264), the B-frames right after
the keyframe it lands on are decoded without their reference, so a seek to one of them gives a damaged picture, as
ffmpeg's does. Reading forward decodes everything and gives the true frames.

Audio: `AudioReader` reads the best audio stream as interleaved float32, at a chosen sample rate and channel count
(default: the stream's own), the samples `ffmpeg -vn -ar R -ac C -f f32le` gives; `Seek(time)` as `-ss`.

```csharp
using var audio = new AudioReader("video.mp4", new() { SampleRate = 16000, Channels = 1 }); // e.g. for speech models
while (audio.TryRead(out var chunk))
    Process(chunk.Samples); // ReadOnlySpan<float>, valid until the next read
```

Spectral statistics: `SpectralStats` runs FFmpeg's own aspectralstats filter over an `AudioReader`'s samples, window
by window (default 2048 samples, Hann, half overlap): per channel the mean, variance, centroid, spread, skewness,
kurtosis, entropy, flatness, crest, flux, slope, decrease and rolloff, the values `-af
aspectralstats,ametadata=print` prints for the same samples (6 significant digits, as the filter hands them out).

```csharp
using var audio = new AudioReader("video.mp4", new() { Channels = 1 });
using var stats = new SpectralStats(audio, new() { WindowSize = 4096 });
while (stats.TryRead(out var window))
    if (window.Channels[0].Flatness < 0.01) // e.g. a sustained tone: energy in few frequencies
        Console.WriteLine($"{window.Time}: tone near {window.Channels[0].Centroid:F0} Hz");
```

Waveform peaks: `WaveformData.Read(audioReader, options)` computes what BBC's audiowaveform does (per point of
`SamplesPerPixel` samples, default 256, or `PixelsPerSecond`, the minimum and maximum 16-bit sample, channels
averaged unless `SplitChannels`), and `Save(stream, bits)` writes its binary .dat (8 or 16 bits), which peaks.js and
other waveform viewers load. Byte for byte audiowaveform's .dat for WAV, FLAC and other PCM; for lossy codecs the two
decoders can differ slightly.

```csharp
using var audio = new AudioReader("talk.mp4");
var waveform = WaveformData.Read(audio, new() { PixelsPerSecond = 100 });
using var dat = File.Create("talk.dat");
waveform.Save(dat, bits: 8); // or read waveform.Min(0, i), waveform.Max(0, i) to draw it yourself
```

Audio filter graphs: `AudioFilter` runs an ffmpeg filter graph over an `AudioReader`'s audio, the samples `ffmpeg
-af GRAPH -f f32le` gives, byte for byte (the graph gets the decoded audio in its own sample format, as ffmpeg's does;
the reader's rate and channels convert after it, as -ar and -ac). Time windows are ffmpeg's own `enable=` option, and
analysis filters' results are in `Metadata`.

```csharp
using var audio = new AudioReader("show.mp4");
using var eq = new AudioFilter(audio, "highpass=f=80,equalizer=f=3000:t=q:w=1:g=-6:enable='between(t,10,20)'");
while (eq.TryRead(out var chunk))
    Process(chunk.Samples);

using var speech = new AudioReader("show.mp4");
using var silence = new AudioFilter(speech, "silencedetect=n=-35dB:d=0.5");
while (silence.TryRead(out _))
    if (silence.Metadata.TryGetValue("lavfi.silence_start", out var start))
        Console.WriteLine($"silence from {start} s");
```

Our native libraries carry volume, equalizer, bass, treble, highpass, lowpass, bandpass, bandreject, afade, pan,
acompressor, alimiter, dynaudnorm, agate, ebur128, loudnorm, silencedetect and astats; a full FFmpeg build in
`LibraryDirectory` brings every filter it has.

Writing audio: `AudioWriter` encodes interleaved float32 samples to MP3 (LAME), AAC (.m4a, .aac, .mka) or WAV, the
file `ffmpeg -f f32le -ar R -ac C -i - -c:a ENCODER [-b:a B] OUT` writes, byte for byte with `Bitexact = true` on
both sides (without it, files carry FFmpeg's usual encoder tags). Read, filter, write:

```csharp
using var audio = new AudioReader("talk.mp4", new() { SampleRate = 44100, Channels = 1 });
using var clean = new AudioFilter(audio, "highpass=f=80,dynaudnorm");
using (var mp3 = new AudioWriter("talk.mp3", new() { SampleRate = 44100, Channels = 1, BitRate = 96_000 }))
    while (clean.TryRead(out var chunk))
        mp3.Write(chunk.Samples);
```

Stream copy: `Remux.Copy(input, output, options)` copies streams into another container without re-encoding, the
file `ffmpeg -i IN -map ... -c copy OUT` writes (timestamps, codec tags, metadata and chapters as ffmpeg copies them):

```csharp
Remux.Copy("film.mkv", "picture.mp4", new() { Streams = StreamSelection.Video });
Remux.Copy("film.mkv", "sound.m4a", new() { Streams = StreamSelection.Audio });
Remux.Copy("film.mkv", "commentary.mka", new() { StreamIndices = [2] });
```

Our native libraries carry the AAC, MP3 and 16-bit PCM encoders and the mp4, mov, ipod (.m4a), matroska, matroska_audio (.mka), webm, adts, mp3, wav, flac, ogg, opus, mpegts, srt, webvtt, ass muxers.
Writing video: `VideoWriter` encodes frames to H.264 (x264) in .mp4/.mkv/.mov, optionally with an AAC track, the
file `ffmpeg -c:v libx264` writes from the same frames with the same FFmpeg build (byte for byte with `Bitexact` on
both sides). It needs FFmpeg with x264: the `ShotDetector.Native.Gpl.<rid>` packages, used **instead of**
ShotDetector.Native, or your own build. Those are GPL: an app that ships with them falls under the GPL, and H.264 is
covered by Via LA's AVC patent pool either way (docs/encoding-package.md).

```csharp
using var reader = new VideoFrameReader("in.mkv", new() { Format = FrameFormat.Bgr24 });
using var writer = new VideoWriter("out.mp4", new()
{
    Width = 1280, Height = 720, FrameRate = new(25, 1),
    PixelFormat = "yuv420p", Crf = 23, Preset = "medium",
    KeyframeEvery = TimeSpan.FromSeconds(2), X264Params = "scenecut=0", // IDR every 2 s exactly
});
while (reader.TryRead(out var frame))
    writer.Write(frame.Data);
```

Reference tone: `ReferenceTone.Find(audioReader, options)` returns the runs of line-up tone (a steady 1 kHz sine, as in
"bars and tone") with their start, end and level. In 20 ms windows a channel counts when 90% of its energy is at the
frequency (about ±15 Hz) above -40 dBFS, at a steady level (±1 dB); breaks up to `MaxGap` (1 s; EBU and GLITS line-up
cut one channel now and then) don't split a run, and runs shorter than `MinDuration` (5 s) are left out, so beeps and
held notes in music don't count.

```csharp
using var audio = new AudioReader("tape.mxf");
foreach (var tone in ReferenceTone.Find(audio))
    Console.WriteLine($"tone {tone.Start}-{tone.End} at {tone.Level:0.0} dBFS");
```

`MediaProbe.Probe(path or Stream)` reads a file's properties without decoding (codec, size, pixel format, colour
tags, field order, frame rates, time base, duration, frame count, rotation, container, audio, and `Streams`: every
stream with its type, codec, codec tag, language, title, flags, bit rate, duration and audio details; optionally
every packet's timestamp). `FrameDecoder` is the lower level: decoded frames as FFmpeg holds them (`GetPlane`), with
seeking. `BgrConverter` turns its current frame into BGR at its own size, the bytes `-vf scale,format=bgr24` gives
(OpenCV's too); `Convert(rows, out stride)` converts only the slices holding the rows you read, for when you sample a
few rows of each frame (ShotDetector reads its resize's rows this way).

## Speed

Reading the first 500 frames (benchmarks/FrameReader.Benchmarks, BenchmarkDotNet; GitHub runners, 2026-10-09; the
three give the same bytes). FFMpegCore and the executable run ffmpeg as a process and read rawvideo from its stdout.

| Video | Output | FrameReader | ffmpeg executable | FFMpegCore pipe |
|---|---|---|---|---|
| Big Buck Bunny 640x360 | whole frames, Bgr24 | x64 157 ms, ARM 127 ms | 251 / 193 ms | 455 / 324 ms |
| Tears of Steel 1280x534 | whole frames, Bgr24 | x64 426 ms, ARM 347 ms | 673 / 540 ms | 1,199 / 858 ms |
| Sintel trailer 1920x1080 | whole frames, Bgr24 | x64 1,228 ms, ARM 969 ms | 1,883 / 1,477 ms | 3,647 / 2,380 ms |
| Big Buck Bunny 640x360 | 160x90 Gray8 | x64 161 ms, ARM 127 ms | 210 / 164 ms | 236 / 184 ms |
| Tears of Steel 1280x534 | 160x90 Gray8 | x64 427 ms, ARM 332 ms | 469 / 384 ms | 502 / 393 ms |
| Sintel trailer 1920x1080 | 160x90 Gray8 | x64 1,206 ms, ARM 933 ms | 1,249 / 931 ms | 1,306 / 963 ms |

Whole frames: 1.5-1.6x faster than reading the executable's pipe, 2.5-3x faster than FFMpegCore. Small frames:
ahead or equal (decoding is the cost then; ffmpeg scales before the pipe). Allocations: FrameReader 592 bytes in all
for 500 frames; the executable path 1.1 MB (its process and buffers); FFMpegCore 0.4 MB for small frames and 10-86 MB
for whole ones.

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
