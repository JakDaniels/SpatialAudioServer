using Microsoft.Extensions.Options;
using SpatialAudioServer.Configuration;
using SpatialAudioServer.Rooms;

namespace SpatialAudioServer.Hosting;

/// <summary>
/// Removes participants that are gone in all but name:
/// <list type="bullet">
/// <item>whose WebRTC connection stayed failed or disconnected for Media:DeadConnectionTimeout: clients that vanished
/// without leaving (closed lid, lost network, crashed browser);</item>
/// <item>whose join token expired unused (Auth:JoinTokenLifetime): the client never connected and now cannot.</item>
/// </list>
/// Without this they would keep their place in the room (and their mixer chains and encoders) until someone removed them.
/// </summary>
internal sealed class ParticipantSweeper(
    RoomManager rooms,
    IOptions<MediaOptions> media,
    TimeProvider time,
    ILogger<ParticipantSweeper> log) : BackgroundService
{
    private static readonly TimeSpan MaxInterval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var deadTimeout = media.Value.DeadConnectionTimeout;
        if (deadTimeout <= TimeSpan.Zero)
        {
            log.LogInformation("Dead-connection removal is off (Media:DeadConnectionTimeout = 0)");
        }

        // Often enough that a dead participant goes within about a quarter of the timeout of reaching it.
        var interval = deadTimeout > TimeSpan.Zero
            ? TimeSpan.FromMilliseconds(Math.Clamp(deadTimeout.TotalMilliseconds / 4, 250, MaxInterval.TotalMilliseconds))
            : MaxInterval;
        using var timer = new PeriodicTimer(interval, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    if (deadTimeout > TimeSpan.Zero)
                    {
                        rooms.DropDeadConnections(deadTimeout);
                    }
                    rooms.DropExpiredJoins();
                }
                catch (Exception ex)
                {
                    log.LogError(ex, "Participant sweep failed");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
