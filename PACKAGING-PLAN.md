# Packaging and releases: plan

Status: **under way** (2026-11-17): steps 1–4 done (versioning, CI, code changes, the x64 `.deb` packages, tested);
step 5 built (`.rpm` for Fedora, arm64), x64 packages tested in CI containers; step 6 (Windows MSIs) built and
tested in CI and by hand; step 7 done: **v0.2.0 released** (2026-11-17); step 8: **public** since 2026-11-17
(settings done), and the signed apt/dnf repositories are live on GitHub Pages. The plan is complete; left are
extras: openSUSE ([#15](https://github.com/JakDaniels/SpatialAudioServer/issues/15)), the optional GitHub Support purge, and checks on real Fedora (SELinux, dnf from the
repository) and arm64 hardware ([#16](https://github.com/JakDaniels/SpatialAudioServer/issues/16)).
Decisions so far are marked **Decided**; open points are listed at the end. [PLAN.md](PLAN.md) stays the
specification of the server itself.

## Goals

- Installable packages for the server and for the load tool:
  - Linux: `.deb` and `.rpm`, for **x64 and arm64**;
  - Windows: an installer for **x64**.
- Built by a GitHub workflow from a version tag, attached to a GitHub release with checksums.
- No .NET installation needed on the target: packages carry a self-contained .NET build.
- GStreamer from the distribution on Linux; on Windows, embedded in the MSI.

**Decided:**
- Linux arm64 yes, Windows arm64 no.
- Windows: the GStreamer parts we need are embedded in the MSI (see "GStreamer on Windows").
- No Windows code signing: an open-source project with no budget for a certificate (but see the free option under
  Signing).
- The load tool gets its own packages on every platform.

## Supported systems

**Decided:** the **server** on any `.deb`- or `.rpm`-based distribution that ships **GStreamer 1.24 or later**; the
**load tool** also on **Ubuntu 22.04** (GStreamer 1.20), where it is already used for load runs (the
self-contained build from `scripts/build-loadtest.sh` runs there). The first target to test is **Ubuntu 24.04**
(GStreamer 1.24.2): a `.deb` for it comes first.

| Package | GStreamer floor | Also on |
|---|---|---|
| server (`spatialaudioserver`) | 1.24 | |
| load tool (`spatialaudioserver-loadtest`) | 1.20 | Ubuntu 22.04 (and other 1.20+ distributions) |

The dependency package names are the same on Ubuntu 22.04, 24.04 and 26.04, so **one load tool `.deb`** per
architecture installs on all of them, with its GStreamer dependencies at `>= 1.20`.

| Family | Examples with GStreamer ≥ 1.24 (to confirm per release) | Package |
|---|---|---|
| Debian / Ubuntu | Ubuntu 24.04, 25.x, 26.04; Debian 13 | `.deb` |
| Fedora | Fedora 40 and later. **Not RHEL / Rocky / Alma 10:** their GStreamer has no `webrtcbin` (step 5) | `.rpm` |
| openSUSE | Tumbleweed, Leap 16 (to check) | `.rpm`, different package names (see below) |

**1.24 is the floor we must prove, not assume.** We develop on 1.28. There are two risks:

1. **Elements and properties.** What the server uses has existed for a long time: `webrtcbin` with data channels,
   `audiomixmatrix`, `audiointerleave`, `opusenc`/`opusdec`, `rtpopuspay`, and the ICE agent's
   `min/max-rtp-port` (1.20). Still, each needs checking on 1.24.
2. **The binding.** GstSharp.Net 1.28.17 is generated from GStreamer 1.28. Calling a function that 1.24 lacks fails
   only when it is called (a missing entry point), not at start-up.

The answer for both is the same: **run the full test suite against 1.24 in CI** (an `ubuntu:24.04` container), and
make that a release gate. If something fails, we either avoid that call or raise the floor.

## Linux packages

**Tool:** [nfpm](https://nfpm.goreleaser.com/), with one YAML per package. It produces `.deb` and `.rpm` from the
same description, runs anywhere (a GitHub runner, a developer's machine), and handles systemd units and install
scripts.

**Server package `spatialaudioserver`:**

| What | Where |
|---|---|
| Program (self-contained `linux-x64` / `linux-arm64` publish) | `/usr/lib/spatialaudioserver/` |
| Command wrapper (`spatialaudioserver apikey …`, `admin …`) | `/usr/bin/spatialaudioserver` |
| Configuration (a config file: kept and merged on upgrade) | `/etc/spatialaudioserver/appsettings.json` |
| Where things are, for the unit and the wrapper (a config file) | `/etc/spatialaudioserver/environment` |
| Data: SQLite database, `keys/`, dev certificate | `/var/lib/spatialaudioserver/` (owned by the service user) |
| Service | `/usr/lib/systemd/system/spatialaudioserver.service` (the hardened unit from `docs/internet.md`) |
| Docs | `/usr/share/doc/spatialaudioserver/` (README, running.md, internet.md, licences) |

- **Install script:**
  - creates the system user `spatialaudio` and the data directory;
  - enables the service, but does not start it until it is configured: an admin password and API keys come first,
    and the post-install message says how;
  - on upgrade, restarts the service if it was running.
- **Remove script:** stops and disables the service. The data stays, unless the package is purged (`.deb`).

**Dependencies, by family:**

| | Debian / Ubuntu | Fedora | openSUSE |
|---|---|---|---|
| core and base | `gstreamer1.0-plugins-base` (≥ 1.24) | `gstreamer1-plugins-base` | `gstreamer-plugins-base` |
| webrtcbin, audiomixmatrix, … | `gstreamer1.0-plugins-bad` | `gstreamer1-plugins-bad-free` | `gstreamer-plugins-bad` |
| rtpopuspay, … | `gstreamer1.0-plugins-good` | `gstreamer1-plugins-good` | `gstreamer-plugins-good` |
| ICE (libnice) | `gstreamer1.0-nice` | `libnice-gstreamer1` | `gstreamer-plugin-libnice` (to check) |
| other | `libsqlite3-0`, `libicu*` (or invariant globalisation) | `sqlite-libs`, `libicu` | `libsqlite3-0`, `libicu` |

The names differ between Fedora and openSUSE, so either build **two `.rpm` variants**, or use capability
dependencies that both provide. Fedora generates `gstreamer1(element-webrtcbin)` provides automatically; whether
openSUSE has an equivalent is to check. **Proposal:** a Fedora `.rpm` first, openSUSE afterwards. The binding
calls the GStreamer libraries directly, so GObject Introspection typelibs (`gir1.2-*`) are not needed at run time
(to confirm in the 1.24 test).

**Load tool package `spatialaudioserver-loadtest`:**
- the self-contained load tool in `/usr/lib/spatialaudioserver-loadtest/`, with `/usr/bin/sas-loadtest`;
- the pre-rendered voices (`voices/`, 2.3 MB);
- the same GStreamer packages as the server, but at **`>= 1.20`**, so it installs on Ubuntu 22.04; `libflite1`
  (Fedora: `flite`) only as *Recommends*, since it is needed only for `--audio flite`;
- no service, no user.

## Windows installers

**Tool:** [WiX Toolset](https://wixtoolset.org/) (v5 or v6), built with `dotnet build` of a `.wixproj` on a
`windows-latest` runner.

**Server MSI:**
- the program in `C:\Program Files\SpatialAudioServer\`;
- configuration and data in `C:\ProgramData\SpatialAudioServer\` (kept on upgrade and uninstall);
- the Windows service (`ServiceInstall`, automatic start, as `LocalService` or a virtual account);
- optional firewall rules for 5000/5001 and the media port range;
- major upgrades in place.

**Load tool MSI:** the program and voices in `C:\Program Files\SpatialAudioServer Load Test\`, on `PATH`; no service.

### GStreamer on Windows

**Decided (2026-11-16): embed the GStreamer parts we need in the MSI.**

The deciding argument: a GStreamer installed separately on Windows is not updated either, whether by hand or by a
bootstrapper. It stays at its version until someone reinstalls it. So shipping it ourselves loses nothing on
updates, and it is the same model the packages already use for .NET (self-contained: a .NET security fix means a
release of ours). On Linux the distribution updates GStreamer, so Linux packages keep depending on it.

**What embedding gives:**
- **One MSI**, which works offline and with managed (GPO) deployment. There is no bootstrapper, and no dependence on
  GStreamer's download site at install time.
- **The tested version is the version that runs:** CI tests on Windows against exactly the GStreamer it ships, instead
  of whatever a user has.
- **No interference with other installs:** our copy is private to our folder, and another program's GStreamer is
  neither used nor touched.

**How:**
- **No Git LFS needed.** Nothing large goes into the repository. The release workflow downloads a **pinned** GStreamer
  1.28.x runtime (URL and SHA-256 in the workflow), copies the DLLs and plugins we need into the build, and WiX
  harvests them into the MSI (`<install dir>\gstreamer\`).
- **The plugin list:**
  - core and base: `coreelements`, `audioconvert`, `audioresample`, `audiomixer`, `audiotestsrc`, `volume`;
  - `opus`; `rtp`, `rtpmanager`, `srtp`, `dtls`;
  - `webrtc`, `nice`;
  - `audiomixmatrix`, `interleave`, `level`, `audiofx` (panorama, amplify);
  - for the load tool also its decoders: `playback`, `wavparse`, and whatever it needs for audio files.

  Plus their DLL dependencies (glib, gobject, libnice, openssl, libsrtp, opus, …), found by walking the imports, not
  by hand. The server's existing plugin check at start-up reports any element still missing.
- **The server finds it:** at start-up on Windows, if `<install dir>\gstreamer\` exists, the server sets
  `GST_PLUGIN_PATH` and `GST_PLUGIN_SYSTEM_PATH` (so only our plugins are loaded), `GST_REGISTRY` (a registry cache in
  `ProgramData`) and the DLL search path, for its own process only, before GStreamer initialises (code change 4).
  Without that folder it uses an installed GStreamer as today, which covers development machines.
- **Size:** about 40–80 MB.
- **Licence:** GStreamer and its libraries are LGPL (some dependencies have other permissive licences). We link
  dynamically, include the licence texts, and name the exact GStreamer version and where its source is, in the MSI's
  licence notes and `THIRD-PARTY-NOTICES`. Only plugins with compatible licences: no GPL-only plugins (`ugly`) are
  needed.

**Keeping it updated:**
- A scheduled workflow (weekly) checks GStreamer's releases and opens an issue when there is a newer 1.28.x (or a
  newer stable series), so we know to bump the pin and release.
- GStreamer's security advisories are announced on its website; the issue links them.

**Considered and not chosen:**
- **A WiX Burn bundle** (`setup.exe`) that downloads GStreamer's official installer when it is missing, checks its hash
  and installs it silently before our MSI. That is one download, but an `.exe`, dependent on GStreamer's site at
  install time, and the installed GStreamer still never updates itself.
- **Requiring GStreamer to be installed first,** as development does now: two installers, and any version.

### Signing

- **Windows:** unsigned, as decided. Expect SmartScreen's "Windows protected your PC" until the download builds
  reputation; the release notes and the README say how to get past it, and give the SHA-256 checksum to compare.
  Free option to look at later ([#17](https://github.com/JakDaniels/SpatialAudioServer/issues/17)): [SignPath Foundation](https://signpath.org/) signs open-source projects' releases at
  no cost (it needs the project to meet their criteria and the build to run in CI, which this plan has).
- **Linux:** packages can be GPG-signed for free. Needed only once there is an apt/yum repository (below); for files
  attached to a release, the checksums are enough.

## Versioning

- **Decided:** versions come from git tags, so a release is made by pushing a tag.
- **Done (2026-11-16, on `develop`):** [MinVer](https://github.com/adamralph/minver) 8.0.0 in
  `src/Directory.Build.props`, so the server, the load tool and the tests share one version:
  - tag `v0.2.0` → every build of that commit is `0.2.0`;
  - builds after it are `0.2.1-alpha.0.N` (N commits on);
  - before the first tag (`MinVerMinimumMajorMinor` 0.2): `0.2.0-alpha.0.N`, N counted from the first commit;
  - the SDK appends `+<commit>` to `InformationalVersion`, which `/api/status`, the viewer data and both programs'
    `--version` show (`Hosting/AppVersion.cs`);
  - workflows must check out the full history (`fetch-depth: 0`), or MinVer cannot find the tag and the height.
- **The first tag** `v0.2.0` goes on `main` when `develop` is first merged there.
- SemVer tags: `vMAJOR.MINOR.PATCH`, with optional pre-releases `v0.2.0-rc.1`. Until 1.0, minor versions may break
  compatibility; the release notes say when, especially for the control API that the OpenSim module uses.
- **Mapping to the package formats:**

  | Format | Rule | Example |
  |---|---|---|
  | deb | pre-releases sort before releases with `~` | `0.2.0~rc.1` |
  | rpm | `Version: 0.2.0`, `Release: 0.rc.1` (or `0.2.0~rc.1`, which newer rpm understands) | |
  | MSI | must be numeric, `major.minor.build` with each ≤ 255.255.65535, so pre-releases can't be expressed: either no MSI for pre-releases, or a build number counted up (`0.2.0` → MSI `0.2.0`, `0.2.1-alpha.0.7` → MSI `0.2.1` with the same upgrade code; to decide) | |

- The database already migrates itself on start-up, so upgrades need nothing else. Downgrades are not supported:
  a newer database refuses to open with an older server, which is already checked.

## Small code changes the packages need

Each of these is small, and each needs its own change and tests when the work starts:

1. **Configuration from `/etc` and `ProgramData`** (done 2026-11-16): `SAS_CONFIG_DIR` names a directory whose
   `appsettings.json` (and `appsettings.{Environment}.json`) apply on top of the program's own and below
   environment variables (`Hosting/ConfigDirectory.cs`). Packages: `/etc/spatialaudioserver`,
   `C:\ProgramData\SpatialAudioServer`.
2. **Data outside the program folder** (done, no code needed): `Storage:DatabasePath` and
   `Server:DevCertificatePath` take any path, and the cookie `keys/` follow the database. The Linux package sets
   both in `/etc/spatialaudioserver/environment` (below `/var/lib/spatialaudioserver/`).
3. **The command wrapper** (done): `packaging/linux/spatialaudioserver` runs `apikey …` and `admin …` as the
   service user (`runuser` from root, or directly as that user), with the service's environment file, so commands
   see the same configuration and database and the files stay the service's. With no command it prints usage:
   the server itself runs as the service. The files in `packaging/linux/`:
   - `spatialaudioserver.service`: the hardened unit, `EnvironmentFile=/etc/spatialaudioserver/environment`,
     `StateDirectory=spatialaudioserver` (so systemd creates `/var/lib/spatialaudioserver` for the service user);
   - `environment`: where things are (configuration directory, database, dev certificate), read by the unit and
     the wrapper alike;
   - `appsettings.json`: the commented template for `/etc/spatialaudioserver/appsettings.json`.
4. **Windows: the embedded GStreamer.** At start-up, if `<install dir>\gstreamer\` exists, set `GST_PLUGIN_PATH`,
   `GST_PLUGIN_SYSTEM_PATH`, `GST_REGISTRY` and the DLL search path for the process, before GStreamer initialises
   (server and load tool).
5. **Load tool voices:** it looks for `voices/` next to itself (`--voices` overrides). Fine as long as the package
   keeps them together.

## Branches and the GitHub workflows

**Decided (2026-11-16):**
- Development happens on a **`develop`** branch. CI runs **no tests** there: the tests are run locally while
  developing, as so far.
- **`main`** gets the full tests: a pull request from `develop` into `main` runs everything, both GStreamer versions,
  Windows and the 22.04 load tool check, and is merged when green.
- Packages can be built from `develop` as **drafts for testing**, never as release versions.
- Releases are tags on `main`. Once the repository is public, released packages also go into the GitHub Pages
  repositories.
- **No self-hosted runner.**

```
.github/workflows/ci.yml        pull requests into main (skipped for docs-only changes), and by hand
  test-1.28   ubuntu:26.04 container (GStreamer 1.28): build, full test suite (≈ 8 min)
  test-1.24   ubuntu:24.04 container (GStreamer 1.24, the floor): the same
  windows     windows-latest: fetch the pinned GStreamer (hash checked), embed it, full test suite against it
  load-22.04  ubuntu:22.04 container: build and install the load tool .deb, check it starts and finds its elements

.github/workflows/drafts.yml    by hand only (workflow_dispatch) on develop; choose linux, windows or both
  linux       dotnet publish -r linux-{x64,arm64} --self-contained → nfpm → server and load tool, .deb and .rpm
  windows     fetch and embed the pinned GStreamer, publish win-x64 → WiX → server MSI, load tool MSI
  draft       replace the files of the single pre-release "develop (draft)" with these, and SHA256SUMS

.github/workflows/release.yml   a pull request merged into main, or a tag v* pushed by hand (step 7)
  prepare     ci.yml green for the commit; after a merge: the next version and the tag, notes from CHANGELOG.md
  packages    packages.yml, as for drafts, with the release version: a draft GitHub release
  publish     with the pull request's label "publish" (once public, then the GitHub Pages apt/yum repositories too)
  changelog   after a merge: the version's section in CHANGELOG.md on develop

.github/workflows/packages.yml  reusable (workflow_call): linux, test, windows, shared by drafts.yml and release.yml

.github/workflows/gstreamer-watch.yml   weekly: an issue when GStreamer has a newer release than the Windows pin
```

- **gstreamer-watch.yml (done 2026-11-17):** Mondays (and by hand). `scripts/gstreamer-latest.sh` finds the newest
  stable GStreamer (even minor version) with an MSVC x86_64 installer and the SHA-256 GStreamer publishes beside it.
  When that is newer than `GSTREAMER_VERSION` in packages.yml, it opens the issue "GStreamer X.Y.Z for the Windows
  installers" with the two values to change (and, for a new series, a reminder to check the plugin list and try
  the MSIs by hand). One open issue at a time: older ones are closed as superseded, and all are closed once the
  pin is current. Linux packages use the distributions' GStreamer, so only the Windows pin is watched.

- **Running (2026-11-16):** `ci.yml` with `test-1.28` and `test-1.24` (GStreamer and .NET already installed
  in the prepared images). `develop` was the repository's default branch while private (`main` once public), so "Run workflow" ran it
  by hand on `develop`. Still to come: `load-22.04`, and a decision on `windows` ([#14](https://github.com/JakDaniels/SpatialAudioServer/issues/14)).
  - **Both pass the full suite** (183 tests) on the private repository's 2-core runners, about 12 minutes per job.
  - **GStreamer 1.24 is supported** (Ubuntu 24.04's 1.24.2). Getting there needed three changes, listed in PLAN.md
    "GStreamer version differences".
- **Versions** come from MinVer (see Versioning):
  - Draft builds are never on a release tag, so they get pre-release versions such as `0.2.1-alpha.0.14`, 14 commits
    after `v0.2.0`. A draft cannot be mistaken for a release.
  - In packages that shows as `0.2.1~alpha.0.14`, which sorts before the real `0.2.1`, so installing the release
    later is a clean upgrade.
  - Draft MSIs need a numeric version: `0.2.1.14`-style, with the commit count as the build number.
- **Drafts in one rolling pre-release, not in workflow artefacts:** a private repository's Actions storage on the free
  plan is about 500 MB, and one set of packages is about 400 MB, so artefacts would fill it. Release files do not
  count against it. The "develop (draft)" pre-release always holds just the latest drafts, visible only to the
  repository's collaborators while it is private. Its tag (`develop-draft`) is moved to the commit built, and it is
  marked as a pre-release, never as latest.
- **The gate on main:** while private, merging only when the pull request is green is a habit, not a rule. GitHub's
  branch protection, which would enforce it, needs a paid plan for private repositories. Once public it is free, and
  `main` should then require the `ci.yml` checks.
- **Releases do not re-run the tests:** a release tag is put on a `main` commit whose `ci.yml` run passed. The release
  workflow checks that run's result through the API and stops if it was not green.
- **arm64:**
  - The self-contained .NET publish cross-builds from an x64 runner, and nfpm does not care about the architecture.
  - Testing the arm64 packages on arm64 is by hand at first, on a Raspberry Pi 5 or an arm64 VM. Once public,
    GitHub's free arm64 Linux runners can run the suite there.
- **Caching:**
  - NuGet packages, and the pinned GStreamer download for Windows.
  - **Prepared container images** (done 2026-11-16): `ghcr.io/<owner>/sas-ci:1.28`, `:1.24` (GStreamer and the .NET
    SDK installed) and `:22.04` (GStreamer only, for the load tool `.deb`), from `.github/ci-images/Dockerfile`.
    `ci-images.yml` builds and pushes them when the Dockerfile changes, monthly (security updates, .NET patches) and
    by hand. They save about a minute per job (installing took ~40 s, .NET ~15 s) and a dependency on outside
    downloads mid-run. The packages stay private with the repository; ci.yml pulls them with its own token.
- **Load tool on 22.04:**
  - The `load-22.04` job installs the load tool `.deb` and checks it starts and finds its GStreamer elements. That
    needs `sas-loadtest --check`, a small addition.
  - Its media tests need a server, which is not supported on 22.04, so real load runs there stay manual, as now.

## Package repositories (GitHub Pages)

**Built (2026-11-17), changing the plan below:** the repositories live on **this repository's own Pages**,
`https://jakdaniels.github.io/SpatialAudioServer/` (`deb/`, `rpm/`, `key.asc`, an `index.html` with the set-up
commands), not in a separate `sas-packages` repository. A release's packages are 374 MB, so committing them to a
repository would grow its history by that much every release; instead `pages.yml` rebuilds the whole site from the
latest two published releases' files (not drafts or pre-releases; about 750 MB, under Pages' 1 GB) and deploys it
with GitHub's Pages deployment. No deploy key either.
- `scripts/build-repos.sh`: apt (`apt-ftparchive`; suite `stable`, component `main`, amd64 and arm64; `Release`
  signed as `InRelease` and `Release.gpg`) and dnf (`createrepo_c` per architecture; each rpm signed with
  `rpmsign`, `repomd.xml` signed; a `.repo` file with `gpgcheck` and `repo_gpgcheck`). It signs only with the key
  whose public half is `packaging/signing-key.asc`.
- `pages.yml` runs on main only (the github-pages environment deploys from main): a release published by hand
  relays to it, `release.yml`'s publish job starts it (a release published with a workflow's token starts no
  workflow), and "Run workflow" on main rebuilds by hand.
- **The key:** RSA 4096, no passphrase of its own, 5 years, made by the owner in a throwaway keyring; the private
  key is the Actions secret `SAS_SIGNING_KEY` and kept offline as a backup; the public key is committed.
- **Tested locally** (v0.2.0 packages, a throwaway key): `apt-get update` from the built repository accepts it and
  offers 0.2.0 for both packages and architectures, and refuses a tampered `InRelease` (BADSIG); every rpm
  verifies with the key imported and fails without it; both `repomd.xml` and `Release.gpg` signatures are good; the
  builder refuses a key other than the published one.
- **Owner's settings:** Settings → Pages → Source "GitHub Actions"; the secret `SAS_SIGNING_KEY`.
- **Live (2026-11-17):** the first run (by hand on main) deployed v0.2.0. Checked from outside: every file served,
  the live key is the project key (`FDAB7F3A2631D29B2E7241F6E90038A888E70DFC`, RSA 4096, expires 2031-11-16), a
  real `apt-get update` against the live address accepts the signed index and offers 0.2.0, `apt-get download`
  fetches a package checked against it, and both `repomd.xml` signatures are good. Not yet tried: a real
  `dnf install` from the repository on a Fedora machine.

The original plan, for reference:

**Decided:** apt and yum repositories on GitHub Pages, so users add the repository once and get updates through
`apt upgrade` / `dnf upgrade`. Releases stay on GitHub Releases too.

- **What it is:** static files on a Pages site, e.g. `https://<owner>.github.io/sas-packages/`, in its own repository
  (`sas-packages`), so the source repository's Pages stay free for docs:
  - `deb/`: an apt repository, `dists/stable/main/binary-{amd64,arm64}/` and `pool/`, made with `reprepro` (or
    `aptly`). One suite serves 22.04, 24.04 and 26.04, since the packages carry their own dependencies' versions.
  - `rpm/`: a yum/dnf repository, `x86_64/` and `aarch64/` with `repodata/`, made with `createrepo_c`.
- **Signing:** a GPG key made for the project; the private key as an Actions secret, the public key published at the
  repository's root. `reprepro` signs `Release`/`InRelease`; the `.rpm` files are signed with `rpmsign` and the
  `repodata` too. Free.
- **Release step:** after the GitHub release, the workflow checks out `sas-packages`, adds the new packages, prunes old
  ones, regenerates and signs the indexes, and pushes. Pages then serves it.
- **User set-up**, shown in the README:

  ```bash
  curl -fsSL https://<owner>.github.io/sas-packages/key.asc | sudo gpg --dearmor -o /usr/share/keyrings/sas.gpg
  echo "deb [signed-by=/usr/share/keyrings/sas.gpg] https://<owner>.github.io/sas-packages/deb stable main" |
    sudo tee /etc/apt/sources.list.d/sas.list
  sudo apt update && sudo apt install spatialaudioserver      # or spatialaudioserver-loadtest
  ```

  For dnf, a `.repo` file with `baseurl` and `gpgkey`.
- **Size limits:** a Pages site may be about **1 GB**, and a single file at most 100 MB. Self-contained packages are
  about 50 MB each, and a release adds about 8 of them (server and load tool, x64 and arm64, deb and rpm), so ~400 MB.
  So **the repository keeps the latest two releases**; older ones stay downloadable from GitHub Releases (apt's index
  cannot point at release download URLs, so the files have to live in the site itself). Should that become tight:
  - **framework-dependent packages:** a few MB each, depending on the distribution's `dotnet-runtime-10.0` (Fedora and
    Ubuntu 26.04 have it; 24.04 to check; 22.04 has only .NET 8, so the load tool would stay self-contained);
  - **a hosted service with a free open-source plan** (e.g. Cloudsmith).
- **While the repository is private:** a Pages site from a private repository needs a paid plan, and the packages
  should not be public before the project is. So until then, packages exist only as the "develop (draft)"
  pre-release and as draft releases (see the workflows above), and the Pages repository starts with the first
  public release.

## Actions minutes

**Decided:** the repository stays **private** until there is something concrete to release; it then becomes public.

| | Private (now) | Public (later) |
|---|---|---|
| GitHub-hosted runners (Linux x64/arm64, Windows) | 2000 minutes a month; Windows minutes count ×2 | free, unlimited |

**What the workflows above cost while private** (rough, with the prepared container images):

| Run | Minutes (Windows counted ×2) | How often |
|---|---|---|
| `ci.yml`: a pull request into main (1.28 and 1.24 suites ≈ 20, Windows suite ≈ 25 ×2, 22.04 check ≈ 3) | ≈ 75 | per merge; not again on main (2026-11-17: the merge has the pull request's files, already tested) |
| `drafts.yml`, Linux only | ≈ 5 | when you want something to test |
| `drafts.yml`, Linux and Windows | ≈ 25 | likewise |
| `release.yml` | ≈ 25 | per release |
| `gstreamer-watch.yml` | < 1 | weekly |
| pushes to `develop` | 0 | |

So with a merge every week or two and a few draft builds, about **200–450 minutes a month**: well inside 2000.
`develop` costs nothing, however often it is pushed.

**Ways to keep it down if needed:**
- path filters, so docs-only changes do not run `ci.yml`;
- `concurrency: cancel-in-progress`, so a pull request updated quickly runs only its latest commit;
- running the Windows suite only in `release.yml`, not on every pull request. That saves ≈ 50 a merge, but Windows
  problems are then found only at release time.

**Once public:** GitHub's runners are free, so `ci.yml` can also run on pull requests from others, and the arm64 suite
can run on GitHub's arm64 runners. The limits above stop mattering.

## Order of work

1. **Versioning:** MinVer, and a first tag (`v0.2.0`).
2. **Branches and CI:** a `develop` branch for day-to-day work; `ci.yml` (both GStreamer versions) on pull requests
   into `main`, and the prepared container images. The first run against 1.24 answers whether Ubuntu 24.04 is
   supported, before any packaging.
3. **Code changes 1–3** (configuration directory, data paths, command wrapper).
4. **`drafts.yml` with the `.deb` for Ubuntu 24.04 (x64)**, server and load tool: test by hand on 24.04; the load tool
   `.deb` also on 22.04.
   *Built and tested (2026-11-16):* `scripts/build-packages.sh [x64|arm64]` (the .NET publishes, then nfpm
   with `packaging/linux/nfpm-server.yaml` and `nfpm-loadtest.yaml`; also by hand, with nfpm installed) and
   `drafts.yml` (by hand on `develop`, nfpm 2.48.0 pinned by checksum). Packages are self-contained with
   invariant globalisation (no libicu dependency); file modes are normalised (0644, programs 0755). The load tool
   is `/usr/bin/sas-loadtest`, a launcher for the real program beside its voices.
   Release files are named with the SemVer version (`spatialaudioserver_0.2.1-alpha.0.5_amd64.deb`), since GitHub
   turns `~` in a file name into `.`; the version inside the package keeps its `~`.
   Tested by hand: on Ubuntu 24.04 (GStreamer 1.24.2) the server package installs (with the dependencies apt
   pulls in, mostly for `gstreamer1.0-plugins-bad`), the wrapper sets the admin password and creates keys through
   sudo, the service starts, the admin UI and web client work, an upgrade to the next draft restarts the service
   and keeps keys and settings, and remove (data kept) and purge (data and `/etc` gone, the `spatialaudio` user
   kept) behave. The load tool package installs and passes load runs on 24.04 and on 22.04 (GStreamer 1.20).
5. The rest of Linux: `.rpm` (Fedora), arm64, openSUSE.
   *Built and tested in CI (2026-11-16; no machines here for them):* the `.rpm` for Fedora comes from the
   same nfpm files (`overrides: rpm:` with Fedora's package names), and arm64 is
   cross-built on the x64 runner (`build-packages.sh arm64`: `aarch64` binaries checked). `drafts.yml` then
   installs the x64 packages in clean containers of Ubuntu 24.04 and 26.04, Debian 13 and Fedora (latest), and the load tool alone on Ubuntu 22.04, and tries them with
   `scripts/test-packages.sh`: the wrapper creates a key as the service user, the server starts as that user with
   the service's environment and reports no missing GStreamer elements, and a short load run passes. Not covered
   there: systemd itself (containers have none), SELinux on Fedora (the program under `/usr/lib` gets the
   `lib_t` label; to check on a real host whether the service needs a context of its own), and arm64 (no arm64
   runners while private). openSUSE: later, as its own variant.
   - **Passed every check** (second run): Ubuntu 24.04 and 26.04, Debian 13 and Fedora (server and load tool),
     Ubuntu 22.04 (load tool).
   - **RHEL 10 and rebuilds are not supported (decided 2026-11-16).** The first CI run on Rocky Linux 10 installed
     the packages (GStreamer 1.26.7, EPEL and CRB enabled), but the server stopped at start-up: "Missing GStreamer
     elements: webrtcbin". RHEL builds `gstreamer1-plugins-bad-free` without it, and EPEL does not add it. That
     would change if RHEL or EPEL ships webrtcbin, or with a GStreamer bundled into a RHEL package, as the Windows
     MSI will do (a larger job with its own security-update duty); not planned for now.
6. **Windows:** code change 4, the GStreamer selection and the server and load tool MSIs, tested on Windows.
   *Built and tested in CI (2026-11-17); to test by hand on Windows:*
   - **Code change 4** (`Media/EmbeddedGStreamer.cs`): with `gstreamer\bin` beside the program, the process uses
     only that GStreamer (plugin path, scanner, its own registry cache in the account's local app data, `PATH`,
     GstSharp's `NativeLoader.Configure`). Plus an optional `environment` file beside the program
     (`Hosting/ProgramEnvironment.cs`), the Windows counterpart of `/etc/spatialaudioserver/environment`: the MSI's
     points configuration and data at `%ProgramData%\SpatialAudioServer`, for the service and for an
     administrator's `SpatialAudioServer.exe apikey …` alike.
   - **GStreamer:** 1.28.8, pinned by SHA-256. From 1.28 GStreamer ships one Inno Setup `.exe` per architecture
     (517 MB for x64; no MSIs or merge modules any more). CI installs it silently into a scratch folder, and
     `packaging/windows/gather-gstreamer.py` copies the listed plugins, GStreamer's libraries and every DLL they
     import (walked with pefile; the Microsoft C++ runtime from System32 if GStreamer lacks it), the plugin
     scanner and the licence texts. The scratch install is then removed, so the test proves the MSIs use their own.
   - **MSIs** (WiX 6.0.2, `packaging/windows/`; WiX 7 needs its maintenance-fee EULA accepted):
     - server: the service as the virtual account `NT SERVICE\SpatialAudioServer` (decided), started on install;
       `C:\ProgramData\SpatialAudioServer` readable by it, `data\` writable by it, both closed to other users
       (SDDL with the service's SID, which is derived from its name); the site `appsettings.json` installed once and
       kept, with the media ports 40000–40999; the program on `PATH`; firewall rules for TCP 5000–5001 and UDP
       40000–40999 as an optional feature, off by default (decided); upgrades in place.
     - load tool: program, voices and GStreamer in `C:\Program Files\SpatialAudioServer Load Test\`, on `PATH`,
       with `sas-loadtest.cmd`.
     - MSI versions: `0.2.1-alpha.0.5` becomes `0.2.1.5` (MSI compares three parts; same-version upgrades allowed).
   - **Test** (`scripts/test-windows.ps1`, on the runner): install with the firewall rules, the service's account,
     an API key from the command, `/api/status` with every element, the load tool's run, uninstall (data kept).
     **Passed** on `windows-latest` (second run; the first stopped at MinVer's version before a restore): 26 plugins
     and 65 DLLs, 37 MB of GStreamer; MSIs 53 MB (server) and 58 MB (load tool); the service as its virtual account,
     both firewall rules, GStreamer 1.28.8 with no missing elements (the build's GStreamer removed first), the load
     run 100 % heard; about 6 minutes. WiX warns ICE61 (same-version upgrades, intended) and ICE60 (cosmetic).
     **Tested by hand** on Windows (2026-11-17): alpha.0.91 installed (feature tree, service account, embedded
     GStreamer used although another is installed, ProgramData ACLs, browser client, admin UI, firewall feature
     added later, load tool, uninstall keeping the data), then alpha.0.94 upgraded over it. Firewall rules named
     just `SpatialAudioServer` / `SpatialAudioServer.LoadTest` are Windows's own, made by "Allow access" on its
     prompt for a program run by hand; they are not the MSI's and an uninstall leaves them.
7. **The release workflow**, putting it all together, with draft GitHub releases while private.
   - **Done (2026-11-17): v0.2.0 released** with it (second attempt, see below): every job passed, 11 files named
     `0.2.0`, notes from CHANGELOG.md, the packaged changelog with the 0.2.0 section; published by hand.
     The build and the package tests moved from `drafts.yml` into the
     reusable `packages.yml` (inputs: the kind of release, its tag, the expected version, the notes' first part), so
     drafts and releases build the same way; `drafts.yml` now only calls it. `release.yml` runs on a pushed tag `v*`:
     - a `check` job stops unless the tag is `vMAJOR.MINOR.PATCH[-pre]`, its commit is on `main`, and `ci.yml` passed
       for that commit (a docs-only commit skips `ci.yml`: run it by hand on main, then re-run the release);
     - `packages.yml` then builds everything, checks the build's version equals the tag's, makes a **draft**
       release "SpatialAudioServer X.Y.Z" (a pre-release for `-rc.1` etc.), tests the packages from it (drafts
       cannot be found by tag, so by the release's API address) and adds the MSIs. A re-run replaces an
       unpublished draft; a published release is never touched.
   - **Release notes (open point 7, decided):** the annotated tag's message, under a line naming the version and
     commit, then the install instructions; edited on the draft if needed, then published by hand.
   - **Changelog (2026-11-17):** `CHANGELOG.md` has an `## Unreleased` section that gains a line with each commit
     changing the server or the load tool (what and why; nothing for CI, packaging, tests or docs: CLAUDE.md). At
     release the script offers that section as the tag message (just save), and afterwards, on develop, makes it
     the version's section and starts `Unreleased` again. The packages carry a changelog made at build time from
     the tags' messages (`scripts/changelog.sh`, the same in `build-windows.ps1`), so a release's packages hold its
     own notes: `/usr/share/doc/<package>/CHANGELOG.md` and each MSI's `docs` folder; drafts show the Unreleased
     entries on top.
   - **The first attempt at v0.2.0 (2026-11-17)** stopped at "Make the release": the `~` to `-` rename tried to
     rename release files (no `~`) to themselves. Fixed; the tag was deleted (nothing had been published) and is
     made again after the fix reaches main.
   - **One CI run per release (2026-11-17):** `ci.yml` no longer runs on pushes to main. A merge from develop,
     when main has nothing of its own, has exactly the files of the pull request's head, which the pull request's
     run tested; `release.yml` and the script accept that run (checked with `git diff` between the two commits).
     If the files differ (a commit made on main) or nothing was tested (a docs-only pull request), CI runs on main.
   - **Making a release (automatic since 2026-11-17, after v0.2.0):** merging a pull request from `develop` into
     `main` makes the release. `release.yml` (event: pull request closed and merged) checks the pull request's
     `ci.yml` run as above, takes the next version (`scripts/next-version.sh`: patch, or minor / major by the
     pull request's label `minor` / `major`), tags the merge commit with the `## Unreleased` entries of CHANGELOG.md
     as the message, builds and tests everything into a draft release, publishes it when the pull request has the
     label `publish`, and on develop merges main back in and commits the version's section to CHANGELOG.md (pull
     develop afterwards). The merge back matters: the tag is on main's merge commit, and without it develop's
     history lacks the tag, so MinVer versioned drafts after v0.2.0 as `0.2.0-alpha.N`, below the release (found
     2026-11-17; main was merged into develop by hand once, and `make-release.sh` does the same for hand-made tags). No
     Unreleased entries (only CI, packaging or docs changes merged): no release. By hand still works: push a tag
     (`scripts/make-release.sh`), e.g. for a pre-release `v0.3.0-rc.1` or after a docs-only merge.
   - **README badges for releases:** a row of static "download" badges per platform (Ubuntu/Debian, Fedora,
     Windows), each linking to the latest release's files, plus the release workflow's status badge. GitHub's own
     badges are per workflow, not per job, so status per platform would need the workflow to write its own SVGs
     (e.g. to a `badges` branch); not wanted for drafts, whose run page already shows each platform.
8. **Going public:** make the repository public, require the `ci.yml` checks on `main`, the first public release,
   and the GitHub Pages apt/yum repository.
   - **Prepared (2026-11-17):** the history of both repositories checked (no keys, tokens or passwords in any
     version; only private LAN addresses in examples; the commit emails stay, decided); `SECURITY.md`,
     `CONTRIBUTING.md` and an Installing section in the README added; `ci.yml` also runs on pull requests into
     `develop` (contributors' pull requests). Decided: `main` becomes the default branch, and os-webrtc-sas goes
     public at the same time. The GitHub Pages repositories come afterwards, as their own piece of work.
   - **The settings (repository owner, in the web UI), in this order:**
     1. Merge `develop` into `main` by pull request (no release: nothing under Unreleased), so `main` has these
        files and `gstreamer-watch.yml` (scheduled workflows run from the default branch).
     2. Optional, before going public: ask GitHub Support to purge the commits from before the history rewrite
        (still reachable through the merged pull requests).
     3. Both repositories: Settings → General → Danger Zone → Change visibility → Public.
     4. SpatialAudioServer: Settings → General → Default branch → `main`.
     5. Settings → Code security → Private vulnerability reporting → Enable (SECURITY.md relies on it).
     6. Settings → Rules → Rulesets → New branch ruleset for `main`: require a pull request, require the status
        checks `test-1.28` and `test-1.24`, block force pushes. Not for `develop` beyond blocking force pushes:
        release.yml pushes the CHANGELOG commit there.
     7. The CI images: the package `sas-ci` (owner's Packages tab) → Package settings → Change visibility →
        Public, or pull requests from forks cannot pull them.
     8. Settings → Actions → General → "Fork pull request workflows from outside collaborators": require approval
        for all outside collaborators.
   - **Done (2026-11-17):** both repositories are public, `main` is the default branch, private vulnerability
     reporting is on, the `sas-ci` images pull without a login. Rulesets: `main` (no deletion or force push, a
     pull request with no approvals needed, merge commits only, checks `test-1.28` and `test-1.24` from GitHub
     Actions, no bypass) and `develop` (no deletion or force push only). Merge commits only, because the release
     check matches the pull request's CI run through the merge's second parent. Outside contributors' workflows
     need approval (set by the owner; not visible to a collaborator's API). `drafts.yml` fails visibly when
     started from a branch other than develop. Left: the optional GitHub Support purge, and the GitHub Pages
     apt/yum repositories as their own piece of work.
   - **History rewritten (done 2026-11-17):** the self-contained load tool binary
     (`scripts/loadtest-linux-x64/SpatialAudioServer.LoadTest`, about 950 MB over 19 versions, committed until
     CI built it) was removed from every commit with `git filter-repo`, then `develop`, `main`, `v0.2.0` and
     `develop-draft` were force-pushed. Every branch and tag kept the same files; one commit that only rebuilt the
     binary ("Load tool: rebuild with --npc") became empty and was dropped, so the commit IDs changed (main
     55b5aa0 → e4d0a93; the v0.2.0 notes were updated). A clone went from 137 MB to under 4 MB. Other clones must
     be re-cloned or reset (`git fetch --force --tags origin && git reset --hard origin/develop`). The old commits
     stay reachable on GitHub only through the merged pull requests' refs, which clones do not fetch; GitHub
     Support can purge them before going public if wanted.

The planned `acousticspace` GStreamer plugin (docs/gstreamer_acousticspace_design.md, "Development and Packaging")
gets its own repository and its own packages the same way: `.deb`/`.rpm` into the system plugin directory, recommended
(not required) by the server package; on Windows, its DLL inside the server MSI's embedded GStreamer.

The OpenSim add-on (`os-webrtc-sas`) is not part of this: its DLL is built against a particular OpenSim version, so
it gets its own release (a zip per OpenSim version) in its own repository.

## Open points

1. ~~The 1.24 test result~~: passed (2026-11-16), so Ubuntu 24.04 stays a target (RHEL 10 is out for another reason: step 5).
2. **openSUSE:** a separate `.rpm` variant, or capability dependencies, or not at first.
3. **MSI versions for pre-releases:** none, or a counted build number.
4. **Windows service account:** `LocalService`, or a virtual account (`NT SERVICE\SpatialAudioServer`), which can
   own `ProgramData\SpatialAudioServer` alone.
5. **Firewall rules on Windows:** add them by default, or leave them as an installer option.
6. **Package names:** `spatialaudioserver` and `spatialaudioserver-loadtest`, with the command `sas-loadtest`.
7. ~~**Release notes:**~~ the tag message, edited on the draft (step 7).
8. **The repository name and owner for the Pages packages** (`sas-packages`), and whether to try framework-dependent
   packages to save space.
