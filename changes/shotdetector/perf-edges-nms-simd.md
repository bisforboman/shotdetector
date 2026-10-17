### Faster

- Edge detection's non-maximum suppression works on 8 pixels at a time and the hysteresis needs no border checks:
  together with the other edge change, `--stats` runs (and edge weights) take 0.68-0.74x the time of 3.4.0 on an
  8-core desktop, the stats the same byte for byte.
