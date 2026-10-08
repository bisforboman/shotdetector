# ShotDetector.Native.&lt;rid&gt;

FFmpeg's shared libraries for [ShotDetector](https://github.com/bisforboman/shotdetector)'s in-process decoding,
one package per platform: `win-x64`, `linux-x64`, `linux-arm64`, `linux-musl-x64`, `osx-arm64`.

```
dotnet add package ShotDetector.Native.linux-x64
```

With the package referenced, ShotDetector finds the libraries next to your app and decodes in-process by default
(`VideoDecoder.Auto`): faster, with about half the memory of running the ffmpeg executable, and the same results.
Files are probed in-process too, so ffmpeg only needs to be installed for streams, URLs and exports (split-video,
save-images).

## What's inside

FFmpeg 8.1.3 built for decoding only: libavcodec (decoders), libavformat (demuxers), libswscale, libswresample,
libavutil and libavfilter with only yadif (deinterlacing). No encoders, muxers, devices or network protocols. Two
libraries are linked in statically: zlib 1.3.1 (PNG, compressed MOV/MKV headers) and dav1d 1.5.1 (AV1).
`configure.txt` in the package has the source URLs, their SHA-256 and the exact configure line; the build script
is `tools/native/build-ffmpeg.sh` in the repository.

## Licence

These are FFmpeg's libraries, under the GNU Lesser General Public License 2.1 or later (`COPYING.LGPL`). They are
dynamically loaded, so you can replace them with your own build of the same FFmpeg version. FFmpeg's source:
https://ffmpeg.org/releases/ffmpeg-8.1.3.tar.xz. dav1d is under the BSD 2-Clause licence (`COPYING.dav1d`), zlib
under the zlib licence. ShotDetector itself is MIT.
