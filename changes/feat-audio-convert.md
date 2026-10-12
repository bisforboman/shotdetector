### New

- FrameReader: `Audio.Convert(input, output, options)`, the file `ffmpeg -i IN -vn [-af G] [-c:a E] [-b:a B] OUT`
  writes, byte for byte: the decoder's own samples through the filter graph to the encoder, converted to its nearest
  format, rate and channel layout as the command line does (a downmix of integer samples normalized, of float ones
  not), with the input's metadata and chapters (issue #192).

### Fixed

- `AudioWriter` converts to the encoder's channel layouts too, as ffmpeg does: 5.1 into MP3 is downmixed to stereo
  instead of failing.
