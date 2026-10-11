### New

- CLI `--audio-filter "<graph>"` and `--audio-out <file>`: an ffmpeg `-af` graph over the audio, with analysis
  filters' results (silencedetect, ebur128, astats) printed with their times, and the filtered audio written
  (encoded by the extension). E.g. line-up tone notched out and the result as MP3, the same audio ffmpeg gives:
  `shotdetect -i in.mxf --audio-filter "equalizer=f=1000:t=q:w=8:g=-40:enable='between(t,0,60)'" --audio-out out.mp3`.
