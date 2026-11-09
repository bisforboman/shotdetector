# Subtitle Edit: a prototype and numbers (2026-11-09)

### Question

What to do with issue #633, "ShotDetector as Subtitle Edit's shot-change engine"? The scoping note
(docs/ideas/subtitle-edit.md, 2026-10-31) found that Subtitle Edit (SE) has no engine seam for shot changes. It
shipped the first slice, `--save-shotchanges`, and parked the SE side until a comparison against ground truth showed a
gain.

Choices:

1. Prototype it and get numbers. Wire `ShotDetection.Detect` (adaptive) into a local branch of SE's Avalonia version,
   with the sensitivity slider mapped onto adaptive's threshold. Then compare it with FFmpeg's `scene` filter on real
   clips against hand-labelled cuts.
2. A licence check only.
3. Skip it.

### Answer

The owner, 2026-11-09: **1, prototype and numbers.** Nothing goes to Subtitle Edit (no issue, discussion, PR or fork)
without the owner's explicit OK.

The outcome is in [docs/subtitle-edit-prototype.md](../subtitle-edit-prototype.md). In short: the licences fit (SE5 is
MIT, SE4 is GPL-3.0, and FastYuv must stay out of SE's single-file build). The NuGet route works only through the
ffmpeg executable plus ffprobe, because SE's FFmpeg.AutoGen 9 breaks our in-process decoding. At SE's default,
adaptive finds many more cuts than `scene` (81 % against 50 %), but it also flags many more false cuts on material with
repeated frames (telecined film, low-bitrate encodes). Whether to approach SE with these numbers was put to the owner
next.
