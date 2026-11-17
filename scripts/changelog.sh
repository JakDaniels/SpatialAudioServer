#!/usr/bin/env bash
# Prints the changelog, made from the release tags' messages (newest first): the same text and layout as
# CHANGELOG.md, which scripts/make-release.sh adds to. The packages carry it (build-packages.sh; build-windows.ps1
# does the same in PowerShell), so a release's packages hold its own notes, which CHANGELOG.md gets only later.
#   scripts/changelog.sh [VERSION]
# With the build's VERSION, a build that is not a release (a draft) gets on top the "## Unreleased" entries of
# CHANGELOG.md, headed with that version.
set -euo pipefail

echo "# Changelog"
echo
echo "What changed in each release: the same text as its release notes."
echo
if [ -n "${1:-}" ] && ! git describe --exact-match --tags --match 'v[0-9]*' HEAD >/dev/null 2>&1; then
  echo "## Unreleased ($1)"
  echo
  unreleased="$(awk '/^## / { inside = ($0 ~ /^## Unreleased/); next } inside' CHANGELOG.md 2>/dev/null \
    | sed -e '/./,$!d' | sed -e :a -e '/^\n*$/{$d;N;ba' -e '}')"
  echo "${unreleased:-Nothing noted since the last release.}"
  echo
fi
git for-each-ref --sort=-v:refname --format='%(refname:strip=2)' 'refs/tags/v[0-9]*' | while read -r tag; do
  date="$(git tag -l --format='%(taggerdate:short)' "$tag")"
  [ -n "$date" ] || date="$(git log -1 --format=%cs "$tag")"
  echo "## ${tag#v} ($date)"
  echo
  # The message without a signature, and without blank lines at either end.
  git tag -l --format='%(contents)' "$tag" | sed '/-----BEGIN PGP SIGNATURE-----/,$d' \
    | sed -e '/./,$!d' | sed -e :a -e '/^\n*$/{$d;N;ba' -e '}'
  echo
done
