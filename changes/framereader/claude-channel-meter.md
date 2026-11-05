### New

- `ChannelMeter`: a track's channels one by one, on samples you decode, without FFmpeg: each channel's silences alone
  (silencedetect's on that channel), its RMS level (astats'), its samples at full scale and the runs of them
  (`ChannelMeterOptions.ClipRun`), and the first two channels' mean phase correlation (aphasemeter's), as
  `ChannelMeasures`: a dead or silent channel, one out of phase, clipping, dual mono.
