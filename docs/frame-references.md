# Decoded frames that outlive the next one (FrameReader)

Status: design (2026-10-17), reviewed; next the build. Backlog: "Conversion (BgrConverter) off the decoding thread".

## Why

After 3.4.0, in-process detection of HD video is bound by the decoding thread: it decodes, then converts each frame to
BGR (`BgrConverter`, 148 us per frame at 1280x534, measured), and only then decodes the next. The scoring thread waits.
Converting on other threads needs the frame to stay valid after the decoder moves on, which FrameReader can't express
today: `FrameDecoder` has one current frame, overwritten by `Next()`, and `BgrConverter` converts "the decoder's
current frame". swscale's own slice threads were measured and made it slower (1.43x-1.84x on Tears of Steel).

## Proposal

A reference to a decoded frame, and a converter that takes one:

```csharp
public sealed class DecodedFrame : IDisposable           // FrameReader
{
    public int Width { get; }
    public int Height { get; }
    public string PixelFormat { get; }
    public long Pts { get; }                             // as FrameDecoder.Pts gave it
    public ReadOnlySpan<byte> GetPlane(int plane, out int stride);
    public void Dispose();                               // av_frame_free: gives the buffers back to the decoder's pool
}

public sealed class FrameDecoder
{
    public DecodedFrame TakeFrame();                     // av_frame_clone of the current frame: a reference, no copy
}

public sealed class BgrConverter
{
    public BgrConverter();                               // not tied to a decoder
    public ReadOnlySpan<byte> Convert(DecodedFrame frame, out int stride);
    public ReadOnlySpan<byte> Convert(DecodedFrame frame, ReadOnlySpan<int> rows, out int stride);
}
```

- `TakeFrame` references the frame's buffers (FFmpeg's refcounting): nothing is copied, and the decoder keeps
  decoding into other buffers from its pool. Each frame held costs one decoded frame of memory until disposed.
- The new `Convert` overloads make the same swscale calls on the same frame as today's, so they give the same bytes;
  a converter is used by one thread at a time (an `SwsContext` isn't thread-safe), so each worker has its own.
- Additive, so FrameReader 2.6.0 (minor; package validation passes). The decoder-bound `BgrConverter(decoder)` stays.

## Not covered

- Row-wise deinterlacing (`RowDeinterlacer`, FastYuv's yadif): its rows are computed on demand from the previous,
  current and next decoded frames inside the decoder. `TakeFrame` computes all rows first (as `GetPlane` does), so it
  works, but the deinterlacing itself stays on the decoding thread.
- Hardware frames are taken after the transfer to system memory, as `Next()` gives them today.

## ShotDetector's use

The in-process pipeline: the decoding thread takes each frame and hands it to K converter workers (each with its own
`BgrConverter` and copy of the resize's source rows); a sequence number restores the order before the scoring thread
resizes and scores. K = 1 on small frames (where conversion is cheap and threads cost more than they save), up to 2-4
on HD; measured before choosing.

## Proof

- Unit tests: `Convert(TakeFrame())` equals `Convert` on the decoder's current frame, byte for byte, for 8- and 10-bit,
  4:2:0/4:2:2/4:4:4, size changes with `KeepFirstSize`, crops and rotations; a frame stays valid after later
  `Next()` calls and after the decoder is disposed (until the frame is).
- The parity and real-world jobs compare detection output as today.
- A/B on this desktop and the weekly benchmark (the 2-vCPU runners, where extra threads may cost more than they
  save): the pipeline keeps K = 1 wherever it doesn't gain.

## Decided (2026-10-17)

The user's choices (docs/decisions/2026-10-17-frame-references.md): the type is `DecodedFrame`; only
`FrameDecoder.TakeFrame` and the `BgrConverter` overloads for now (`VideoFrameReader` later, if asked for); ShotDetector's
conversion workers on by default, one on small frames and more on HD only where measured faster.
