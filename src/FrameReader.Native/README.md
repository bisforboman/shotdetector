# FrameReader.Native.&lt;rid&gt;

FFmpeg's shared libraries for FrameReader and [ShotDetector](https://github.com/bisforboman/shotdetector), one package
per platform: `win-x64`, `linux-x64`, `linux-arm64`, `linux-musl-x64`, `osx-arm64`.

```
dotnet add package FrameReader.Native.linux-x64
```

Or `FrameReader.Native`, which depends on every platform's package at its version: one reference for an app built on
one platform and run on another (a publish for one runtime, `-r linux-x64`, still copies only that platform's
libraries). A single platform's package keeps a build smaller. Until FrameReader 2.2.0 these were `ShotDetector.Native`
and `ShotDetector.Native.<rid>`; their 2.x versions only depend on these, and ShotDetector 3.0 no longer publishes them.

With the package referenced, FrameReader finds the libraries next to your app. ShotDetector then decodes in-process
by default (`VideoDecoder.Auto`): faster and with less memory than running the ffmpeg executable, and the same
results. Files are probed in-process too, so ffmpeg only needs to be installed for streams, URLs and exports
(split-video, save-images).

## What's inside

FFmpeg 8.1.3 built for decoding, plus audio encoding and muxing: libavcodec (decoders; the AAC, MP3 via LAME and
PCM encoders, FFmpeg's own MPEG-2 and MPEG-4 Part 2 for FrameReader's TestMedia, and PNG), libavformat (demuxers; the mp4, mov, ipod (.m4a), avi, matroska, matroska_audio (.mka), webm, adts, mp3, wav, flac, ogg, opus, mpegts, mpeg (program streams), image2 (image sequences), srt, webvtt, ass and dash muxers), libswscale, libswresample,
libavutil and libavfilter with only yadif (deinterlacing), fps (FrameReader's FrameRate), aspectralstats (its SpectralStats) and
the audio filters its AudioFilter offers: volume, equalizer, bass, treble, highpass, lowpass, bandpass, bandreject, afade, pan, acompressor, alimiter, dynaudnorm, agate, ebur128, loudnorm, silencedetect, astats, aformat, aresample; and
TestMedia's sources: testsrc2, smptebars, mandelbrot, color, sine, anullsrc, concat, setfield, setsar, setparams, trim, atrim. No H.264 or other external encoders, and no devices. Network: http (and its proxy, over tcp) and https, for URLs
in-process; https through Windows' own TLS (Schannel), macOS's (Secure Transport, TLS 1.2 at most) and on Linux
OpenSSL 3.5, checking the server's certificate against the system's trusted ones. Libraries linked in statically:
zlib 1.3.2 (PNG, compressed MOV/MKV headers), dav1d 1.5.4 (AV1), LAME 3.100 (MP3, LGPL) and, on Linux, OpenSSL
3.5.9 (Apache-2.0).
`configure.txt` in the package has the source URLs, their SHA-256 and the exact configure line, and
`sbom.spdx.json` the same components as an SPDX 2.3 SBOM (versions, sources, SHA-256, licences and CPEs, for
vulnerability scanners); the build script is `tools/native/build-ffmpeg.sh` in the repository.

## Licence

These are FFmpeg's libraries, under the GNU Lesser General Public License 2.1 or later (`COPYING.LGPL`). They are
dynamically loaded, so you can replace them with your own build of the same FFmpeg version. FFmpeg's source:
https://ffmpeg.org/releases/ffmpeg-8.1.3.tar.xz. dav1d is under the BSD 2-Clause licence (`COPYING.dav1d`), zlib
under the zlib licence, OpenSSL (Linux) under the Apache License 2.0 (`COPYING.openssl`). FrameReader and
ShotDetector themselves are MIT.
