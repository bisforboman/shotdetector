### Faster

- Parallel detection is now the default from 4 CPUs on (was 8), still for frames up to 3840x2160: on GitHub's 4-CPU
  x64 and ARM runners 2 chunks were 1.07x to 1.8x faster on every film and clip measured, 1080p included, for 10 to
  54 MB more memory and, on 2-core SMT machines, up to 8% more CPU time (17% at low resolution with stats). On 4 to 7
  CPUs that is 2 or 3 chunks. `Mode = DetectionMode.Sequential` (CLI `--sequential`) opts out.
