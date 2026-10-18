### New

- `BgrConverter.Size`: convert every frame to a given size (the stream's, from its headers), whichever frame comes
  first: after a seek into a part of another size, frames still come at the stream's size, as OpenCV's buffer and
  ffmpeg's `scale=` give them.
