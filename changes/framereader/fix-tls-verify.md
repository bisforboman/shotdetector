### Fixed

- https checks the server's certificate: FFmpeg 8 doesn't by default, so a self-signed or forged one was accepted
  (in-process with FrameReader.Native on Windows, or any FFmpeg build with TLS). `tls_verify=0` in `InputOptions`
  turns the check off.
