# FrameReader changelog

FrameReader's versions are git tags (`framereader-vX.Y.Z`); each one publishes the FrameReader package and a GitHub
release. Semantic versioning: a breaking change to its public API is a major version. What's merged but not released
yet is in changes/framereader/ (one file per pull request).

Up to 2.1.0, FrameReader was released together with ShotDetector at the same version: those versions' changes are in
ShotDetector's [CHANGELOG.md](../../CHANGELOG.md), each marked "FrameReader:".

## 2.7.0 – 2026-10-18

Decoder options (CodecOptions), the codec's extradata and first packet from MediaProbe, and BgrConverter.Size.

### New

- `FrameDecoderOptions.CodecOptions`: FFmpeg decoder options, as ffmpeg's before `-i`. `idct` = `simple` decodes
  MPEG-1/2 and MJPEG with the C IDCT, so ARM gives x86's frames (ARM's default IDCT rounds differently). A name the
  decoder doesn't have is an `ArgumentException`, as are `max_pixels` and `threads` (set through `MaxPixels` and
  `Threads`, which they would otherwise override).
- `VideoStreamInfo.ExtraData` (the codec's extradata, as ffprobe's `-show_data` prints it) and
  `ProbeOptions.FirstVideoPacket` / `MediaInfo.FirstVideoPacket` (the first video packet's bytes, reading up to that
  packet only): what a decoder sees first, e.g. an MPEG-4 Part 2 stream's encoder signature.
- `BgrConverter.Size`: convert every frame to a given size (the stream's, from its headers), whichever frame comes
  first: after a seek into a part of another size, frames still come at the stream's size, as OpenCV's buffer and
  ffmpeg's `scale=` give them.

## 2.6.1 – 2026-10-17

A patch release: the Linux native packages' library paths without the double slash.

### Fixed

- FrameReader.Native's Linux packages: the libraries' paths no longer have a double slash (`runtimes/linux-x64/native/libavcodec.so.62`, not `native//`); the files and how they load are unchanged.

## 2.6.0 – 2026-10-17

DecodedFrame: decoded frames that outlive the next one, converted to BGR on any thread.

### New

- `FrameDecoder.TakeFrame()` returns a `DecodedFrame`: the current frame as a reference to its buffers (no copy) that
  stays valid after `Next()` and after the decoder is disposed, until it is. `new BgrConverter()` with
  `Convert(DecodedFrame, ...)` converts one to BGR on any thread (one converter per thread), the same bytes as
  converting the decoder's current frame.

## 2.5.0 – 2026-10-16

https on Linux (OpenSSL) and macOS, certificates checked for HLS segments too, and fixes from a security review.

### New

- FrameReader.Native reads https on Linux and macOS too: OpenSSL 3.5 linked in on Linux (Apache-2.0,
  `COPYING.openssl`; FrameReader passes the system's certificate bundle as `ca_file` unless `SSL_CERT_FILE` is set),
  macOS's own TLS (Secure Transport). The server's certificate is checked on every platform.

### Fixed

- The segments, keys and variant playlists an HLS playlist names are opened with the server's certificate checked
  too: FFmpeg passes only a fixed set of options on to them, without `tls_verify`, so a playlist naming https
  segments read them unchecked. `tls_verify=0` or a `ca_file` of your own in `InputOptions` leave them as FFmpeg opens
  them.
- Row-wise deinterlacing (a `RowDeinterlacer`) read past a frame when an interlaced stream got smaller mid-way, which
  could crash the process; that's a `FrameReaderException` now.
- `FFmpegLibraries.Load()` on Windows: the system's places are PATH's folders, by full path; the libraries were
  loaded by bare name, which also searched the current folder first, so DLLs someone put there could load.

## 2.4.0 – 2026-10-16

URLs over http (https on Windows), cancellable and time-limited network reads, read errors reported instead of a shortened result, a frame size cap and a hardened Linux build.

### New

- `FrameDecoderOptions.MaxPixels`: the largest frame decoded (FFmpeg's `max_pixels`), 8192 x 8192 by default, so a
  crafted file claiming a huge size fails to decode instead of exhausting memory; 0 lifts it.
- FrameReader.Native on Linux (x64, arm64, musl) is built hardened (FFmpeg's `--toolchain=hardened`: stack protector,
  `_FORTIFY_SOURCE`, full RELRO, PIC), so a bug in a decoder fed untrusted media is harder to exploit in-process.
- FrameReader.Native reads URLs: http (and its proxy, over tcp) on every platform, https on Windows through Windows'
  own TLS (Schannel; elsewhere https would need a TLS library linked in). Only these network protocols are in the build.
- `FFmpegLibraries.HasProtocol(name)`: whether the libraries can read a URL with that scheme.
- `FrameDecoderOptions`, `AudioReaderOptions` and `ProbeOptions` take a `CancellationToken` that also stops a read
  FFmpeg waits in, such as a stalled network read (FFmpeg's interrupt callback); `Remux.Copy`'s token does too. A
  cancelled read is an `OperationCanceledException`.
- FrameReader.Native packages carry `sbom.spdx.json`, an SPDX 2.3 SBOM of the FFmpeg build (FFmpeg, zlib, dav1d and
  LAME: versions, sources, SHA-256, licences and CPEs), and so do the `shotdetect` release archives.

### Fixed

- `FrameDecoder`, `AudioReader` and `MediaProbe` (packet timestamps): a read that fails part way (a network reset, a
  `Stream` that throws, an I/O error) ended the input as if it were over, giving a silently shortened result; it's a
  `FrameReaderException` (`DecodeFailed`) now, with the stream's exception inside. A truncated or damaged file still
  ends where its data does, as in OpenCV.
- An https URL's server certificate is checked: FFmpeg 8 doesn't by default, so a self-signed or forged one was
  accepted (with any FFmpeg build that has TLS; FrameReader.Native's https, new here, checks from the start).
  `tls_verify=0` in `InputOptions` turns the check off. The segments an HLS playlist names are still opened without it (FFmpeg doesn't
  pass the option on).

## 2.3.0 – 2026-10-14

MediaException, the shared base of FrameReader's and ShotDetector's exceptions (issue #239), and FFmpeg.AutoGen bounded below 9.

### New

- `MediaException`: the base of `FrameReaderException` and ShotDetector's `ShotDetectionException` (issue #239), so one
  `catch (MediaException)` covers both packages, each keeping its own `Reason`. `FrameReaderException` is therefore an
  `InvalidOperationException` now: a `catch (InvalidOperationException)` also catches FrameReader's failures.
- FFmpeg.AutoGen's dependency is `[8.1.0, 9.0.0)`: 9.x binds FFmpeg 9's libraries, which FrameReader doesn't load.

## 2.2.1 – 2026-10-14

Fixes: FrameReader.Native loads on Alpine without LD_LIBRARY_PATH, and two progress-report fixes.

With the ShotDetector.Native aliases, NuGet picks FrameReader.Native 2.2.0 (the lowest version they allow): add
`FrameReader.Native.<rid>` 2.2.1 to get the fix. The shotdetect CLI's Alpine binary keeps 2.2.0's libraries until
ShotDetector's next release (on Alpine it uses the ffmpeg executable meanwhile).

### Fixed

- `FrameReader.Native.linux-musl-x64`: the libraries didn't load on Alpine unless their folder was on `LD_LIBRARY_PATH`
  (musl's loader doesn't look next to a library for its dependencies); every Linux build now carries a
  `$ORIGIN` RUNPATH (issue #278).
- Progress: the first report's `Position` could be a tick below zero (an MP3's start after the encoder delay); it starts
  at zero (issue #274).
- `ReferenceTone.Find`: progress on an input shorter than `SearchDuration` reports the input's duration, so `Fraction`
  moves at the real pace and the last report is at the end of the input (issue #273).

## 2.2.0 – 2026-10-14

FrameReader's first release of its own: the FrameReader.Native packages, PNG and MPEG program stream output, display rotation for stream copies, packet positions, and video frame types chosen by the encoder as ffmpeg does.

### New

- `FrameReader.Native.<rid>` and `FrameReader.Native`: FFmpeg's libraries for FrameReader (and ShotDetector), the
  packages that were `ShotDetector.Native`, now released with FrameReader.
- `RemuxOptions.DisplayRotation`: the video's display rotation in place of the input's, as ffmpeg's `-display_rotation`.
- `TestMediaOptions.VideoQuality`: a fixed quantizer, as ffmpeg's `-q:v`.
- `MediaInfo.PacketPositions`: each video packet's byte position (ffprobe's `packet=pos`), read along with
  `PacketTimestamps`.
- FrameReader.Native: the png encoder and the mpeg (program stream) and image2 (image sequence) muxers, so
  `TestMedia` and `VideoWriter` can write .mpg files and PNG frames.

### Fixed

- `TestMedia` and `Video.Transcode`: the encoder followed each frame's own picture type (FFmpeg's test sources mark
  every frame I, a decoder its input's), so TestMedia wrote all-intra video and Transcode copied its input's frame
  types; like ffmpeg, the encoder now picks them (unless a keyframe is forced).

## 2.1.0 – 2026-10-13

Released with ShotDetector 2.1.0: `TestMedia`, `Video.Transcode`, `Remux.CanCopy`, `FFmpegLibraries.MissingFilters`,
progress and async overloads for the whole-file jobs (ShotDetector's [CHANGELOG.md](../../CHANGELOG.md)).
