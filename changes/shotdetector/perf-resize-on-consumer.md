### Faster

- In-process detection on x64 is faster where decoding isn't the limit: on an 8-core desktop, wall time 0.74x
  (Elephants Dream 426x240), 0.64x (Big Buck Bunny 640x360), 0.86x (Tears of Steel 1280x534), 0.97x (Elephants Dream
  1080p VP9, decode-bound). The resize's horizontal pass uses SSE2, a pixel's three channels at once with the same
  integer arithmetic, and the resize runs on the thread that scores frames, which used to wait for the decoding thread
  most of the time. The results are the same, byte for byte.
