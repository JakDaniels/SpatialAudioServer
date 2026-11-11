using Gst;

namespace WebRtcEcho.Media;

/// <summary>
/// Server side of one browser connection: the server answers the browser's offer and sends the decoded
/// microphone audio straight back.
/// </summary>
/// <remarks>
/// Shape mirrors the planned egress: a silent live source keeps an audiomixer producing output, so the
/// Opus stream runs before any media arrives, and the incoming stream is added to the mixer on pad-added
/// (the dynamic-add path of the real per-listener mix).
/// </remarks>
public sealed class EchoSession : WebRtcPeer
{
    private const string ReceiveBranch =
        "queue ! rtpopusdepay ! opusdec plc=true ! audioconvert ! audioresample ! " +
        "capsfilter caps=\"audio/x-raw,rate=48000,channels=2\""; // bare caps can't end a bin description

    private int _branchBuilt;

    private EchoSession(int payloadType, ILogger log)
        : base(Describe(payloadType), log)
    {
    }

    public string Id { get; } = Guid.NewGuid().ToString("N");

    public string Answer { get; private set; } = "";

    /// <summary>Builds the pipeline, starts it and answers the offer.</summary>
    public static EchoSession Create(string offerSdp, ILogger log)
    {
        // The send branch is linked before the offer arrives, so its payload type has to be the one the
        // browser offered for Opus (Chrome uses 111, Firefox 109).
        var payloadType = OpusPayloadType(offerSdp) ?? throw new ArgumentException("The offer has no Opus.");
        var session = new EchoSession(payloadType, log);
        try
        {
            session.Start();
            session.Answer = session.AnswerOffer(offerSdp);
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    private static string Describe(int pt) =>
        "webrtcbin name=webrtc bundle-policy=max-bundle " +
        "audiotestsrc is-live=true wave=silence ! audio/x-raw,rate=48000,channels=2 ! " +
        "audiomixer name=mix latency=40000000 ! audioconvert ! audioresample ! audio/x-raw,rate=48000,channels=2 ! " +
        $"opusenc ! rtpopuspay pt={pt} ! application/x-rtp,media=audio,encoding-name=OPUS,payload={pt},clock-rate=48000 ! " +
        "webrtc.";

    protected override void OnPadAdded(Pad pad)
    {
        // One audio m-line, so one branch.
        if (Interlocked.Exchange(ref _branchBuilt, 1) != 0)
        {
            return;
        }

        using var branch = Global.ParseBinFromDescription(ReceiveBranch, ghostUnlinkedPads: true);
        if (!Pipeline.Add(branch))
        {
            throw new InvalidOperationException("The pipeline did not take the receive branch.");
        }

        var mixer = Pipeline.GetByName("mix") ?? throw new InvalidOperationException("No mixer.");
        var mixerPad = mixer.RequestPadSimple("sink_%u") ?? throw new InvalidOperationException("No mixer pad.");
        var branchSrc = branch.GetStaticPad("src") ?? throw new InvalidOperationException("No branch src.");
        var branchSink = branch.GetStaticPad("sink") ?? throw new InvalidOperationException("No branch sink.");

        // Downstream first, then sync state, then feed it.
        var linked = branchSrc.Link(mixerPad);
        if (linked != PadLinkReturn.Ok)
        {
            throw new InvalidOperationException($"Linking the branch to the mixer: {linked}");
        }
        branch.SyncStateWithParent();
        linked = pad.Link(branchSink);
        if (linked != PadLinkReturn.Ok)
        {
            throw new InvalidOperationException($"Linking {pad.Name} to the branch: {linked}");
        }
        Log.LogInformation("Incoming audio linked to the mixer via {Pad}", mixerPad.Name);
    }
}
