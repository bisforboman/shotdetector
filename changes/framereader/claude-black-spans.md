### New

- `PictureMeter` counts black pixels at a pixel threshold of your choice (`new PictureMeter(format, first, end,
  blackPixelThreshold)`, blackdetect's `pix_th`), and `PictureMeasures` judges frames at any picture threshold
  (`IsBlack(i, pictureThreshold)`, `BlackSpans(minSeconds, pictureThreshold)`, blackdetect's `pic_th`): blackdetect's
  spans for any of its thresholds, not only its defaults. `PictureMeasures.BlackPixelThreshold` and `BlackPixels(i)`
  say what was counted; measures at another threshold carry it in their bytes (the default's bytes are unchanged) and
  don't join with others'.
- `BlackPixels`: blackdetect on one frame's luma alone, its threshold (`Threshold`) and count (`Count`) on planes of
  yours, or `Of(decoder)` and `Of(frame)` on FrameReader's in any format blackdetect takes as it is (gray and NV12
  too), for a black frame check without the meter's other figures.
