# Release notes: one file per change

Every change that matters to the people who run Megáfono and the apps that integrate it adds
**one file here**, in the same branch as the change. Nobody edits `CHANGELOG.md` for a change: it
holds only released sections. When a release is made, `.github/scripts/changelog.sh prepare X.Y.Z`
gathers every file here into one `## X.Y.Z - <date>` section of `CHANGELOG.md` and deletes the files.
This README is never collected.

Because two branches never edit the same file, merging after a release can't put a note inside a
released section (the old trap with a shared "Unreleased" heading).

## The file

- Name: lowercase letters, digits and dashes, then `.md`. Use the branch or feature name:
  `006-something.md`, `fix-queue-flake.md`.
- Content: one or more of these subsections, and nothing else (no title, no text outside a subsection):
  `### Added`, `### Changed`, `### Fixed`, `### Security`, `### Upgrade`.
- Each subsection is `- ` bullets. A bullet may continue on following lines indented by two spaces.
  Write for the people who run Megáfono and the apps that integrate it, in plain words.
- `Upgrade` is only for what an upgrade needs beyond deploying the new image.

```markdown
### Added

- The thing people can now do, in a sentence or two. Continuation lines
  are indented by two spaces.

### Upgrade

- Anything to do before or after deploying.
```

## The release

The release gathers one subsection per kind, in the order Added, Changed, Fixed, Security, Upgrade.
Inside a kind, bullets follow the file names in alphabetical order, then their order in the file.

## Checks

`.github/scripts/changelog.sh check` (run by CI) fails on a bad file name, an empty file, a heading
that isn't one of the five kinds, text outside a subsection, or a `## Unreleased` section in
`CHANGELOG.md`. `.github/scripts/changelog.sh unreleased` prints what the next release would say.
