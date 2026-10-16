### Fixed

- The segments, keys and variant playlists an HLS playlist names are opened with the server's certificate checked
  too: FFmpeg passes only a fixed set of options on to them, without `tls_verify`, so a playlist naming https
  segments read them unchecked. `tls_verify` or `ca_file` in `InputOptions` leave them as FFmpeg opens them.
