# Frame sources without FFmpeg's code, then OnShot for ranges (2026-10-30)

shotdetector-web (browser WebAssembly, frames decoded in the browser) runs `ShotDetection.DetectAsync(YuvVideo,
IAsyncEnumerable<YuvFrame>, ...)`, `ScoreRangeAsync` and `Merge`, and filed two issues against them on 2026-10-30:

- #597: those entry points reach FFmpeg-only code statically, so its trimmed bundle ships FFmpeg.AutoGen (447 KB of
  IL) and the AOT code of the FFmpeg paths, which it never runs. It won't stub ShotDetector's internals itself (ILLink
  substitutions break silently when they change).
- #598: `ScoreRange` from frame 0 doesn't tell `DetectionOptions.OnShot` its shots as `Detect` does, so a run that can
  be resumed after a page reload loses its live shot table (2026-10-30-score-range-on-shot.md).

Both change `ShotDetection`'s run (`Steps`) and the range code.

## Question

Which first, and in one pull request or two?

## Choices

1. **#597 first, then #598**, a pull request each, the second branched from main after the first merges (never
   stacked): the refactor of the run lands, with its check, before a feature is built on it.
2. **#598 first**: the web page's live table sooner; the refactor then moves the new code too.
3. **Both in one pull request**: one review, but a refactor across the executable path and a new API in one diff.

## Answer

**#597 first, then #598** (the owner, 2026-10-30).

## What #597 became

The trimmer's dependency dump on main (3.21.0, `-p:_TrimmerDumpDependencies=true` on benchmarks/wasm's publish) still
showed the bridges the issue measured on 3.20.0: `Steps` constructed the file and Stream readers and called
`FindReferenceTone` and `ScoreChunked`; `Traced` and the progress reports asked `VideoReader.DecodesInProcess`;
`VideoReader.Frames` branched to `FramesInProcess` and the executable's read, and `Pts` to the timestamp pass.

Every one now goes through an interface whose one implementation only a decoded input creates, so the trimmer keeps
none of it for an app that never does (it keeps an interface's implementations only on types something constructs):

- `ShotDetection.IDecodedInput` (ShotDetection.Decoded.cs): a file's or a Stream's reader (with the spill of a Stream
  whose headers come last), the reference-tone trim and scoring in chunks. The file and Stream entry points create
  `DecodedInput`; `Steps` takes it, or the frames' reader.
- `VideoReader.IDecoding` (VideoReader.Decoding.cs): `DecodesInProcess`, the decoded read and the timestamp pass. Only
  the decoded inputs' constructor creates `Decoding`; frames from the caller have none.

Measured on benchmarks/wasm (`Detect` on YuvFrames), AOT publish, `_framework/`, Linux x64, .NET 10.0.13:

| | main (3.21.0) | after |
|---|---|---|
| Raw | 14.40 MB | 10.39 MB (-27.8%) |
| `gzip -6` (tar of the folder) | 4.26 MB | 3.28 MB (-23.0%) |
| `dotnet.native.wasm` | 11.03 MB | 7.84 MB |
| `FFmpeg.AutoGen.wasm` | 447 KB | gone |
| `FrameReader.wasm` | 88 KB | 11 KB |
| `ShotDetector.wasm` | 217 KB | 137 KB |

System.Diagnostics.Process and System.IO.Pipes went too. benchmarks/wasm now also runs `DetectAsync`, `ScoreRangeAsync`,
`ScoreRange` and `Merge` (each must give `Detect`'s CSVs on an input's first frames), so the check covers them: ci.yml's
`wasm-trim` job publishes it trimmed and fails if FFmpeg.AutoGen is in the bundle, printing the chain from `Main` that
reached it (tools/trim_path.py).
