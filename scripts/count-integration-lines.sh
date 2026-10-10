#!/usr/bin/env bash
# Counts the non-blank, non-comment lines of the sample app's Megáfono integration code (SC-007) and fails above 99.
# Comment lines are those starting with // (so /// too) and the lines inside (* ... *) blocks.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
file="${1:-$root/samples/SampleApp/Megafono.fs}"
limit=99

# A directory means its Megafono.fs; a missing file must fail, not count as 0
if [ -d "$file" ]; then file="$file/Megafono.fs"; fi
if [ ! -f "$file" ]; then
  echo "no such file: $file" >&2
  exit 2
fi

count=$(awk '
  inblock { if ($0 ~ /\*\)/) inblock = 0; next }
  /^[[:space:]]*$/ { next }
  /^[[:space:]]*\/\// { next }
  /^[[:space:]]*\(\*/ { if ($0 !~ /\*\)/) inblock = 1; next }
  { n++ }
  END { print n + 0 }
' "$file")

echo "$count"

if [ "$count" -gt "$limit" ]; then
  echo "integration code is $count lines, over the limit of $limit: $file" >&2
  exit 1
fi
