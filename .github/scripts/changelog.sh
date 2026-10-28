#!/usr/bin/env bash
# Read and prepare the release notes: CHANGELOG.md (released sections) and changelog.d/ (the next release).
#
#   changelog.sh notes VERSION       print VERSION's section, without its heading
#   changelog.sh unreleased          print what the next release would say, gathered from changelog.d/
#                                    (may print nothing)
#   changelog.sh prepare VERSION     gather changelog.d/*.md into "## VERSION - <today, UTC>" in CHANGELOG.md
#                                    and delete the files
#   changelog.sh released            fail if a released section differs from the copy in its own tag
#                                    (vX.Y.Z:CHANGELOG.md): released notes are never edited
#   changelog.sh check               fail on a bad fragment in changelog.d/ or a "## Unreleased" section
#
#   CHANGELOG       path of the file (default: CHANGELOG.md at the repository root)
#   CHANGELOG_DIR   the fragments directory (default: changelog.d at the repository root)
#
# Notes are not written in CHANGELOG.md. Each change adds one file in changelog.d/ (its README.md says
# how; the README is never collected), so two branches never edit the same lines and a merge made
# after a release can't put a note inside the released section. A file holds "### Added", "### Changed",
# "### Fixed", "### Security" and "### Upgrade" subsections of "- " bullets (continuation lines indented).
#
# notes prints the lines under "## VERSION" or "## VERSION - date" up to the next "## ", with
# blank lines trimmed at both ends. The release workflow publishes that as the GitHub release's
# notes. It exits 1 when the section is missing or empty, so no release goes out with nothing to say.
#
# prepare writes one "## VERSION - <date>" section above the newest released one, right after the
# intro: one subsection per kind (Added, Changed, Fixed, Security, Upgrade, in that order), each
# kind's bullets in the files' name order and then their order inside the file. It deletes the files it
# gathered. It refuses when changelog.d/ has no notes, when check fails, and when VERSION already has a
# section.
#
# check names the file and line of a fragment with a bad name, an empty file, a heading that isn't one
# of the five kinds, an empty subsection, or text outside a bullet. It also fails when CHANGELOG.md has
# a "## Unreleased" section (a branch started before this rule reintroducing it).
#
# released still protects the released sections. It catches the other trap, an edit inside a section a
# release made: every release tag holds its own section (the release workflow makes sure of it), so each
# "## X.Y.Z - date" here must read exactly as in vX.Y.Z:CHANGELOG.md. Tags made before that rule (whose
# CHANGELOG.md doesn't have their section) are skipped. Needs the tags fetched.
#
# Ported from Frunchy (originally Caravana's tools/release.sh). The release workflow is .github/workflows/release.yml;
# .github/scripts/changelog-test.sh tests this script.
set -euo pipefail

# Files come in name order, the same on every system
export LC_COLLATE=C

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
changelog="${CHANGELOG:-$root/CHANGELOG.md}"
fragdir="${CHANGELOG_DIR:-$root/changelog.d}"

kinds="Added Changed Fixed Security Upgrade"

usage() {
  echo "usage: changelog.sh notes VERSION | unreleased | prepare VERSION | released | check" >&2
  exit 2
}

# A version is digits and dots with an optional -suffix, like the image tag it becomes.
check_version() {
  [[ "$1" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.]+)?$ ]] ||
    { echo "changelog: version $1 isn't X.Y.Z or X.Y.Z-suffix" >&2; exit 2; }
}

# The lines under the heading "## NAME" (exactly that, then the end of the line or " - date"),
# up to the next "## ", blank lines at either end trimmed.
section() {
  awk -v v="$1" '
    /^## / { on = ($2 == v && (NF == 2 || $3 == "-")); next }
    on' "$changelog" | sed -e '/./,$!d' | sed -e ':a' -e '/^\n*$/{$d;N;ba' -e '}'
}

# The fragment files, one path per line, in name order; README.md is never one of them.
fragment_files() {
  local f
  for f in "$fragdir"/*.md; do
    [ -f "$f" ] || continue
    [ "${f##*/}" = "README.md" ] || printf '%s\n' "$f"
  done
}

# What the fragments say, one "### Kind" subsection per kind that has bullets. Blank lines are dropped,
# so the bullets of every file run together.
gather() {
  local kind chunk first=1
  for kind in $kinds; do
    chunk="$(fragment_files | while IFS= read -r f; do
      awk -v k="$kind" '
        /^###[[:space:]]/ { on = ($2 == k && NF == 2); next }
        /^#/ { on = 0; next }
        on && /[^[:space:]]/ { print }' "$f"
    done)"
    [ -n "$chunk" ] || continue
    if [ "$first" -eq 0 ]; then echo; fi
    first=0
    printf '### %s\n\n%s\n' "$kind" "$chunk"
  done
}

notes() {
  local version="$1" text
  check_version "$version"
  text="$(section "$version")"
  if [ -z "$text" ]; then
    echo "changelog: $changelog has no section \"## $version - <date>\", or it is empty" >&2
    # The usual cause: the notes are still in changelog.d/. Say how to fix it.
    if [ -n "$(fragment_files)" ]; then
      echo "changelog: run \".github/scripts/changelog.sh prepare $version\", commit the result, and tag that commit" >&2
    fi
    exit 1
  fi
  printf '%s\n' "$text"
}

unreleased() {
  gather
}

# Problems with the fragments and with CHANGELOG.md, one per line as "changelog: file:line: what" (or
# "changelog: file: what"), on stdout. Prints nothing when all is well.
problems() {
  local f name dir
  dir="${fragdir##*/}"
  if [ -d "$fragdir" ]; then
    for f in "$fragdir"/* "$fragdir"/.[!.]*; do
      [ -e "$f" ] || continue
      name="${f##*/}"
      [ "$name" != "README.md" ] || continue
      if ! [[ "$name" =~ ^[a-z0-9-]+\.md$ ]] || [ ! -f "$f" ]; then
        echo "changelog: $dir/$name: bad name; use lowercase letters, digits and dashes, then .md (a file, not a folder)"
        continue
      fi
      awk -v p="changelog: $dir/$name" '
        function bad(msg) { printf "%s:%d: %s\n", p, NR, msg }
        function endsub() { if (cur != "" && !bullets) printf "%s:%d: subsection \"%s\" has no bullets\n", p, subline, cur }
        /[^[:space:]]/ { text = 1 }
        /^#/ {
          endsub()
          if ($0 ~ /^### (Added|Changed|Fixed|Security|Upgrade)[[:space:]]*$/) { cur = $2; subline = NR; bullets = 0 }
          else { bad("heading \"" $0 "\" is not one of ### Added, ### Changed, ### Fixed, ### Security, ### Upgrade"); cur = "x"; bullets = 1 }
          next
        }
        !/[^[:space:]]/ { next }
        cur == "" { bad("text outside a subsection (start with ### Added, ### Changed, ### Fixed, ### Security or ### Upgrade)"); next }
        /^- / { bullets = 1; next }
        /^[[:space:]]/ && bullets { next }
        { bad("text that is not a \"- \" bullet or an indented continuation line"); bullets = 1 }
        END {
          endsub()
          if (!text) printf "%s: empty file\n", p
        }' "$f"
    done
  fi
  if [ -f "$changelog" ]; then
    awk '/^##[[:space:]]+Unreleased/ { found = 1 } END { exit !found }' "$changelog" &&
      echo "changelog: CHANGELOG.md has a \"## Unreleased\" section; notes now live in changelog.d/. Move its bullets into a new changelog.d/<name>.md (one \"### Added/Changed/Fixed/Security/Upgrade\" subsection per kind) and delete the section."
  fi
  return 0
}

check() {
  local out
  out="$(problems)"
  if [ -n "$out" ]; then
    printf '%s\n' "$out" >&2
    exit 1
  fi
  echo "changelog: changelog.d/ and CHANGELOG.md are in order"
}

prepare() {
  local version="$1" date tmp body out
  check_version "$version"
  if awk -v v="$version" '/^## / && $2 == v { found = 1 } END { exit !found }' "$changelog"; then
    echo "changelog: $changelog already has a section for $version" >&2
    exit 1
  fi
  if [ -z "$(fragment_files)" ]; then
    echo "changelog: no notes in changelog.d ($fragdir): nothing to release" >&2
    exit 1
  fi
  out="$(problems)"
  if [ -n "$out" ]; then
    printf '%s\n' "$out" >&2
    echo "changelog: fix these before releasing" >&2
    exit 1
  fi
  date="$(date -u +%Y-%m-%d)"
  tmp="$(mktemp)"
  body="$(mktemp)"
  gather >"$body"
  if ! [ -s "$body" ]; then
    rm -f "$tmp" "$body"
    echo "changelog: no notes in changelog.d ($fragdir): nothing to release" >&2
    exit 1
  fi
  # Above the newest released section; with none yet, at the end
  awk -v v="$version" -v d="$date" -v bodyfile="$body" '
    function put(   line) {
      print "## " v " - " d
      print ""
      while ((getline line < bodyfile) > 0) print line
      close(bodyfile)
    }
    !done && /^## / { put(); print ""; done = 1 }
    { print }
    END { if (!done) { print ""; put() } }' "$changelog" >"$tmp"
  cat "$tmp" >"$changelog"
  fragment_files | while IFS= read -r f; do rm -f "$f"; done
  rm -f "$tmp" "$body"
  echo "changelog: changelog.d/ is now \"## $version - $date\" in $changelog; commit it, then tag v$version"
}

released() {
  local version tagged theirs mine bad=0 checked=0
  tagged="$(mktemp)"
  for version in $(awk '/^## [0-9]/ { print $2 }' "$changelog"); do
    git -C "$root" show "v$version:CHANGELOG.md" >"$tagged" 2>/dev/null || continue
    theirs="$(CHANGELOG="$tagged" "$0" notes "$version" 2>/dev/null || true)"
    [ -n "$theirs" ] || continue # a tag from before the rule: its CHANGELOG.md doesn't hold its section
    checked=$((checked + 1))
    mine="$(section "$version")"
    if [ "$mine" != "$theirs" ]; then
      echo "changelog: the released section \"## $version\" differs from the one in its tag v$version." >&2
      echo "changelog: released notes are never edited; put this branch's new notes in a file in changelog.d/." >&2
      bad=1
    fi
  done
  rm -f "$tagged"
  [ "$bad" -eq 1 ] || echo "changelog: $checked released section(s) match their tags"
  exit "$bad"
}

case "${1:-}" in
released)
  [ $# -eq 1 ] || usage
  released
  ;;
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
check)
  [ $# -eq 1 ] || usage
  check
  ;;
*) usage ;;
esac
