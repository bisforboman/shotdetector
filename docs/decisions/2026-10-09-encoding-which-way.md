# Encoding: which way (2026-10-09)

After the investigation (docs/encoding-package.md: encoders live in libavcodec, so a separate package is a second
full build replacing ShotDetector.Native). Asked: **MP3 (LAME), AAC and muxers in ShotDetector.Native, still LGPL,
+~0.5 MB zipped; H.264 stays with the ffmpeg executable (recommended)**; not chosen: platform H.264 encoders
(Media Foundation, VideoToolbox, NVENC, VAAPI), OpenH264 built from source (AVC patents left to app vendors), or a
separate GPL build with x264.

Since 2026-10-10 ([2026-10-10-h-264-for-81-s-user.md](2026-10-10-h-264-for-81-s-user.md)) a GPL package with x264 was
built for #81, and dropped again on 2026-10-11
([2026-10-11-drop-the-gpl-native-packages.md](2026-10-11-drop-the-gpl-native-packages.md)): H.264 needs an FFmpeg with
libx264.
