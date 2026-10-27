### Fixed

- Decoding with the ffmpeg executable (`--decoder process`, or wherever FFmpeg's libraries can't load): a file whose chroma siting changes midway (10-bit and other formats whose conversion interpolates chroma, such as two encodes joined) is now converted with each frame's own siting, as OpenCV does, not the first frame's for the whole video: the file is read again from each change with the new siting. A stream can't be read again; it keeps the first frame's siting and the result now says so in its warnings.
