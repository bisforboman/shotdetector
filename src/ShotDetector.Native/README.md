# ShotDetector.Native.&lt;rid&gt;

FFmpeg's shared libraries for [ShotDetector](https://github.com/bisforboman/shotdetector)'s in-process decoding,
one package per platform: `win-x64`, `linux-x64`, `linux-arm64`, `linux-musl-x64`, `osx-arm64`.

```
dotnet add package ShotDetector.Native.linux-x64
```

With the package referenced, ShotDetector finds the libraries next to your app and decodes in-process by default
(`VideoDecoder.Auto`): faster, with about half the memory of running the ffmpeg executable, and the same results.
ffprobe is still used to read a video's properties, so ffmpeg must still be installed.

## What's inside

FFmpeg 8.1.3 built for decoding only: libavcodec (decoders), libavformat (demuxers), libswscale, libswresample
and libavutil. No encoders, muxers, filters, devices, network protocols or external libraries. `configure.txt`
in the package has the source URL, its SHA-256 and the exact configure line; the build script is
`tools/native/build-ffmpeg.sh` in the repository.

## Licence

These are FFmpeg's libraries, under the GNU Lesser General Public License 2.1 or later (`COPYING.LGPL`). They are
dynamically loaded, so you can replace them with your own build of the same FFmpeg version. FFmpeg's source:
https://ffmpeg.org/releases/ffmpeg-8.1.3.tar.xz. ShotDetector itself is MIT.
