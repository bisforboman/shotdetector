# Adaptive: ignore repeated frames (2026-11-17)

The Subtitle Edit prototype ([docs/subtitle-edit-prototype.md](../subtitle-edit-prototype.md)) found 67 false cuts from
adaptive at Subtitle Edit's default slider, all on a telecined film (Duck and Cover) and a low-bitrate encode
(Elephants Dream), and put them down to repeated frames: a repeated frame changes by zero, which pulls down the
neighbour mean adaptive divides by. The backlog row "Adaptive: ignore repeated frames" (issue #633) asked for an option
that leaves repeated frames out of that mean, measured again on the same labels.

## Question: an option that departs from scenedetect?

Choices:

1. An option, off by default, documented with the other places ShotDetector differs.
2. Change adaptive itself.
3. Leave adaptive as scenedetect has it.

The owner approved the option, off by default (2026-11-17).

## Sub-choices (Claude's, pending the owner's review)

**What counts as repeated: content_val below 0.5**, not exactly 0. A frame repeated before a lossy encode isn't bit
for bit the same after it. In a 24 to 30 fps copy of the Sintel trailer made by repeating frames (x264 CRF 18), none of
the 313 repeats scored exactly 0; 17 % scored below 0.05, 63 % below 0.25, 86 % below 0.5 and 93 % below 1. On the labelled clips, 0.5 and exactly 0
gave the same false cuts, 1 missed 3 more cuts and 2 missed 19 more (table below), so the threshold sits below where
slow motion starts. It is a constant (`AdaptiveDetector.RepeatedFrameScore`), not an option of its own; a second knob
can follow if material needs it.

**The repeated frame's own score: unchanged.** It still gets its ratio and its stats row, as in scenedetect, and can't
be a cut anyway (its content_val is far below `min_content_val`). Only its place in other frames' means goes. When every
neighbour in the window is a repeat (a still picture, then a change), the mean is 0, which scenedetect already treats as
the highest ratio when the frame passes `min_content_val`.

**The window stays 2 × `--frame-window` frames**, with the repeats left out of the mean (fewer frames averaged), rather
than reaching further for more frames. A fixed window keeps cuts reported the same number of frames late, and chunked
detection's overlap unchanged (docs/parallel-detection.md).

**Names**: `DetectionOptions.IgnoreRepeatedFrames` (and `DetectorSettings.IgnoreRepeatedFrames` for one detector of
several), `--ignore-repeated-frames` on the CLI, `ignore-repeated-frames` in `[detect-adaptive]` of a config file. Off,
nothing changes, the ranges' fingerprints included (the line is written only when set).

## What it measured

The prototype's labels (243 hand-labelled cuts; missed / false), against FFmpeg `scene` as Subtitle Edit runs it:

| Configuration | Missed / false |
|---|---:|
| FFmpeg `scene` 0.4 (SE's default) | 121 / 7 |
| FFmpeg `scene` 0.2 | 20 / 28 |
| Adaptive 3 (slider 0.4) | 46 / 67 |
| Adaptive 3, `--ignore-repeated-frames` | 47 / 67 |
| Adaptive 1.5 (slider 0.2) | 17 / 118 |
| Adaptive 1.5, `--ignore-repeated-frames` | 17 / 118 |

At adaptive 3, a repeated-frame threshold of exactly 0 gave 46 / 67, 1 gave 49 / 67 and 2 gave 65 / 63. **The option
doesn't remove the false cuts.** The reason is not repeated frames: both files are archive.org 512 kb/s encodes with a
keyframe every 12 frames, and their content_val jumps at each keyframe (the picture's detail resets), 3 to 4 times the
frames around it in a moving shot. Of adaptive 3's 67 false cuts, 56 sit exactly on a keyframe and 64 within a frame of
one; of the 115 cuts it found, 16 do. The prototype's mpdecimate count measured near-duplicate blocks, not frames that
pull down the mean: in these files, 2.5 % (Duck and Cover) and 8 % (Elephants Dream) of the frames score below 0.5, stills and black
included.

On frames that do repeat (the same 24 to 30 fps copies, of the Sintel trailer and Tears of Steel's first 4 minutes),
adaptive 3 gives the original's cuts with or without the option. At 1.5 the option halves the cuts the copy adds: 6 to
3 on Tears of Steel, 4 to 2 on Sintel.

scenedetect's own `--min-content-val` trades the keyframe false cuts against Duck and Cover's low-contrast real cuts: 3
with 25 gave 70 / 15, 1.5 with 25 gave 47 / 31, against FFmpeg 0.2's 20 / 28.

## Follow-up: what to do with the option

The option works as designed but doesn't fix what it was approved for. Choices, put to the owner with the draft PR:

1. **Drop the option; keep the findings.** The PR becomes docs only: this record, §6 of the prototype note and the
   backlog rows (a new Maybe row for the keyframe pulses). No new divergence from scenedetect without a measured gain
   on the labels. *Recommended.*
2. **Keep the option** (merge as is): a small gain where frames really repeat, none on the labels; one more documented
   place where ShotDetector can differ.
3. **Keep the option and take up the keyframe pulses next** (a design for the owner first: real cuts land on keyframes
   too, 16 of the 115 found).
4. **Drop the Subtitle Edit side** (keep `--save-shotchanges`): FFmpeg `scene` at 0.2 beats every ShotDetector
   setting tried on these labels.

Not answered yet.
