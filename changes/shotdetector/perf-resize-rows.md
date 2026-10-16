### Faster

- The resize to scenedetect's size resizes each source row once, as OpenCV does: about 15% less resize time per
  frame for videos up to twice the processed height (426x240, for one), and about 40% when upscaling.
