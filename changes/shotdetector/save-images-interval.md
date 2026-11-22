### New

- save-images by interval, scenedetect 0.7.3's `save-images -i/--interval` (its #280): `--image-interval <duration>`
  gives each shot floor(length / duration) + 1 images instead of `--num-images`, at least `--min-images` and at most
  `--max-images` (0: no limit), with scenedetect's file names and frames; `--save-html` and `--contact-sheet` take each
  shot's own. In the library: `ImageOptions.Interval`/`IntervalDuration`, `MinImages`, `MaxImages`; in a config file,
  `[save-images]`'s `interval`, `min-images` and `max-images`.

### Fixed

- `Export.SaveImages` (`--save-images`, `--save-html`'s thumbnails, `--contact-sheet`) through the ffmpeg executable
  failed when more than about 100 frames were wanted (34 shots at the default 3 images): ffmpeg couldn't parse the
  frame selection. It is now a balanced expression of any length.
- The config file's `[save-images]` `quality` and `compression` were applied but reported as unsupported.
