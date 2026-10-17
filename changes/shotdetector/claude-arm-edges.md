### Faster

- Edge detection on ARM (Linux arm64, Apple Silicon): the non-maximum suppression works on 4 pixels at a time with
  128-bit vectors where it used to run one by one; the edge detector takes 0.72-0.88x the time on film frames on
  GitHub's ARM runners, the edges the same byte for byte.
