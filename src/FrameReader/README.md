# FrameReader

[![NuGet](https://img.shields.io/nuget/v/FrameReader.svg?label=FrameReader)](https://www.nuget.org/packages/FrameReader)
[![NuGet](https://img.shields.io/nuget/v/FrameReader.Native.svg?label=FrameReader.Native)](https://www.nuget.org/packages/FrameReader.Native)

Video and audio for .NET, in-process through FFmpeg's libraries: no ffmpeg process, no pipes, no allocation per
frame, and the same bytes as ffmpeg's command line, so you can check them against it.

- **Frames** at the size and pixel format you ask for (`-vf scale=W:H:flags=bicubic,format=F -f rawvideo`'s pixels),
  with exact seeking, thumbnails, frame rates and deinterlacing.
- **Audio**: samples in any format and rate, FFmpeg's audio filters, spectral statistics, waveform peaks, reference
  tones.
- **Writing**: MP3, AAC and WAV, JPEG and PNG images (`ImageEncoder`), H.264 with an FFmpeg built with x264, stream
  copy (`Remux`), synthetic test media (`TestMedia`).
- **Probing**: streams, packets and ffprobe's JSON (`MediaProbe`).

```
dotnet add package FrameReader
dotnet add package FrameReader.Native.linux-x64   # FFmpeg's libraries for your platform (or FrameReader.Native: all)
```

FrameReader has its own versions (semantic versioning) and
[changelog](https://github.com/bisforboman/shotdetector/blob/main/src/FrameReader/CHANGELOG.md). It began as
[ShotDetector](https://github.com/bisforboman/shotdetector)'s in-process decoding, which now depends on it; issues go
to that repository, labelled `framereader`. MIT; FFmpeg's libraries are LGPL and loaded dynamically
([FFmpeg's libraries](#ffmpegs-libraries)).

## Quick start

```csharp
using FrameReader;

using var reader = new VideoFrameReader("video.mp4", new VideoFrameReaderOptions
{
    Width = 160, Height = 90,          // both or neither (neither: the frame's own size; or MaxWidth, below)
    FrameFormat = FrameFormat.Gray8,   // Bgr24 (default), Rgb24, Bgra32, Rgba32, Gray8, Yuv420p
});
while (reader.TryRead(out var frame))
{
    // frame.Index, frame.Time (from the input's start, as ffmpeg gives it), frame.Pts
    ReadOnlySpan<byte> pixels = frame.Data; // Width x Height, rows packed
}
```

`MaxWidth = 1280` instead of `Width` and `Height` caps the width and keeps the aspect ratio, as ffmpeg's
`scale='min(1280,iw)':-2` does: a wider frame is scaled down, a narrower one keeps its width, and the height follows,
rounded to an even number (measured after autorotate).

Seeking and thumbnails: `reader.Seek(time)` positions the reader so the next frame is the one `ffmpeg -ss T -i X`
gives (the first at or after the time: from the keyframe before it, decoding forward, as ffmpeg's accurate seek does;
forwards or backwards, any number of times), and `reader.TryReadAt(time, out frame)` seeks and reads one frame.

```csharp
using var thumbs = new VideoFrameReader("video.mp4", new() { Width = 320, Height = 180, FrameFormat = FrameFormat.Rgba32 });
foreach (var t in new[] { 10, 60, 120 })
    if (thumbs.TryReadAt(TimeSpan.FromSeconds(t), out var frame))
        Save(frame.Data.ToArray(), frame.Width, frame.Height);
```

A picture every second of a film: read forward instead of seeking. `TryReadForwardTo(time, out frame)` decodes on to
the first frame at or after the time and converts only that one, so the whole film is one pass (seeking to each
second re-decodes from a keyframe every time: on a 1080p trailer, 4x the CPU). When the next picture is further away
than a keyframe (at least a second ahead, per the container's index), it jumps instead of decoding the frames in
between, still exact (not in MPEG-PS, AVI with B-frames or raw streams, whose seeks don't land where a full decode has
the frame; where a container's index by dts lands past the frame, as in MP4 with MPEG-4 Part 2 B-frames, from the
keyframe before). Frames before the wanted one that no other frame references (most B-frames of H.264, HEVC,
MPEG-1/2 and MPEG-4 Part 2) aren't decoded at all, also by `ReadAt` and `TryReadAt`'s seek: the frames given are the
full decode's all the same, byte for byte (not when deinterlacing, nor on the GPU). Reading forward at steps, those
after the wanted frame and before the next time stay out too, while the frame threads and `ReadAhead` decode ahead:
`ReadAt` knows the next time, `TryReadForwardTo` guesses it after steps of one length (in mp4, mov and mkv, whose index
it can go back by, and MPEG-TS, back to a keyframe it has read; where a guess was too late, what it left out is read again from the keyframe before, and it takes
twice as many equal steps to guess again). CPU for Tears of Steel (12 min,
1280x534), with keyframes every 0.75 s / every 4.4 s (x264), measured before that:

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
using var sheet = new VideoFrameReader("film.mp4", new() { Width = 320, Height = 180, FrameFormat = FrameFormat.Rgba32 });
for (int second = 0; sheet.TryReadForwardTo(TimeSpan.FromSeconds(second), out var frame); second++)
    Save(frame.Data.ToArray(), frame.Width, frame.Height);
```

Or, as ffmpeg's `fps` filter does it (`-vf fps=1`): `FrameRate = new Rational(1, 1)` runs the decoded frames through
FFmpeg's own fps filter (nearest-frame rounding, duplicating or dropping), so you get the frames `-vf
fps=R,scale=W:H:flags=bicubic,format=F` gives, byte for byte; only those are converted. `1/10` for one every ten
seconds. The two rules can differ by a frame at the edges: reading forward takes the frame at or after each second,
fps rounds each frame onto its timeline (and decides whether a last frame fits).

```csharp
using var sheet = new VideoFrameReader("film.mp4", new() { Width = 320, Height = 180, FrameFormat = FrameFormat.Rgba32, FrameRate = new(1, 1) });
while (sheet.TryRead(out var frame))
    Save(frame.Data.ToArray(), frame.Width, frame.Height); // frame.Time: 0, 1, 2, ... s
```

Image files: `ImageEncoder` writes a frame as a JPEG (FFmpeg's `mjpeg`), a PNG (`png`) or a WebP (`libwebp`), to a `Stream` or as a
`byte[]`, one encoder for any number of frames. The file is the one ffmpeg writes from the same raw frame with
`-frames:v 1 out.jpg` (WebP: `-c:v libwebp out.webp`, as a build that has `libwebp_anim` picks that one for .webp),
byte for byte with the same libraries (a JPEG's comment names libavcodec's version): the frame is
converted to the pixel format ffmpeg picks for the encoder as its command line converts it (the scale filter it
inserts), and the frame's aspect ratio and colour tags (`VideoFrame.SampleAspectRatio`, `ColorRange`, `ColorSpace`,
`ColorPrimaries`, `ColorTransfer`, as ffmpeg's frame has them after the same filters) go where ffmpeg puts them (JFIF
density, PNG's pHYs, cHRM and gAMA). Options are ffmpeg's: `JpegQscale` is `-q:v` (2 to 31, lower is better; not
ShotDetector's 0-100 save-images quality), `PngCompressionLevel` is `-compression_level` (0 to 9: speed against size,
lossless at every level); WebP's are libwebp's: `WebPQuality` is `-quality` (0 to 100, default 75; the scale of
ShotDetector's save-images WebP quality, whose default is 100), `WebPLossless` is `-lossless 1`, and
`WebPCompressionLevel` is `-compression_level` (libwebp's method, 0 to 6, default 4: speed against size). As ffmpeg
picks, a YUV frame is encoded from yuv420p and an RGB or gray one from bgra, lossy or lossless: libwebp converts bgra to
YUV for lossy and yuv420p to RGB for lossless itself (ffmpeg warns of both), so read frames as RGB for lossless WebP.

```csharp
using var frames = new VideoFrameReader("video.mp4", new() { FrameRate = new(1, 1), MaxWidth = 1280, FrameFormat = FrameFormat.Yuv420p, ReadAhead = 4 });
using var jpeg = new ImageEncoder(new() { Format = ImageFormat.Jpg, JpegQscale = 2 });
while (frames.TryRead(out var frame))
    File.WriteAllBytes($"frame_{frame.Index + 1:D6}.jpg", jpeg.Encode(frame));
```

`ReadAhead = 4` overlaps that loop's encoding and writing with decoding, as ffmpeg's threaded pipeline does: a thread of
the reader's own decodes up to 4 frames ahead (deinterlacing and rotating them too) while the loop works on the frame it
has, for any per-frame work between reads (encoding, analysis, uploads) and with `TryRead`, `TryReadForwardTo`,
`FrameRate` and seeks alike. The read still converts the frame it gives, so the frame is valid until the next read as
without, and the frames, their tags and any error are the same, in the same order. It pays where decoding leaves cores
free; where it keeps every core busy there's nothing to overlap with (1 or 2 hand over nearly every frame on its own and
can cost a little more than they save). Each frame ahead holds a decoded frame's memory (about 3 MB at 1080p).

Without `JpegQscale`, JPEG uses ffmpeg's default, its rate control, which carries its state from frame to frame in one
ffmpeg run: `ImageEncoder` starts it afresh for every image, so each file is what a one-frame run
(`-frames:v 1 out.jpg`) writes for that frame; one ffmpeg run writing N frames (`frame_%06d.jpg`) differs from its second
frame on. With a `JpegQscale` both are the same. A JPEG of a `Yuv420p` frame the reader scaled is close to but not
byte-identical with ffmpeg's one command (`-vf scale=...,format=yuv420p out.jpg`): there ffmpeg converts to the
encoder's full range in the same scaling pass, here the reader scales and the encoder converts. And a JPEG of an RGB
frame of a source tagged BT.709 is converted to YUV with BT.601's matrix, as ffmpeg does from a raw RGB frame (its one
command keeps the source's matrix). A `Yuv420p` frame of a 4:2:0 source at its own size is exactly
`ffmpeg -i X -frames:v 1 out.jpg`.

For ffmpeg's one command itself, scaled or not, encode the decoder's frame (`FrameDecoder.TakeFrame`) instead: the
`DecodedFrame` overloads run ffmpeg's own graph, the frame, then a size or filter (`ImageFilterOptions`: `Width` and
`Height` as `-vf scale=W:H`, `MaxWidth` as `-vf scale='min(N,iw)':-2`, or `Filter`, any `-vf` chain that gives one frame
for each frame in), then the conversion to the encoder's format, so the file is `ffmpeg [-ss T] -i X [-vf ...] -frames:v 1
out.jpg`'s for that frame, byte for byte with the same FFmpeg build. The decoder's options are ffmpeg's: its frames are
autorotated as ffmpeg's are, and deinterlaced only as with `-vf yadif,...`.

```csharp
using var decoder = new FrameDecoder("video.mp4");
using var png = new ImageEncoder(new() { Format = ImageFormat.Png });
if (decoder.Next())
{
    using var frame = decoder.TakeFrame();
    File.WriteAllBytes("first.png", png.Encode(frame, new ImageFilterOptions { MaxWidth = 1280 })); // ffmpeg -i video.mp4 -vf scale='min(1280,iw)':-2 -frames:v 1 first.png
}
```

`VideoFrameReader` hands out the same decoded frames where its reads would land: `TryTake`, `TryTakeForwardTo` and
`TryTakeAt` position as `TryRead`, `TryReadForwardTo` and `TryReadAt` do (keyframe jumps, ffmpeg's seek, `ReadAhead`,
deinterlacing and autorotation as the reader's `Decoder` says) and give the frame unconverted, at its own size and pixel
format: the conversion's options (`Width`, `Height`, `MaxWidth`, `FrameFormat`) don't apply to it, so one reader can
both read and take, each moving the same position. A taken frame is the caller's to dispose, a reference to the decoded
buffers (no copy), valid after the next read and after the reader is disposed; `DecodedFrame.Time` is its time as
`VideoFrame.Time` (null without a timestamp). A picture every second, each file
`ffmpeg -i video.mp4 -ss <i> -vf scale='min(1280,iw)':-2 -frames:v 1` byte for byte:

```csharp
var filter = new ImageFilterOptions { MaxWidth = 1280 };
using var jpeg = new ImageEncoder(new() { Format = ImageFormat.Jpg, JpegQscale = 2 });
using var reader = new VideoFrameReader("video.mp4", new() { ReadAhead = 2 });
for (var i = 0; reader.TryTakeForwardTo(TimeSpan.FromSeconds(i), out var frame); i++)
    using (frame)
        File.WriteAllBytes($"frame_{i + 1:D6}.jpg", jpeg.Encode(frame, filter));
```

That is the frame at or after each second (ffmpeg's `-vf fps=1:round=up` where frames fall on the seconds);
`-vf fps=1`'s default rounding keeps the last frame of each second's slot instead (0.48 s, 1.48 s, ... at 25 fps). For
those, take from a reader with `FrameRate`: `TryTake` then gives the fps filter's frames, and each file is
`ffmpeg -i video.mp4 -vf fps=1,scale='min(1280,iw)':-2 frame_%06d.jpg`'s byte for byte (with `JpegQscale`: without it,
ffmpeg's rate control carries over from one frame to the next in one run). `TryTakeAt` and `TryTakeForwardTo` stay
refused there, as `Seek` and `TryReadForwardTo` are.

```csharp
using var reader = new VideoFrameReader("video.mp4", new() { FrameRate = new(1, 1), ReadAhead = 2 });
for (var i = 1; reader.TryTake(out var frame); i++)
    using (frame)
        File.WriteAllBytes($"frame_{i:D6}.jpg", jpeg.Encode(frame, filter));
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

Where ffmpeg's `-ss` can't seek, the reader gives the full decode's frame instead, decoding from the start:

- Raw elementary streams (.m2v, .m4v written with `-f m4v`, .h264, .hevc: demuxers without timestamps). ffmpeg's command
  line drops their timestamps, so its frames are timed from 0 by the frame rate (the reader's are too), its seek lands on
  some keyframe and keeps every frame from there, and raw H.264 and HEVC, which have no timestamps at all, give no frame
  at any time. `Seek` reads such a stream from its first byte (or on, when the time is ahead of the frame read last).
- A seek the container refuses (ffmpeg's margin before the first index entry: AVI, FLV near the start). ffmpeg only
  warns and reads on from where the refused seek left the input, past the first keyframe: a frame seconds later, or
  none at all.

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
waveform.Save(dat, WaveformBits.Eight); // or read waveform.Min(0, i), waveform.Max(0, i) to draw it yourself
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
acompressor, alimiter, dynaudnorm, agate, ebur128, loudnorm, silencedetect and astats; a full FFmpeg build loaded with
`FFmpegLibraries.Load(folder)` brings every filter it has.

`Result(filter, option)` reads what an analysis filter keeps as a read-only option, at full precision: after the last
chunk, the whole input's. `Loudness.Measure(audio)` uses it for EBU R128 / ITU-R BS.1770 loudness, `ffmpeg -af
ebur128=peak=true+sample`'s figures (integrated loudness, loudness range and its ends, sample and true peak), including
the samples after the last whole 100 ms block, which ebur128's per-frame metadata leaves out:

```csharp
using var audio = new AudioReader("programme.mxf");
var loudness = Loudness.Measure(audio);
Console.WriteLine($"{loudness.Integrated:0.0} LUFS, LRA {loudness.Range:0.0} LU, true peak {loudness.TruePeak:0.0} dBTP");
```

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

As ffmpeg does, what the encoder can't take is converted to its nearest sample format, rate and channel layout (5.1
into MP3 comes out stereo). For a whole file, `Audio.Convert` is `ffmpeg -i IN -vn [-af G] [-c:a E] [-b:a B] OUT` in one
call: the decoder's own samples through the graph to the encoder, with the input's metadata, the file the command
line writes. Including its conversion: a downmix of integer samples (PCM, FLAC) is normalized, of float ones (AAC,
MP3) it isn't, which the read-filter-write loop above can't do (it works in float):

```csharp
Audio.Convert("in.mxf", "out.mp3", new() { Filter = "equalizer=f=1000:t=q:w=8:g=-40:enable='between(t,0,60)'" });
```

Before copying, `Remux.CanCopy(input, output, options)` says whether the streams can go into that container, and why
not, per stream (`"stream 0 (mpeg2video): the webm muxer doesn't take mpeg2video"`): FFmpeg's own answer, from the muxer,
with nothing written; DASH is checked against the mp4 muxer that writes its segments.

Stream copy: `Remux.Copy(input, output, options)` copies streams into another container without re-encoding, the
file `ffmpeg -i IN -map ... -c copy OUT` writes (timestamps, codec tags, metadata and chapters as ffmpeg copies them):

```csharp
Remux.Copy("film.mkv", "picture.mp4", new() { Streams = StreamSelection.Video });
Remux.Copy("film.mkv", "sound.m4a", new() { Streams = StreamSelection.Audio });
Remux.Copy("film.mkv", "commentary.mka", new() { StreamIndices = [2] });
Remux.Copy("film.mkv", "web.mp4", new() { MuxerOptions = new Dictionary<string, string> { ["movflags"] = "+faststart" } });
Remux.Copy("phone.mp4", "upright.mp4", new() { DisplayRotation = 90 });  // ffmpeg's -display_rotation 90
```

`MuxerOptions` are ffmpeg's output options without the dash. With the `dash` muxer they give MPEG-DASH: fMP4 init and
media segments and a static MPD, the files `ffmpeg -c copy -f dash` writes with the same options (separate audio and
video adaptation sets here; segments are written next to the .mpd):

```csharp
Remux.Copy("film.mkv", "out/manifest.mpd", new()
{
    Container = "dash",
    MuxerOptions = new Dictionary<string, string>
    {
        ["seg_duration"] = "2.4",
        ["use_template"] = "1",
        ["use_timeline"] = "0",
        ["init_seg_name"] = "init-$RepresentationID$.mp4",
        ["media_seg_name"] = "chunk-$RepresentationID$-$Number%05d$.m4s",
        ["adaptation_sets"] = "id=0,streams=v id=1,streams=a",
    },
});
```

Folders in the segment names are created, and `$ContentType$` (`video`, `audio`, `text`) gives Shaka Packager's
layout, which FFmpeg's muxer has no placeholder for: `["init_seg_name"] = "$ContentType$/init.mp4"` and
`["media_seg_name"] = "$ContentType$/$Number$.m4s"` write `video/init.mp4`, `video/1.m4s`, `audio/init.mp4`, ...
with the MPD's templates to match (two streams of one type need `$RepresentationID$` in the name as well).

Segments end at keyframes, so `seg_duration` is a target: for exact 2.4 s segments the video needs a keyframe every
2.4 s (`VideoWriterOptions.KeyframeEvery`).

`Encryption` adds Common Encryption (CENC, AES-CTR with your key ID and key): FFmpeg's mp4 muxer encrypts, the files
ffmpeg's `-encryption_scheme cenc-aes-ctr -encryption_key K -encryption_kid I` gives (for DASH, through the dash
muxer's `format_options`). With `Widevine = true` on DASH output, each init segment also gets Widevine's `pssh` box
(the one `pssh-box.py --widevine-system-id --key-id` writes) and each adaptation set in the MPD a `ContentProtection`
for CENC (`cenc:default_KID`) and one for Widevine (the box as `cenc:pssh`), as Shaka Packager writes them. The
license service still needs the key. When your DRM provider supplies the Widevine pssh data (with its provider,
content ID and so on), pass it as `WidevinePsshData`: it goes into the box unchanged, as
`pssh-box.py --widevine-system-id --pssh-data` and Shaka's `--pssh` do.

```csharp
Remux.Copy("film.mp4", "out/manifest.mpd", new()
{
    Container = "dash",
    MuxerOptions = dashOptions, // as above
    Encryption = new() { KeyId = "a1b2c3d4e5f60718293a4b5c6d7e8f90", Key = "00112233445566778899aabbccddeeff", Widevine = true },
});
```

Our native libraries carry the AAC, MP3 and 16-bit PCM encoders and the mp4, mov, ipod (.m4a), matroska, matroska_audio (.mka), webm, adts, mp3, wav, flac, ogg, opus, mpegts, srt, webvtt, ass and dash muxers.
A whole file re-encoded in one call: `Video.Transcode(input, output, options)` is `ffmpeg -i IN [-vf yadif] -c:v E
-pix_fmt yuv420p -g G -force_key_frames ... OUT`, for making a source that isn't ready for DASH ready for `Remux.Copy`:
deinterlaced when the stream is flagged interlaced (`Deinterlace`: null by default; true or false to force), frames
upright, the first audio stream re-encoded, keyframes every `KeyframeEvery`. H.264 needs an encoder your libraries have
(`libx264`, or `libopenh264` with Cisco's binary); FrameReader.Native has none, for licensing (`mpeg4` works for tests).

```csharp
await Video.TranscodeAsync("in.mxf", "out.mp4", new() { Encoder = "libx264", Gop = 72, KeyframeEvery = TimeSpan.FromSeconds(2.4), AudioEncoder = "aac" });
```

Writing video: `VideoWriter` encodes frames to H.264 (x264) in .mp4/.mkv/.mov, optionally with an AAC track, the
file `ffmpeg -c:v libx264` writes from the same frames with the same FFmpeg build (byte for byte with `Bitexact` on
both sides). It needs FFmpeg libraries with libx264, which FrameReader.Native doesn't have: a "gpl" FFmpeg 8 build
of your own (a shared one, such as BtbN's `gpl-shared`), loaded with `FFmpegLibraries.Load(folder)`. Those are GPL: an
app that ships with them falls under the GPL, and H.264 is covered by Via LA's AVC patent pool either way
(docs/encoding-package.md).

On CPUs with AVX-512, a file can rarely differ from ffmpeg's while .NET's tiered compilation is on (in CI, about 1
run in 250 of a five-file test; still valid H.264): x264's AVX-512 code and the runtime's JIT thread in one process.
Each alone gives ffmpeg's bytes. For byte-exact output there, turn tiered compilation off
(`<TieredCompilation>false</TieredCompilation>` or `DOTNET_TieredCompilation=0`), as CI does, or keep x264 off AVX-512
on both sides (its `asm` flags in `X264Params` and `-x264-params`).

```csharp
using var reader = new VideoFrameReader("in.mkv", new() { FrameFormat = FrameFormat.Bgr24 });
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

Samples decoded elsewhere (a browser's own decoder, no FFmpeg loaded): `ReferenceToneFinder` and `WaveformBuilder` take
them a chunk at a time and give what `Find` and `Read` give for the same samples (which run through them). The
finder's `Add` returns false once `SearchDuration` is read, so decoding can stop there.

```csharp
var finder = new ReferenceToneFinder(sampleRate, channels, new() { SearchDuration = TimeSpan.FromMinutes(3) });
var builder = new WaveformBuilder(sampleRate, channels, new() { PixelsPerSecond = 100 });
foreach (var (samples, time) in decodedPackets) // float, interleaved
{
    builder.Add(samples, time);
    finder.Add(samples, time); // false: the search is done
}

var tones = finder.Finish();
var waveform = builder.Finish();
```

Test media without the ffmpeg executable: `TestMedia.Write(path, options)` makes a synthetic clip from FFmpeg's own
sources and encoders, in-process, for tests that need real media without committing any. Video sources are
concatenated in order, so the cuts fall at known frames; interlaced output is flagged and coded as such.

```csharp
TestMedia.Write("clip.ts", new()
{
    Video = ["testsrc2=d=4", "smptebars=d=3", "mandelbrot,trim=duration=2"], // cuts at frames 100 and 175
    Size = (320, 240),
    FrameRate = new(25, 1),
    FieldOrder = FieldOrder.TopFirst,                                        // mpeg2video with +ilme+ildct, flagged tt
    Audio = "sine=frequency=1000:duration=9",
    VideoEncoder = "mpeg2video",
    VideoQuality = 3,                                                        // ffmpeg's -q:v 3; null: the encoder's default rate
    AudioEncoder = "aac",
});
```

The FrameReader.Native libraries have the sources (testsrc2, smptebars, mandelbrot, color, sine, anullsrc), concat,
trim and the encoders (mpeg2video, mpeg4, png, aac, PCM), and write program streams (.mpg) and image sequences
(`frames/%04d.png`); a system FFmpeg has them too. It's ffmpeg's `-f lavfi` inputs
and a concat, not byte for byte.

`MediaProbe.Probe(path or Stream)` reads a file's properties without decoding (codec, size, pixel format, colour
tags, field order, frame rates, time base, duration, frame count, rotation, container, audio, and `Streams`: every
stream with its type, codec, codec tag, language, title, flags, bit rate, duration and audio details; the video's
codec extradata; optionally every packet's timestamp, and the first video packet's bytes). A local MP4/MOV file's packet
timestamps come from its sample tables without reading its media data (h264, HEVC, MPEG-2/4, MJPEG and ProRes video;
not fragmented), so that scan reads kilobytes instead of the file. `MediaProbe.ProbeJson(path)` gives ffprobe's `-print_format json -show_format
-show_streams` instead: every field ffprobe prints (profile, level, aspect ratios, colour, disposition, tags, side
data, ...), with ffprobe's keys and value types, unknown values left out as there; it opens each stream's decoder, as
ffprobe does. **The advanced layer:** `FrameDecoder` is the lower level, decoded frames as FFmpeg holds them (`GetPlane`; or
`CopyRows`, only the rows you read, which with a row deinterlacer are the only ones deinterlaced), with
seeking, `HasFrameThreads(codec)` to tell whether a codec's decoder decodes several frames at once (where more threads
than CPUs can pay), and each frame's own chroma location and colour tags (`ChromaLocation`, `ColorSpace`, `ColorRange`: the
decoder's, where `VideoStreamInfo` gives a container's header, which can say otherwise). `BgrConverter` turns its current frame into BGR at its own size, the bytes `-vf scale,format=bgr24` gives
(OpenCV's too); `Convert(rows, out stride)` converts only the slices holding the rows you read, for when you sample a
few rows of each frame (ShotDetector reads its resize's rows this way).

## Speed

Reading frames against the ffmpeg executable's pipe and FFMpegCore (benchmarks/FrameReader.Benchmarks,
BenchmarkDotNet, the first 500 frames of three films at whole size and at 160x90, weekly on GitHub's x64 and ARM
runners): the current numbers are in the [latest runs](https://github.com/bisforboman/shotdetector/actions/workflows/frame-reader-bench.yml). Whole frames come out well ahead of both
(no process, no pipe); small frames, where decoding is the cost, ahead or equal. FrameReader allocates next to
nothing per frame (frames are views of FFmpeg's buffers); the pipe readers allocate per frame.

## Frame lifetime

`VideoFrame` is a `ref struct` over the reader's own buffer: `Data` is valid until the next `TryRead` (or
`Dispose`). Copy it (`frame.Data.ToArray()`) to keep a frame. A `DecodedFrame` (`TryTake`, `FrameDecoder.TakeFrame`)
holds its decoded buffers until disposed, after the reader or decoder too.

## What it handles

- Files, image sequence patterns (`frames/%04d.png`, with `InputOptions` `framerate`), and `Stream`s (headers first:
  mkv, webm, ts, mov or a faststart mp4).
- An rtsp server's side: `InputOptions` `rtsp_flags` = `listen` waits for a publisher to push to the URL. FFmpeg 8.1's
  own default `listen_timeout` (-1, wait forever) fails to open the input ("Numerical result out of range", on the
  command line too), so FrameReader passes `listen_timeout` = `0`, the same wait without end, when you set none (your
  own value is used as it is, in seconds; -1 fails as in FFmpeg). The options' `CancellationToken` stops the wait.
- Deinterlacing (`FrameDecoderOptions.Deinterlace`, yadif as `-vf yadif`), the display matrix applied as ffmpeg's
  autorotate does (a 90-degree turn of 4:2:2, which ffmpeg converts first, is refused), decoder threads.
- Decoder options (`FrameDecoderOptions.CodecOptions`, as ffmpeg's before `-i`): `idct` = `simple` gives x86's
  frames on ARM for MPEG-1/2 and MJPEG, whose default IDCT rounds differently there.
- Errors as `FrameReaderException` with a `Reason`: the libraries missing, an input FFmpeg can't open (or without
  video), a decoding failure.

Errors: what you pass wrong (sizes, rates, unknown option names, also in `InputOptions` and `CodecOptions`) is an
`ArgumentException`; what the media or the libraries do is a `FrameReaderException` with a `Reason` (`LibrariesNotFound`,
`InvalidInput`, `DecodeFailed`, `WriteFailed`). It's a `MediaException` (an `InvalidOperationException`), as ShotDetector's `ShotDetectionException`
is, so one `catch (MediaException)` covers both packages (from 2.3.0). The one-call operations (`Remux.Copy`, `WaveformData.Read`, `VideoFrameReader.ReadAt`) take a
`CancellationToken`; the pull loops stop when you stop reading. The whole-file jobs (`Audio.Convert`, `Remux.Copy`,
`WaveformData.Read`, `ReferenceTone.Find`) report progress through their options' `Progress`, one `MediaProgress`
(position, duration, fraction) for all of them, about 10 times a second and once at the end; each has an async overload
(`ConvertAsync`, `CopyAsync`, `ReadAsync`, `FindAsync`, and `MediaProbe.ProbeJsonAsync`) that runs it off the calling
thread.

## Security

A path or URL is opened by FFmpeg, as `ffmpeg -i` opens it: prefixes such as `concat:` or `subfile:` work, and a remote
playlist names further URLs. For untrusted input, pass FFmpeg's `protocol_whitelist` in `InputOptions` (for an http
URL, `http,https,tcp,tls,crypto,httpproxy`, a second layer over FFmpeg's own checks, which already keep a remote
playlist from local files) and `rw_timeout` (microseconds)
so a stalled server fails. An https URL's server certificate is checked against the system's trusted ones (FFmpeg 8
doesn't by default; `tls_verify=0` in `InputOptions` turns that off), and so are the URLs the input opens itself (an
HLS playlist's segments, keys and variants), which FFmpeg opens with a fixed set of options that leaves the check out:
FrameReader adds it to each, unless `InputOptions` set `tls_verify=0` or a `ca_file`; the options' `CancellationToken` stops a read FFmpeg waits in. Filter graphs you pass
(`AudioFilter`, `Audio.Convert`'s) are FFmpeg's syntax, and some filters of a full build read files (`movie`,
`amovie`): don't build them from untrusted input. Decoding runs FFmpeg's code in your process; keep the libraries
current (FrameReader.Native follows FFmpeg's point releases). A stream claiming a frame larger than
`FrameDecoderOptions.MaxPixels` (8192 x 8192 by default) fails to decode, so a crafted file can't exhaust memory.

## FFmpeg's libraries

FFmpeg 8's shared libraries (libavcodec 62, libavformat 62, libswscale 9, libavutil 60; libavfilter 11 to
deinterlace and filter, libswresample for audio). The FrameReader.Native.&lt;rid&gt; packages carry a build for
win-x64, linux-x64, linux-arm64, linux-musl-x64 and osx-arm64 with everything below but x264
(for `VideoWriter`, load a build with it); a "shared" FFmpeg 8 build or the system's (`apk add ffmpeg-libs`)
works too. They load once per process, the first time anything needs them: next to the app, else the system's places;
to use a folder of your own, call `FFmpegLibraries.Load(folder)` first (the one place that takes one). FrameReader is MIT; FFmpeg's libraries are LGPL and loaded dynamically.

What each part needs from a build of your own, beyond the decoders and demuxers of your input. Check at startup with
`FFmpegLibraries.Missing(Capability...)`, which knows this table, plus `MissingFilters(graph)` for a filter graph of
your own (the same format, so the lists concatenate; a graph that doesn't parse is `"filter graph"`), or name by name
with `HasEncoder`, `HasFilter` and `HasMuxer` (and `HasProtocol` for a URL's scheme: FrameReader.Native has http and
https):

| Part | Encoders | Filters | Muxers |
| --- | --- | --- | --- |
| `VideoFrameReader`, `FrameDecoder` | | `buffer`, `buffersink`, `fps` (`FrameRate`), `yadif` (deinterlacing) | |
| `AudioReader`, `WaveformData`, `ReferenceTone` | | | |
| `AudioFilter` | | `abuffer`, `abuffersink`, `aformat`, and the graph's own (`equalizer`, ...) | |
| `SpectralStats` | | `abuffer`, `abuffersink`, `aspectralstats` | |
| `AudioWriter`, `Audio.Convert` | the codec's (`libmp3lame`, `aac`, `pcm_s16le`, ...) | `abuffer`, `abuffersink`, `aformat`, `aresample` | the container's (`mp3`, `ipod` for .m4a, `wav`, ...) |
| `VideoWriter` | `libx264` (and `aac` for an audio track) | `buffer`, `buffersink`, `format`, `null`, `scale` (and the audio ones) | `mp4`, `matroska` or `mov` |
| `Remux.Copy` | | | the output's (`dash` for an .mpd) |
| `ImageEncoder` | `mjpeg` (JPEG), `png` (PNG), `libwebp` (WebP) | `buffer`, `buffersink`, `format`, `scale` (and an `ImageFilterOptions.Filter`'s own) | |

```csharp
var missing = FFmpegLibraries.Missing(Capability.Deinterlace | Capability.AudioFilter | Capability.Mp3 | Capability.Dash)
    .Concat(FFmpegLibraries.MissingFilters(myGraph)) // your own graph's filters, e.g. "equalizer=...,aresample"
    .ToList();
if (missing.Count > 0)
    throw new InvalidOperationException("This FFmpeg lacks " + string.Join(", ", missing)); // e.g. "filter yadif"
```
