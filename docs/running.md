# Running SpatialAudioServer (Linux and Windows)

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
dotnet run --project src/SpatialAudioServer -- admin set-password            # admin UI password
```

With a published build, run `SpatialAudioServer apikey create …` / `admin set-password` from its folder instead.
For testing, a fixed key can be set in configuration instead (`Auth:ApiKeys`, see below).

### Testing switch

`--delete-existing-rooms` deletes every stored room at start-up (a clean, idle server for test runs).

## 3. Configuration

`appsettings.json` next to the program, overridden by `appsettings.{Environment}.json` (e.g. the git-ignored
`appsettings.Development.json` that `dotnet run` uses) and then by environment variables, with `__` between levels:
`Server__HttpsPort=443`, `Auth__ApiKeys__0=devkey`, `Media__IdleRoomTimeout=00:01:00`.

| Setting | Default | |
|---|---|---|
| `Server:HttpPort`, `Server:HttpsPort` | 5000, 5001 | 0 turns one off |
| `Server:CertificatePath`, `CertificatePassword` | unset | a real PFX; unset = self-signed dev certificate in `data/devcert.pfx` |
| `Server:CertificateKeyPath` | unset | with a PEM `CertificatePath` (e.g. Let's Encrypt's `fullchain.pem`): its `privkey.pem` |
| `Server:ListenAddress` | all interfaces | e.g. `127.0.0.1` behind a reverse proxy |
| `Server:TrustedProxies` | `[]` | reverse proxies whose `X-Forwarded-For`/`-Proto` are believed ([internet.md](internet.md)) |
| `Admin:AllowedNetworks`, `Auth:AllowedNetworks` | `[]` (anywhere) | where the admin UI / the control API may be used from; others get 404 |
| `Storage:DatabasePath` | `data/spatialaudio.db` | rooms, keys, history |
| `Auth:ApiKeys` | `[]` | fixed control-API keys (never stored) |
| `Auth:JoinTokenLifetime` | 5 min | a participant who has not connected by then is removed (`join-expired`) |
| `Admin:SessionLifetime` | 8 h | sliding |
| `Limits:MaxRooms`, `MaxParticipantsPerRoom`, `MaxParticipantsTotal` | 16, 32, 512 | |
| `Limits:MaxRequestBodyBytes` | 64 KB | |
| `Limits:SignalRequestsPerSecond` | 30 | per participant on `/signal/…`, bursts of 2×; 0 = off |
| `Limits:SignalRequestsPerSecondPerAddress` | 300 | per client address on `/signal/…`; 0 = off |
| `Media:IdleRoomTimeout` | 30 s | a room's pipeline starts with its first participant and stops this long after the last leaves |
| `Media:DeadConnectionTimeout` | 10 s | a connection failed/disconnected this long is removed (`connection-lost`) |
| `Media:Engine`, `Media:SpatialEngine` | `matrix-shared`, `matrix` | default mix engine for new rooms |
| `Media:IceServers` | `[]` | STUN/TURN (not needed on a LAN) |
| `Media:Network:MinPort`, `MaxPort` | any | the media port range (UDP and ICE-TCP), for firewalls |
| `Media:Network:PublicAddress` | unset | the public address behind 1:1 NAT: host candidates are also given with it |
| `Media:Spatial:DistanceModel`, `ReferenceDistance`, `Rolloff`, `MaxRange`, `Profiles` | exponential, 2 m, 1, 60 m | distance curve; rooms may name a profile (`outdoor`, `meeting`) |
| `Media:OpusComplexity`, `OpusFrameMs`, `OpusBitrate` | 5, 40, 64000 | per listener's mix |

## 4. Network

- **HTTPS:** browsers only allow the microphone on HTTPS (or `localhost`). The generated dev certificate covers
  `localhost`, the host name and the machine's IPv4 addresses; accept the browser warning once per browser
  (delete `data/devcert.pfx` to regenerate it after an address change).
- **Firewall:** allow TCP 5000/5001 **and UDP for WebRTC media** (ICE uses ephemeral UDP ports).
  - Ubuntu with ufw: `sudo ufw allow 5000:5001/tcp` and allow UDP from the LAN (e.g. `sudo ufw allow from 10.0.0.0/8 proto udp`).
  - Windows: allow the program when Windows Defender Firewall asks on first start, for **Private** networks
    (with `dotnet run` it is `src\SpatialAudioServer\bin\Debug\net10.0\SpatialAudioServer.exe`), or add a rule:
    `New-NetFirewallRule -DisplayName SpatialAudioServer -Direction Inbound -Program C:\sas\SpatialAudioServer.exe -Action Allow -Profile Private`.
    Check the network is set to Private, not Public.
- Rate limits use the client's address. Behind a reverse proxy, list it in `Server:TrustedProxies`, or every client
  shares the proxy's address.
- **On the internet** (TLS, the media port range, NAT, TURN, hardening): see [internet.md](internet.md).

## 5. Running as a service

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

## 6. Load tool

The load tool needs GStreamer too. On Linux use the self-contained build in `scripts/loadtest-linux-x64/` (see its
README). On Windows run it from the repository:

```powershell
$env:SAS_API_KEY = "sas_..."
dotnet run --project src\SpatialAudioServer.LoadTest -c Release -- --server http://10.2.2.235:5000 --participants 2 --duration 10
dotnet run --project src\SpatialAudioServer.LoadTest -c Release -- --server http://10.2.2.235:5000 --room <room id> --audio voices --spatial
```

The pre-rendered voices (`--audio voices`, found in `scripts/loadtest-linux-x64/voices`) work everywhere;
`--audio flite` is Linux-only.
