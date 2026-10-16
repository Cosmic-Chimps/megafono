#!/usr/bin/env bash
# Keep the newest KEEP released versions of the GHCR package and delete the rest.
#
#   OWNER    user or organization that owns the package (default: $GITHUB_REPOSITORY_OWNER)
#   PACKAGE  package name (default: megafono; the release also runs it for megafono-gateway)
#   KEEP     how many tagged versions to keep (default: 10)
#   DRY_RUN  1 = print what would be deleted, delete nothing (default: 0)
#
# Needs: gh (authenticated through GH_TOKEN), jq, and docker with buildx.
#
# Why this is not "delete everything old": a multi-arch image is an index (the
# version that carries the tags) pointing at one manifest per architecture, plus
# buildx's attestation manifests. GHCR lists each of those as a separate UNTAGGED
# package version. Caravana pins the index digest, so a kept index needs its
# children: deleting "all untagged versions" would break the images we keep.
# So: work out the children of every version from its manifest, delete the old
# indexes together with their own children, and never delete a digest that a
# kept index uses.
#
# Untagged versions that belong to no index we looked at (orphans from an
# interrupted push, say) are left alone on purpose: without a parent we cannot
# tell what they are for.
set -euo pipefail

OWNER="${OWNER:-${GITHUB_REPOSITORY_OWNER:-}}"
PACKAGE="${PACKAGE:-megafono}"
KEEP="${KEEP:-10}"
DRY_RUN="${DRY_RUN:-0}"

if [ -z "$OWNER" ]; then
  echo "OWNER is not set and GITHUB_REPOSITORY_OWNER is empty." >&2
  exit 2
fi
case "$KEEP" in
  ''|*[!0-9]*) echo "KEEP must be a number, got '$KEEP'." >&2; exit 2 ;;
esac
if [ "$KEEP" -lt 1 ]; then
  echo "KEEP must be at least 1." >&2
  exit 2
fi

# The registry wants lowercase; the API does not care.
owner_lc="$(printf %s "$OWNER" | tr '[:upper:]' '[:lower:]')"
registry_image="ghcr.io/$owner_lc/$PACKAGE"

owner_type="$(gh api "users/$OWNER" --jq .type)"
case "$owner_type" in
  Organization) base="/orgs/$OWNER/packages/container/$PACKAGE/versions" ;;
  User) base="/users/$OWNER/packages/container/$PACKAGE/versions" ;;
  *) echo "Unexpected owner type '$owner_type' for $OWNER." >&2; exit 1 ;;
esac

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

# One JSON object per version, newest first among the tagged ones.
gh api --paginate "$base?per_page=100" --jq '.[]' | jq -s '.' > "$work/all.json"

jq 'map(select((.metadata.container.tags // []) | length > 0)) | sort_by(.created_at) | reverse' \
  "$work/all.json" > "$work/tagged.json"

tagged_count="$(jq 'length' "$work/tagged.json")"
echo "$registry_image: $(jq 'length' "$work/all.json") versions, $tagged_count tagged, keeping $KEEP."
if [ "$tagged_count" -le "$KEEP" ]; then
  echo "Nothing to delete."
  exit 0
fi

# Kept: the newest KEEP tagged versions, plus anything tagged latest.
# Everything else tagged is a candidate.
jq --argjson keep "$KEEP" '
  to_entries
  | map(select(.key < $keep or (.value.metadata.container.tags | index("latest"))))
  | map(.value.name)' "$work/tagged.json" > "$work/kept.json"
jq --slurpfile kept "$work/kept.json" '
  map(select(.name as $n | $kept[0] | index($n) | not)) | map(.name)' \
  "$work/tagged.json" > "$work/candidates.json"

# The digests of an index's children, one per line (none for a single-arch manifest).
children() {
  docker buildx imagetools inspect --raw "$registry_image@$1" | jq -r '.manifests[]?.digest'
}

# A kept version we cannot read could hide children we must not delete, so
# that stops the run. A candidate we cannot read is only skipped: its children
# stay behind as orphans, which is safe.
: > "$work/keep-digests.txt"
while read -r digest; do
  echo "$digest" >> "$work/keep-digests.txt"
  children "$digest" >> "$work/keep-digests.txt"
done < <(jq -r '.[]' "$work/kept.json")

: > "$work/delete-digests.txt"
while read -r digest; do
  if kids="$(children "$digest")"; then
    echo "$digest" >> "$work/delete-digests.txt"
    [ -z "$kids" ] || echo "$kids" >> "$work/delete-digests.txt"
  else
    echo "Skipping $digest: its manifest could not be read." >&2
  fi
done < <(jq -r '.[]' "$work/candidates.json")

sort -u "$work/keep-digests.txt" -o "$work/keep-digests.txt"
sort -u "$work/delete-digests.txt" -o "$work/delete-digests.txt"
comm -23 "$work/delete-digests.txt" "$work/keep-digests.txt" > "$work/to-delete.txt"

# Map digests to version ids. A digest with no listing entry is already gone.
jq -R -s 'split("\n") | map(select(length > 0))' "$work/to-delete.txt" > "$work/to-delete.json"
jq -r --slurpfile d "$work/to-delete.json" '
  map(select(.name as $n | $d[0] | index($n)))
  | sort_by(.created_at) | .[]
  | [(.id | tostring), .name, ((.metadata.container.tags // []) | join(",") | if . == "" then "(untagged)" else . end)]
  | @tsv' "$work/all.json" > "$work/versions.tsv"

if [ ! -s "$work/versions.tsv" ]; then
  echo "Nothing to delete."
  exit 0
fi

deleted=0
while IFS=$'\t' read -r id digest tags; do
  if [ "$DRY_RUN" = "1" ]; then
    echo "Would delete version $id $digest tags: $tags"
  else
    echo "Deleting version $id $digest tags: $tags"
    gh api -X DELETE "$base/$id" --silent
  fi
  deleted=$((deleted + 1))
done < "$work/versions.tsv"

if [ "$DRY_RUN" = "1" ]; then
  echo "Dry run: $deleted versions would be deleted."
else
  echo "Deleted $deleted versions."
fi
