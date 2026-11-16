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
/// <summary>Command-line switches that are not configuration (see Program.cs).</summary>
/// <param name="DeleteExistingRooms">Delete every stored room at start-up (testing).</param>
public sealed record StartupFlags(bool DeleteExistingRooms);

internal sealed class StartupService(
    Database database,
    GstRuntime gst,
    RoomManager rooms,
    ApiKeyStore apiKeys,
    IOptions<MediaOptions> media,
    IOptions<AuthOptions> auth,
    StartupFlags flags,
    Api.AdminPassword adminPassword,
    RoomStore roomStore,
    ActivityLog activity,
    IRoomMedia roomMedia,
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
        if (!Media.Engines.MixEngines.Exists(media.Value.SpatialEngine))
        {
            throw new InvalidOperationException(
                $"Media:SpatialEngine '{media.Value.SpatialEngine}' is not a mix engine. Available: " +
                string.Join(", ", Media.Engines.MixEngines.All.Select(e => e.Name)) + ".");
        }
        if (!MediaOptions.OpusFrameSizes.Contains(media.Value.OpusFrameMs))
        {
            throw new InvalidOperationException(
                $"Media:OpusFrameMs must be one of {string.Join(", ", MediaOptions.OpusFrameSizes)} (is {media.Value.OpusFrameMs}).");
        }
        var spatial = media.Value.Spatial;
        if (spatial.ReferenceDistance <= 0 || spatial.MaxRange < spatial.ReferenceDistance || spatial.Rolloff <= 0
            || spatial.ClusterGainStepDb <= 0 || spatial.ClusterPanSteps < 1 || !Enum.IsDefined(spatial.DistanceModel))
        {
            throw new InvalidOperationException(
                "Media:Spatial needs ReferenceDistance > 0, MaxRange ≥ ReferenceDistance, Rolloff > 0, DistanceModel " +
                "Inverse, Linear or Exponential, ClusterGainStepDb > 0 and ClusterPanSteps ≥ 1.");
        }
        foreach (var (name, profile) in spatial.Profiles)
        {
            if (profile.ReferenceDistance <= 0 || profile.MaxRange < profile.ReferenceDistance || profile.Rolloff <= 0
                || !Enum.IsDefined(profile.DistanceModel))
            {
                throw new InvalidOperationException(
                    $"Media:Spatial:Profiles:{name} needs ReferenceDistance > 0, MaxRange ≥ ReferenceDistance, Rolloff > 0 and a DistanceModel.");
            }
        }
        foreach (var server in media.Value.IceServers)
        {
            if (!string.IsNullOrEmpty(server.Secret) && (server.Username is not null || server.Credential is not null))
            {
                throw new InvalidOperationException("Media:IceServers: an entry has either Secret or Username/Credential, not both.");
            }
            if (server.CredentialLifetime <= TimeSpan.Zero)
            {
                throw new InvalidOperationException("Media:IceServers: CredentialLifetime must be positive.");
            }
        }
        var network = media.Value.Network;
        if ((network.MinPort != 0 || network.MaxPort != 0)
            && (network.MinPort < 1024 || network.MaxPort > 65535 || network.MinPort > network.MaxPort))
        {
            throw new InvalidOperationException(
                $"Media:Network needs 1024 ≤ MinPort ≤ MaxPort ≤ 65535, or both 0 (is {network.MinPort}–{network.MaxPort}).");
        }
        if (!string.IsNullOrWhiteSpace(network.PublicAddress) && !System.Net.IPAddress.TryParse(network.PublicAddress, out _))
        {
            throw new InvalidOperationException($"Media:Network:PublicAddress '{network.PublicAddress}' is not an IP address.");
        }
        if (media.Value.InactiveConnectionTimeout < TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"Media:InactiveConnectionTimeout must not be negative (is {media.Value.InactiveConnectionTimeout}); 0 disables it.");
        }
        if (media.Value.DeadConnectionTimeout < TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"Media:DeadConnectionTimeout must not be negative (is {media.Value.DeadConnectionTimeout}); 0 disables it.");
        }
        if (media.Value.IdleRoomTimeout < TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"Media:IdleRoomTimeout must not be negative (is {media.Value.IdleRoomTimeout}); 0 stops a room's media as soon as it empties.");
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

        if (flags.DeleteExistingRooms)
        {
            // Before Restore, so the deleted rooms never get media pipelines.
            var stored = roomStore.All();
            foreach (var room in stored)
            {
                roomStore.Delete(room.Id);
                activity.Event("room.deleted", room.Id, detail: $"{room.Name} (--delete-existing-rooms)");
            }
            log.LogWarning("--delete-existing-rooms: deleted {Count} stored rooms", stored.Count);
        }

        rooms.Restore();

        if (auth.Value.ApiKeys.Count(k => !string.IsNullOrWhiteSpace(k)) == 0 && !apiKeys.List().Any(k => k.RevokedAt is null))
        {
            log.LogWarning(
                "No API key is configured, so the control API is unusable. Create one with " +
                "'SpatialAudioServer apikey create <label>' or set Auth__ApiKeys__0");
        }
        if (!adminPassword.IsSet)
        {
            log.LogWarning("No admin password is set, so the admin UI (/admin/) cannot be used. Set one with " +
                "'SpatialAudioServer admin set-password'");
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Graceful stop (Ctrl+C, SIGTERM, Windows service stop): ends every participant's session with reason
    /// "server-shutdown" so the history is accurate, then stops all room pipelines in parallel.
    /// </summary>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        var ended = rooms.Shutdown();
        (roomMedia as IDisposable)?.Dispose();
        log.LogInformation("Stopped: ended {Count} participant sessions and stopped room media in {Ms} ms",
            ended, started.ElapsedMilliseconds);
        return Task.CompletedTask;
    }
}
