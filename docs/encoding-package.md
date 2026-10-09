# Encoding: investigation (2026-10-09)

Issue #81 asked for H.264 encoding (pixel format, `-g`/`-keyint_min`, forced keyframes) and MP3 encoding. We
declined it as out of scope; the user reopened it as a question: could a separate package carry it? This is what we
found. Not legal advice: the uncertain points are flagged.

## Where encoders have to live

Encoders are part of `libavcodec`, muxers of `libavformat`. They can't come as an add-on beside our decode-only
`ShotDetector.Native.<rid>` libraries: an "encoding package" is a **second, complete FFmpeg build** with the same
library names, used *instead of* ShotDetector.Native. (Or the encoders that fit go into ShotDetector.Native itself.)

## Size (linux-x64, our build script plus each set; throwaway CI run on `debug/encoding-sizes`)

| Build | Libraries | Zipped (≈ package) | Added, zipped |
|---|---|---|---|
| Today: decode only | 19.3 MB | 8.3 MB | |
| + muxers (mp4, mov, matroska/webm, adts, mp3, wav) | 19.7 MB | 8.5 MB | +0.2 MB |
| + muxers, AAC (FFmpeg's own), LAME (MP3), OpenH264 | 21.0 MB | 9.1 MB | +0.8 MB |
| + muxers, AAC, LAME, x264 | 22.2 MB | 9.5 MB | +1.3 MB |

OpenH264 is C++: a static build makes libavcodec need `libstdc++` (link with `-lstdc++`). x264 builds from its git
`stable` branch (VideoLAN's tarball endpoint returned a non-archive). Other platforms weren't built; expect similar.

## Quality and the options #81 asked for (Sintel trailer 1080p, first 20 s, 2 Mbit/s, FFmpeg 8.1)

| Encoder | Wall time | Size | VMAF | Profile | Keyframes every 2 s (`-g 48 -keyint_min 48 -force_key_frames`) |
|---|---|---|---|---|---|
| x264, medium | 3.5 s | 5.3 MB | **93.9** | High | exactly, with `-x264-params scenecut=0` (otherwise also at scene cuts) |
| x264, veryfast | 2.3 s | 5.2 MB | 92.3 | High | same |
| OpenH264 | 3.2 s | 3.9 MB (undershoots) | 90.1 | "High" flag; no B-frames | exactly |
| Media Foundation (Windows) | 1.4 s | 6.5 MB | 85.2 | Constrained Baseline | exactly |

Pixel formats: x264 takes 4:2:0/4:2:2/4:4:4, 8 and 10-bit; OpenH264 and Media Foundation 4:2:0 8-bit only. MP3 via
LAME at 192 kbit/s works as expected.

## Licences and patents

- **MP3 (LAME):** LGPL; FFmpeg's `--enable-libmp3lame` needs neither `--enable-gpl` nor `--enable-nonfree`. The MP3
  patent programme ended in 2017 (last patent expired). No known restriction. (One file, `fft.c`, carries a GPL
  header per Debian; worth checking for a clean inventory.)
- **AAC (FFmpeg's encoder):** LGPL, part of FFmpeg. AAC has its own patent pool (Via LA); decoders and encoders in
  products are licensed per unit. (Not researched in depth; flag.)
- **H.264, any encoder:** the Via LA AVC pool is active (patent list dated 2026-09-28). Encoders count as units:
  the first 100,000 a year per licensee free, then $0.20/$0.10. Free or open-source distribution isn't exempt. The
  last major US patent reportedly expires around late 2027; others (other countries, later features) into the 2030s
  (unverified against the list).
- **OpenH264 (BSD):** Cisco's patent cover applies only to Cisco's own binary, downloaded separately to the end
  user's device, user-switchable, attributed, and for non-commercial use. Bundled in a NuGet package or built from
  source: no cover; the redistributor is the AVC licensee. No Cisco binary for musl.
- **x264 (GPL-2.0+):** turns the whole FFmpeg build GPL. In the FSF's view an app that loads it in-process is a
  combined work and must be GPL when distributed. x264 LLC sells a commercial licence, without the AVC patents.
- **Platform encoders** (Media Foundation, VideoToolbox, NVENC, VAAPI): fit our LGPL build, add almost nothing to
  the size, and the OS or GPU vendor probably carries the patent licence for the codec. No vendor states that this
  covers third-party apps (unconfirmed). Not everywhere: Windows N editions, musl, containers, CI, machines without
  a suitable GPU; output and options differ per backend.

## Options

1. **MP3, AAC and muxers into ShotDetector.Native** (+~0.5 MB zipped): LGPL as today, no new package. Covers MP3,
   audio extraction and Remux. H.264 only via the ffmpeg executable (SplitVideo's `Args`).
2. **Option 1 plus platform H.264 encoders** (Media Foundation on Windows, VideoToolbox on macOS; NVENC/VAAPI where
   present): still one LGPL package, H.264 where the OS has it, patents probably the vendor's. Not deterministic
   and missing on some systems.
3. **Option 1 plus OpenH264 built from source:** H.264 everywhere, LGPL, but with the AVC patent licensing left to
   whoever ships an app with it (documented), and baseline-class quality.
4. **A separate GPL build with x264 (`ShotDetector.Native.Gpl.<rid>`) replacing ShotDetector.Native:** the best
   H.264 and every option #81 asked for; GPL for every app that ships it, AVC patents still theirs. A second set of
   native builds and packages to maintain.

They combine: 1 is the base of 2 and 3; 4 can come later on its own if anyone asks for x264 in-process.

Whichever is chosen, the API would mirror the readers: a `VideoFrameWriter` (frames in, an encoder and muxer out)
and an `AudioWriter`, in FrameReader, tested byte for byte against ffmpeg's command line where the encoder is
deterministic (x264, OpenH264, LAME and FFmpeg's AAC with fixed threads are; platform encoders aren't).
