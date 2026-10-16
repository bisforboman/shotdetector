### Faster

- Edge detection (`--stats`, and edge weights in the content and adaptive detectors) is about 3.8x faster: Sobel and
  the dilation work on 8 and 16 pixels at a time with the same integer arithmetic. `--stats` runs took 0.51-0.60x the
  wall time on an 8-core desktop; the edge maps and stats are the same, byte for byte.
