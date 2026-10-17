### Fixed

- A `Stream` input (and the CLI's `-i -`) reads with FFmpeg's protocol whitelist set to `pipe`, as a file reads only as
  a file, so nothing in the piped data can open another protocol.
- Windows: the named pipe the ffmpeg executable writes frames to accepts only the current user's connections, so
  another local user can't connect to it first and break the run.
