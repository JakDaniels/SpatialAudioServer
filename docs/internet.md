# Running SpatialAudioServer on an internet-facing host

[running-from-prebuilt-binaries.md](running-from-prebuilt-binaries.md) and [running-from-source.md](running-from-source.md)
cover installing and running on a LAN, and [configuration.md](configuration.md) every setting. This guide covers
what changes when the server is reachable from the internet: TLS, the firewall, NAT, TURN for clients behind strict
networks, and hardening. Every setting here is off by default, which suits a LAN (except the media port range,
which the packages set to 40000–40999); settings can be given in `appsettings.json` or as environment variables
(`Media__Network__MinPort=40000`).

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
server believe `X-Forwarded-For` / `X-Forwarded-Proto` from those proxies only. **Behind a proxy, set it**: several
protections depend on it.

| Without `TrustedProxies` | With it |
|---|---|
| Every client appears as the proxy: one shared rate limit and admin-login throttle for everyone | each client is limited and throttled on its own address |
| `Admin:AllowedNetworks` / `Auth:AllowedNetworks` see only the proxy's address (allow it, and you allow everyone) | the lists see each client's address |
| `Admin:RequireHttps` sees plain HTTP from the proxy and refuses every admin login (a proxy on the same host included) | the proxy's `X-Forwarded-Proto: https` counts as HTTPS |

**List the address the proxy's connections arrive from**, which is not always the proxy's LAN address. When the
server runs in a container or VM, or on another subnet, the connections come from the proxy host's address on that
network: e.g. the bridge address `192.168.21.1` for a container at `192.168.21.209`, or Docker's gateway
(`172.17.0.1`) for a published port. To find it:

- on the proxy host, `ip route get <server address>`: the `src` it shows; or
- from the server itself: set `Admin:AllowedNetworks` to `[ "192.0.2.1" ]` for a moment, open `/admin/` through the
  proxy, and the log says `Refused /admin/ from <address>`. Put your real list back afterwards.

An address logged as `::ffff:192.168.21.1` is IPv4 `192.168.21.1` seen on a dual-stack socket: list it without the
`::ffff:`. A wrong `TrustedProxies` does not stop `/admin/` loading; it shows at login, as "This request came through
a proxy that is not in Server:TrustedProxies".

Only list proxies that are yours: a listed proxy's headers are believed, so a client that could reach the server
through another listed address could claim any address. The proxy must send `X-Forwarded-For` and
`X-Forwarded-Proto` (Caddy does by default; nginx with the lines below), and should redirect plain HTTP to HTTPS
(Caddy does by default; with nginx, add a `server { listen 80; return 301 https://$host$request_uri; }`). Caddy:

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
use-auth-secret
static-auth-secret=a-long-random-secret
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
      "Secret": "a-long-random-secret", "CredentialLifetime": "12:00:00" }
  ]
}
```

Open 3478 (UDP and TCP), 5349 (TCP) and coturn's relay range (UDP) in the firewall. If coturn runs on the same host
as the media range, keep the two ranges apart. If clients' networks allow only 443, run coturn's TLS listener on 443
on a separate address from the web server.

**About the credentials:** with `Secret` (coturn's `use-auth-secret` / `static-auth-secret`, the "TURN REST API"
scheme), the server makes each client its own TURN credentials: username `{expiry}:{participant id}`, password
`base64(HMAC-SHA1(secret, username))`, valid for `CredentialLifetime` (12 h). The secret itself never leaves the
server, and credentials stop working once they expire. Fixed `Username`/`Credential` also work (with coturn's
`lt-cred-mech` and a `user=` line), but they are handed to every client that joins, so they are semi-public. Either
way, keep coturn's quotas (`total-quota`, `user-quota`, `max-bps`) and the `denied-peer-ip` rules above, so the relay
cannot be used to reach your internal network.

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
- **Admin login over HTTPS only:** `"Admin": { "RequireHttps": true }` refuses admin login over plain HTTP and marks
  the session cookie Secure. The one exception is a request made on the machine itself (e.g. `curl
  http://localhost:5000` while setting up); a request through a proxy on the same host does not count as that. Behind
  a proxy that terminates TLS, list it in `TrustedProxies` (section 2), or every admin login is refused.
- **Keys and passwords.**
  - Control-API keys: create one per integration with `SpatialAudioServer apikey create <label>`, optionally with
    `--expires 90d` (or a date) for keys handed to people or tools. `apikey list` shows each key's last use and
    expiry, and flags keys unused for 90 days. Revoke keys you no longer need with `apikey revoke <id or prefix>`.
  - Avoid `Auth:ApiKeys` in configuration on a public host: those keys are not stored hashed, and can't be revoked
    with the command.
  - The admin password: set it with `admin set-password`, or give a hash in `Admin:PasswordHash` (from
    `admin hash-password`). Changing it ends every admin session. After 5 failed logins from one address, or 30 from
    anywhere, within a minute, logins are refused for a minute.
  - Admin changes made with the session cookie are refused if the request comes from another site's page, even on
    the same domain.
- **Limits.** The defaults are sized for one host: 16 rooms × 32 participants, request bodies ≤ 64 KB, signalling ≤ 30
  requests/s per participant and ≤ 300/s per address. Lower `Limits:SignalRequestsPerSecondPerAddress` if you expect
  few clients per address.
- **The OpenSim module** should use `https://` in `ServerURI` with `AcceptSelfSignedCertificate = false`, and its own
  API key.
- **Logs and updates.** `journalctl -u spatialaudio` holds the event log (joins, leaves, refused requests). Keep
  .NET, GStreamer and the OS patched: media parsing (SDP, RTP, Opus) is the part that faces untrusted input.
- **Backups.** `/opt/sas/data/spatialaudio.db` holds rooms, hashed keys and the session history: copy it with the
  service stopped, or with `sqlite3 spatialaudio.db ".backup copy.db"`. `/opt/sas/data/keys` holds the keys that
  protect admin session cookies: keep it private (only the service user may read it); losing it just logs the admin
  out.

## 7. Checklist

1. DNS name pointing at the host; certificate in place (section 2); `AllowedHosts` set.
2. Firewall: TCP 443, and the media range for UDP and TCP (section 3), plus TURN's ports if used.
3. Behind NAT: `PublicAddress` set and the media range forwarded on the same ports (section 4).
4. `Admin:AllowedNetworks`, `Auth:AllowedNetworks` and `Admin:RequireHttps` set (section 6); TURN, if used, with a
   `Secret` (section 5).
5. Service running as `spatialaudio` with the stricter unit; API keys created and stored, none left in configuration.
6. Test from outside your network: the web client from a phone on mobile data, and a viewer from another site.
