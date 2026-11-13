using Microsoft.Extensions.Options;
using SpatialAudioServer.Configuration;
using SpatialAudioServer.Rooms;

namespace SpatialAudioServer.Hosting;

/// <summary>
/// Removes participants whose WebRTC connection stayed failed or disconnected for Media:DeadConnectionTimeout:
/// clients that vanished without leaving (closed lid, lost network, crashed browser). Without this they would keep
/// their mixer chains and encoders running until someone deleted them.
/// </summary>
internal sealed class DeadConnectionSweeper(
    RoomManager rooms,
    IOptions<MediaOptions> media,
    TimeProvider time,
    ILogger<DeadConnectionSweeper> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var timeout = media.Value.DeadConnectionTimeout;
        if (timeout <= TimeSpan.Zero)
        {
            log.LogInformation("Dead-connection removal is off (Media:DeadConnectionTimeout = 0)");
            return;
        }

        // Check often enough that a participant goes within about a quarter of the timeout of reaching it.
        var interval = TimeSpan.FromMilliseconds(Math.Clamp(timeout.TotalMilliseconds / 4, 250, 5000));
        using var timer = new PeriodicTimer(interval, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    rooms.DropDeadConnections(timeout);
                }
                catch (Exception ex)
                {
                    log.LogError(ex, "Dead-connection sweep failed");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
