### New

- save-images' quality and compression, as scenedetect's `--quality` and `--compression`: `ImageOptions.Quality` (JPEG,
  WebP; 0-100) and `ImageOptions.Compression` (PNG; 0-9), and the CLI's `--image-quality` and `--image-compression`
  (and `quality`, `compression` under `[save-images]` in a config file). PNG's and WebP's mean what OpenCV's do;
  JPEG's is mapped onto ffmpeg's `-q:v`. The defaults are unchanged.
