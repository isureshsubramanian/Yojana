#!/bin/sh
# Pre-push guard: refuses a push if any commit being pushed adds a path that is
# internal-only (docs, mockups, research, screenshots, sample data, benchmark
# results, plans, ADRs, reports). Mirrors the internal section of .gitignore;
# change both together. Install with: make hooks
pattern='(^|/)(docs|reports|research_notes|research|mockups|screenshots|shots[^/]*|plans?|adrs?|benchmarks|samples|sample-data)/|(^|/)bench-[^/]*\.(md|json)$|\.(png|jpe?g|gif|webp|pdf)$|(^|/)\.env$|(^|/)\.claude/'
zero=0000000000000000000000000000000000000000
status=0
while read -r local_ref local_sha remote_ref remote_sha; do
  [ "$local_sha" = "$zero" ] && continue
  if [ "$remote_sha" = "$zero" ]; then range="$local_sha"; else range="$remote_sha..$local_sha"; fi
  bad=$(git log --format= --name-only "$range" | sort -u | grep -E "$pattern")
  if [ -n "$bad" ]; then
    echo "push refused: internal-only paths in $local_ref:" >&2
    echo "$bad" | sed 's/^/  /' >&2
    status=1
  fi
done
exit $status
