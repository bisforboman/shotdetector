### Fixed

- MOV/MP4 files with a clean aperture (a `clap` box, as iPhones write) decoded through the ffmpeg executable were
  cropped to it (FFmpeg 7.1 and newer crop by default), so stats differed from scenedetect, which reads the whole frame
  as in-process decoding already did. The executable is now told `-apply_cropping codec` for detection and save-images.
