# Working setup for unattended work (2026-10-06)

### Question

The user wants the project to keep moving overnight. What of the StyleBro repository's setup should be copied?

### Choices

1. `CLAUDE.md` with status and gotchas, `docs/backlog.md`, `docs/decisions.md`.
2. PR pipeline with a protected `main`.
3. Real-world workflow: public videos vs scenedetect, nightly.
4. Mutation testing of the scenedetect-quirk guards.

### Answer

**All four.** And the backlog's order: robustness on real videos, then performance, then the road to 1.0, with
0.4.0 released first.
