# Issue #42: in-process deinterlacing, decoder visibility, probe scope, broadcast samples, 1.0 (2026-10-07)

### Question

After adopting 0.8.0 the same user asks for: deinterlacing in-process (15.2 s vs 3.9 s on an 80 s 1080i MXF, 2 CPUs);
seeing which decoder ran from DetectStreamAsync, a distinct error and a startup check for missing libraries; the
container name and audio presence in the probe (and passing the probe back); interlaced broadcast formats in CI; and
1.0. Which parts, and how do they line up with 1.0?

### Choices

- Which: in-process deinterlacing; decoder visibility + fail fast; probe container + audio; broadcast samples in CI.
- 1.0: the API additions in 0.9.0, then freeze; or freeze now and the additions in 1.1.
- Reply: when it ships; draft for approval.

### Answer

**All four; the API additions in 0.9.0 (with the 0.6.4 removal and ARM), then two quiet weeks to 1.0; reply when it
ships.** In-process deinterlacing and the CI samples change no API, so they can land during the freeze. Passing the
probe back stays out (detection's cost is the packet scan).
