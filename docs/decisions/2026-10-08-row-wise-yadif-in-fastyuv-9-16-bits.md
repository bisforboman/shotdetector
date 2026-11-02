# Row-wise yadif: in FastYuv, 9-16 bits follow FFmpeg's C code (2026-10-08)

Faster deinterlacing was the user's pick after 0.9.0 ("Faster deinterlacing (Recommended)"); when it first lost to
FFmpeg's threaded yadif on many cores, the user chose "Keep digging" over shelving it or landing it opt-in for small
machines (the cause turned out to be side data piling up on the output frame). The port is of FFmpeg's LGPL code, so
it lives in ShotDetector.FastYuv behind `IYadif`, like the yuv420p converter. FFmpeg 8.1's x86 SIMD yadif is
nondeterministic for 9 to 16 bits (a different output on every run of the same command); 8-bit SIMD equals the C
code. The port follows the C code, so 10-bit results are deterministic and equal `ffmpeg -cpuflags 0 -vf yadif`.

Since 2.0 (CHANGELOG.md) `IYadif` is FrameReader's `IRowDeinterlacer`.

Since 2026-11-02 ([2026-11-02-deterministic-yadif-at-9-16-bits.md](2026-11-02-deterministic-yadif-at-9-16-bits.md))
the default path is deterministic too: the run-to-run difference turned out to be a race between yadif's slice
threads, not its SIMD, so above 8 bits FFmpeg's yadif runs on one thread, where it equals the C code.
