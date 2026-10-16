# The backlog as a list (2026-10-16)

Two PRs that edited neighbouring backlog rows (#311 and #313, after #312) conflicted on GitHub, whose merge doesn't
use the local row-wise merge driver. Asked: **one file, a list with a blank line between items** (git merges changes
separated by an unchanged line, on GitHub too), over one file per item (no conflicts at all, but no single ordered
page, and reordering renames files) or leaving the table. The merge driver (tools/backlog-merge.py) and its
.gitattributes line went with the table.
