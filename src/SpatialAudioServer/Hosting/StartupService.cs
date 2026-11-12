using Microsoft.Extensions.Options;
using SpatialAudioServer.Configuration;
using SpatialAudioServer.Media;
using SpatialAudioServer.Rooms;
using SpatialAudioServer.Storage;

namespace SpatialAudioServer.Hosting;

/// <summary>
/// Runs before the server accepts requests: migrates the database, starts GStreamer (failing start-up if it is
/// unusable), restores persisted rooms and warns when no API key exists.
/// </summary>
internal sealed class StartupService(
    Database database,
    GstRuntime gst,
    RoomManager rooms,
    ApiKeyStore apiKeys,
    IOptions<MediaOptions> media,
    IOptions<AuthOptions> auth,
    ILogger<StartupService> log) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        log.LogInformation("Database {Path}, schema version {Version}", database.Path, database.SchemaVersion);
        if (!Media.Engines.MixEngines.Exists(media.Value.Engine))
        {
            throw new InvalidOperationException(
                $"Media:Engine '{media.Value.Engine}' is not a mix engine. Available: " +
                string.Join(", ", Media.Engines.MixEngines.All.Select(e => e.Name)) + ".");
        }
        if (!MediaOptions.OpusFrameSizes.Contains(media.Value.OpusFrameMs))
        {
            throw new InvalidOperationException(
                $"Media:OpusFrameMs must be one of {string.Join(", ", MediaOptions.OpusFrameSizes)} (is {media.Value.OpusFrameMs}).");
        }

        if (media.Value.Enabled)
        {
            var status = gst.Initialize();
            if (!status.Ok)
            {
                throw new InvalidOperationException(
                    status.Error ?? $"Required GStreamer elements are missing: {string.Join(", ", status.MissingElements)}. " +
                    "Install gst-plugins-good/bad and gstreamer1.0-nice (see scripts/setup-ubuntu.sh).");
            }
        }
        else
        {
            log.LogWarning("Media is disabled (Media:Enabled=false); GStreamer is not loaded");
        }

        rooms.Restore();

        if (auth.Value.ApiKeys.Count(k => !string.IsNullOrWhiteSpace(k)) == 0 && !apiKeys.List().Any(k => k.RevokedAt is null))
        {
            log.LogWarning(
                "No API key is configured, so the control API is unusable. Create one with " +
                "'SpatialAudioServer apikey create <label>' or set Auth__ApiKeys__0");
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
