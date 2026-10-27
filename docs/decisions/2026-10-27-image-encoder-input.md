# The image encoder's input, default quality and promise (2026-10-27)

Follow-up to [2026-10-27-image-encoder.md](2026-10-27-image-encoder.md) (issue #542), from building the encoder.

### Question 1

A `VideoFrame` carries no aspect ratio or colour tags, but they change the encoded bytes: the sample aspect ratio is in
JFIF's density and PNG's pHYs (setsar 0, 1 and 4/3 give three files of each), primaries and transfer in PNG's cHRM and
gAMA, the colour range and matrix in the conversion to the encoder's YUV or RGB. How should the encoder get them?

### Choices

1. **A**: optional tags on `VideoFrame`, filled by `VideoFrameReader` with what ffmpeg's frame carries after the same
   filters.
2. **A plus a raw-buffer overload** taking pixels, size, format and the tags from the caller.
3. **A plus a decoder-frame overload** (`DecodedFrame`, ffmpeg's `-i X out.jpg` path itself).

### Answer

**1. A** (the owner, 2026-10-27). Exactly the tags the bytes depend on: `SampleAspectRatio` (0/1 when unknown),
`ColorRange`, `ColorSpace`, `ColorPrimaries`, `ColorTransfer` (FFmpeg's names, null when unspecified, as `MediaInfo`'s).
`VideoFrame` is a ref struct only FrameReader creates, so they are get-only properties: additive, and a `default` frame
reads as untagged. The aspect ratio follows ffmpeg: the container's over the codec's (`FrameDecoder` now applies it, as
ffmpeg's decoder side does; no pixel output changes), swapped by a rotation, changed by scaling to another shape; gray is
tagged as ffmpeg's link has it (full range, no matrix).

### Question 2

Without a qscale, ffmpeg's JPEG uses rate control, whose state carries from frame to frame in one codec context: frame
2 of a run that writes several differs from frame 2 written alone. What should a reused encoder do?

### Choices

1. **A fresh codec context per frame without a qscale** (reused with one): every image is a one-frame ffmpeg run's.
2. **Identity only with a qscale**, documented.

### Answer

**1. Fresh per frame** (the owner, 2026-10-27). Opening and freeing the mjpeg context at 1080p takes about 0.1 ms
(measured: 0.07-0.16 ms), against roughly 10-15 ms to convert and encode the frame. One ffmpeg run writing N frames
(`frame_%06d.jpg`) differs from its second frame on; a test checks both.

### Question 3

Building it showed that ffmpeg's one command `-vf scale=...,format=F out.jpg` isn't the reader's frame then the
encoder: ffmpeg folds the conversion to the encoder's full range into the user's scale (one swscale pass from TV range
at the old size to PC range at the new), where the reader scales and the encoder converts; and from an RGB frame of a
BT.709-tagged source, its one command keeps BT.709 for the YUV, a raw RGB frame gets BT.601. What should the encoder
promise?

### Choices

1. **Raw-frame exact now, plus a backlog row** for an encoder that scales from a decoded frame (the CLI's graph itself).
2. **Exact one-command now**.
3. **Raw-frame exact only**, no row.

### Answer

**1. Raw-frame exact** (the owner, 2026-10-27), as `VideoWriter` promises: the file ffmpeg writes from the same raw
frame with its tags (`-f rawvideo ... -vf setparams=...,setsar=... -frames:v 1 out.jpg`), byte for byte, tested for
every `FrameFormat`, both formats and their options, scaled and not, on eight sources. Close to, not identical with,
the one command when the reader scales; a `Yuv420p` frame of a 4:2:0 source at its own size is exactly
`ffmpeg -i X -frames:v 1 out.jpg`. The scaling encoder becomes a backlog row.
