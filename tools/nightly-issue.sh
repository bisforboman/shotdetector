#!/bin/bash
# A failed nightly run, reported where it isn't missed: a comment on the open issue with this title, or a new issue.
#   tools/nightly-issue.sh "<title>" "<run url>"     (GH_TOKEN with issues: write, GITHUB_REPOSITORY; REST only)
set -euo pipefail
title=$1 url=$2 repo=${GITHUB_REPOSITORY:-bisforboman/shotdetector}
body="The nightly run failed on main at $(git rev-parse --short HEAD): $url"
number=$(gh api --paginate "repos/$repo/issues?state=open&per_page=100" \
  --jq ".[] | select(.pull_request == null and .title == \"$title\") | .number" | sed -n 1p)
if [ -n "$number" ]; then
  gh api -X POST "repos/$repo/issues/$number/comments" -f body="$body" --jq .html_url
else
  gh api -X POST "repos/$repo/issues" -f title="$title" -f body="$body" --jq .html_url
fi
