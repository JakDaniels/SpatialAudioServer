using System.Globalization;
using Gst;
using SpatialAudioServer.Configuration;

namespace SpatialAudioServer.Media.Engines;

/// <summary>Building blocks engines share: Opus decode/encode branches, silence, linking and live unlinking.</summary>
internal static class MediaBlocks
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Caps of the RTP webrtcbin expects on a send pad.</summary>
    public static string SendCaps(int payloadType) =>
        $"application/x-rtp,media=audio,encoding-name=OPUS,payload={payloadType},clock-rate=48000";

    /// <summary>Opus RTP in → raw mono 48 kHz out (sink and src ghost pads).</summary>
    public static Bin OpusDecoder(string name)
    {
        var bin = Global.ParseBinFromDescription(
            "queue ! rtpopusdepay ! opusdec plc=true ! audioconvert ! audioresample ! " +
            "capsfilter caps=\"audio/x-raw,rate=48000,channels=1\"",
            ghostUnlinkedPads: true);
        bin.SetName(name);
        return bin;
    }

    /// <summary>Raw stereo in → S16 stereo 48 kHz → Opus → RTP with the negotiated payload type out.</summary>
    public static Bin OpusEncoder(string name, int payloadType, MediaOptions options)
    {
        var bin = Global.ParseBinFromDescription(
            "audioconvert ! capsfilter caps=\"audio/x-raw,format=S16LE,rate=48000,channels=2\" ! " +
            $"opusenc bitrate={options.OpusBitrate.ToString(CultureInfo.InvariantCulture)} inband-fec=true ! " +
            $"rtpopuspay pt={payloadType} ! " +
            $"capsfilter caps=\"{SendCaps(payloadType)}\"",
            ghostUnlinkedPads: true);
        bin.SetName(name);
        return bin;
    }

    /// <summary>A live silent source: keeps a mixer (and so the outgoing stream) running when nobody is talking.</summary>
    public static Element Silence(string name)
    {
        var silence = Make("audiotestsrc", name);
        silence.SetProperty("is-live", true);
        Global.UtilSetObjectArg(silence, "wave", "silence");
        return silence;
    }

    public static Element Make(string factory, string name) =>
        ElementFactory.Make(factory, name) ?? throw new InvalidOperationException($"GStreamer element {factory} is missing.");

    public static void Add(Pipeline pipeline, params Element[] elements)
    {
        foreach (var element in elements)
        {
            if (!pipeline.Add(element))
            {
                throw new InvalidOperationException($"Could not add {element.Name} to the room pipeline.");
            }
        }
    }

    public static void Link(Pad? source, Pad? sink, string what)
    {
        if (source is null || sink is null)
        {
            throw new InvalidOperationException($"Linking {what}: missing pad.");
        }
        var result = source.Link(sink);
        if (result != PadLinkReturn.Ok)
        {
            throw new InvalidOperationException($"Linking {what}: {result}.");
        }
    }

    /// <summary>
    /// Unlinks a running source pad (typically a tee request pad) from an IDLE probe, so no buffer is mid-push.
    /// Falls back to a plain unlink if the pad never goes idle within 2 s (a blocked downstream).
    /// </summary>
    public static void UnlinkWhenIdle(Pad source, Pad sink, ILogger log, string what)
    {
        var unlinked = new ManualResetEventSlim();
        source.AddProbe(PadProbeType.Idle, (pad, _) =>
        {
            pad.Unlink(sink);
            unlinked.Set();
            return PadProbeReturn.Remove;
        });
        if (!unlinked.Wait(ProbeTimeout))
        {
            log.LogWarning("Idle probe timed out unlinking {What}; unlinking anyway", what);
            source.Unlink(sink);
        }
    }

    /// <summary>Stops the elements and removes them from the pipeline (in the order given).</summary>
    public static void Remove(Pipeline pipeline, IEnumerable<Element?> elements)
    {
        var list = elements.OfType<Element>().ToList();
        foreach (var element in list)
        {
            element.SetState(State.Null);
        }
        foreach (var element in list)
        {
            pipeline.Remove(element);
        }
    }

    public static string ShortId(Guid id) => id.ToString("N")[..8];
}
