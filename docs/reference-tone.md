# Reference tone (1 kHz line-up tone): detect and remove

Status: **In progress** (backlog section 4). Decisions in [decisions.md](decisions.md) ("Reference tone").

## Problem

Some source files start (and occasionally end) with a **reference tone**, also called line-up or
alignment tone: a steady 1 kHz sine wave used for level alignment, typically 30-60 seconds long. In
video sources it usually runs alongside colour bars ("bars and tone"); in audio-only files it appears on
its own.

ShotDetector treats this segment as programme content today. That causes:

- The tone (and the bars, when present) ends up in the output as if it were part of the material.
- Downstream audio analysis (speech, music, levels) is skewed by a long, loud, artificial signal.
- The tone has to be found and trimmed by hand.

## Characteristics of the tone

- Fixed frequency, nominally 1 kHz (may drift slightly on older tape transfers).
- Constant level, commonly around -18 dBFS (EBU) or -20 dBFS (SMPTE).
- Usually at the very start of the file, sometimes also at the end.
- May be **deliberately interrupted**: some line-up conventions (EBU stereo line-up, BBC GLITS) briefly cut
  one channel at regular intervals, so the tone isn't always continuous.
- In video files it typically coincides with a static colour-bar frame.

## Expected behaviour

- ShotDetector identifies reference-tone segments and reports them with start/end timestamps.
- The user can choose to have these segments removed (trimmed) from the output.
- Normal programme audio, such as music with sustained notes or a short beep inside the content, is not
  flagged as tone.
- Opt-in and separate from detection: scenedetect has no such feature, so it must not change any cut,
  stat or export that is compared against scenedetect.

## Acceptance criteria

- [ ] A file with leading 1 kHz tone has the tone segment detected with timestamps accurate to well under a second.
- [ ] Interrupted line-up tones are reported as one segment, not many fragments.
- [ ] Files without tone produce no detections.
- [ ] Trimming the detected segment yields output that starts at the real programme content.
- [ ] Parity with scenedetect is unchanged when the feature is off.

## Open questions (for the user)

- Scope: detection only first, with trimming as a follow-up?
- Input: the user wants this for "certain audio files"; ShotDetector takes video today. Audio-only input,
  or only the audio track of video files?
