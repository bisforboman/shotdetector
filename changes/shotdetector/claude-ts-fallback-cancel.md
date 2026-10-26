### Fixed

- Cancelling a detection of a file whose packets lack timestamps (MPEG-PS) now stops it at once: in-process, it used to decode the whole file first to read each frame's time (issue #523).
