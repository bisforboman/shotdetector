# The 1.0 plan and versioning (2026-10-07, issue #18)

### Question

When should 1.0 ship, how does the default follow a scenedetect release that changes results, and what counts
as breaking afterwards?

### Choices

- When: after a quiet 0.7 (two weeks, no API change, nightlies green); right after 0.6.1; when the native
  libraries package lands.
- New scenedetect: a new mode in a minor with the default switched in a major; or the default follows the latest
  in a minor.
- Breaking: API and default results; or API only.

### Answer

**After a quiet 0.7. The default follows the latest scenedetect in a minor. API and default results are
breaking**, except changes that come from following scenedetect. README "Versions" has the policy.

Since 2026-10-08 ([2026-10-08-1-0-without-a-quiet-period.md](2026-10-08-1-0-without-a-quiet-period.md)) 1.0 came without
the quiet period. Since 2026-10-31
([2026-10-31-retiring-scenedetect-modes.md](2026-10-31-retiring-scenedetect-modes.md)) the policy also says when an
older `PySceneDetectVersion` goes.
