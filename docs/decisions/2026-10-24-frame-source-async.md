# A frame-source run that can be cancelled on single-threaded WebAssembly (2026-10-24)

### Question

shotdetector-web runs the frame-source `ShotDetection.Detect(YuvVideo, IEnumerable<YuvFrame>, ...)` in a web worker on
single-threaded browser-wasm (issue #492). The run pulls its frames synchronously, so the worker's event loop is blocked
until it ends: a "cancel" message never arrives to cancel the token, and the page has to terminate the worker (losing the
runtime and the opened file). There is no other thread to run it on. How does a frame-source run become cancellable there?

### Choices

1. **`DetectAsync` over `IAsyncEnumerable<YuvFrame>`** (recommended): the source awaits between frames (yielding to the
   event loop every few frames), the run makes progress on those awaits, and the token cancels it as it does today.
2. **A public push API** (start, push a frame, finish): the caller owns the loop.
3. **Both.**

### Answer

**1** (the owner, 2026-10-24): one new overload, `ShotDetection.DetectAsync(YuvVideo, IAsyncEnumerable<YuvFrame>,
DetectionOptions?, CancellationToken)` returning `Task<DetectionResult>`, with the synchronous overload's options,
refusals and result.

How it is built: the run (`ShotDetection.Steps`, which was `RunTraced`, its body otherwise unchanged) is an iterator that yields where its next action
takes a frame from the caller, and ends with the result. Every input drives it: a file or a `Stream` to the end without
stopping (its yields do nothing there, so nothing about those paths changed), frames from the caller through a one-frame
slot (`FrameFeed`) that the driver fills before each step: `Detect` from the caller's `IEnumerator`, `DetectAsync` by
awaiting the caller's `IAsyncEnumerator` (no `Task.Run`, no other thread, never sync-over-async). Both frame-source
overloads therefore run the same steps and pull the same frames: one per step, none ahead. The frames before a start
time, which the synchronous reader took inside its seek, are taken one per step before the run starts (as many as the
seek's estimate needs, computed by the same method). An empty slot where a frame is taken is an internal error, never an
end of the video. The token is checked before each frame is pulled (so also while the frames before a start time are
taken), and the async enumerator is disposed when the run ends, fails or is cancelled. A source that never yields runs
the whole detection before `DetectAsync` returns: the page decides how often to give the event loop a turn.

Proved by `FrameSourceAsyncTests` (the synchronous result, shots told, progress, warnings and frames pulled on synthetic
cases; cancellation with at most one more frame pulled, also before the start time; everything on the caller's thread),
`FrameInputTests` (a file's decoded frames through both overloads; the same refusals) and `tests/FramesWithoutFfmpeg`
(the asynchronous run loads nothing of FFmpeg either).
