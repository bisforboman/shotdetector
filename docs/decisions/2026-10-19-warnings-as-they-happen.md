# Warnings as they happen, for streaming callers too (2026-10-19)

### Question

How should streaming callers (`DetectStreamAsync`) get the warnings?

`DetectionResult.Warnings` (docs/decisions/2026-10-18-library-warnings.md) left DetectStreamAsync out: it yields shots
and has no result object. Its warnings are known at different times: the https HLS playlist decoded by the ffmpeg
executable (a security notice: its segments are fetched without a certificate check) right after the probe; why
`DetectionMode.Parallel` read sequentially before the first frame; a fallback from scoring in chunks mid-run, before
the sequential read. A long or live input can run for hours before any result exists.

### Choices

1. **`DetectionOptions.Warnings`**, an `IProgress<string>` told each warning as soon as it is known, like `Progress`;
   it works for Detect too, which still fills `DetectionResult.Warnings`.
2. **A new DetectStream method** returning an object with the shots and the warnings, readable after the end: too
   late for the security notice on a long or live input.
3. **Leave it** as it is.

### Answer

**1. `DetectionOptions.Warnings`.** One place in the detection makes each warning: it goes into the result's list and
to the option at once, so both hold the same sentences in the same order. Like `Progress`, it is called synchronously
on the thread running the detection (Detect's caller, DetectAsync's and DetectStreamAsync's background thread): the
HLS notice and the not-chunked reason before any shot, the chunk fallback before the sequential read. With
`System.Progress<T>`, a report is posted to the context that created it and can arrive after a shot; a synchronous
`IProgress<string>` sees it first.
