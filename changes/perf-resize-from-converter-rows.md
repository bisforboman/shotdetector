### Faster

- In-process decoding resizes straight from the converted rows instead of copying the pixels it reads first:
  about 16% less CPU on SD video and 5% on 1080p (content detector, pinned to 2 CPUs). Results are unchanged.
