### New

- FrameReader.Native reads https on Linux and macOS too: OpenSSL 3.5 linked in on Linux (Apache-2.0,
  `COPYING.openssl`; FrameReader passes the system's certificate bundle as `ca_file` unless `SSL_CERT_FILE` is set),
  macOS's own TLS (Secure Transport). The server's certificate is checked on every platform.
