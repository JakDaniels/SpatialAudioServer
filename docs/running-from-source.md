# Running SpatialAudioServer from source (Linux and Windows)

This guide builds and runs the server from the repository. To install a released package instead (Linux .deb/.rpm,
or the Windows installer with its own GStreamer and service), see
[running-from-prebuilt-binaries.md](running-from-prebuilt-binaries.md).

The server is one .NET 10 program that uses the GStreamer native libraries installed on the machine. It serves the
control API, signalling, the test client (`/client/`) and the admin UI (`/admin/`) on the same ports.

## 1. Prerequisites

| | Linux (Ubuntu 24.04 / 26.04) | Windows 10/11 x64 |
|---|---|---|
| .NET | .NET 10 SDK (to build) or runtime (to run a published build) | same |
| GStreamer | 1.24 or newer (developed on 1.28.2) with plugins base, good, bad and libnice | official 1.28 installer |

### Linux

```bash
scripts/setup-ubuntu.sh        # GStreamer + plugins, .NET SDK, sqlite3; prints a plugin check
```

### Windows

1. **.NET 10 SDK:** `winget install Microsoft.DotNet.SDK.10` (or the installer from https://dotnet.microsoft.com/download).
2. **GStreamer:** download the **MSVC 64-bit runtime** installer for 1.28.x from
   https://gstreamer.freedesktop.org/download/ (since 1.28 an `.exe`, `gstreamer-1.0-msvc-x86_64-1.28.x.exe`).
   Choose the **full / complete** installation so gst-plugins-bad (webrtcbin, DTLS, SRTP, SCTP) and libnice are
   included. The development installer is not needed.
3. **Check it** in a new terminal (the installer adds its `bin` folder to `PATH`; if not, run it from that folder):

   ```powershell
   gst-inspect-1.0 --version
   foreach ($e in "webrtcbin","nicesrc","dtlssrtpenc","srtpenc","sctpenc","opusenc","opusdec","audiomixer","audiomixmatrix","audiointerleave","input-selector") {
     if (gst-inspect-1.0 $e 2>$null) { "ok      $e" } else { "MISSING $e" }
   }
   ```

   The binding finds GStreamer by itself (registry, the installer's environment variables, the usual install folders,
   `PATH`). The server checks every element it needs at start-up and refuses to start, naming what is missing.

## 2. Build and start

From the repository root:

```bash
dotnet run --project src/SpatialAudioServer                     # HTTP 5000, HTTPS 5001
```

Or publish once and run the result from anywhere (it uses its own folder for `wwwroot`, `appsettings.json` and `data/`):

```bash
dotnet publish src/SpatialAudioServer -c Release -o ~/sas            # Windows: -o C:\sas
~/sas/SpatialAudioServer                                             # Windows: C:\sas\SpatialAudioServer.exe
```

Stop it with **Ctrl+C** (or SIGTERM / a service stop). Everyone's session is closed in the history as
`server-shutdown` and all room pipelines stop; this takes about a second.

### First-time setup (once per database)

```bash
dotnet run --project src/SpatialAudioServer -- apikey create region-module   # prints a sas_… key: keep it
# optionally with an expiry: apikey create visitor-tool --expires 90d (or --expires 2027-01-31)
dotnet run --project src/SpatialAudioServer -- admin set-password            # admin UI password
```

With a published build, run `SpatialAudioServer apikey create …` / `admin set-password` from its folder instead.
For testing, a fixed key can be set in configuration instead (`Auth:ApiKeys`, see [configuration.md](configuration.md)).

### Testing switch

`--delete-existing-rooms` deletes every stored room at start-up (a clean, idle server for test runs).

## 3. Configuration and network

All settings, where they come from and which ports to open: [configuration.md](configuration.md). A build from
source reads `appsettings.json` beside the program (`dotnet run` also reads the git-ignored
`appsettings.Development.json`) and keeps its data in `data/` there. On an internet-facing host, also read
[internet.md](internet.md).

**Firewall** (TCP 5000/5001, and UDP for WebRTC media; ICE takes any free UDP port unless `Media:Network:MinPort` /
`MaxPort` fix a range):

- Ubuntu with ufw: `sudo ufw allow 5000:5001/tcp` and allow UDP from the LAN (e.g. `sudo ufw allow from 10.0.0.0/8 proto udp`).
- Windows: allow the program when Windows Defender Firewall asks on first start, for **Private** networks
  (with `dotnet run` it is `src\SpatialAudioServer\bin\Debug\net10.0\SpatialAudioServer.exe`), or add a rule:
  `New-NetFirewallRule -DisplayName SpatialAudioServer -Direction Inbound -Program C:\sas\SpatialAudioServer.exe -Action Allow -Profile Private`.
  Check the network is set to Private, not Public.

## 4. Running as a service

### Linux (systemd)

```ini
# /etc/systemd/system/spatialaudio.service
[Unit]
Description=SpatialAudioServer
After=network-online.target
Wants=network-online.target

[Service]
Type=notify
User=spatialaudio
WorkingDirectory=/opt/sas
ExecStart=/opt/sas/SpatialAudioServer
Environment=DOTNET_ENVIRONMENT=Production
TimeoutStopSec=30
Restart=on-failure

[Install]
WantedBy=multi-user.target
```

`sudo systemctl enable --now spatialaudio`; logs with `journalctl -u spatialaudio -f`. The `spatialaudio` user owns
`/opt/sas/data`.

### Windows service

```powershell
sc.exe create SpatialAudioServer binPath= "C:\sas\SpatialAudioServer.exe" start= auto
sc.exe start SpatialAudioServer
```

The service account needs GStreamer on its `PATH` only if the binding cannot find it otherwise (it normally does through
the installer's registry entries). Logs go to the Windows Event Log (Application). Run `apikey create` / `admin set-password`
from the same folder before starting it, so they use the same `data\spatialaudio.db`.

## 5. Load tool

The load tool needs GStreamer too. Its packages are the easiest way to get it
([running-from-prebuilt-binaries.md](running-from-prebuilt-binaries.md#load-tool)). From source: on Linux use the
self-contained build made by `scripts/build-loadtest.sh` in `scripts/loadtest-linux-x64/` (see its README), or run
it from the repository as on Windows:

```powershell
$env:SAS_API_KEY = "sas_..."
dotnet run --project src\SpatialAudioServer.LoadTest -c Release -- --server http://10.2.2.235:5000 --participants 2 --duration 10
dotnet run --project src\SpatialAudioServer.LoadTest -c Release -- --server http://10.2.2.235:5000 --room <room id> --audio voices --spatial
```

The pre-rendered voices (`--audio voices`, found in `scripts/loadtest-linux-x64/voices`) work everywhere;
`--audio flite` is Linux-only.
