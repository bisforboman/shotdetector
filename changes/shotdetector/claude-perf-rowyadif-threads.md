### New

- `IYuvChromaRows`: a YUV converter can say which chroma rows converting a row reads; decoding in-process, ShotDetector
  then takes only the rows it converts from the decoder. ShotDetector.FastYuv's converters implement it.

### Faster

- Deinterlacing with FastYuv's `Yadif` together with FastYuv's converter (`Yadif = new Yadif()` and
  `Yuv420Converter = new SwscaleYuv420()`, in-process) now deinterlaces and copies only the rows the conversion reads
  (about 3 in 10 at 1080), not whole frames. That pairing was slower than FFmpeg's own yadif and than `Yadif` with the
  default converter; on 150 s of 1080i25 MPEG-2 (4 vCPUs) it now takes 17.2 s instead of 24.1 s for 4:2:2 MXF and 11.8 s
  instead of 16.0 s for 4:2:0 MPEG-TS, a third less CPU time, and 1.35-1.54x as fast in parallel mode. Results are
  unchanged, byte for byte.
- FastYuv in-process copies only the rows it converts for progressive video too (10-bit h264: 1.04-1.09x).
- Parallel detection gives h264, HEVC and other frame-threaded decoders twice the CPUs' decoder threads (at most 16),
  shared among the chunks, for frames up to 1920 x 1080: each chunk's decoder now has up to the CPUs' count. On 4 vCPUs,
  150 s of 1080p h264 took 15.1 s instead of 16.8 s (8-bit) and 19.5 s instead of 23.8 s (10-bit), in the same CPU time
  and about 30 MB more memory. A sequential run, larger frames and `DecodeThreads` you set are unchanged.
