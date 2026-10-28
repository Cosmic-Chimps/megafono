#!/usr/bin/env bash
# Tests for .github/scripts/changelog.sh. Every scenario runs in a fresh temporary tree (mktemp -d) through
# the script's CHANGELOG and CHANGELOG_DIR overrides, so the real CHANGELOG.md and changelog.d/ are never
# touched. Exits 1 when any scenario fails.
#
#   bash .github/scripts/changelog-test.sh
set -uo pipefail

script="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/changelog.sh"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

fails=0
total=0
out=""
code=0

pass() { total=$((total + 1)); echo "ok   $1"; }
fail() { total=$((total + 1)); fails=$((fails + 1)); echo "FAIL $1"; echo "     $2"; }

# A fresh tree: CHANGELOG.md with the intro and two released sections, and an empty changelog.d/
fresh() {
  rm -rf "$work/t"
  mkdir -p "$work/t/changelog.d"
  cat >"$work/t/CHANGELOG.md" <<'EOF'
# Changelog

Intro line.

## 0.2.0 - 2026-01-02

### Fixed

- Old fix.

## 0.1.0 - 2026-01-01

### Added

- First thing.
EOF
  export CHANGELOG="$work/t/CHANGELOG.md" CHANGELOG_DIR="$work/t/changelog.d"
}

# run ARGS...: the script's output (both streams) in $out, its exit code in $code
run() {
  out="$("$script" "$@" 2>&1)"
  code=$?
}

expect_code() { # NAME WANTED
  if [ "$code" -eq "$2" ]; then pass "$1"; else fail "$1" "exit $code, wanted $2; output: $out"; fi
}

expect_has() { # NAME TEXT: $out contains TEXT
  case "$out" in
  *"$2"*) pass "$1" ;;
  *) fail "$1" "output lacks \"$2\"; output: $out" ;;
  esac
}

expect_lacks() { # NAME TEXT
  case "$out" in
  *"$2"*) fail "$1" "output has \"$2\"; output: $out" ;;
  *) pass "$1" ;;
  esac
}

# A fragment file: frag NAME, the content on stdin
frag() { cat >"$CHANGELOG_DIR/$1"; }

two_fragments() {
  frag a-first.md <<'EOF'
### Added

- A one.
  continues here.
- A two.

### Fixed

- Fix from a.

### Upgrade

- Upgrade from a.
EOF
  frag b-second.md <<'EOF'
### Changed

- Change from b.

### Added

- B one.

### Fixed

- Fix from b.
EOF
  echo "A README that is never collected." >"$CHANGELOG_DIR/README.md"
}

# ---- prepare with two fragments that share kinds
fresh
two_fragments
run prepare 0.3.0
expect_code "prepare with two fragments succeeds" 0
if [ -z "$(ls "$CHANGELOG_DIR" | grep -v '^README.md$')" ] && [ -f "$CHANGELOG_DIR/README.md" ]; then
  pass "prepare deletes the fragments and keeps the README"
else
  fail "prepare deletes the fragments and keeps the README" "left: $(ls "$CHANGELOG_DIR")"
fi
run notes 0.3.0
expect_code "notes reads the prepared version" 0
expected="### Added

- A one.
  continues here.
- A two.
- B one.

### Changed

- Change from b.

### Fixed

- Fix from a.
- Fix from b.

### Upgrade

- Upgrade from a."
if [ "$out" = "$expected" ]; then
  pass "kinds in order, bullets in file-name order"
else
  fail "kinds in order, bullets in file-name order" "got: $out"
fi
head_lines="$(sed -n 1,8p "$CHANGELOG")"
case "$head_lines" in
"# Changelog

Intro line.

## 0.3.0 - $(date -u +%Y-%m-%d)

### Added") pass "the new section sits right after the intro, dated today (UTC)" ;;
*) fail "the new section sits right after the intro, dated today (UTC)" "top: $head_lines" ;;
esac
if grep -n '^## ' "$CHANGELOG" | sed 's/^[0-9]*://' | awk '{ print $2 }' | tr '\n' ' ' | grep -q '^0.3.0 0.2.0 0.1.0 $'; then
  pass "the new section is above the released ones"
else
  fail "the new section is above the released ones" "$(grep '^## ' "$CHANGELOG")"
fi
if sed -n '/^## 0.2.0/,$p' "$CHANGELOG" | grep -q 'First thing.' && grep -q '^- Old fix.$' "$CHANGELOG"; then
  pass "released sections are untouched"
else
  fail "released sections are untouched" "released text is missing"
fi
run prepare 0.3.0
expect_code "prepare refuses a version that already has a section" 1
fresh
two_fragments
run prepare 0.2.0
expect_code "prepare refuses an existing released version too" 1
run check
expect_code "check passes after a refused prepare (nothing was deleted)" 0
test -f "$CHANGELOG_DIR/a-first.md" && pass "a refused prepare keeps the fragments" || fail "a refused prepare keeps the fragments" "a-first.md is gone"
run prepare not-a-version
expect_code "prepare refuses a bad version" 2

# ---- prepare with none
fresh
before="$(cat "$CHANGELOG")"
run prepare 0.3.0
expect_code "prepare with no fragments is refused" 1
expect_has "the refusal says there are no notes in changelog.d" "no notes in changelog.d"
[ "$(cat "$CHANGELOG")" = "$before" ] && pass "a refused prepare leaves CHANGELOG.md as it was" || fail "a refused prepare leaves CHANGELOG.md as it was" "changed"
echo "only a readme" >"$CHANGELOG_DIR/README.md"
run prepare 0.3.0
expect_code "a README alone is no notes" 1
rm -rf "$CHANGELOG_DIR"
run prepare 0.3.0
expect_code "a missing changelog.d is no notes" 1

# ---- prepare refuses a broken fragment
fresh
frag bad.md <<'EOF'
### Added

- Fine.

### Misc

- Not a kind.
EOF
before="$(cat "$CHANGELOG")"
run prepare 0.3.0
expect_code "prepare refuses a fragment that check would fail" 1
[ "$(cat "$CHANGELOG")" = "$before" ] && [ -f "$CHANGELOG_DIR/bad.md" ] && pass "and changes nothing" || fail "and changes nothing" "files changed"

# ---- unreleased
fresh
run unreleased
expect_code "unreleased with no fragments succeeds" 0
[ -z "$out" ] && pass "unreleased prints nothing with no fragments" || fail "unreleased prints nothing with no fragments" "output: $out"
two_fragments
run unreleased
expect_code "unreleased with fragments succeeds" 0
if [ "$out" = "$expected" ]; then pass "unreleased prints what prepare would write"; else fail "unreleased prints what prepare would write" "got: $out"; fi
ls "$CHANGELOG_DIR" | grep -q 'a-first.md' && pass "unreleased deletes nothing" || fail "unreleased deletes nothing" "fragments gone"

# ---- notes, released section missing
fresh
two_fragments
run notes 0.3.0
expect_code "notes on a version with no section fails" 1
expect_has "and points to prepare while notes are waiting in changelog.d" "changelog.sh prepare 0.3.0"
run notes 0.2.0
expect_code "notes on a released version works" 0
[ "$out" = "### Fixed

- Old fix." ] && pass "notes prints the section without its heading" || fail "notes prints the section without its heading" "got: $out"

# ---- check passes
fresh
run check
expect_code "check passes with no fragments" 0
two_fragments
run check
expect_code "check passes with good fragments and a README" 0

# ---- check fails
fresh
frag bad-kind.md <<'EOF'
### Added

- Fine.

### Misc

- Not a kind.
EOF
run check
expect_code "check fails on a heading that is not one of the five kinds" 1
expect_has "and names the file and the line" "changelog.d/bad-kind.md:5"

fresh
frag stray.md <<'EOF'
Some words first.

### Added

- Fine.
EOF
run check
expect_code "check fails on text outside a subsection" 1
expect_has "and names the file and the line (outside)" "changelog.d/stray.md:1"

fresh
frag stray2.md <<'EOF'
### Added

- Fine.
A line that is not a bullet.
EOF
run check
expect_code "check fails on text that is not a bullet" 1
expect_has "and names the file and the line (not a bullet)" "changelog.d/stray2.md:4"

fresh
frag title.md <<'EOF'
# A title

### Added

- Fine.
EOF
run check
expect_code "check fails on a title heading" 1
expect_has "and names the file and the line (title)" "changelog.d/title.md:1"

fresh
frag empty-section.md <<'EOF'
### Added

### Fixed

- Fine.
EOF
run check
expect_code "check fails on a subsection with no bullets" 1
expect_has "and names the line of the subsection" "changelog.d/empty-section.md:1"

fresh
: >"$CHANGELOG_DIR/empty.md"
run check
expect_code "check fails on an empty file" 1
expect_has "and names the file" "changelog.d/empty.md: empty file"

fresh
printf '\n\n' >"$CHANGELOG_DIR/blank.md"
run check
expect_code "check fails on a file of only blank lines" 1

fresh
printf '### Added\n\n- Fine.\n' >"$CHANGELOG_DIR/Bad_Name.md"
run check
expect_code "check fails on a bad name (capitals and an underscore)" 1
expect_has "and names the file (bad name)" "changelog.d/Bad_Name.md: bad name"

fresh
printf '### Added\n\n- Fine.\n' >"$CHANGELOG_DIR/notes.txt"
run check
expect_code "check fails on a file that is not .md" 1
expect_has "and names the file (not .md)" "changelog.d/notes.txt: bad name"

# ---- check fails on a stale "## Unreleased" (a branch started before the switch, merged after it)
unreleased_message='CHANGELOG.md has a "## Unreleased" section; notes now live in changelog.d/. Move its bullets into a new changelog.d/<name>.md (one "### Added/Changed/Fixed/Security/Upgrade" subsection per kind) and delete the section.'

fresh
cat >"$CHANGELOG" <<'EOF'
# Changelog

Intro line.

## Unreleased

### Added

- A bullet from an old branch.

## 0.2.0 - 2026-01-02

### Fixed

- Old fix.
EOF
run check
expect_code "check fails on a ## Unreleased section with bullets" 1
expect_has "and says what to do" "changelog: $unreleased_message"

fresh
cat >"$CHANGELOG" <<'EOF'
# Changelog

Intro line.

## Unreleased

## 0.2.0 - 2026-01-02

### Fixed

- Old fix.
EOF
run check
expect_code "check fails on a ## Unreleased heading alone" 1
expect_has "and says what to do (heading alone)" "changelog: $unreleased_message"

fresh
cat >"$CHANGELOG" <<'EOF'
# Changelog

Intro line.

## Unreleased
EOF
run check
expect_code "check fails on a ## Unreleased heading that is the last line" 1

fresh
two_fragments
run prepare 0.3.0
expect_code "prepare works on a tree that has no Unreleased section" 0
run check
expect_code "check passes after prepare" 0

fresh
echo "- stray" >>"$CHANGELOG"
printf '### Added\n\n- Fine.\n' >"$CHANGELOG_DIR/ok.md"
run prepare 0.3.0
expect_code "prepare works when CHANGELOG.md has trailing text" 0

echo
if [ "$fails" -eq 0 ]; then
  echo "changelog-test: all $total checks passed"
else
  echo "changelog-test: $fails of $total checks failed" >&2
  exit 1
fi
