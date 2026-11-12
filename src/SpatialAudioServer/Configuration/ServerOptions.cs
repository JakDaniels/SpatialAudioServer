namespace SpatialAudioServer.Configuration;

/// <summary>Listening ports and TLS. Section "Server".</summary>
public sealed class ServerOptions
{
    public const string Section = "Server";

    /// <summary>Plain HTTP port on all interfaces; 0 disables it.</summary>
    public int HttpPort { get; set; } = 5000;

    /// <summary>HTTPS port on all interfaces; 0 disables it. Browsers need HTTPS for the microphone off localhost.</summary>
    public int HttpsPort { get; set; } = 5001;

    /// <summary>PFX certificate to use. When unset, a self-signed development certificate is generated.</summary>
    public string? CertificatePath { get; set; }

    public string? CertificatePassword { get; set; }

    /// <summary>Where the generated development certificate is kept (relative to the content root).</summary>
    public string DevCertificatePath { get; set; } = "data/devcert.pfx";
}

/// <summary>Section "Storage".</summary>
public sealed class StorageOptions
{
    public const string Section = "Storage";

    /// <summary>SQLite database file (relative to the content root).</summary>
    public string DatabasePath { get; set; } = "data/spatialaudio.db";
}

/// <summary>Section "Auth".</summary>
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
}

/// <summary>Section "Limits". Defaults are the absolute maxima in PLAN.md.</summary>
public sealed class LimitOptions
{
    public const string Section = "Limits";

    public int MaxRooms { get; set; } = 16;

    public int MaxParticipantsPerRoom { get; set; } = 32;

    public int MaxParticipantsTotal { get; set; } = 512;
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

    /// <summary>Mix engine for rooms created without one (see MixEngines; listed by GET /api/status).</summary>
    public string Engine { get; set; } = Media.Engines.MixEngines.Default;

    /// <summary>Per-(source, listener) chain queue size; older audio is dropped beyond this.</summary>
    public uint ChainQueueMs { get; set; } = 200;
}
