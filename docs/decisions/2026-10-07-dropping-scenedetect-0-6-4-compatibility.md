# Dropping scenedetect 0.6.4 compatibility (2026-10-07)

The user: "we can drop the 0.6.4 compatibility now". Removed `PySceneDetectVersion.V0_6_4` and every 0.6.4 code
path (frame-number positions, whole-number downscale, fade split, histogram re-initialisation, save-images frame
picks), its tests, mutation guard and CI parity jobs. Kept the `Compatibility` option and the enum with
`V0_7_1`: the versioning policy keeps the previous release available when the default follows a newer scenedetect.
Breaking, so in 0.9.0, and the two quiet weeks before 1.0 restart.

Since 2026-10-08 ([2026-10-08-1-0-without-a-quiet-period.md](2026-10-08-1-0-without-a-quiet-period.md)) there is no
quiet period before 1.0 to restart; how later modes are retired:
[2026-10-31-retiring-scenedetect-modes.md](2026-10-31-retiring-scenedetect-modes.md).
