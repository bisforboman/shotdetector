# Spectral statistics (2026-10-09)

Issue #81's spectral statistics, through FFmpeg's aspectralstats filter. Asked the API shape: **a `SpectralStats`
class over an `AudioReader` with TryRead (its rate, channels and seeking; recommended)**, not a static
`SpectralStats.Read(path)`; and where: **FrameReader only (recommended)**, no ShotDetector API or CLI command yet.
