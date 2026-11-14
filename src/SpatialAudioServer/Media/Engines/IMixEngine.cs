using Gst;
using SpatialAudioServer.Configuration;
using SpatialAudioServer.Rooms;

namespace SpatialAudioServer.Media.Engines;

/// <summary>
/// A room's mixing topology: everything between each participant's incoming RTP (from webrtcbin) and the RTP sent
/// back to them. <see cref="RoomPipeline"/> owns the webrtcbin endpoints, signalling, ICE and the order of teardown;
/// an engine owns decode, mix and encode, and can arrange them however it likes (per-pair chains, shared encoders,
/// a mixer in C#, …) as long as every listener hears every other unmuted participant and not themselves.
/// </summary>
/// <remarks>
/// <para>Threading: every member runs on the room's worker (<see cref="MixEngineContext.Worker"/>) except
/// <see cref="AttachSource"/>, which runs on a webrtcbin streaming thread. <see cref="AttachSource"/> for a
/// participant always happens before <see cref="Disconnect"/> for that participant, or not at all.</para>
/// <para>Lifecycle per participant: <see cref="ParticipantAdded"/> → (offer) <see cref="AttachListener"/> →
/// (audio arrives) <see cref="AttachSource"/> → … <see cref="Disconnect"/> → <see cref="Release"/>, and the
/// attach…release cycle may repeat (a re-offer rebuilds the endpoint) before <see cref="ParticipantRemoved"/>.</para>
/// </remarks>
internal interface IMixEngine : IDisposable
{
    /// <summary>The participant joined the room (no media yet). Keep its gain/mute for when it does.</summary>
    void ParticipantAdded(ParticipantSnapshot participant);

    /// <summary>Mute, gain, position or orientation changed.</summary>
    void ParticipantUpdated(ParticipantSnapshot participant);

    /// <summary>The participant left the room. Its media has already been released.</summary>
    void ParticipantRemoved(Guid participantId);

    /// <summary>
    /// The participant has offered: build what feeds <paramref name="rtcSink"/> (webrtcbin's send pad for the
    /// negotiated transceiver) with Opus RTP of payload type <paramref name="payloadType"/>, link it and start it.
    /// The pipeline is PLAYING; start downstream elements before upstream ones. Throwing fails the offer.
    /// </summary>
    void AttachListener(Guid participantId, int payloadType, Pad rtcSink);

    /// <summary>
    /// Streaming thread: the participant's Opus RTP has started arriving on <paramref name="rtpSource"/>. Link it to
    /// something running before returning (webrtcbin pushes as soon as this returns). Post anything slow, such as
    /// wiring the source into other participants' mixes, to the worker.
    /// </summary>
    void AttachSource(Guid participantId, Pad rtpSource);

    /// <summary>
    /// Worker, with everything still running: take the participant out of every other participant's mix and stop
    /// mixing into their ear. Their webrtcbin is stopped and removed after this returns.
    /// </summary>
    void Disconnect(Guid participantId);

    /// <summary>Worker, after the participant's webrtcbin has stopped: remove their remaining elements.</summary>
    void Release(Guid participantId);

    /// <summary>
    /// Worker: (source, listener) pairs currently being mixed. Engine-neutral, so the shared tests can check the
    /// mix-minus bookkeeping of every engine (n connected participants → n(n−1)).
    /// </summary>
    int MixedPairs { get; }

    /// <summary>Worker: engine-specific counters for stats and comparison (element counts, encoders, …).</summary>
    IReadOnlyDictionary<string, long> Counters { get; }

    /// <summary>Each participant's incoming audio level (RMS, 0–1 full scale, peak-held); empty if the engine has none.</summary>
    IReadOnlyDictionary<Guid, double> InputLevels { get; }
}

/// <summary>What an engine is given to work with. One per room.</summary>
internal sealed class MixEngineContext(
    Guid roomId, int maxParticipants, Pipeline pipeline, RoomWorker worker, MediaOptions options, ILogger log, bool spatial = false)
{
    /// <summary>The room's spatial policy, or null for a non-spatial room (everyone centred at their gain).</summary>
    public Spatial.ISpatialPolicy? Spatial { get; } = spatial ? new Spatial.InverseDistancePolicy(options.Spatial) : null;

    /// <summary>
    /// How <paramref name="source"/> sounds to <paramref name="listener"/>, including the source's gain/mute. Sources are
    /// heard from their position; listeners hear from their listener pose if they have one, else their own. A viewer's
    /// own report (tethered rooms) is used within the tethers (<see cref="Spatial.EffectivePose"/>).
    /// </summary>
    public Spatial.Rendering Render(ParticipantSnapshot source, ParticipantSnapshot listener)
    {
        if (Spatial is null)
        {
            return Media.Spatial.Rendering.Centre.Scale(source.Muted ? 0 : source.Gain);
        }
        var from = Media.Spatial.EffectivePose.Of(source, Options.Spatial);
        var to = Media.Spatial.EffectivePose.Of(listener, Options.Spatial);
        return Spatial.Render(from.Speaker, to.Listener, to.ListenerOrientation).Scale(source.Muted ? 0 : source.Gain);
    }

    public Guid RoomId { get; } = roomId;

    /// <summary>The room's participant limit (fixed for its life): engines may size fixed resources by it.</summary>
    public int MaxParticipants { get; } = maxParticipants;

    /// <summary>The room's pipeline, already PLAYING. Add elements to it and sync their state.</summary>
    public Pipeline Pipeline { get; } = pipeline;

    /// <summary>The room's worker thread: post graph changes here from streaming threads.</summary>
    public RoomWorker Worker { get; } = worker;

    public MediaOptions Options { get; } = options;

    public ILogger Log { get; } = log;
}
