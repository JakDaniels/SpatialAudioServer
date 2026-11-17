#!/usr/bin/env bash
# Copyright 2026 Simon Annetts/Jak Daniels
# SPDX-License-Identifier: Apache-2.0

# Builds the apt and dnf repositories for GitHub Pages (PACKAGING-PLAN.md "Package repositories"), signed with the
# project key, from release packages already downloaded:
#   scripts/build-repos.sh PACKAGES_DIR SITE_DIR SITE_URL
# PACKAGES_DIR holds the .deb and .rpm files to serve (pages.yml puts the latest two releases there). The signing
# key must be in gpg's keyring (pages.yml imports it from a secret), and its public half must equal
# packaging/signing-key.asc, which the site serves as key.asc. Needs apt-ftparchive (apt-utils), createrepo_c
# (createrepo-c), rpmsign (rpm) and gpg.
# Layout:  deb/  dists/stable/{Release,InRelease,Release.gpg,main/binary-{amd64,arm64}/Packages[.gz]}, pool/main/
#          rpm/  {x86_64,aarch64}/*.rpm with repodata/ (repomd.xml signed), spatialaudioserver.repo
#          key.asc, index.html
set -euo pipefail

packages="$(cd "${1:?usage: $0 PACKAGES_DIR SITE_DIR SITE_URL}" && pwd)"
site="${2:?}"
url="${3:?}"
url="${url%/}"
root="$(cd "$(dirname "$0")/.." && pwd)"

# The signing key: the one secret key in the keyring, and it must be the published one.
fingerprint="$(gpg --batch --with-colons --list-secret-keys | awk -F: '$1 == "fpr" { print $10; exit }')"
[ -n "$fingerprint" ] || { echo "No secret key in gpg's keyring." >&2; exit 1; }
published="$(gpg --batch --with-colons --show-keys "$root/packaging/signing-key.asc" | awk -F: '$1 == "fpr" { print $10; exit }')"
[ "$fingerprint" = "$published" ] || { echo "The signing key $fingerprint is not packaging/signing-key.asc ($published)." >&2; exit 1; }
# The key has no passphrase of its own (the Actions secret holding it is encrypted), so nothing asks for one.
sign() { gpg --batch --yes --local-user "$fingerprint" "$@"; }

rm -rf "$site"
mkdir -p "$site/deb/pool/main" "$site/rpm/x86_64" "$site/rpm/aarch64"
site="$(cd "$site" && pwd)"
cp "$root/packaging/signing-key.asc" "$site/key.asc"
touch "$site/.nojekyll"

# apt: one suite ("stable", component "main") for every supported Debian and Ubuntu release, since the packages
# carry what they need.
cp "$packages"/*.deb "$site/deb/pool/main/"
(
  cd "$site/deb"
  for arch in amd64 arm64; do
    dir="dists/stable/main/binary-$arch"
    mkdir -p "$dir"
    apt-ftparchive --arch "$arch" packages pool/main > "$dir/Packages"
    gzip -9kn "$dir/Packages"
  done
  apt-ftparchive \
    -o APT::FTPArchive::Release::Origin=SpatialAudioServer \
    -o APT::FTPArchive::Release::Label=SpatialAudioServer \
    -o APT::FTPArchive::Release::Suite=stable \
    -o APT::FTPArchive::Release::Codename=stable \
    -o "APT::FTPArchive::Release::Architectures=amd64 arm64" \
    -o APT::FTPArchive::Release::Components=main \
    -o "APT::FTPArchive::Release::Description=SpatialAudioServer packages" \
    release dists/stable > Release.tmp
  mv Release.tmp dists/stable/Release
  sign --clearsign -o dists/stable/InRelease dists/stable/Release
  sign --armor --detach-sign -o dists/stable/Release.gpg dists/stable/Release
)

# dnf: each package signed itself (rpmsign), and the metadata (repomd.xml) signed too.
for rpm in "$packages"/*.rpm; do
  case "$rpm" in
    *.x86_64.rpm) cp "$rpm" "$site/rpm/x86_64/" ;;
    *.aarch64.rpm) cp "$rpm" "$site/rpm/aarch64/" ;;
  esac
done
# (rpmsign warns that it cannot set GPG_TTY when there is no terminal, as in CI: harmless, so filtered out.)
rpmsign --addsign --define "_gpg_name $fingerprint" "$site"/rpm/*/*.rpm 2> >(grep -v 'GPG_TTY' >&2)
for arch in x86_64 aarch64; do
  createrepo_c --quiet "$site/rpm/$arch"
  sign --armor --detach-sign -o "$site/rpm/$arch/repodata/repomd.xml.asc" "$site/rpm/$arch/repodata/repomd.xml"
done
cat > "$site/rpm/spatialaudioserver.repo" <<EOF
[spatialaudioserver]
name=SpatialAudioServer
baseurl=$url/rpm/\$basearch
enabled=1
gpgcheck=1
repo_gpgcheck=1
gpgkey=$url/key.asc
EOF

# A page for people who open the address in a browser.
versions="$(ls "$site/deb/pool/main" | sed -nE 's/^spatialaudioserver_([^_]+)_amd64\.deb$/\1/p' | sort -V -r | paste -sd, - | sed 's/,/, /g')"
cat > "$site/index.html" <<EOF
<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>SpatialAudioServer packages</title>
<style>body{font:16px/1.5 system-ui,sans-serif;max-width:52rem;margin:2rem auto;padding:0 1rem}
pre{background:#f4f4f4;padding:.8rem;overflow-x:auto}@media (prefers-color-scheme:dark){body{background:#111;color:#ddd}pre{background:#222}a{color:#8cf}}</style>
</head><body>
<h1>SpatialAudioServer packages</h1>
<p>Package repositories for <a href="https://github.com/JakDaniels/SpatialAudioServer">SpatialAudioServer</a>, so
that updates come with the system's own updates. Versions here: $versions. Every release is also on the
<a href="https://github.com/JakDaniels/SpatialAudioServer/releases">Releases page</a>, with the Windows installers.</p>
<h2>Ubuntu 24.04 or later, Debian 13 or later (load tool: Ubuntu 22.04 or later)</h2>
<pre>curl -fsSL $url/key.asc | sudo gpg --dearmor -o /usr/share/keyrings/spatialaudioserver.gpg
echo "deb [signed-by=/usr/share/keyrings/spatialaudioserver.gpg] $url/deb stable main" |
  sudo tee /etc/apt/sources.list.d/spatialaudioserver.list
sudo apt update
sudo apt install spatialaudioserver        # and/or spatialaudioserver-loadtest</pre>
<h2>Fedora</h2>
<pre>sudo curl -fsSL -o /etc/yum.repos.d/spatialaudioserver.repo $url/rpm/spatialaudioserver.repo
sudo dnf install spatialaudioserver        # and/or spatialaudioserver-loadtest</pre>
<p>The signing key's fingerprint: <code>$fingerprint</code>.</p>
</body></html>
EOF

du -sh "$site"
find "$site" -type f | sed "s|^$site/||" | sort
