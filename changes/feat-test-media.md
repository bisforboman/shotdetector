### New

- FrameReader: `TestMedia.Write(path, options)` writes synthetic test media in-process from FFmpeg's own sources
  (test patterns, colour bars, tones) and encoders: video sources concatenated in order (cuts at known frames),
  progressive or interlaced, an audio source, mpeg2video/mpeg4/aac/PCM. Tests of media code no longer need the
  ffmpeg executable to make their clips (issue #237).
- ShotDetector.Native: the source filters (testsrc2, smptebars, mandelbrot, color, sine, anullsrc), concat, setfield,
  setsar, setparams, trim and atrim; the mpeg2video, mpeg4, pcm_s24le, pcm_s32le and pcm_f32le encoders; the avi muxer.
