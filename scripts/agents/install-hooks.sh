#!/bin/sh
# Installs the parallel-agent git hooks into the repository's COMMON git dir,
# so they apply to the main checkout and every worktree at once.
# Re-run after editing scripts/agents/hooks/*.
set -e
here=$(cd "$(dirname "$0")" && pwd)
common=$(git rev-parse --git-common-dir)
case "$common" in /*|?:*) ;; *) common="$(pwd)/$common" ;; esac
mkdir -p "$common/hooks"
for h in pre-commit reference-transaction; do
  cp "$here/hooks/$h" "$common/hooks/$h"
  chmod +x "$common/hooks/$h"
  echo "installed $h -> $common/hooks/$h"
done
if [ -n "$(git config --get core.hooksPath)" ]; then
  echo "WARNING: core.hooksPath is set; the installed hooks will be ignored." >&2
fi
