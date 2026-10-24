#!/usr/bin/env bash
# Read and prepare CHANGELOG.md for a release.
#
#   changelog.sh notes VERSION       print VERSION's section, without its heading
#   changelog.sh unreleased          print the "## Unreleased" section (may print nothing)
#   changelog.sh released BASE_FILE  fail when a released section of BASE_FILE differs here
#   changelog.sh prepare VERSION     turn "## Unreleased" into "## VERSION - <today, UTC>"
#
#   CHANGELOG   path of the file (default: CHANGELOG.md at the repository root)
#
# notes prints the lines under "## VERSION" or "## VERSION - date" up to the next "## ", with
# blank lines trimmed at both ends. The release workflow publishes that as the GitHub release's
# notes. It exits 1 when the section is missing or empty, so no release goes out with nothing to say.
#
# prepare puts "## VERSION - <date>" right under "## Unreleased": the entries that were
# Unreleased become the version's, and an empty "## Unreleased" stays on top for the next release.
# It refuses an empty Unreleased section and a version that already has a section.
#
# Ported from Umbral (itself from Caravana's tools/release.sh). The release workflow is .github/workflows/release.yml.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
changelog="${CHANGELOG:-$root/CHANGELOG.md}"

usage() {
  echo "usage: changelog.sh notes VERSION | unreleased | prepare VERSION | released BASE_FILE" >&2
  exit 2
}

# A version is digits and dots with an optional -suffix, like the image tag it becomes.
check_version() {
  [[ "$1" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.]+)?$ ]] ||
    { echo "changelog: version $1 isn't X.Y.Z or X.Y.Z-suffix" >&2; exit 2; }
}

# The lines under the heading "## NAME" (exactly that, then the end of the line or " - date"),
# up to the next "## ", blank lines at either end trimmed. A second argument reads another file.
section() {
  awk -v v="$1" '
    /^## / { on = ($2 == v && (NF == 2 || $3 == "-")); next }
    on' "${2:-$changelog}" | sed -e '/./,$!d' | sed -e ':a' -e '/^\n*$/{$d;N;ba' -e '}'
}

# A released section never changes. The trap this catches: a branch made before a release adds
# its entries under "## Unreleased"; the release renames that heading on main; merging the branch
# then puts the entries inside the released section, Unreleased is empty and the next release is
# refused. CI runs this against the commit before a push (or a PR's base) so the merge turns main
# red at once. BASE_FILE is that commit's CHANGELOG.md.
released() {
  local base="$1" version changed=""
  [ -f "$base" ] || { echo "changelog: $base doesn't exist" >&2; exit 2; }
  while read -r version; do
    if [ "$(section "$version" "$base")" != "$(section "$version")" ]; then
      changed="$changed $version"
    fi
  done < <(awk '/^## / && $2 != "Unreleased" { print $2 }' "$base")
  if [ -n "$changed" ]; then
    echo "changelog: released sections changed:$changed. Released notes don't change: move new" >&2
    echo "changelog: entries back under \"## Unreleased\" (a branch made before that release puts them" >&2
    echo "changelog: there on merge). To correct a released section on purpose, put" >&2
    echo "changelog: \"[edit released changelog]\" in the commit message or the pull request's title." >&2
    exit 1
  fi
  echo "changelog: every released section is unchanged"
}

notes() {
  local version="$1" text
  check_version "$version"
  text="$(section "$version")"
  if [ -z "$text" ]; then
    echo "changelog: $changelog has no section \"## $version - <date>\", or it is empty" >&2
    # The usual cause: the changes are still under "Unreleased". Say how to fix it.
    if grep -q '^## Unreleased' "$changelog"; then
      echo "changelog: run \".github/scripts/changelog.sh prepare $version\", commit the result, and tag that commit" >&2
    fi
    exit 1
  fi
  printf '%s\n' "$text"
}

unreleased() {
  local text
  text="$(section Unreleased)"
  if [ -n "$text" ]; then
    printf '%s\n' "$text"
  fi
}

prepare() {
  local version="$1" date tmp
  check_version "$version"
  if awk -v v="$version" '/^## / && $2 == v { found = 1 } END { exit !found }' "$changelog"; then
    echo "changelog: $changelog already has a section for $version" >&2
    exit 1
  fi
  if ! awk '/^## Unreleased/ { on = 1; next } /^## / { on = 0 } on && /[^[:space:]]/ { found = 1 } END { exit !found }' "$changelog"; then
    echo "changelog: the \"## Unreleased\" section of $changelog is missing or empty: nothing to release" >&2
    exit 1
  fi
  date="$(date -u +%Y-%m-%d)"
  tmp="$(mktemp)"
  awk -v v="$version" -v d="$date" '
    !done && /^## Unreleased[[:space:]]*$/ { print; print ""; print "## " v " - " d; done = 1; next }
    { print }' "$changelog" >"$tmp"
  cat "$tmp" >"$changelog"
  rm -f "$tmp"
  echo "changelog: \"## Unreleased\" is now \"## $version - $date\"; commit it, then tag v$version"
}

case "${1:-}" in
notes)
  [ $# -eq 2 ] || usage
  notes "$2"
  ;;
unreleased)
  [ $# -eq 1 ] || usage
  unreleased
  ;;
prepare)
  [ $# -eq 2 ] || usage
  prepare "$2"
  ;;
released)
  [ $# -eq 2 ] || usage
  released "$2"
  ;;
*) usage ;;
esac
