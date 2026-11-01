# Shot Chapters' black spans: from ShotDetector's own pass (2026-11-01)

The Jellyfin plugin (bisforboman/jellyfin-plugin-shotchapters, docs/decisions/2026-10-31-jellyfin-plugin.md) marks
black stretches as media segments. It finds them as runs of frames whose ThresholdDetector `average_rgb` is under 12,
lasting 1 s or more. FrameReader 2.19.0's `PictureMeter.BlackSpans()` gives ffmpeg's `blackdetect` spans exactly, so
the question was whether, and how, the plugin should use blackdetect's measure instead.

## What was measured (2026-11-01, BtbN FFmpeg 8.1, `blackdetect=d=1` with its default thresholds)

| File | blackdetect | average_rgb < 12 |
| --- | --- | --- |
| Tears of Steel 720p | 5 spans: 0-8.92, 64.46-69.92, 588-589, 705.42-709.5, 730.33-734.13 s | none |
| Sintel trailer 480p | 4 spans | 6 spans |
| Big Buck Bunny trailer 480p | none | none |

- **Tears of Steel:** the master's black frames have an `average_rgb` of 14.3 (raised black), so a fixed 12 never
  fires. blackdetect's threshold is relative to the black level, so it does.
- **Sintel trailer:** three of the six `average_rgb` spans are dark scenes rather than black, and the spans both
  measures find end at different times.
- **Cost:** a second pass of blackdetect alone took 19.4 s on Tears of Steel, against 31.1 s for the detection pass
  (adaptive and threshold detectors, executable, sequential), about 60% more.

## Choices

1. Run jellyfin-ffmpeg's own blackdetect filter in a second pass from the plugin. Exactly blackdetect, no work in
   this repository, about 60% more scan time.
2. **Ask ShotDetector for black spans from its detection pass**, for example blackdetect in the executable's ffmpeg
   graph with its log parsed as showinfo's is. There is no extra decode, but the plugin waits for a ShotDetector
   release.
3. Pipe yuv420p frames to `PictureMeter` in a second pass. The same result and the same extra decode as 1, with more
   data through the pipe.
4. Keep `average_rgb`. It costs nothing, but misses raised-black masters and marks dark scenes as black.

## Answer

**2** (the owner, 2026-11-01).

Until that API is released, the plugin keeps `average_rgb`. Once it is out, the plugin switches to it and its
settings key changes, so every file is scanned again once.

The API should:
- give spans equal to ffmpeg's blackdetect;
- work on the executable path, because the plugin ships no native libraries;
- join spans across parallel chunks.
