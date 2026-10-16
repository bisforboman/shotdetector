### Fixed

- Row-wise deinterlacing (a `RowDeinterlacer`) read past a frame when an interlaced stream got smaller mid-way, which
  could crash the process; that's a `FrameReaderException` now.
- `FFmpegLibraries.Load()` on Windows: the system's places are PATH's folders, by full path; the libraries were
  loaded by bare name, which also searched the current folder first, so DLLs someone put there could load.
