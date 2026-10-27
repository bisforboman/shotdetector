# An image encoder and MaxWidth for FrameReader (2026-10-27)

### Question

Issue #542 asks FrameReader for a JPEG encoder (FFmpeg's mjpeg) and a MaxWidth option. What should I do?

A service samples a video at 1 fps and sends the frames to a vision model as JPEG, still through the ffmpeg executable
(`-vf fps=1,scale='min(1280,iw)':-2` to `.jpg`, and `-q:v 2` for extra timestamps). `VideoFrameReader` covers the
decoding (`FrameRate`, `TryReadAt`/`TryReadForwardTo`) and `MediaProbe` the duration; what's missing is turning a frame
into a JPEG without another image library, and a width cap that keeps the aspect ratio without probing first.

### Choices

1. **Both, two PRs**: `VideoFrameReaderOptions.MaxWidth` first, then a JPEG encoder on FFmpeg's `mjpeg`, with `mjpeg`
   in the native build and `Capability.Jpeg`; one FrameReader 2.12.0 release after both.
2. **MaxWidth now, JPEG later**: the option only; the encoder as a backlog row.
3. **Backlog rows only**: neither now.
4. **Decline**: a JPEG encoder is out of FrameReader's scope.

### Answer

**1. Both, two PRs** (the owner, 2026-10-27). `MaxWidth` is ffmpeg's `scale='min(N,iw)':-2` byte for byte: never
upscaled, the height `av_rescale(w, ih, iw * 2) * 2` (to the nearest even number, ties up, so an odd height becomes
even even when the width stays; 0 becomes the input's, as libavfilter fills in a link's size), measured after
autorotate; refused together with `Width` and `Height`.

How the JPEG encoder's default quality (ffmpeg's rate control, whose state carries over in a reused codec context)
keeps each frame identical to a one-frame ffmpeg run is settled in the encoder's PR and added here.

## Which formats (2026-10-27)

### Question

Which image formats should FrameReader's new encoder cover?

PNG costs nothing (`png` is already among the native build's encoders); JPEG needs `mjpeg` added; WebP needs libwebp
in every native build variant (Linux glibc and musl, Windows, macOS).

### Choices

1. **JPEG + PNG, WebP later**: one image encoder, named for images, with a format choice named as ShotDetector's
   export names them (`jpg`, `png`, `webp`); `webp` refused with a clear message until its encoder ships;
   `Capability.Jpeg` and `Capability.Png`.
2. **JPEG, PNG and WebP now**: libwebp in every native build variant too.
3. **JPEG only, as asked**.

### Answer

**1. JPEG + PNG, WebP later** (the owner, 2026-10-27). Options are named for ffmpeg's, per format, and refused for
the other: JPEG's qscale (`-q:v`, 2-31, lower is better; not ShotDetector's 0-100 export quality) and PNG's
compression level (`-compression_level`: speed against size, not quality). No generic "Quality". WebP becomes a
backlog row: libwebp in every native variant, then the `webp` format and `Capability.WebP`.
