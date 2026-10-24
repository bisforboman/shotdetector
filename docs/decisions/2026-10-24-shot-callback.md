# Shots as they're found, for every Detect (2026-10-24)

shotdetector-web runs `ShotDetection.Detect(YuvVideo, IEnumerable<YuvFrame>, ...)` on single-threaded browser
WebAssembly and wanted each shot as soon as it is known, so its page fills during a long run and keeps what was found
when the run is cancelled (issue #493). `Run` already told DetectStreamAsync each shot (`onShot`); the frame-source
overload passed nothing.

## Question

What shape should "each shot as soon as it is known" take on Detect?

## Choices

1. **`DetectionOptions.OnShot`, an `IProgress<Shot>?`**, reported synchronously on the detection's thread like
   `DetectionOptions.Warnings`, for every Detect overload (file path, Stream, frames). DetectStreamAsync keeps its own
   channel.
2. **An extra `Action<Shot>? onShot` parameter** on the frame-source Detect only.
3. **The option, but refused** (an ArgumentException) on the path and Stream overloads.

## Answer

**1. `DetectionOptions.OnShot` on every overload.** How it behaves, decided while building it:

- **It only listens.** Setting it changes neither the shots nor how the video is read: `DetectionMode.Auto` still
  scores a file in chunks for Detect. Chunked, the shots are told in order, once, after every chunk is scored (the
  decisions run in frame order then); `DetectionMode.Sequential` tells them while decoding. Making OnShot switch Auto
  to sequential would have slowed every file run that only wanted to log its shots.
- **When:** as DetectStreamAsync's: with one detector and neither DropShortScenes nor MergeLastScene, each shot once
  the cut ending it is confirmed, the last at the end; otherwise all at the end, as a later cut can still change them.
- **DetectStreamAsync with OnShot set:** both get every shot (OnShot first, then the shot is yielded), as
  DetectStreamAsync already tells `DetectionOptions.Warnings`. Refusing it would make one options object unusable for
  both calls for no reason.
- **The thread:** synchronous on the thread running the detection, so every shot is told before Detect returns; a
  `System.Progress<T>` posts to the thread that created it instead, as with `Warnings`.
