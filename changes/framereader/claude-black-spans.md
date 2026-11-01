### New

- `PictureMeter` counts black pixels at a pixel threshold of your choice (`new PictureMeter(format, first, end,
  blackPixelThreshold)`, blackdetect's `pix_th`), and `PictureMeasures` judges frames at any picture threshold
  (`IsBlack(i, pictureThreshold)`, `BlackSpans(minSeconds, pictureThreshold)`, blackdetect's `pic_th`): blackdetect's
  spans for any of its thresholds, not only its defaults. `PictureMeasures.BlackPixelThreshold` and `BlackPixels(i)`
  say what was counted; measures at another threshold carry it in their bytes (the default's bytes are unchanged) and
  don't join with others'.
