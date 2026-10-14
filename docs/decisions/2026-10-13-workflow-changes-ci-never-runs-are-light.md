# Workflow changes CI never runs are light (2026-10-13)

The user, seeing #256 (a new workflow) run every test: redundant? Changes under .github/ were all heavy. Asked: **a
small PR now** making release.yml, benchmark.yml, frame-reader-bench.yml, upstream.yml, dependabot-auto-merge.yml
and dependabot.yml light in the three changes jobs (CI doesn't run them, so a full run proves nothing about them);
ci.yml, realworld.yml, mutation.yml, native.yml and .github/actions/ stay heavy. Over waiting for the FrameReader
plan's CI step or leaving it.
