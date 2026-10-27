### Faster

- The ffmpeg-executable path (`--decoder process`, and the library without FFmpeg's shared libraries): on HD video whose frames are sampled for the resize, ffmpeg now decodes the two pixel maps once and repeats them, instead of re-reading and re-decoding them for every frame. Results are unchanged.
