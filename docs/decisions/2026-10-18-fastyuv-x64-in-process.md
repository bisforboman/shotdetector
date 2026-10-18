# FastYuv in-process on x64 (2026-10-18)

### Question

The release-candidate performance review measured FastYuv in-process on x64 at ~9-12% more CPU than the default path on
4:2:2 (~4% on 4:2:0), and the default path is exact there too (it converts only the rows the resize reads with FFmpeg's
own swscale). Should ShotDetector ignore an explicit FastYuv converter in-process on x64 and use the cheaper default
path?

### Choices

1. No, keep it as set: an explicit converter is used as asked; the README says FastYuv isn't needed for exactness
   in-process on x64 (recommended). 2. Yes, prefer the default path in-process on x64 (same results, less CPU; FastYuv
   still runs on ARM and through the executable).

### Answer

**No, keep it as set.** The backlog item is Won't do.
