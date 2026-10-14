# Decisions, one file each (2026-10-14)

Two pull requests that each recorded a decision both added it at the top of docs/decisions.md and conflicted (#263 and
#264). Asked: **one file per decision**, `docs/decisions/YYYY-MM-DD-what.md` (over the same files with a generated
index, which would conflict again unless only CI wrote it, or one file appended at the end, which still conflicts
when two pull requests append). The 84 entries were split by a script; citations point at the folder, with the date
where they had one.
