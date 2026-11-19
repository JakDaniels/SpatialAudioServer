# Running SpatialAudioServer from the packages (Linux and Windows)

Each release has packages on the [Releases page](https://github.com/JakDaniels/SpatialAudioServer/releases): .deb
and .rpm for Linux (x64 and arm64), and installers (MSI) for Windows x64. They need no .NET: it is built in. They
install the server as a service, with its settings and data kept apart from the program so upgrades keep them. To
build and run from the repository instead, see [running-from-source.md](running-from-source.md).

| | Linux packages | Windows installer |
|---|---|---|
| Systems | Ubuntu 24.04 or later, Debian 13 or later, Fedora | Windows 10 or later, Windows Server 2019 or later |
| GStreamer | from the distribution (1.24 or newer), pulled in as dependencies | **built in** (in the program folder); no separate install |
| Service | systemd unit `spatialaudioserver`, user `spatialaudio`; installed and enabled, **not started** | Windows service `SpatialAudioServer`, account `NT SERVICE\SpatialAudioServer`; **started** at install, automatic start |
| Program | `/usr/lib/spatialaudioserver/` | `C:\Program Files\SpatialAudioServer\` (added to `PATH`) |
| Site settings | `/etc/spatialaudioserver/appsettings.json` | `C:\ProgramData\SpatialAudioServer\appsettings.json` |
| Data (rooms, keys, history, dev certificate) | `/var/lib/spatialaudioserver/` | `C:\ProgramData\SpatialAudioServer\data\` |
| Logs | `journalctl -u spatialaudioserver -f` | Event Viewer, Windows Logs, Application (warnings and errors) |
| Documentation | `/usr/share/doc/spatialaudioserver/` | `C:\Program Files\SpatialAudioServer\docs\` |
| Firewall | not touched ([below](#firewall-linux)) | optional installer feature, **off by default** ([below](#firewall-windows)) |

## Linux

### Installing

**Ubuntu / Debian**, from the signed package repository, so updates come with `apt upgrade`:

```bash
curl -fsSL https://jakdaniels.github.io/SpatialAudioServer/key.asc | sudo gpg --dearmor -o /usr/share/keyrings/spatialaudioserver.gpg
echo "deb [signed-by=/usr/share/keyrings/spatialaudioserver.gpg] https://jakdaniels.github.io/SpatialAudioServer/deb stable main" |
  sudo tee /etc/apt/sources.list.d/spatialaudioserver.list
sudo apt update && sudo apt install spatialaudioserver
```

**Fedora**, from the signed package repository (updates with `dnf upgrade`):

```bash
sudo curl -fsSL -o /etc/yum.repos.d/spatialaudioserver.repo https://jakdaniels.github.io/SpatialAudioServer/rpm/spatialaudioserver.repo
sudo dnf install spatialaudioserver
```

Or install a downloaded file: `sudo apt install ./spatialaudioserver_<version>_amd64.deb` /
`sudo dnf install ./spatialaudioserver-<version>-1.x86_64.rpm`. The repositories, with the signing key's
fingerprint, are described at https://jakdaniels.github.io/SpatialAudioServer/; they hold the latest two releases,
and every release stays on the Releases page.

The package recommends the [acousticmultimixer](https://github.com/JakDaniels/acousticmultimixer) GStreamer
plugin (`gstreamer1.0-acousticmultimixer`, Fedora `gstreamer1-acousticmultimixer`), which the `acoustic` mix engine
needs: installing from the repositories brings it in. With a downloaded file, install the plugin's package from its
Releases page too, or the server simply does not offer that engine.

### First-time setup

```bash
sudo spatialaudioserver admin set-password          # the admin UI's password
sudo spatialaudioserver apikey create <label>       # a control-API key, e.g. for the OpenSim add-on: keep the sas_… value
sudoedit /etc/spatialaudioserver/appsettings.json   # optional: ports, certificate, allowed networks
sudo systemctl start spatialaudioserver
```

The `spatialaudioserver` command runs the administration commands (`apikey create|list|revoke`,
`admin set-password|clear-password`) as the service's user, with the service's settings, so they work on the same
database. It needs `sudo`. The server itself only runs as the service.

### Day to day

- Start, stop, restart: `sudo systemctl start|stop|restart spatialaudioserver`. Restart after changing the settings.
- Logs: `journalctl -u spatialaudioserver -f`.
- Local changes to the service unit (e.g. `AmbientCapabilities=CAP_NET_BIND_SERVICE` to use port 443):
  `sudo systemctl edit spatialaudioserver`.

### Firewall (Linux)

The packages do not change the firewall. They set the WebRTC media range to UDP 40000–40999 in
`/etc/spatialaudioserver/appsettings.json`. Open that range and TCP 5000–5001; TCP 40000–40999 is optional (ICE-TCP,
for clients whose networks block UDP).

```bash
# Ubuntu / Debian with ufw
sudo ufw allow 5000:5001/tcp
sudo ufw allow 40000:40999/udp
sudo ufw allow 40000:40999/tcp                      # optional: ICE-TCP

# Fedora with firewalld
sudo firewall-cmd --permanent --add-port=5000-5001/tcp --add-port=40000-40999/udp --add-port=40000-40999/tcp
sudo firewall-cmd --reload
```

Installs from 0.2.1 or earlier have no media range in their settings file, because upgrades keep that file. Add
`"Media": { "Network": { "MinPort": 40000, "MaxPort": 40999 } }` to it to get one.

### Upgrading and removing

- **Upgrade:** `sudo apt upgrade` / `sudo dnf upgrade` (or install a newer file). A running service is restarted;
  the settings and data are kept. If you changed `appsettings.json`, the package's new version of it is not
  installed over yours (apt asks you which to keep).
- **Remove:** `sudo apt remove spatialaudioserver` / `sudo dnf remove spatialaudioserver` stops and removes the
  service and the program, and keeps `/var/lib/spatialaudioserver` (rooms, keys, history) and the `spatialaudio`
  user, so a reinstall carries on. apt also keeps `/etc/spatialaudioserver`; dnf keeps a changed settings file as
  `appsettings.json.rpmsave`.
- **Remove everything** (Ubuntu / Debian): `sudo apt purge spatialaudioserver` also deletes the settings and the
  data. On Fedora, delete `/etc/spatialaudioserver` and `/var/lib/spatialaudioserver` by hand.

## Windows

### Installing

Download `SpatialAudioServer-<version>-x64.msi` from the Releases page and run it. It is not signed yet, so
Windows SmartScreen warns: choose "More info", then "Run anyway".

The installer carries its own copy of GStreamer, with only the libraries and plugins the server needs, in
`C:\Program Files\SpatialAudioServer\gstreamer\`, including the acousticmultimixer plugin for the `acoustic` mix
engine. Nothing else needs to be installed, and an existing GStreamer installation is neither needed nor used.

On the **Custom Setup** page, the features are:

- **SpatialAudioServer** (always installed): the program, its GStreamer, the documentation and the Windows service.
- **Windows Firewall rules** (not selected by default): click it and choose "Will be installed on local hard
  drive" to have the installer open the ports ([below](#firewall-windows)).

The service is installed and started straight away, and starts with Windows.

**Unattended install**, from an administrator command prompt:

```powershell
msiexec /i SpatialAudioServer-<version>-x64.msi /qn                    # server and service only
msiexec /i SpatialAudioServer-<version>-x64.msi /qn ADDLOCAL=ALL       # with the firewall rules
```

### First-time setup

From a command prompt or PowerShell opened with **"Run as administrator"** (the data folder is only writable by
administrators and the service):

```powershell
SpatialAudioServer admin set-password          # the admin UI's password
SpatialAudioServer apikey create <label>       # a control-API key: keep the sas_… value
notepad C:\ProgramData\SpatialAudioServer\appsettings.json    # optional: ports, certificate, allowed networks
Restart-Service SpatialAudioServer             # after changing the settings
```

`SpatialAudioServer.exe` reads the `environment` file beside it, so these commands use the same settings and database
as the service. Run with no arguments it would try to start a second server beside the service; use the service.

### Day to day

- Start, stop, restart: `Start-Service`, `Stop-Service`, `Restart-Service SpatialAudioServer` (administrator), or
  the Services app. If it stops unexpectedly, Windows restarts it after 10 seconds.
- Logs: Event Viewer, Windows Logs, Application, source SpatialAudioServer. Only warnings and errors go there by
  default; for everything, add `"Logging": { "EventLog": { "LogLevel": { "Default": "Information" } } }` to the
  settings. The admin UI's event log shows joins, leaves and connection problems either way.

### Firewall (Windows)

With the **Windows Firewall rules** feature, the installer adds two inbound rules, for all network profiles
(Domain, Private and Public), and removes them on uninstall:

| Rule | Opens |
|---|---|
| SpatialAudioServer (HTTP, HTTPS) | TCP 5000, 5001 |
| SpatialAudioServer (WebRTC media) | UDP 40000–40999 |

The installed `appsettings.json` sets the same media range. If you change the ports in the settings, change the
rules to match by hand (`wf.msc`, or `Set-NetFirewallRule`).

The rules do not cover ICE-TCP (TCP 40000–40999), which only clients whose networks block UDP need. To allow it,
add a rule yourself (the uninstaller does not remove it):

```powershell
New-NetFirewallRule -DisplayName "SpatialAudioServer (WebRTC media, TCP)" -Direction Inbound -Protocol TCP -LocalPort 40000-40999 -Action Allow
```

Without the feature, add rules yourself. A service gets no "allow this app" prompt from Windows Defender Firewall,
so without rules nobody else can connect:

```powershell
New-NetFirewallRule -DisplayName "SpatialAudioServer (HTTP, HTTPS)" -Direction Inbound -Protocol TCP -LocalPort 5000,5001 -Action Allow
New-NetFirewallRule -DisplayName "SpatialAudioServer (WebRTC media)" -Direction Inbound -Protocol UDP -LocalPort 40000-40999 -Action Allow
```

To add the feature to an existing install, use "Change" on SpatialAudioServer in Control Panel, Programs and
Features, or run a newer installer and select it.

### Upgrading and removing

- **Upgrade:** run the newer MSI over the installed one. It replaces the program and its GStreamer and keeps
  `C:\ProgramData\SpatialAudioServer` (settings and data) and the firewall rules.
- **Uninstall** (Settings, Apps, or Programs and Features): removes the service, the program, its `PATH` entry and the
  installer's firewall rules. `C:\ProgramData\SpatialAudioServer` is kept, so a reinstall carries on with the same
  rooms, keys and settings. Delete that folder by hand for a clean removal.

## After installing

- Open the admin UI at `https://<host>:5001/admin/`, or the test web client at `https://<host>:5001/client/`
  (test mode needs an API key). Until a real certificate is configured, the browser warns about the self-signed
  one: accept it once per browser.
- All the settings, and the ports to open: [configuration.md](configuration.md). On an internet-facing host (a real
  certificate, NAT, TURN, hardening): [internet.md](internet.md).
- For Second Life / OpenSim viewers, add the region module [os-webrtc-sas](https://github.com/JakDaniels/os-webrtc-sas)
  to OpenSim, with an API key from above.

## Load tool

The load tool fills rooms with fake WebRTC clients, or adds talking bots to a room for listening tests. It has its
own packages. Run it on a different machine from the server: the fake clients cost about as much CPU as the server.

- **Linux** (also Ubuntu 22.04), from the same repositories: `sudo apt install spatialaudioserver-loadtest` /
  `sudo dnf install spatialaudioserver-loadtest`. Command: `sas-loadtest`.
- **Windows:** `SpatialAudioServer-LoadTest-<version>-x64.msi`, with its own GStreamer like the server's. It puts
  `sas-loadtest` (and `SpatialAudioServer.LoadTest.exe`) on `PATH`; open a new terminal after installing.

```bash
export SAS_API_KEY=sas_...                     # PowerShell: $env:SAS_API_KEY = "sas_..."
sas-loadtest --server http://<host>:5000 --participants 2 --duration 10
sas-loadtest --server http://<host>:5000 --room <room id> --audio voices --spatial
sas-loadtest --help
```

The pre-rendered voices (`--audio voices`) are installed with it; `--audio flite` works on Linux only.
