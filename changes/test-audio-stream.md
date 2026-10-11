### Fixed

- FrameReader, audio from a `Stream` (read as ffmpeg reads `-i -`): MP3 kept the encoder's end padding (a few hundred
  samples more than ffmpeg gives; the input reported no size, which the mp3 demuxer took for a concatenated file),
  and `AudioReader.Seek` on a Stream threw instead of decoding on and trimming as ffmpeg's `-ss` does on a pipe.
