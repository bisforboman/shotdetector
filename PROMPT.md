# ShotDetector – starting prompt

I want to build a small C# console app that mimics PySceneDetect's shot/cut detection, as a learning project. Build it in this folder (C:\dev\ShotDetector).

Goal: given a video file, detect cuts and output a shot list (start/end frame + timecode per shot), similar to `scenedetect -i video.mp4 detect-adaptive list-scenes`.

## Approach

- Decode frames by running ffmpeg as a subprocess and reading raw frames from stdout (e.g. `-f rawvideo -pix_fmt bgr24`, downscaled to a small width). No native bindings like OpenCvSharp; I want the detection logic in my own code. Also read the frame rate/frame count (e.g. via ffprobe) so timecodes are correct.
- Before writing the detectors, read PySceneDetect's source (github.com/Breakthrough/PySceneDetect, ContentDetector and AdaptiveDetector) and port the logic faithfully: BGR→HSV conversion, per-channel mean absolute difference, weighted content_val, default thresholds, the adaptive rolling-window ratio, min_content_val, minimum scene length, and its downscaling behaviour. Note anywhere we deliberately differ.
- Implement ContentDetector first, then AdaptiveDetector (selectable via CLI option). ThresholdDetector (fades) is optional/later.
- Output: print a table like list-scenes, and optionally write CSV (and JSON) with shot number, start/end frame, start/end timecode, duration.
- Keep it simple and readable: plain loops over Span<byte> are fine, no premature SIMD.

## Verification

- Add a small comparison script/command that runs `scenedetect ... detect-adaptive list-scenes` on the same video and reports cuts that differ (with a tolerance of a frame or two).
- Add unit tests for the pure parts (HSV conversion, content_val, adaptive logic, timecode formatting) using synthetic frames.

## Before you start

Check whether dotnet, ffmpeg/ffprobe and Python/scenedetect are available on my machine and ask me before installing anything. Use the latest .NET SDK I have installed. Init a git repo and make small commits as you go. Briefly propose the project structure before writing code.
