### Fixed

- A rotated video whose frames got larger mid-stream (a larger encode joined on) read past a buffer in-process and
  could crash the process; every frame is now turned at the first frame's size, as through the ffmpeg executable.
- ffprobe's timestamp pass (MPEG-PS without packet timestamps, through the executable) read without the input's
  protocol whitelist, timeout, certificate check and frame size cap, so a crafted playlist could reach other
  protocols or addresses there; it reads as the other passes do now.
- ffmpeg and ffprobe are started by full path, next to the app or from PATH: by bare name, Windows and .NET on Unix
  also looked in the current folder, so an `ffmpeg` someone put there could run.
- A video claiming frames larger than 8192 x 8192 fails at the probe, before buffers are sized from it (the FastYuv
  path allocated them first).
- A spilled stream (an mp4 without faststart read from a `Stream`) goes in a temp folder of its own, readable only by
  the user on Linux and macOS; it was readable by every local user.
- ffprobe takes the input with `-i`, so a file named like one of its options (`-report`) is read as a file.
