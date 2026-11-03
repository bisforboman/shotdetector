# FFmpeg report: yadif's x86 SIMD at 9-16 bits gives a different picture on every run (slice threads)

A draft for the owner to file on FFmpeg's tracker (trac.ffmpeg.org; a public post from the owner's account, never a
session's). The text from "Summary" on is the report. Reproduced on 2026-11-02 in a Linux cloud container.

---

## Summary

On x86 with more than one filter thread, `yadif` on 9 to 16-bit input gives a different output on every run of the
same command, for some frame widths. The differing pixels are always the first one to three pixels of the first row
of a slice (luma rows 48 and 96 of 240, chroma rows 24 and 48 of 120 below). With one filter thread
(`yadif=threads=1` or `-filter_threads 1`), or with `-cpuflags 0`, every run gives the same output, and the two equal
each other. 8-bit input is unaffected.

## Version

```
ffmpeg version n8.1.3-9-g29e619e767-20260930
```

BtbN's static build `ffmpeg-n8.1.3-9-g29e619e767-linux64-gpl-8.1.tar.xz` (release `autobuild-2026-09-30-13-08`,
SHA-256 `97ce978979194b5cf7e06a5e68020dbdaa7a4f3294c5452b6a1bc347100dbb79`). Not checked on git master.

## Machine

- Intel(R) Xeon(R) Processor @ 2.10GHz, 4 logical CPUs, Linux 6.18 x86_64
- CPU flags include sse2 ssse3 sse4_1 avx avx2 avx512f avx512bw avx512vl

## How to reproduce

```
for i in 1 2 3 4 5 6; do
  ffmpeg -hide_banner -loglevel error -f lavfi -i testsrc2=s=320x240:r=25:d=1 \
    -vf format=yuv420p10le,yadif -f md5 -
done
```

Output (six runs, five different results):

```
MD5=f4cc8583e87d29d1a6981e4d4345376c
MD5=8e44240851f213e3d809552ae921806e
MD5=77bddf96ddc4493f3f70166c4acb46f4
MD5=d97488482c7ca5915660a5b6b67bdc04
MD5=673a42fb4722da95b045ce80fb70ca7b
MD5=77bddf96ddc4493f3f70166c4acb46f4
```

The same command with `yadif=threads=1`, with `-filter_threads 1`, or with `-cpuflags 0` before `-i` gives the same
hash on every run, and the same hash in all three cases:

```
MD5=fa5bae4fb3f72d23aad87ab490b6897e
```

8-bit (`format=yuv420p,yadif`) is the same on every run and equals `-cpuflags 0`: `MD5=efe83a432f332d756b682d573ecc1671`.

## What varies

- **Width.** At 240 rows, widths 64, 128, 256, 320, 448, 512, 640, 704 and 1280 vary; 160, 192, 288, 336, 384, 416,
  480, 720 and 1920 don't (6 runs each, `testsrc2=s=<w>x240`). 1920x1080 doesn't vary either.
- **CPU flags.** It still varies with `-cpuflags -avx512`, `-cpuflags -avx2-avx512`, `-cpuflags -avx-avx2-avx512` and
  with only `mmx+mmxext+sse+sse2`: every x86 16-bit version seems affected, not only the AVX-512 one.
- **Decoded input.** The same happens on frames from a decoder (interlaced MPEG-2 at 320x240 converted to
  `yuv420p10le` with FFV1, then `-vf yadif`), not only with lavfi sources.
- **Where.** Comparing the raw output (`-f rawvideo`) of three runs with the `-cpuflags 0` output: three or four
  pixels differ per run, all at columns 0 to 2 of luma rows 48 or 96, or of chroma rows 24 or 48, i.e. the first row
  of a slice.

## Likely cause (not confirmed)

The differing pixels sit at the start of a slice's first row, right after the previous slice's last row in memory.
At these widths the output planes' linesize equals the row's size (no padding). It looks as if the x86 16-bit
`filter_line` writes a few samples past the end of the row it filters, into the first samples of the next row. Within
one slice the next row is written afterwards (copied or filtered), which overwrites them, so single-threaded output
is right. Across a slice boundary the next row belongs to another thread, and which write lands last depends on
timing.

## Workaround

`yadif=threads=1` (the filter's own threads option) or `-filter_threads 1`; `-cpuflags 0` also works but turns SIMD
off everywhere in the process.
