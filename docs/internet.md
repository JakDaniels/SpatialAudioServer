# Running SpatialAudioServer on an internet-facing host

[running.md](running.md) covers installing and running on a LAN. This guide covers what changes when the server is
reachable from the internet: TLS, the firewall, NAT, TURN for clients behind strict networks, and hardening. Every
setting here is off by default, which suits a LAN; settings can be given in `appsettings.json` or as environment
variables (`Media__Network__MinPort=40000`).

## 1. What is exposed

| What | Port | Who needs it |
|---|---|---|
| HTTPS: web client (`/client/`), signalling (`/signal/`), control API (`/api/`), admin UI (`/admin/`) | TCP 443 (or 5001) | browsers, SL viewers (through their simulator), the OpenSim module, you |
| WebRTC media (audio, the SL data channel) | **UDP** range, e.g. 40000–40999, and the same range over **TCP** (ICE-TCP) | every voice client |
| TURN, if you run one ([section 5](#5-turn-for-clients-behind-strict-networks)) | UDP/TCP 3478, TLS 5349 or 443 | browsers behind networks that block UDP |

Viewers send their audio straight to the server: the simulator only relays signalling. So the server's media ports
must be reachable from every viewer, not only from the simulators.

## 2. TLS

Browsers only allow the microphone on HTTPS, so a public server needs a real certificate. Two ways:

**a) Directly in SpatialAudioServer** (simplest; a host of its own):

```bash
sudo certbot certonly --standalone -d voice.example.org     # port 80 free while it runs
```

```json
"Server": {
  "HttpPort": 0,
  "HttpsPort": 443,
  "CertificatePath": "/etc/letsencrypt/live/voice.example.org/fullchain.pem",
  "CertificateKeyPath": "/etc/letsencrypt/live/voice.example.org/privkey.pem"
}
```

The certificate is read at start-up, so restart after a renewal: a certbot deploy hook,
`/etc/letsencrypt/renewal-hooks/deploy/spatialaudio.sh` containing `systemctl restart spatialaudio`. The service user
must be able to read the key: e.g. a `ssl-cert` group with read access to `/etc/letsencrypt/{live,archive}`. Binding
port 443 as a non-root user needs `AmbientCapabilities=CAP_NET_BIND_SERVICE` in the systemd unit (below). A PFX
also works: `CertificatePath` to the `.pfx` and `CertificatePassword`.

**b) Behind a reverse proxy** (nginx, Caddy, … on the same host or in front of it): the proxy terminates TLS and
passes HTTP on.

```json
"Server": {
  "HttpPort": 5000,
  "HttpsPort": 0,
  "ListenAddress": "127.0.0.1",
  "TrustedProxies": [ "127.0.0.1" ]
}
```

`ListenAddress` keeps the plain HTTP port off the network. `TrustedProxies` (addresses or CIDR ranges) makes the
server believe `X-Forwarded-For` / `X-Forwarded-Proto` from those proxies only, so rate limits, the admin login
throttle and the allow-lists below see each client's own address. Without it, every client appears as the proxy;
with a proxy listed that is not really yours, a client could claim any address. Caddy:

```
voice.example.org {
    reverse_proxy 127.0.0.1:5000
}
```

nginx needs long timeouts for the signalling long-poll (`GET /signal/…/ice` waits up to 30 s):

```nginx
location / {
    proxy_pass http://127.0.0.1:5000;
    proxy_set_header Host $host;
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    proxy_set_header X-Forwarded-Proto $scheme;
    proxy_read_timeout 60s;
}
```

The proxy only carries HTTP: media never goes through it.

Also set `AllowedHosts` to the real name (`"AllowedHosts": "voice.example.org"`), so requests for other host names are
refused.

## 3. Firewall and the media port range

Without a range, ICE takes any free port, so the firewall would have to allow all UDP. Fix the range:

```json
"Media": {
  "Network": { "MinPort": 40000, "MaxPort": 40999 }
}
```

Each connection takes a port per local address, so allow at least the participant limit (512 by default) times the
number of addresses the host has, with room to spare. The same range is used for ICE-TCP (passive TCP candidates),
which lets clients behind UDP-blocking firewalls connect over TCP.

ufw:

```bash
sudo ufw allow 443/tcp
sudo ufw allow 40000:40999/udp
sudo ufw allow 40000:40999/tcp
```

Cloud providers' security groups need the same rules. Windows: `New-NetFirewallRule -DisplayName "SAS media" -Direction
Inbound -Protocol UDP -LocalPort 40000-40999 -Action Allow` (and the same for TCP and for 443).

## 4. NAT and the public address

Clients reach the server at the addresses in its ICE candidates. A host directly on a public address needs nothing
more. Behind NAT (most cloud VMs, a home or office router) it only knows its private address, so tell it the public
one:

```json
"Media": {
  "Network": { "MinPort": 40000, "MaxPort": 40999, "PublicAddress": "203.0.113.5" }
}
```

Every local host candidate is then also given to clients with the public address and the same port (the local ones
stay, for clients on the same network). This needs **1:1 NAT that keeps port numbers**: forward the whole media range
(UDP and TCP) to the host on the same ports. Cloud VMs with a public IP (AWS elastic IP, Azure, GCP, Hetzner) behave
like this; on a router, forward the range to the server.

The alternative is a STUN server in `Media:IceServers`, from which the server learns its public address itself
(server-reflexive candidates). That works through more kinds of NAT but adds a lookup per connection and depends on
the STUN server; with a known public address and a forwarded range, `PublicAddress` is the reliable choice.

## 5. TURN for clients behind strict networks

**You probably don't need TURN.** TURN exists mostly for peer-to-peer calls where *both* ends are behind NAT. Here one
end, the server, is directly reachable (a public address, or 1:1 NAT with the media range forwarded), which removes
most of the need.

**Not needed** (with the media range open for UDP and TCP):

- **Clients behind any ordinary NAT**: home routers, mobile networks, carrier-grade NAT, even "symmetric" NAT. The
  client sends out to the server, its NAT lets the replies back in, and the server learns the client's outside
  address from the packets themselves. STUN is not needed either.
- **Clients whose network blocks UDP but allows outgoing TCP to any port**: they connect with ICE over TCP to the
  server's TCP range.

**Needed when:**

1. **The client's network allows only web ports**: corporate, hotel, school or public Wi-Fi that blocks UDP *and*
   outgoing TCP except 80/443. TURN over TLS on port 443 (`turns:…:443`) gets through, since to the firewall it
   looks like HTTPS.
2. **Clients must go through an HTTP proxy** (some corporate networks): browsers can tunnel TURN over TLS through the
   proxy; direct ICE can't.
3. **A firewall inspects traffic** and blocks WebRTC's encrypted media or anything that is not real TLS: TURN over
   TLS on 443 usually passes where plain TURN may not.
4. **The server cannot be reached directly**: it is behind NAT where you can't forward the media range (a router you
   don't control, carrier-grade NAT on the server's own line). A TURN server on a public host can then relay for
   both sides (the server uses `Media:IceServers` too), though fixing the hosting is usually simpler.
5. **Address families differ**: IPv6-only clients and an IPv4-only server, or the reverse. TURN can bridge them.

**SL viewers do not use this list.** Viewers use their own ICE settings (Firestorm configures STUN, and as far as we
know no TURN), so for them the media range must be directly reachable; a viewer on a web-ports-only network can't
connect, TURN or not. TURN only helps browser users.

**In practice:** open the media range for UDP and TCP on a public address, and most users never need TURN. Add a
TURN server with TLS on port 443 only if you expect users on locked-down corporate or institutional networks.

You configure TURN in `Media:IceServers`; browsers get the list from `GET /signal/{id}/config`. A coturn set-up (on
the same host or another):

```bash
sudo apt install coturn
```

```ini
# /etc/turnserver.conf
listening-port=3478
tls-listening-port=5349
realm=voice.example.org
fingerprint
lt-cred-mech
user=sas:a-long-random-password
cert=/etc/letsencrypt/live/voice.example.org/fullchain.pem
pkey=/etc/letsencrypt/live/voice.example.org/privkey.pem
min-port=49160
max-port=49999
# Don't relay into private networks:
denied-peer-ip=10.0.0.0-10.255.255.255
denied-peer-ip=172.16.0.0-172.31.255.255
denied-peer-ip=192.168.0.0-192.168.255.255
total-quota=100
```

```json
"Media": {
  "IceServers": [
    { "Urls": [ "stun:voice.example.org:3478" ] },
    { "Urls": [ "turn:voice.example.org:3478?transport=udp", "turn:voice.example.org:3478?transport=tcp",
                "turns:voice.example.org:5349?transport=tcp" ],
      "Username": "sas", "Credential": "a-long-random-password" }
  ]
}
```

Open 3478 (UDP and TCP), 5349 (TCP) and coturn's relay range (UDP) in the firewall. If coturn runs on the same host
as the media range, keep the two ranges apart. If clients' networks allow only 443, run coturn's TLS listener on 443
on a separate address from the web server.

**About the credentials:** the TURN username and password in `Media:IceServers` are handed to every client that
joins, so treat them as semi-public: use an account just for this, with coturn's quotas (`total-quota`,
`user-quota`, `max-bps`) and the `denied-peer-ip` rules above so it cannot be used to reach your internal network.
Short-lived TURN credentials (coturn's `use-auth-secret`) are not supported yet.

Test with the WebRTC "Trickle ICE" page (search for it: it is part of the WebRTC samples): with the TURN entry
added, it should list `relay` candidates.

## 6. Hardening

- **Run as an unprivileged user** (`spatialaudio`), owning only `/opt/sas/data` (the SQLite database and the dev
  certificate). The program files are root-owned and read-only. A stricter systemd unit:

  ```ini
  [Service]
  Type=notify
  User=spatialaudio
  WorkingDirectory=/opt/sas
  ExecStart=/opt/sas/SpatialAudioServer
  Environment=DOTNET_ENVIRONMENT=Production
  AmbientCapabilities=CAP_NET_BIND_SERVICE      # only to bind 443 directly
  NoNewPrivileges=true
  ProtectSystem=strict
  ProtectHome=true
  PrivateTmp=true
  ReadWritePaths=/opt/sas/data
  TimeoutStopSec=30
  Restart=on-failure
  ```

- **Limit the admin UI and the control API by network.** The admin UI needs only your own addresses, the control API
  only the integration's (the simulators running the OpenSim module):

  ```json
  "Admin": { "AllowedNetworks": [ "198.51.100.0/24" ] },
  "Auth":  { "AllowedNetworks": [ "198.51.100.20", "198.51.100.21" ] }
  ```

  Others get 404. Client signalling and the web client are not affected: they work with join and session tokens.
  Behind a proxy this relies on `TrustedProxies` (section 2).
- **Keys and passwords.**
  - Control-API keys: create one per integration with `SpatialAudioServer apikey create <label>`. Check them with
    `apikey list`, and revoke unused ones with `apikey revoke <id or prefix>`.
  - Avoid `Auth:ApiKeys` in configuration on a public host: those keys are not stored hashed, and can't be revoked
    with the command.
  - The admin password: set it with `admin set-password`, or give a hash in `Admin:PasswordHash` (from
    `admin hash-password`).
- **Limits.** The defaults are sized for one host: 16 rooms × 32 participants, request bodies ≤ 64 KB, signalling ≤ 30
  requests/s per participant and ≤ 300/s per address. Lower `Limits:SignalRequestsPerSecondPerAddress` if you expect
  few clients per address.
- **The OpenSim module** should use `https://` in `ServerURI` with `AcceptSelfSignedCertificate = false`, and its own
  API key.
- **Logs and updates.** `journalctl -u spatialaudio` holds the event log (joins, leaves, refused requests). Keep
  .NET, GStreamer and the OS patched: media parsing (SDP, RTP, Opus) is the part that faces untrusted input.
- **Backups.** `/opt/sas/data/spatialaudio.db` holds rooms, hashed keys and the session history: copy it with the
  service stopped, or with `sqlite3 spatialaudio.db ".backup copy.db"`.

## 7. Checklist

1. DNS name pointing at the host; certificate in place (section 2); `AllowedHosts` set.
2. Firewall: TCP 443, and the media range for UDP and TCP (section 3), plus TURN's ports if used.
3. Behind NAT: `PublicAddress` set and the media range forwarded on the same ports (section 4).
4. `Admin:AllowedNetworks` and `Auth:AllowedNetworks` set (section 6).
5. Service running as `spatialaudio` with the stricter unit; API keys created and stored, none left in configuration.
6. Test from outside your network: the web client from a phone on mobile data, and a viewer from another site.
