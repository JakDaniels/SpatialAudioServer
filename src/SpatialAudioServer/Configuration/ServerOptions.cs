namespace SpatialAudioServer.Configuration;

/// <summary>Listening ports and TLS. Section "Server".</summary>
public sealed class ServerOptions
{
    public const string Section = "Server";

    /// <summary>Plain HTTP port on all interfaces; 0 disables it.</summary>
    public int HttpPort { get; set; } = 5000;

    /// <summary>HTTPS port on all interfaces; 0 disables it. Browsers need HTTPS for the microphone off localhost.</summary>
    public int HttpsPort { get; set; } = 5001;

    /// <summary>
    /// Only listen on this address (e.g. "127.0.0.1" behind a reverse proxy on the same host). Unset: all interfaces.
    /// </summary>
    public string? ListenAddress { get; set; }

    /// <summary>
    /// The certificate: a PFX (with <see cref="CertificatePassword"/>), or a PEM certificate chain (e.g. Let's
    /// Encrypt's fullchain.pem) with its key in <see cref="CertificateKeyPath"/>. Read at start-up, so restart after
    /// a renewal. When unset, a self-signed development certificate is generated.
    /// </summary>
    public string? CertificatePath { get; set; }

    public string? CertificatePassword { get; set; }

    /// <summary>The PEM private key (e.g. privkey.pem) for a PEM <see cref="CertificatePath"/>.</summary>
    public string? CertificateKeyPath { get; set; }

    /// <summary>Where the generated development certificate is kept (relative to the content root).</summary>
    public string DevCertificatePath { get; set; } = "data/devcert.pfx";

    /// <summary>
    /// Reverse proxies (addresses or CIDR ranges, e.g. "127.0.0.1", "10.0.0.0/8") whose X-Forwarded-For and
    /// X-Forwarded-Proto are believed, so rate limits, the admin login throttle and the network allow-lists see the
    /// client's address. Empty: forwarded headers are ignored (a client could otherwise claim any address).
    /// </summary>
    public List<string> TrustedProxies { get; set; } = [];
}

/// <summary>Section "Storage".</summary>
public sealed class StorageOptions
{
    public const string Section = "Storage";

    /// <summary>SQLite database file (relative to the content root).</summary>
    public string DatabasePath { get; set; } = "data/spatialaudio.db";
}

/// <summary>Section "Auth".</summary>
/// <summary>The admin web UI (<c>/admin/</c>).</summary>
public sealed class AdminOptions
{
    public const string Section = "Admin";

    /// <summary>
    /// The admin password as a hash from <c>SpatialAudioServer admin hash-password</c> (for containers / config
    /// management). When set it wins over the one stored with <c>admin set-password</c>.
    /// </summary>
    public string? PasswordHash { get; set; }

    /// <summary>An admin session lasts this long after its last request.</summary>
    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromHours(8);

    /// <summary>
    /// Where the admin UI and its API (<c>/admin/</c>, <c>/api/admin/</c>) may be used from: addresses or CIDR
    /// ranges. Empty: anywhere (the password still applies). Others get 404.
    /// </summary>
    public List<string> AllowedNetworks { get; set; } = [];
}

public sealed class AuthOptions
{
    public const string Section = "Auth";

    /// <summary>
    /// Bootstrap control-API keys from configuration (e.g. env <c>Auth__ApiKeys__0</c>). They always work and are
    /// never stored. Further keys are created with <c>SpatialAudioServer apikey create</c> and kept hashed in SQLite.
    /// </summary>
    public List<string> ApiKeys { get; set; } = [];

    /// <summary>How long a participant's one-time join token is valid.</summary>
    public TimeSpan JoinTokenLifetime { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Where the control API (<c>/api/</c>, but not the admin UI's) may be used from: addresses or CIDR ranges, e.g.
    /// the simulators running the OpenSim module. Empty: anywhere (the API key still applies). Others get 404.
    /// Client signalling (<c>/signal/</c>) and the web client are not affected.
    /// </summary>
    public List<string> AllowedNetworks { get; set; } = [];
}

/// <summary>Section "Limits". Defaults are the absolute maxima in PLAN.md.</summary>
public sealed class LimitOptions
{
    public const string Section = "Limits";

    public int MaxRooms { get; set; } = 16;

    public int MaxParticipantsPerRoom { get; set; } = 32;

    public int MaxParticipantsTotal { get; set; } = 512;

    /// <summary>Largest request body Kestrel accepts (SDP offers are a few KB, participant metadata at most 16 KB).</summary>
    public long MaxRequestBodyBytes { get; set; } = 64 * 1024;

    /// <summary>
    /// Signalling requests (<c>/signal/{participantId}/…</c>) per second per participant, with bursts of twice this.
    /// Real clients use a few per second (pose updates at most 20). 0 turns the limit off.
    /// </summary>
    public int SignalRequestsPerSecond { get; set; } = 30;

    /// <summary>
    /// Signalling requests per second from one client address (all its participants together; the load tool runs up to
    /// 32 from one machine), with bursts of twice this. 0 turns the limit off.
    /// </summary>
    public int SignalRequestsPerSecondPerAddress { get; set; } = 300;
}

/// <summary>Section "Media".</summary>
public sealed class MediaOptions
{
    public const string Section = "Media";

    /// <summary>Initialise GStreamer at start-up and refuse to start if required plugins are missing.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>webrtcbin's RTP jitterbuffer latency (its default is 200 ms; LAN needs much less).</summary>
    public uint JitterBufferMs { get; set; } = 100;

    /// <summary>How long each listener's audiomixer waits for late input before mixing without it.</summary>
    public uint MixerLatencyMs { get; set; } = 40;

    /// <summary>Opus bitrate of each listener's stereo mix, bits/s.</summary>
    public int OpusBitrate { get; set; } = 64000;

    /// <summary>
    /// opusenc complexity 0–10 for each mix (10 is opusenc's default and the most CPU). Encoding is the largest
    /// per-participant cost once mixing is cheap; 5 cut total server CPU by ~17% at 16 people (matrix engine).
    /// </summary>
    public int OpusComplexity { get; set; } = 5;

    /// <summary>
    /// Duration of each Opus frame (= RTP packet) the server sends: 10, 20, 40 or 60 ms. Longer frames halve the
    /// per-packet work (payloading, SRTP, sending) at the cost of latency. Clients choose their own for what they send.
    /// </summary>
    public int OpusFrameMs { get; set; } = 40;

    public static readonly int[] OpusFrameSizes = [10, 20, 40, 60];

    /// <summary>matrix-shared: how many recent talkers get their own (mix-minus) encoder; everyone else shares one mix.</summary>
    public int ActiveSpeakers { get; set; } = 4;

    /// <summary>matrix-shared: a decoded buffer louder than this (RMS, dBFS) counts as voice.</summary>
    public double VadThresholdDb { get; set; } = -50;

    /// <summary>matrix-shared: how long a talker stays active after their last voiced buffer.</summary>
    public int VadHangoverMs { get; set; } = 800;

    /// <summary>
    /// A participant whose WebRTC connection has been failed or disconnected this long is removed (logged as
    /// participant.left, reason "connection-lost"). The server only sees "failed" ~27 s after a client vanishes (ICE
    /// consent freshness), by which time a live client has noticed and is re-offering (which resets the clock), so
    /// this can be short. Zero disables it.
    /// </summary>
    public TimeSpan DeadConnectionTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// A room's pipeline starts with its first participant and stops once the room has been empty this long (so a quick
    /// leave and rejoin does not rebuild it). Zero stops it as soon as the last participant leaves.
    /// </summary>
    public TimeSpan IdleRoomTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Mix engine for spatial rooms created without one. matrix: in a spatial room matrix-shared rarely shares (each
    /// listener hears the talkers differently, so clusters ≈ listeners) and its per-listener switching then costs
    /// ~10% more CPU than matrix (measured 2026-11-13, PLAN.md Phase 6).
    /// </summary>
    public string SpatialEngine { get; set; } = Media.Engines.MatrixEngine.Name;

    /// <summary>Mix engine for rooms created without one (see MixEngines; listed by GET /api/status).</summary>
    public string Engine { get; set; } = Media.Engines.MixEngines.Default;

    /// <summary>
    /// STUN/TURN servers, in RTCIceServer form (<c>urls</c> like "stun:host:3478", "turn:host:3478?transport=udp",
    /// "turns:host:5349"). The server's webrtcbin uses the first STUN URL and every TURN URL; clients get the whole
    /// list from <c>GET /signal/{participantId}/config</c>. Empty (the default) is fine on a LAN.
    /// </summary>
    public List<IceServerConfig> IceServers { get; set; } = [];

    /// <summary>Where WebRTC media listens and what address it gives clients (internet-facing hosts, NAT).</summary>
    public MediaNetworkOptions Network { get; set; } = new();

    /// <summary>Distance model and cluster grid for spatial rooms.</summary>
    public Media.Spatial.SpatialOptions Spatial { get; set; } = new();

    /// <summary>Per-(source, listener) chain queue size; older audio is dropped beyond this.</summary>
    public uint ChainQueueMs { get; set; } = 200;
}

/// <summary>One STUN/TURN server entry, shaped like the browser's RTCIceServer.</summary>
/// <summary>Section "Media:Network". All off by default, which suits a LAN.</summary>
public sealed class MediaNetworkOptions
{
    /// <summary>
    /// The UDP (and ICE-TCP) port range for media, so a firewall can open just that: each connection takes a port
    /// per local address, so allow at least the participant limit (e.g. 40000–40999). 0 and 0: any port.
    /// </summary>
    public uint MinPort { get; set; }

    public uint MaxPort { get; set; }

    /// <summary>
    /// The host's public address when it is behind 1:1 NAT (a cloud VM, a forwarded DMZ host): every local host
    /// candidate is also given to clients with this address and the same port, so the NAT must keep ports (and
    /// forward the media range). The local candidates stay, for clients on the same network. Unset: local
    /// addresses only (STUN in <see cref="MediaOptions.IceServers"/> can find the public one instead).
    /// </summary>
    public string? PublicAddress { get; set; }
}

public sealed class IceServerConfig
{
    public List<string> Urls { get; set; } = [];

    public string? Username { get; set; }

    public string? Credential { get; set; }
}
