### Faster

- In-process detection is about 20-33% faster on x64 (Elephants Dream 426x240 0.67x, Big Buck Bunny 640x360 0.76x,
  Tears of Steel 1280x534 0.80x wall time on an 8-core desktop): the resize's horizontal pass uses SSE2, a pixel's
  three channels at once with the same integer arithmetic, and runs on the thread that scores frames, which used to
  wait for the decoding thread most of the time. The results are the same, byte for byte.
