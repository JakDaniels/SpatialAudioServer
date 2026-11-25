# Configuring SpatialAudioServer

The settings and network notes for every kind of install: the packages and Windows installer
([running-from-prebuilt-binaries.md](running-from-prebuilt-binaries.md)) and builds from source
([running-from-source.md](running-from-source.md)). For an internet-facing host, see also [internet.md](internet.md).

## 1. Where settings come from

Later sources override earlier ones:

1. `appsettings.json` next to the program (the defaults; replaced on upgrade, so do not edit it).
2. `appsettings.{Environment}.json` next to the program (e.g. the git-ignored `appsettings.Development.json` that
   `dotnet run` uses).
3. The same two files in the directory named by `SAS_CONFIG_DIR`, if it is set. The packages set it, so site
   settings live apart from the program and survive upgrades:

   | Install | Site settings | Data (database, dev certificate) |
   |---|---|---|
   | Linux packages | `/etc/spatialaudioserver/appsettings.json` | `/var/lib/spatialaudioserver/` |
   | Windows installer | `C:\ProgramData\SpatialAudioServer\appsettings.json` | `C:\ProgramData\SpatialAudioServer\data\` |
   | From source | (none unless you set `SAS_CONFIG_DIR`) | `data/` beside the program |

4. Environment variables, with `__` between levels: `Server__HttpsPort=443`, `Auth__ApiKeys__0=devkey`,
   `Media__IdleRoomTimeout=00:01:00`. The packages keep theirs (where the settings and data are) in an `environment`
   file: `/etc/spatialaudioserver/environment` on Linux, `C:\Program Files\SpatialAudioServer\environment` on Windows.
5. Command-line switches: `--Section:Key=value`.

The JSON files may contain `//` comments. Restart the server after changing them.

## 2. Settings

| Setting | Default | |
|---|---|---|
| `Server:HttpPort`, `Server:HttpsPort` | 5000, 5001 | 0 turns one off |
| `Server:CertificatePath`, `CertificatePassword` | unset | a real PFX; unset = self-signed dev certificate |
| `Server:CertificateKeyPath` | unset | with a PEM `CertificatePath` (e.g. Let's Encrypt's `fullchain.pem`): its `privkey.pem` |
| `Server:DevCertificatePath` | `data/devcert.pfx` | where the self-signed dev certificate is kept |
| `Server:ListenAddress` | all interfaces | e.g. `127.0.0.1` behind a reverse proxy |
| `Server:TrustedProxies` | `[]` | reverse proxies whose `X-Forwarded-For`/`-Proto` are believed ([internet.md](internet.md)) |
| `Admin:AllowedNetworks`, `Auth:AllowedNetworks` | `[]` (anywhere) | where the admin UI / the control API may be used from; others get 404 |
| `Storage:DatabasePath` | `data/spatialaudio.db` | rooms, keys, history |
| `Auth:ApiKeys` | `[]` | fixed control-API keys (never stored) |
| `Auth:JoinTokenLifetime` | 5 min | a participant who has not connected by then is removed (`join-expired`) |
| `Admin:SessionLifetime` | 8 h | sliding; changing the admin password ends every session |
| `Admin:RequireHttps` | false | admin login only over HTTPS (except from the machine itself), Secure cookie |
| `Limits:MaxRooms`, `MaxParticipantsPerRoom`, `MaxParticipantsTotal` | 16, 32, 512 | |
| `Limits:MaxRequestBodyBytes` | 64 KB | |
| `Limits:SignalRequestsPerSecond` | 30 | per participant on `/signal/…`, bursts of 2×; 0 = off |
| `Limits:SignalRequestsPerSecondPerAddress` | 300 | per client address on `/signal/…`; 0 = off |
| `Media:IdleRoomTimeout` | 30 s | a room's pipeline starts with its first participant and stops this long after the last leaves |
| `Media:DeadConnectionTimeout` | 10 s | a connection failed/disconnected this long is removed (`connection-lost`) |
| `Media:InactiveConnectionTimeout` | 30 s | a connection that has delivered nothing (no audio, RTCP or data) this long counts as failed (GStreamer 1.24's only way to notice a vanished client). A client that only listens sends no audio but reports what it receives about every 5 s, so keep this well above that |
| `Media:Engine`, `Media:SpatialEngine` | `matrix-shared`, `matrix` | default mix engine for new rooms (`pairchains`, `matrix`, `matrix-shared`, and `acoustic` where the acousticmultimixer GStreamer plugin is installed; `GET /api/status` lists what this server has) |
| `Media:IceServers` | `[]` | STUN/TURN (not needed on a LAN); a TURN entry takes `Username`/`Credential`, or a `Secret` (coturn `static-auth-secret`) for per-client credentials valid `CredentialLifetime` (12 h) |
| `Media:Network:MinPort`, `MaxPort` | any (the packages: 40000, 40999) | the media port range (UDP and ICE-TCP), for firewalls |
| `Media:Network:PublicAddress` | unset | the public address behind 1:1 NAT: host candidates are also given with it |
| `Media:Spatial:DistanceModel`, `ReferenceDistance`, `Rolloff`, `MaxRange`, `Profiles` | exponential, 2 m, 1, 60 m | distance curve; rooms may name a profile (`outdoor`, `meeting`) |
| `Media:Spatial:NearDistance` | 2 m | `acoustic` rooms: closer than this a voice's reflections and reverb are turned down (to −12 dB at 0.5 m), so it stands out from the room; 0 = never |
| `Media:Spatial:Taper` | 0.75 | voices fade smoothly to silence from this fraction of `MaxRange` (by default 45 m to 60 m) instead of cutting off at it; 1 = no fade |
| `Media:OpusComplexity`, `OpusFrameMs`, `OpusBitrate` | 5, 40, 64000 | per listener's mix |

## 3. Network

- **HTTPS:** browsers only allow the microphone on HTTPS (or `localhost`). Without a configured certificate the
  server makes a self-signed one covering `localhost`, the host name and the machine's IPv4 addresses; accept the
  browser warning once per browser. Delete the dev certificate (see the table above for where it is) and restart to
  regenerate it after an address change. A real certificate: [internet.md](internet.md), section 2.
- **Ports to open:**

  | Port | For |
  |---|---|
  | TCP 5000, 5001 | HTTP, HTTPS: the API, signalling, the web client and the admin UI |
  | UDP 40000–40999 | WebRTC media, when `Media:Network:MinPort`/`MaxPort` set this range (the packages do) |
  | TCP 40000–40999 | ICE-TCP over the same range, for clients whose networks block UDP (optional) |

  Without a media range, ICE takes any free UDP port, so the firewall has to allow all UDP from your clients.
  Each guide shows its platform's firewall commands.
- Rate limits use the client's address. Behind a reverse proxy, list it in `Server:TrustedProxies`, or every client
  shares the proxy's address.
- **On the internet** (TLS, NAT, TURN, hardening): see [internet.md](internet.md).
