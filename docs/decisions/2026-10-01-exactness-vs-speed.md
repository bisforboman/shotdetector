# Exactness vs speed (2026-10-01)

"Yes, do that for now. Better to have a correct baseline." **Decision:** bit-exact ports of OpenCV's operations
are the default path; faster approximations (`FfmpegResize`) are opt-in and documented as differing.
