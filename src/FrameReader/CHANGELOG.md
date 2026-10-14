# FrameReader changelog

FrameReader's versions are git tags (`framereader-vX.Y.Z`); each one publishes the FrameReader package and a GitHub
release. Semantic versioning: a breaking change to its public API is a major version. What's merged but not released
yet is in changes/framereader/ (one file per pull request).

Up to 2.1.0, FrameReader was released together with ShotDetector at the same version: those versions' changes are in
ShotDetector's [CHANGELOG.md](../../CHANGELOG.md), each marked "FrameReader:".

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
