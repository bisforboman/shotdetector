### New

- FrameReader: `FFmpegLibraries.Missing(Capability...)` lists what loaded FFmpeg libraries lack for FrameReader's
  features (`"filter yadif"`, `"encoder libmp3lame"`, `"muxer dash"`), empty when they have everything: a startup
  check that needn't track what each feature uses (issue #195).
