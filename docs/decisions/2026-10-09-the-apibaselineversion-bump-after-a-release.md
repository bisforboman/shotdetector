# The ApiBaselineVersion bump after a release (2026-10-09)

A PR opened with the workflow's own token gets no CI runs, so it can't merge by itself. Asked how a stable release
should lead to the bump: **release.yml opens an issue once nuget.org serves the version (recommended)**; not chosen: a
PR plus CI started by workflow_dispatch, a PR with an app token or PAT, or deriving the baseline from tags at pack time.
