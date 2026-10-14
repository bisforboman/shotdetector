### Fixed

- `FrameReader.Native.linux-musl-x64`: the libraries didn't load on Alpine unless their folder was on `LD_LIBRARY_PATH`
  (musl's loader doesn't look next to a library for its dependencies); every Linux build now carries a
  `$ORIGIN` RUNPATH (issue #278).
