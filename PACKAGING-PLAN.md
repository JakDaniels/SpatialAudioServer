# Packaging and releases: plan

Status: **plan only, nothing built yet** (2026-11-16). Decisions so far are marked **Decided**; open points are
listed at the end. [PLAN.md](PLAN.md) stays the specification of the server itself.

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

**Decided:** any `.deb`- or `.rpm`-based distribution that ships **GStreamer 1.24 or later**. The first target to
test is **Ubuntu 24.04** (GStreamer 1.24.2): a `.deb` for it comes first.

| Family | Examples with GStreamer ≥ 1.24 (to confirm per release) | Package |
|---|---|---|
| Debian / Ubuntu | Ubuntu 24.04, 25.x, 26.04; Debian 13 | `.deb` |
| Fedora / RHEL | Fedora 40 and later; RHEL / Rocky / Alma 10 (to check) | `.rpm` |
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

| | Debian / Ubuntu | Fedora / RHEL | openSUSE |
|---|---|---|---|
| core and base | `gstreamer1.0-plugins-base` (≥ 1.24) | `gstreamer1-plugins-base` | `gstreamer-plugins-base` |
| webrtcbin, audiomixmatrix, … | `gstreamer1.0-plugins-bad` | `gstreamer1-plugins-bad-free` | `gstreamer-plugins-bad` |
| rtpopuspay, … | `gstreamer1.0-plugins-good` | `gstreamer1-plugins-good` | `gstreamer-plugins-good` |
| ICE (libnice) | `gstreamer1.0-nice` | `libnice-gstreamer1` | `gstreamer-plugin-libnice` (to check) |
| other | `libsqlite3-0`, `libicu*` (or invariant globalisation) | `sqlite-libs`, `libicu` | `libsqlite3-0`, `libicu` |

The names differ between Fedora/RHEL and openSUSE, so either build **two `.rpm` variants**, or use capability
dependencies that both provide. Fedora generates `gstreamer1(element-webrtcbin)` provides automatically; whether
openSUSE has an equivalent is to check. **Proposal:** a Fedora/RHEL `.rpm` first, openSUSE afterwards. The binding
calls the GStreamer libraries directly, so GObject Introspection typelibs (`gir1.2-*`) are not needed at run time
(to confirm in the 1.24 test).

**Load tool package `spatialaudioserver-loadtest`:**
- the self-contained load tool in `/usr/lib/spatialaudioserver-loadtest/`, with `/usr/bin/sas-loadtest`;
- the pre-rendered voices (`voices/`, 2.3 MB);
- the same GStreamer dependencies; `libflite1` (Fedora: `flite`) only as *Recommends*, since it is needed only for
  `--audio flite`;
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
  Free option to look at later: [SignPath Foundation](https://signpath.org/) signs open-source projects' releases at
  no cost (it needs the project to meet their criteria and the build to run in CI, which this plan has).
- **Linux:** packages can be GPG-signed for free. Needed only once there is an apt/yum repository (below); for files
  attached to a release, the checksums are enough.

## Versioning

- **Decided:** versions come from git tags, so a release is made by pushing a tag.
- **Proposal:** [MinVer](https://github.com/adamralph/minver), one package reference in `Directory.Build.props`, with
  no code changes:
  - tag `v0.2.0` → every build of that commit is `0.2.0`;
  - builds after it are `0.2.1-alpha.0.N` (N commits on);
  - the commit hash goes into `InformationalVersion`, as today (`/api/status` and the OpenSim module show it).
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

1. **Configuration from `/etc` and `ProgramData`:** the program looks for `appsettings.json` next to itself today.
   The packages need it in `/etc/spatialaudioserver/` and `C:\ProgramData\SpatialAudioServer\`, e.g. an environment
   variable or command-line switch naming the configuration directory, set by the service unit. Environment
   variables already work for single settings.
2. **Data outside the program folder:** `Storage:DatabasePath` already takes a path, and the cookie `keys/` and the
   dev certificate follow it. Packages set it to `/var/lib/spatialaudioserver/spatialaudio.db` (Windows:
   `ProgramData`). The dev certificate path needs the same treatment.
3. **The command wrapper:** `spatialaudioserver apikey …` run by an administrator must use the service's
   configuration and database, and run as the service user (so files stay owned by it). The wrapper does
   `sudo -u spatialaudio …` with the same environment the unit sets.
4. **Windows: the embedded GStreamer.** At start-up, if `<install dir>\gstreamer\` exists, set `GST_PLUGIN_PATH`,
   `GST_PLUGIN_SYSTEM_PATH`, `GST_REGISTRY` and the DLL search path for the process, before GStreamer initialises
   (server and load tool).
5. **Load tool voices:** it looks for `voices/` next to itself (`--voices` overrides). Fine as long as the package
   keeps them together.

## The GitHub workflow

```
.github/workflows/ci.yml        on every push and pull request
  test-1.28   container ubuntu:26.04: install GStreamer + .NET, build, full test suite (≈ 8 min)
  test-1.24   container ubuntu:24.04: the same against GStreamer 1.24 (the floor)

.github/workflows/release.yml   on a tag v*
  test        both test jobs above (a release only from green tests)
  linux       matrix x64 / arm64: dotnet publish -r linux-{x64,arm64} --self-contained (cross-publish from x64)
              → nfpm → server and load tool, .deb and .rpm
  windows     windows-latest: fetch the pinned GStreamer (hash checked), pick the plugins and their DLLs,
              publish win-x64 → full test suite against that embedded GStreamer → WiX → server MSI, load tool MSI
  release     gather the artefacts, SHA256SUMS, create the GitHub release from the tag (notes from the tag message
              or a CHANGELOG section)
```

- **arm64:** the self-contained .NET publish cross-builds from an x64 runner, and nfpm does not care about the
  architecture. The arm64 packages still need testing on arm64: GitHub has arm64 Linux runners, or use a Raspberry
  Pi 5 or arm64 VM by hand at first. The media tests need GStreamer on the test machine.
- **Caching:** NuGet packages, and the pinned GStreamer download for Windows.
- **Windows tests:** the release workflow runs the full suite on `windows-latest` against the embedded GStreamer, so
  what ships is what was tested; `ci.yml` may run it too once it is quick enough.
- **GStreamer watch:** `.github/workflows/gstreamer-watch.yml`, weekly, opens an issue when GStreamer has a newer
  release than the Windows pin.
- **Later:** an apt and yum repository (GitHub Pages with `reprepro`/`createrepo_c`, or a hosted service), so updates
  come through `apt upgrade` / `dnf upgrade`; that is when GPG signing becomes necessary.

## Order of work

1. **Versioning:** MinVer, and a first tag (`v0.2.0`).
2. **CI:** the two test jobs (1.28 and 1.24). The 1.24 job answers whether Ubuntu 24.04 is supported, before any
   packaging.
3. **Code changes 1–3** (configuration directory, data paths, command wrapper).
4. **`.deb` for Ubuntu 24.04 (x64)**, server and load tool: test by hand on 24.04.
5. The rest of Linux: `.rpm` (Fedora/RHEL), arm64, openSUSE.
6. **Windows:** code change 4, the GStreamer selection and the server and load tool MSIs, tested on Windows.
7. **The release workflow**, putting it all together; then the first public release.

The OpenSim add-on (`os-webrtc-sas`) is not part of this: its DLL is built against a particular OpenSim version, so
it gets its own release (a zip per OpenSim version) in its own repository.

## Open points

1. **The 1.24 test result** (order step 2) decides Ubuntu 24.04 and RHEL/Rocky 10.
2. **openSUSE:** a separate `.rpm` variant, or capability dependencies, or not at first.
3. **MSI versions for pre-releases:** none, or a counted build number.
4. **Windows service account:** `LocalService`, or a virtual account (`NT SERVICE\SpatialAudioServer`), which can
   own `ProgramData\SpatialAudioServer` alone.
5. **Firewall rules on Windows:** add them by default, or leave them as an installer option.
6. **Package names:** `spatialaudioserver` and `spatialaudioserver-loadtest`, with the command `sas-loadtest`.
7. **Release notes:** a `CHANGELOG.md` kept by hand, or notes from the tag message.
