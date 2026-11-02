### Fixed

- Deinterlacing 9 to 16-bit video (`FrameDecoderOptions.Deinterlace`, and the readers and `Video.Transcode` that use
  it) gives the same frames on every run. FFmpeg 8.1's x86 yadif varies from run to run there when it runs on several
  threads (its slices race); FrameReader now runs it on one thread above 8 bits, where it equals FFmpeg's C code
  (`ffmpeg -cpuflags 0 -vf yadif`). 8-bit deinterlacing is unchanged.
