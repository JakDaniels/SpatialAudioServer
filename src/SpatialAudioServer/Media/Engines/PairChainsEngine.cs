using System.Collections.Concurrent;
using Gst;
using SpatialAudioServer.Rooms;
using static SpatialAudioServer.Media.Engines.MediaBlocks;

namespace SpatialAudioServer.Media.Engines;

/// <summary>
/// The first topology: one GStreamer chain per (source, listener) pair, so every pair has its own gain and pan.
/// <code>
/// source (per participant):    webrtcbin src → OpusDecoder (mono 48k) → tee
/// chain (per source≠listener): tee → [queue (leaky) → audioamplify] → audiopanorama → listener audiomixer pad
/// listener (per participant):  silence + chains → audiomixer → OpusEncoder → webrtcbin sink
/// </code>
/// <see cref="Variant.Queued"/> has the bracketed elements: a queue gives every chain its own thread, so a stalled
/// listener cannot hold up a source. <see cref="Variant.Lite"/> drops them: gain is the mixer pad's volume and the
/// source's decoder thread pushes straight through every chain (threads ∝ participants, not pairs).
/// </summary>
internal sealed class PairChainsEngine(MixEngineContext context, PairChainsEngine.Variant variant) : IMixEngine
{
    public const string Name = "pairchains";
    public const string LiteName = "pairchains-lite";

    public enum Variant
    {
        Queued,
        Lite,
    }

    private readonly ConcurrentDictionary<Guid, Member> _members = new(); // written on the worker; read by AttachSource
    private readonly Dictionary<(Guid Source, Guid Listener), Chain> _chains = []; // worker only

    private Pipeline Pipeline => context.Pipeline;

    public int MixedPairs => _chains.Count;

    public IReadOnlyDictionary<string, long> Counters => new Dictionary<string, long>
    {
        ["chains"] = _chains.Count,
        ["encoders"] = _members.Values.Count(m => m.Listener is not null),
        ["decoders"] = _members.Values.Count(m => m.Source is not null),
    };

    public void ParticipantAdded(ParticipantSnapshot participant) =>
        _members.TryAdd(participant.Id, new Member(participant.Id) { Gain = Gain(participant) });

    public void ParticipantUpdated(ParticipantSnapshot participant)
    {
        if (!_members.TryGetValue(participant.Id, out var member))
        {
            return;
        }
        member.Gain = Gain(participant);
        foreach (var chain in _chains.Values.Where(c => c.Source == participant.Id))
        {
            chain.SetGain(member.Gain);
        }
    }

    public void ParticipantRemoved(Guid participantId) => _members.TryRemove(participantId, out _);

    public void AttachListener(Guid participantId, int payloadType, Pad rtcSink)
    {
        var member = _members[participantId];
        var id = ShortId(participantId);
        var mixer = Make("audiomixer", $"mix-{id}");
        mixer.SetProperty("latency", (ulong)context.Options.MixerLatencyMs * 1_000_000);
        var silence = Silence($"silence-{id}");
        var encoder = OpusEncoder($"egress-{id}", payloadType, context.Options);
        var listener = new ListenerPart(mixer, silence, encoder);
        member.Listener = listener;
        try
        {
            Add(Pipeline, mixer, silence, encoder);
            Link(silence.GetStaticPad("src"), mixer.RequestPadSimple("sink_%u"), "silence → mixer");
            Link(mixer.GetStaticPad("src"), encoder.GetStaticPad("sink"), "mixer → encoder");
            Link(encoder.GetStaticPad("src"), rtcSink, "encoder → webrtcbin");
            // Downstream first, so nothing pushes into an element that is not running yet.
            encoder.SyncStateWithParent();
            mixer.SyncStateWithParent();
            silence.SyncStateWithParent();
        }
        catch
        {
            ReleaseListener(member);
            throw;
        }

        // Everyone already sending is mixed into the newcomer's ear.
        foreach (var source in _members.Values)
        {
            EnsureChain(source, member);
        }
    }

    public void AttachSource(Guid participantId, Pad rtpSource)
    {
        // Streaming thread. The source part is handed over synchronously (under the member's lock), so a Release
        // that follows on the worker always sees it; only wiring it into the mixes is posted.
        if (!_members.TryGetValue(participantId, out var member))
        {
            return;
        }
        var id = ShortId(participantId);
        var decoder = OpusDecoder($"ingress-{id}");
        var tee = Make("tee", $"tee-{id}");
        tee.SetProperty("allow-not-linked", true);
        Add(Pipeline, tee, decoder);
        Link(decoder.GetStaticPad("src"), tee.GetStaticPad("sink"), "decoder → tee");
        tee.SyncStateWithParent();
        decoder.SyncStateWithParent();
        Link(rtpSource, decoder.GetStaticPad("sink"), "webrtcbin → decoder");
        member.Source = new SourcePart(decoder, tee);

        // Now mix this participant into everyone else's ear.
        context.Worker.Post(() =>
        {
            foreach (var listener in _members.Values)
            {
                EnsureChain(member, listener);
            }
        });
    }

    public void Disconnect(Guid participantId)
    {
        foreach (var chain in _chains.Values.Where(c => c.Source == participantId || c.Listener == participantId).ToList())
        {
            RemoveChain(chain);
        }
    }

    public void Release(Guid participantId)
    {
        if (!_members.TryGetValue(participantId, out var member))
        {
            return;
        }
        ReleaseListener(member);
        if (member.Source is { } source)
        {
            Remove(Pipeline, [source.Decoder, source.Tee]);
            member.Source = null;
        }
    }

    public void Dispose()
    {
        foreach (var chain in _chains.Values.ToList())
        {
            RemoveChain(chain);
        }
        foreach (var id in _members.Keys.ToList())
        {
            Release(id);
        }
        _members.Clear();
    }

    private static float Gain(ParticipantSnapshot p) => p.Muted ? 0f : (float)p.Gain;

    private void ReleaseListener(Member member)
    {
        if (member.Listener is { } listener)
        {
            Remove(Pipeline, [listener.Silence, listener.Mixer, listener.Encoder]);
            member.Listener = null;
        }
    }

    /// <summary>Adds the source→listener chain if both ends exist and it is not there yet.</summary>
    private void EnsureChain(Member source, Member listener)
    {
        if (source.Id == listener.Id // mix-minus: nobody hears themselves
            || _chains.ContainsKey((source.Id, listener.Id))
            || source.Source is not { } sourcePart
            || listener.Listener is not { } listenerPart)
        {
            return;
        }

        var description = variant == Variant.Queued
            ? $"queue max-size-buffers=0 max-size-bytes=0 max-size-time={(ulong)context.Options.ChainQueueMs * 1_000_000} leaky=downstream ! " +
              "audioamplify name=amp ! audiopanorama name=pan"
            : "audiopanorama name=pan";
        var bin = Global.ParseBinFromDescription(description, ghostUnlinkedPads: true);
        bin.SetName($"chain-{ShortId(source.Id)}-{ShortId(listener.Id)}");
        var chain = new Chain(source.Id, listener.Id, bin, sourcePart.Tee, listenerPart.Mixer);

        Add(Pipeline, bin);
        chain.MixerPad = listenerPart.Mixer.RequestPadSimple("sink_%u") ?? throw new InvalidOperationException("No mixer pad.");
        chain.SetGain(source.Gain);
        chain.SetPan(0f); // centre until the spatial policy (Phase 6) supplies one
        Link(bin.GetStaticPad("src"), chain.MixerPad, "chain → mixer");
        bin.SyncStateWithParent();
        chain.TeePad = sourcePart.Tee.RequestPadSimple("src_%u") ?? throw new InvalidOperationException("No tee pad.");
        Link(chain.TeePad, bin.GetStaticPad("sink"), "tee → chain");

        _chains[(source.Id, listener.Id)] = chain;
    }

    /// <summary>Removes a chain while the pipeline plays: unlink from the tee when idle, stop, release both pads.</summary>
    private void RemoveChain(Chain chain)
    {
        _chains.Remove((chain.Source, chain.Listener));
        if (chain.TeePad is { } teePad && chain.Bin.GetStaticPad("sink") is { } chainSink)
        {
            UnlinkWhenIdle(teePad, chainSink, context.Log, chain.Bin.Name ?? "chain");
            chain.Tee.ReleaseRequestPad(teePad);
        }
        chain.Bin.SetState(State.Null);
        if (chain.MixerPad is { } mixerPad)
        {
            chain.Bin.GetStaticPad("src")?.Unlink(mixerPad);
            chain.Mixer.ReleaseRequestPad(mixerPad);
        }
        Pipeline.Remove(chain.Bin);
    }

    private sealed class Member(Guid id)
    {
        public Guid Id { get; } = id;
        public float Gain { get; set; } = 1f;
        public ListenerPart? Listener { get; set; }

        /// <summary>Set by AttachSource (streaming thread), read and cleared on the worker.</summary>
        public SourcePart? Source
        {
            get => Volatile.Read(ref _source);
            set => Volatile.Write(ref _source, value);
        }

        private SourcePart? _source;
    }

    private sealed record ListenerPart(Element Mixer, Element Silence, Bin Encoder);

    private sealed record SourcePart(Bin Decoder, Element Tee);

    private sealed class Chain(Guid source, Guid listener, Bin bin, Element tee, Element mixer)
    {
        public Guid Source { get; } = source;
        public Guid Listener { get; } = listener;
        public Bin Bin { get; } = bin;
        public Element Tee { get; } = tee;
        public Element Mixer { get; } = mixer;
        public Pad? TeePad { get; set; }
        public Pad? MixerPad { get; set; }

        public void SetGain(float gain)
        {
            if (Bin.GetByName("amp") is { } amp)
            {
                amp.SetProperty("amplification", gain);
            }
            else
            {
                MixerPad?.SetProperty("volume", (double)gain);
            }
        }

        public void SetPan(float pan) => Bin.GetByName("pan")!.SetProperty("panorama", pan);
    }
}
