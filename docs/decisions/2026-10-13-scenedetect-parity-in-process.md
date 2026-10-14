# scenedetect parity in-process (2026-10-13)

CI compared shotdetect with scenedetect only through the ffmpeg executable, while in-process decoding is the default
wherever our FFmpeg build is present. Asked how to cover it: **a decoder axis** (the 0.7.2 jobs, core and FastYuv,
both parts, on the executable and in-process: 4 more jobs of 3-7 min) over switching the 0.7.2 jobs to in-process
(losing the executable's FastYuv and extra comparisons) or one in-process core job.
