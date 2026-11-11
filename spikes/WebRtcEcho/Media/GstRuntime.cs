using Gst;
using Gst.Interop;
using Gst.Sdp;
using Gst.WebRTC;

namespace WebRtcEcho.Media;

/// <summary>Process-wide GStreamer start-up: module registration and plugin check.</summary>
public static class GstRuntime
{
    private static readonly string[] RequiredElements =
    [
        "webrtcbin", "nicesrc", "dtlssrtpenc", "opusenc", "opusdec", "rtpopuspay", "rtpopusdepay",
        "audiomixer", "audiotestsrc", "audioconvert", "audioresample", "audiopanorama", "audioamplify",
    ];

    private static int _initialised;

    public static void Initialize(ILogger log)
    {
        if (Interlocked.Exchange(ref _initialised, 1) != 0)
        {
            return;
        }

        // Each module initialiser registers its boxed types (WebRTCSessionDescription, SDPMessage);
        // they all end in the same gst_init.
        GstSdp.Initialize();
        GstWebRTC.Initialize();

        log.LogInformation("GStreamer {Version}", GstSharp.NativeVersion.Description);

        // A handler that throws on a GStreamer thread is caught by the binding and reported here.
        ExceptionTrap.UnhandledException += ex => log.LogError(ex, "Unhandled exception in a GStreamer callback");

        var missing = RequiredElements.Where(e => ElementFactory.Find(e) is null).ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException($"Missing GStreamer elements: {string.Join(", ", missing)}");
        }
    }
}
