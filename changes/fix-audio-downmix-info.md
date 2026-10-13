### Fixed

- FrameReader: `Audio.Convert` and `AudioFilter` downmix AC-3, E-AC-3 and DTS with the stream's own centre,
  surround and LFE levels, as the ffmpeg command line does: 5.1 AC-3 to MP3 was louder than ffmpeg's and is now the
  same, byte for byte (issue #211).
