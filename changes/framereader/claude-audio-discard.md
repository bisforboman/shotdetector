### Faster

- `AudioReader` and `Audio.Convert` (and so `Video.Convert`'s audio) skip the streams they don't decode, as ffmpeg's
  `-vn` does, so containers that can jump over them (MP4 and MOV, MXF) read only the audio: a 394 MB MXF of 4:2:2 MPEG-2 video and PCM
  audio is read for 55 MB instead of all of it. The samples and files are the same as before. MPEG-TS and Matroska still
  read every byte.
