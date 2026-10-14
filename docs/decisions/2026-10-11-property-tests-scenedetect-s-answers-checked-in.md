# Property tests: scenedetect's answers checked in (2026-10-11)

FrameTime and Stats.PyFloat are checked against scenedetect 0.7.1 and Python on generated cases (FsCheck could not
replace mutation testing: it measures inputs, mutation testing the guards). Asked how the tests get the answers:
**a checked-in file** (`tools/oracle.py` generates the cases with a fixed seed and writes them with scenedetect's
answers to `tests/ShotDetector.Tests/Data/oracle.txt`; rerun when the scenedetect pin moves), over a live Python
process at test time (shrinking, but needs scenedetect in CI and skips elsewhere) and both (live as an opt-in run).
The file runs everywhere, ARM and Alpine included, with no Python.
