# Shots told where timestamps start over (2026-10-28)

`DetectionOptions.OnShot` and `DetectStreamAsync` tell each shot once its cut is confirmed. Where timestamps start over
(two encodes joined), scenedetect labels frames by time, so the later encode's cuts come before cuts already found and
the result's shots are sorted at the end; the shots told before that weren't told again, so a listener's list could
differ from `DetectionResult.Shots` (#497 documented it; backlog Maybe row "Shots told before timestamps start over are
not told again").

Measured on InProcessTests' joined encodes (two h264 .ts encodes, the second's timestamps starting over), content
detector, FFmpeg 8.1: a file, through the executable and in-process, sequential, chunked and 0.7.1, told
`[0-50) [50-100) [100-225)` against the result's `[0-50) [50-75) [75-100) [100-225)`. The probe's packet pts, in decode
order, show the reset before the first frame (a pts below one 16 packets earlier, at packet 100 there). A Stream through
the executable doesn't start over (ffmpeg, without `-copyts`, makes the timestamps continue: cuts 50, 100, 125, 175);
in-process it does, and can't know it ahead. MPEG-PS decoded in-process takes its timestamps from the frames as they
arrive (docs/decisions/2026-10-27-pre-scan.md), so it can't either. Frames from the caller refuse timestamps that go
back (constant frame rate only).

## Question

Retelling shots after a timestamp reset: what should be built?

## Choices

1. **Files up front + hold PS.** A file's probe shows the reset in its decode-ordered packet pts (taken before they are
   sorted, kept in the probe the chunks share): its shots are all told at the end, as with DropShortScenes. MPEG-PS whose
   timestamps arrive with the frames tells them all at the end too. No new API; only those files wait for their shots.
2. **Add a correction event.** A new item ("shots from number N on are these"), e.g. `DetectionOptions.OnShotsReplaced`,
   and a DetectStreamAsync overload yielding events, as it can't take back a yielded shot. No latency, but every listener
   handles a retraction.
3. **Document only.** As now.

## Answer

**1. Files up front + hold PS** (the owner, 2026-10-28). `VideoReader.TimestampsStartOver` (some pts below one at least
16 packets earlier, past any decoder's reordering; MPEG-PS's from the decoding pass's frames, where a frame without a
timestamp, 0, doesn't count; or MPEG-PS's still arriving) turns off telling shots as cuts arrive. A false alarm only
tells the shots later. A Stream decoded in-process still can't know it: the OnShot XML doc says so, and the two
decoders' difference on such a Stream is its own backlog row.

Since 2026-10-29 (docs/decisions/2026-10-29-stream-timestamp-reset.md) the executable runs a Stream with `-copyts` too,
so its timestamps start over as in-process (and as OpenCV's): neither decoder can know the reset ahead on a Stream.
