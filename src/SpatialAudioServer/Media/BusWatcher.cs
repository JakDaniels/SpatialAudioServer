using Gst;
using Gst.GLib;

namespace SpatialAudioServer.Media;

/// <summary>
/// Polls a pipeline's bus on its own thread: logs errors and warnings, and redistributes latency.
/// </summary>
/// <remarks>
/// Latency handling is essential here, not cosmetic. Elements are added to pipelines that are already PLAYING
/// (endpoints, ingress, chains), and each addition posts LATENCY; the application must then call
/// <c>gst_bin_recalculate_latency</c>. Without it the RTP sessions cannot compute running times ("Can't determine
/// running time for this packet without knowing configured latency") and receive paths can stall entirely.
/// </remarks>
public sealed class BusWatcher : IDisposable
{
    private readonly Pipeline _pipeline;
    private readonly string _label;
    private readonly ILogger _log;
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _thread;

    public BusWatcher(Pipeline pipeline, string label, ILogger log)
    {
        _pipeline = pipeline;
        _label = label;
        _log = log;
        _thread = new Thread(Poll) { IsBackground = true, Name = $"bus-{label}" };
        _thread.Start();
    }

    public void Dispose()
    {
        _stop.Cancel();
        _thread.Join();
        _stop.Dispose();
    }

    private void Poll()
    {
        using var bus = _pipeline.GetBus();
        var interval = ClockTime.FromMilliseconds(100);
        while (!_stop.IsCancellationRequested)
        {
            using var message = bus.TimedPopFiltered(interval, MessageType.Error | MessageType.Warning | MessageType.Latency);
            if (message is null)
            {
                continue;
            }
            switch (message.Type)
            {
                case MessageType.Latency:
                    if (!_pipeline.RecalculateLatency())
                    {
                        _log.LogDebug("{Label}: latency recalculation failed (an element is not ready yet)", _label);
                    }
                    break;
                case MessageType.Error:
                    {
                        (GException error, string? debug) = message.ParseError();
                        _log.LogError("{Label}: GStreamer error from {Source}: {Message} ({Debug})", _label, message.SourceName, error.Message, debug);
                        break;
                    }
                default:
                    {
                        (GException warning, string? debug) = message.ParseWarning();
                        _log.LogWarning("{Label}: GStreamer warning from {Source}: {Message} ({Debug})", _label, message.SourceName, warning.Message, debug);
                        break;
                    }
            }
        }
    }
}
