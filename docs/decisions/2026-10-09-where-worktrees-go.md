# Where worktrees go (2026-10-09)

Asked when sibling folders (`C:\dev\ShotDetector-*`) piled up and two sessions collided on one checkout:

- **`.claude/worktrees/<name>` inside the repository, ignored (recommended)**: where Claude Code puts its own,
  so they're all in one place; over `.worktrees/`, and over leaving them as siblings.
- Existing ones: **move only the idle one (recommended)**; a worktree another session is working in stays until done.
