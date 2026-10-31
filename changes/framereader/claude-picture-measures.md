### New

- `PictureMeter`, `PictureMeasures`, `PictureFormat`, `PictureType`: the picture's measures of decoded frames, with
  nothing of FFmpeg, as FFmpeg 8.1's filters compute them (issue #646). It takes 8 to 16 bits in 4:2:0, 4:2:2 or
  4:4:4, from FrameReader's decoder (`Add(DecodedFrame)`, `PictureFormat.Of`) or as planes from any other decoder.
  Per frame:
  - signalstats' YMIN, YAVG, YMAX and BRNG;
  - blackdetect's spans (`BlackSpans`);
  - cropdetect's crop, per frame and over the run (`FrameCrop`, `Crop`, as `CropArea`);
  - colour bars by their definition (`BarsSpans`);
  - the decoder's picture type (`Types`).

  Rotated video is read upright. Ranges measured by meters of their own join back into the whole run's measures, byte
  for byte (`ToBytes`, `FromBytes`, `Join`).
- `DecodedFrame.PictureType`: the decoder's picture type for the frame (I, P, B ...).
