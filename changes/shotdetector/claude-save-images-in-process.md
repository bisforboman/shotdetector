### New

- `--save-images` (`Export.SaveImages`, and `--save-html`'s thumbnails) encodes in-process when the video was decoded
  there, through FrameReader's `ImageEncoder`: no ffmpeg executable needed, and byte for byte the files the ffmpeg
  executable of the same FFmpeg build writes (the executable writes them otherwise, as before).

### Fixed

- `--save-images --image-format webp` with more than one image failed with a FileNotFoundException: ffmpeg picked
  `libwebp_anim` for .webp (its default where built), which wrote one animation of every frame to the first file. The
  executable path now names `libwebp`, so each image is its own still WebP.
