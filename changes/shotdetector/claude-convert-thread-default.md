### Faster

- Sequential in-process detection of HD video (at least 1280 x 720's pixels; a file read without chunks, a stream or a URL) converts the decoded frames on a second thread, apart from decoding: 1.1-1.2x as fast in the measured cases (1080i and 1080p, 4 CPUs), with the same results and the same CPU time, for a little more memory. Parallel runs and smaller video are unchanged.
