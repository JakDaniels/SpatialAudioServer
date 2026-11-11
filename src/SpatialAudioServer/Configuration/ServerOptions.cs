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
}
