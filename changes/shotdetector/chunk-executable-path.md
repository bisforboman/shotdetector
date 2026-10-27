### Faster

- Long files decoded by the ffmpeg executable (`--decoder process`, or wherever FFmpeg's libraries can't load) are now detected in chunks too, one ffmpeg process per chunk, under the same rules as in-process decoding: by default from 4 CPUs on, `--sequential` for one pass, `--parallel` says why it couldn't. Results stay byte for byte the sequential run's; a chunk that can't reproduce them makes the run read sequentially, as in-process.
