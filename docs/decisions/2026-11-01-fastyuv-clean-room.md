# FastYuv: explore a clean-room MIT converter (2026-11-01)

The owner asked whether ShotDetector could do what ShotDetector.FastYuv does, so that FastYuv becomes an option rather
than a must. The scoping note is docs/ideas/fastyuv-optional.md (#689).

FastYuv is needed in three situations:
- on ARM, for x86's results;
- without FFmpeg (`Detect(YuvVideo, ...)`, shotdetector-web);
- for deterministic deinterlacing at 9 to 16 bits.

It is LGPL because every part of it is a port of FFmpeg's code. An MIT version with the same output would need a
clean-room implementation.

## The licence

### Choices

1. Keep FastYuv LGPL, with no clean room (recommended).
2. Explore a clean-room MIT converter. The 8-bit converter (4:2:0 and 4:2:2) is the first slice. The owner arranges
   the legal side and an implementer who hasn't read the port.

### Answer

**2, explore a clean room.**

The usual process has two sides, and the owner's legal side should confirm it before work starts:
- A side that has read the original writes a **functional specification**. It describes inputs and outputs: value
  ranges, which inputs each output channel depends on, and the mapping. It says nothing about how swscale or the
  port computes them, and copies no code structure or table layout.
- A side that has read neither implements the converter from the specification alone.

The conformance oracle takes its expected bytes from FFmpeg's own output on x86 (the ffmpeg executable or FrameReader
on an x64 runner), never from FastYuv.

**Open: who implements.** A Claude session can be told not to open swscale or `src/ShotDetector.FastYuv`, but it may
have FFmpeg's source in its training data. Whether that disqualifies it, so that the implementer must be a person, is
for the owner's legal side to judge.

The scope comes in three slices, in order:
- the 8-bit converter (4:2:0 and 4:2:2);
- 10-bit 4:2:0, which `arch-diff` has also required since 2026-10-18, and which is larger (filters and two scaler
  stages);
- yadif, which only matters for deterministic results at 9 to 16 bits.

The 8-bit slice alone doesn't cover everything ARM needs.

## Making FastYuv less of a must, licence unchanged

### Choices

1. Pick FastYuv up automatically when it is installed (on ARM, on the executable path, for 9 to 16-bit
   deinterlacing; off on x64 in-process) (recommended).
2. A "Do I need FastYuv?" table in the README.
3. Bundle FastYuv in the arm64 CLI binaries, making them LGPL.
4. Nothing more.

### Answer

**4, nothing more.** FastYuv stays an explicit opt-in.

## Parked (same day)

Asked next who implements (a person, or a Claude session with mitigations) and where the work lives, the owner
answered **"Park it for now"**, and left the rest **as a suggestion**.

Nothing has been written: no specification, oracle or repository. When the exploration is picked up again, the
suggestions on record are these:
- The specification side works black-box too. It never opens swscale, `src/ShotDetector.FastYuv` or FastYuv's tests,
  and derives the mapping from FFmpeg x86's output: every Y/U/V value for each colour matrix and range
  `IYuv420Converter.ForColor` accepts. It checks that runners with and without AVX-512, and the executable and
  in-process paths, give the same bytes.
- The work lives in a new MIT repository the owner creates, with no FastYuv code or references, because the
  implementer can't be given this one.
- A person implements, which gives the stronger record. A Claude session's training data may include FFmpeg's source.
  If a session implements anyway, it should be a fresh session with no checkout containing the port, a prompt
  forbidding FFmpeg source, only the specification and oracle as input, and its transcript kept.
