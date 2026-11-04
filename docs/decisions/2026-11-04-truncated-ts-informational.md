# truncated.ts informational (2026-11-04)

#734 (219a234) made sequential runs decode h264 and HEVC up to 1080p with twice the CPUs (at most 16), where OpenCV
uses the CPU count (docs/decisions/2026-10-31-sequential-decoder-threads-remeasure.md). Its own CI runs didn't include
the "more" parity jobs. The next PR to run them (#735) failed in all four (core and fast-yuv, executable and
in-process), on the re-run too, in "Compare damaged files with scenedetect": `samples/broken/truncated.ts`
(the sample cut at 70% of its bytes) had cuts and scene lists equal to scenedetect's, but 2 stats cells per detector
differed, from frame 565 (its last frames, which libavcodec conceals by its thread count). ci.yml's comment on the step
already said to make truncated.ts informational if it ever varied (docs/decisions/2026-10-22-damaged-files-best-effort.md).

## Question

Since #734, CI's damaged-file parity fails on truncated.ts on every path (deterministic on a re-run), blocking every PR
that runs those jobs. How should main be fixed?

## Choices

1. **Make it informational**: truncated.ts moves to the informational damaged files beside corrupt.ts, as ci.yml's
   comment says. #734's speed-up stays.
2. **OpenCV's count again**: sequential runs go back to the CPU count, so damaged h264 conceals as scenedetect's again;
   #734's sequential speed-up goes.
3. **Twice the CPUs, files only**: OpenCV's count when the file shows damage, else twice the CPUs: more code, and a
   damage signal before decoding.

## Answer

**Make it informational** (the owner, 2026-11-04).

## How

ci.yml's "Compare damaged files with scenedetect" compares truncated.ts with `--informational`, with corrupt.ts and
corrupt_copy.ts; base.mp4, truncated.mp4, dup_pts.mkv and res_change.ts stay exact (identical at any thread count,
measured in docs/backlog-done.md's 2026-10-21 rows). Damaged files remain best effort (2026-10-22).
