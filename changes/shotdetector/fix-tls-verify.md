### Fixed

- https URLs check the server's certificate, in-process and with the ffmpeg executable (`-tls_verify 1`): FFmpeg 8
  doesn't by default, so a self-signed or forged one was accepted.
