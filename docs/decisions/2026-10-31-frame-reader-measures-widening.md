# Managed measures in FrameReader: the widened scope (2026-10-31)

Issue #646, after [2026-10-31-frame-reader-measures.md](2026-10-31-frame-reader-measures.md). A later request from the
owner widened that scope: the measures must serve media-qc too, the owner's private file-based QC tool for video
deliveries. media-qc decodes through FrameReader's FFmpeg decoder and through ManagedCodecs' managed H.264/AAC, and
must get the same figures from each. So the picture meter takes 10-bit 4:2:2 planes, as ProRes and DNxHR decode to,
which the first decision left for later. Three questions were put to the owner with the recommended option first.

## Questions, choices and answers

1. **The picture measures' public API.** shotdetector-web's `PictureMeter`/`PictureMeasures` take ManagedCodecs'
   8-bit 4:2:0 `YuvFrame`, which FrameReader can't reference.
   - **Meter and measures**: `PictureFormat` (width, height, bit depth 8 to 16, chroma subsampling for 4:2:0, 4:2:2 and
     4:4:4, full range, rotation, time base) and the planes as bytes with strides, more than 8 bits as 16-bit
     little-endian, as FFmpeg and `DecodedFrame.GetPlane` hold them. `PictureMeter.Add(planes, pts, picture type)`
     fills `PictureMeasures`, which has per frame YMIN, YMAX, YAVG, BRNG, black, bars, the crop and the picture type,
     and over the frames `BlackSpans`, `BarsSpans`, `Crop`, `Take`, `Join`, `ToBytes` and `FromBytes`. An overload
     takes a `DecodedFrame`. The web's shape, widened, so its switch is mostly renames. *Chosen.*
   - Per-frame figures only, from a stateless call; the spans, cropdetect's running crop and serialisation stay in
     each app.
   - Meter and measures without `ToBytes`/`FromBytes`, so FrameReader keeps no binary format compatible.
2. **The managed loudness beside `Loudness.Measure`.**
   - **`LoudnessMeter` giving the existing `LoudnessResult`** (-70 LUFS for silence, as ebur128) plus per-channel
     peaks, fed interleaved chunks. Channel weights default to FFmpeg's layout for the count, or are given by channel
     names. *Chosen.* This is the first decision's answer 2 again; PR #655 builds it.
   - A result type of its own, as the web has.
   - A `Loudness.Measure` overload over a whole buffer.
3. **Silences: whose definition.** The web's `SilenceFinder` reports runs where every channel is under the level for
   the duration. silencedetect counts each channel's samples in turn, so on several channels it can report a silence
   an instant shorter, or starting an instant later, than that.
   - **silencedetect's own counting**, so the spans equal its `silence_start` and `silence_end` to the sample on every
     input, as the picture measures equal their filters. The web's figures may move by an instant on such edges.
     *Chosen.*
   - The web's definition, checked against silencedetect within an instant.

All three: the owner, 2026-10-31.

## What changes from the first decision

- **10-bit and 4:2:2 are in scope** for `PictureMeter`, and so is any depth from 8 to 16 bits and 4:2:0, 4:2:2 and
  4:4:4 chroma, each figure as FFmpeg 8.1's filters compute it at that depth. signalstats scales its broadcast range by
  `1 << (depth - 8)` and doesn't change it for full range. blackdetect's threshold is `0.1 × (2^depth − 1)` in full
  range and `16 × f + 0.1 × 219 × f` in limited, truncated. cropdetect's limit is `24/255` in single precision times
  `2^depth − 1`, rounded. Colour bars, which no filter finds, scale their definition's levels and tolerance by the
  same factor and read the chroma under each bar at its own subsampling.
- **The picture type is kept** (I, P, B ...: FFmpeg's `pict_type`), as FrameReader's own `PictureType` enum, since
  FrameReader can't use ManagedCodecs'. The first decision's note had left it in the web.
- **Silences** are silencedetect's counting, as above. The first decision's API (`SilenceFinder` giving `SilenceSpan`
  in samples) stands.

## Why

The figures are the measures' whole point, and media-qc compares deliveries across two decoders. Integer counts over
the same planes give the same numbers whichever decoder made them, but only if the meter takes the planes as both
decoders give them, at their own depth and chroma layout. Matching silencedetect's counting costs nothing and removes
the one place where "equal to FFmpeg" had an exception.

## Found while building slice 3 (2026-10-31)

Recorded here, with no new question for the owner:

- **4:2:2 under a quarter turn.** ffmpeg's autorotate turns the picture with `transpose`, which can't keep 4:2:2: it
  inserts a bicubic swscale conversion to 4:4:4 first, so signalstats' BRNG counts interpolated chroma there.
  `PictureMeter` counts the decoded planes, the signal's own, which ffmpeg gives with `-noautorotate`; YMIN, YAVG, YMAX,
  blackdetect and cropdetect read luma only and match autorotated ffmpeg. The test compares that one case's BRNG with a
  `-noautorotate` pass. 4:2:0 (which `transpose` takes as it is) and 4:4:4 match autorotated ffmpeg throughout.
- **The planes to feed it.** The checks read the decoder's own planes (`VideoFrameReader.TryTake`, `GetPlane`), which
  are what ffmpeg's filters see. `VideoFrameReader`'s packed `FrameFormat.Yuv420p` output leaves the last chroma row
  empty at odd heights (#656), so it isn't a way to feed the meter until that is fixed.
- **Bars at other depths and layouts.** The definition reads each upright chroma column's luma (the mean of the pixels
  over the chroma sample) and the chroma under its first pixel, so 4:2:2 and 4:4:4 read the chroma they have, and a
  rotated 4:2:2 picture reads it where the turn puts it. Levels and tolerance scale by 2^(depth − 8), full range too
  (FFmpeg's full-range 10-bit levels are 1023/255 of the 8-bit ones, within the tolerance of that).
