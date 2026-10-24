# Shot detection in the browser: a ManagedCodecs showcase (2026-10-24)

The owner wants a browser showcase: shot detection on a video file entirely in the browser (WebAssembly), decoding with
[ManagedCodecs](https://github.com/bisforboman/ManagedCodecs) (pure managed H.264 and MJPEG decoders, bit-exact with
FFmpeg, MP4 and AVI demuxers, already running on browser-wasm; not on NuGet yet) and converting with
ShotDetector.FastYuv. This was the Maybe row "ManagedCodecs as an optional managed decoder backend in FrameReader"
(docs/backlog.md); four questions decided its shape.

## How ShotDetector takes frames from another decoder

### Question

How should ShotDetector take frames from a decoder that isn't FFmpeg?

### Choices

1. **A public frame-source API in ShotDetector**: decoded YUV frames in; ShotDetector converts them with the given
   converter (FastYuv's) and runs the detectors.
2. **A ManagedCodecs backend inside FrameReader**: FrameReader decodes with ManagedCodecs where FFmpeg's libraries
   aren't there.

### Answer

**1. A public frame-source API in ShotDetector.** Any decoder can feed it, FrameReader stays FFmpeg's, and nothing on
the path needs FFmpeg. Built as `ShotDetection.Detect(YuvVideo, IEnumerable<YuvFrame>, DetectionOptions?)`: the
video's properties as FFmpeg reports them (size, average frame rate, time base and start pts, first frame's colour
tags, frame count) and each frame's planes with their strides and pts. A file's frames give exactly the file's result
with the same converter (FrameInputTests: every detector, time range, crop, downscale, frame skip, H.264 with B-frames
in MP4 and in MPEG-TS), and a process that runs it loads neither FFmpeg.AutoGen nor FFmpeg's libraries
(`FrameInputTests.LoadsNothingOfFfmpeg`).

## Where the showcase lives

### Question

Where should the showcase app live?

### Choices

1. **A new repository** (e.g. bisforboman/shotdetector-web): its own CI and GitHub Pages, ManagedCodecs as a submodule
   until it is on NuGet.
2. **A folder in shotdetector**.

### Answer

**1. A new repository.**

## The UI

### Question

What should the showcase's UI be built with?

### Choices

1. **Plain HTML + JSExport on browser-wasm, decoding in a web worker.**
2. **Blazor WebAssembly.**

### Answer

**1. Plain HTML + JSExport on browser-wasm, decoding in a web worker.**

## The license

### Question

FastYuv is LGPL-2.1 (a port of swscale); how is the showcase licensed?

### Choices

1. **Keep FastYuv a separate, replaceable DLL in the bundle; the showcase stays MIT.**
2. **License the showcase LGPL.**

### Answer

**1. FastYuv a separate, replaceable DLL; the showcase MIT.**

## Scope of the first version

8-bit 4:2:0 H.264 in MP4 (where ManagedCodecs and FastYuv meet), constant frame rate; trailers, not films (ManagedCodecs
decodes at 2.3-3.6x FFmpeg's time on one thread). Exactness first: before any UI, the ManagedCodecs + FastYuv path
must give the same scene and stats CSVs as the FFmpeg path, natively and under WASM.
