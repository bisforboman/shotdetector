### Fixed

- An https URL's server certificate is checked, in-process and with the ffmpeg executable (`-tls_verify 1`): FFmpeg
  8 doesn't by default, so a self-signed or forged one was accepted. The segments an HLS playlist names are still
  opened without the check (FFmpeg doesn't pass the option on).
