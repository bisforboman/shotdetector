# Encoding: which way (2026-10-09)

After the investigation (docs/encoding-package.md: encoders live in libavcodec, so a separate package is a second
full build replacing ShotDetector.Native). Asked: **MP3 (LAME), AAC and muxers in ShotDetector.Native, still LGPL,
+~0.5 MB zipped; H.264 stays with the ffmpeg executable (recommended)**; not chosen: platform H.264 encoders
(Media Foundation, VideoToolbox, NVENC, VAAPI), OpenH264 built from source (AVC patents left to app vendors), or a
separate GPL build with x264.
