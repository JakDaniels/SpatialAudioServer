#!/usr/bin/env bash
# Tags a release of main (PACKAGING-PLAN.md step 7), from the repository root, after the pull request from develop
# into main has passed and been merged:
#   scripts/make-release.sh 0.2.0 [--dry-run]       (or 0.2.0-rc.1 for a pre-release)
#   scripts/make-release.sh --changelog 0.2.0       only step 5, for a tag that exists already
#  1. fetches, and takes main's latest commit on GitHub (whatever branch is checked out here); checks the tag is new
#     and newer than the last release;
#  2. ci.yml must have passed for that commit, or for the pull request's head it merged with the same files (the
#     pull request's own run: no second run on main); waits if that is still running; if nothing equivalent was
#     tested (a docs-only pull request, a commit made on main), starts ci.yml on main and waits (about 15 minutes);
#     stops if it failed;
#  3. opens an editor for the tag's message (the release notes' top part), prefilled with the commits since the last
#     release: edit it into what changed; an empty message stops;
#  4. pushes the tag, which starts release.yml (a draft release with every package), waits for it and prints the
#     draft's address. Publish it by hand after a look (the command is printed);
#  5. right after the tag is pushed: the tag's message becomes a section "## 0.2.0 (date)" at the top of
#     CHANGELOG.md, committed on the local develop (made without touching the working tree, whatever is checked
#     out): push develop to keep it; it reaches main with the next merge.
# Needs git and gh (logged in). Only the tag is pushed (step 4); --dry-run stops before it and starts nothing.
set -euo pipefail

usage() { sed -n '4,5s/^# //p' "$0" >&2; exit 2; }
mode=release
if [ "${1:-}" = --changelog ]; then mode=changelog; shift; fi
[ $# -ge 1 ] || usage
version="${1#v}"
dry_run=false
[ "${2:-}" = --dry-run ] && dry_run=true
tag="v$version"
remote=origin

die() { echo "make-release: $*" >&2; exit 1; }
step() { echo; echo "== $*"; }

# The tag's message as a section at the top of CHANGELOG.md, committed on the local develop (not pushed). Made with a
# scratch index, so the working tree and whatever is checked out are left alone.
add_changelog() {
  local tag="$1" version="${1#v}" base date work commit
  base="$(git rev-parse refs/heads/develop)"
  if ! git merge-base --is-ancestor "$remote/develop" "$base"; then
    echo "CHANGELOG.md: local develop is behind $remote/develop; pull it, then: scripts/make-release.sh --changelog $version"
    return 0
  fi
  work="$(mktemp -d)"
  git show "$base:CHANGELOG.md" > "$work/old" 2>/dev/null || : > "$work/old"
  if grep -q "^## $version " "$work/old"; then
    echo "CHANGELOG.md on develop already has $version"
    rm -rf "$work"; return 0
  fi
  date="$(git tag -l --format='%(taggerdate:short)' "$tag")"
  [ -n "$date" ] || date="$(git log -1 --format=%cs "$tag")"
  {
    echo "## $version ($date)"
    echo
    git tag -l --format='%(contents)' "$tag" | sed '/-----BEGIN PGP SIGNATURE-----/,$d' | sed -e :a -e '/^\n*$/{$d;N;ba' -e '}'
    echo
  } > "$work/section"
  if [ -s "$work/old" ]; then
    # Before the first release's section (after the file's own heading and introduction), or at the end.
    awk -v section="$work/section" '
      !done && /^## / { while ((getline line < section) > 0) print line; done = 1 }
      { print }
      END { if (!done) { print ""; while ((getline line < section) > 0) print line } }' "$work/old" > "$work/new"
  else
    { echo "# Changelog"; echo; echo "What changed in each release: the same text as its release notes."; echo
      cat "$work/section"; } > "$work/new"
  fi
  GIT_INDEX_FILE="$work/index" git read-tree "$base"
  GIT_INDEX_FILE="$work/index" git update-index --add --cacheinfo "100644,$(git hash-object -w "$work/new"),CHANGELOG.md"
  commit="$(git commit-tree "$(GIT_INDEX_FILE="$work/index" git write-tree)" -p "$base" -m "CHANGELOG: $version")"
  rm -rf "$work"
  if [ "$(git symbolic-ref -q --short HEAD)" = develop ]; then
    git merge --ff-only --quiet "$commit" \
      || { echo "CHANGELOG.md: develop could not move to ${commit::7} (CHANGELOG.md changed here?): git merge --ff-only $commit"; return 0; }
  else
    git update-ref -m "make-release: CHANGELOG $version" refs/heads/develop "$commit" "$base"
  fi
  echo "CHANGELOG.md: $version added on develop (${commit::7}); push develop to keep it"
}

echo "$version" | grep -Eq '^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$' \
  || die "'$1' is not a version (0.2.0 or 0.2.0-rc.1)"
[ -f src/SpatialAudioServer.slnx ] || die "run it from the repository root"
command -v gh >/dev/null || die "gh is needed"
gh auth status >/dev/null 2>&1 || die "gh is not logged in (gh auth login)"
repo="$(gh repo view --json nameWithOwner -q .nameWithOwner)"

if [ "$mode" = changelog ]; then
  git fetch --quiet --no-tags "$remote" develop 'refs/tags/v*:refs/tags/v*' || die "git fetch failed"
  git rev-parse -q --verify "refs/tags/$tag" >/dev/null || die "no tag $tag"
  add_changelog "$tag"
  exit 0
fi

step "Checks"
# Only the version tags: develop-draft moves with every drafts run and would be refused ("would clobber").
git fetch --quiet --no-tags "$remote" main develop 'refs/tags/v*:refs/tags/v*' || die "git fetch failed"
sha="$(git rev-parse "$remote/main")"
if git rev-parse -q --verify "refs/tags/$tag" >/dev/null || git ls-remote --exit-code --tags "$remote" "$tag" >/dev/null; then
  die "$tag already exists"
fi
previous="$(git tag -l 'v[0-9]*' --sort=-v:refname | head -n 1)"
if [ -n "$previous" ]; then
  newest="$(printf '%s\n%s\n' "${previous#v}" "$version" | sort -V | tail -n 1)"
  [ "$newest" = "$version" ] && [ "${previous#v}" != "$version" ] || die "$tag is not newer than $previous"
  [ "$(git rev-parse "$previous^{commit}")" != "$sha" ] || die "main has nothing new since $previous"
  echo "Release $tag: main at ${sha::7}, $(git rev-list --count "$previous..$sha") commits after $previous"
else
  echo "Release $tag: main at ${sha::7}, the first release"
fi
unmerged="$(git rev-list --count "$sha..$remote/develop")"
[ "$unmerged" = 0 ] || echo "Note: develop has $unmerged commit(s) not in main; they are not in this release."

step "CI for ${sha::7} on main"
# What ci.yml may have tested: this commit, or the pull request's head a merge brought in, if its files are the same.
tested=("$sha")
if merged="$(git rev-parse -q --verify "$sha^2")" && git diff --quiet "$merged" "$sha"; then
  tested+=("$merged")
  echo "Merged from ${merged::7} with the same files: its pull request's ci.yml run counts"
fi
shas_json="$(printf '"%s",' "${tested[@]}")"
shas_json="[${shas_json%,}]"
ci_state() { # the best result of ci.yml runs for those commits: success, running, failure or none
  gh run list -R "$repo" -w ci.yml -L 100 --json headSha,status,conclusion \
    -q "[.[] | select(.headSha as \$h | $shas_json | index(\$h))] |
        if any(.conclusion == \"success\") then \"success\"
        elif any(.status != \"completed\") then \"running\"
        elif length > 0 then \"failure\" else \"none\" end"
}
state="$(ci_state)"
if [ "$state" = none ]; then
  if $dry_run; then
    echo "(dry run) ci.yml has not run for it: would start it on main and wait"
  else
    gh workflow run ci.yml -R "$repo" --ref main
    echo "ci.yml has not run for it (a docs-only pull request?): started it on main; waiting for it to appear…"
    for _ in $(seq 30); do sleep 5; state="$(ci_state)"; [ "$state" = none ] || break; done
    [ "$state" != none ] || die "the ci.yml run did not appear"
  fi
fi
if [ "$state" = running ]; then
  if $dry_run; then
    echo "(dry run) ci.yml is running for it: would wait for it"
  else
    run="$(gh run list -R "$repo" -w ci.yml -L 100 --json databaseId,headSha,status \
      -q "[.[] | select((.headSha as \$h | $shas_json | index(\$h)) and .status != \"completed\")][0].databaseId")"
    echo "Waiting for ci.yml (run $run, about 15 minutes)…"
    gh run watch -R "$repo" "$run" --exit-status --interval 30 >/dev/null || true
    state="$(ci_state)"
  fi
fi
case "$state" in
  success) echo "ci.yml passed" ;;
  failure) die "ci.yml failed for ${sha::7}: fix it on develop and merge again, or re-run it if it was a flake" ;;
esac

step "Tag message"
message_file="$(mktemp)"
trap 'rm -f "$message_file" "$message_file.clean"' EXIT
{
  echo "What changed in $version (replace this with the release's notes: the first line is the summary)."
  echo
  git log --no-merges -n 60 --format='- %s' ${previous:+"$previous.."}"$sha"
  echo
  echo "# Lines starting with # are dropped. An empty message stops the release; nothing has been pushed yet."
} > "$message_file"
if $dry_run; then
  echo "(dry run) would open an editor with:"
  sed 's/^/    /' "$message_file"
  step "Dry run: would tag ${sha::7} as $tag and push the tag, starting release.yml, then add it to CHANGELOG.md on develop"
  exit 0
fi
"${GIT_EDITOR:-$(git var GIT_EDITOR)}" "$message_file"
grep -v '^#' "$message_file" > "$message_file.clean" || true
[ -n "$(tr -d '[:space:]' < "$message_file.clean")" ] || die "empty message: stopped (nothing pushed)"
git tag -a "$tag" -F "$message_file.clean" "$sha"

step "Push $tag"
if ! git push "$remote" "refs/tags/$tag"; then
  git tag -d "$tag" >/dev/null
  die "the push failed; the local tag was removed"
fi

step "CHANGELOG.md"
add_changelog "$tag"

step "release.yml"
run=""
for _ in $(seq 30); do
  sleep 5
  run="$(gh run list -R "$repo" -w release.yml -L 20 --json databaseId,headBranch \
    -q "[.[] | select(.headBranch == \"$tag\")][0].databaseId // empty")"
  [ -n "$run" ] && break
done
[ -n "$run" ] || die "release.yml did not start for $tag: see the Actions page"
echo "Waiting for release.yml (run $run, about 20 minutes)…"
if gh run watch -R "$repo" "$run" --exit-status --interval 30 >/dev/null; then
  echo
  echo "Draft release ready: $(gh release view "$tag" -R "$repo" --json url -q .url)"
  echo "Check it (notes, files), then publish it on that page or with:"
  echo "    gh release edit $tag -R $repo --draft=false"
else
  die "release.yml failed: gh run view $run -R $repo --log-failed (fix, then re-run it from the Actions page)"
fi
