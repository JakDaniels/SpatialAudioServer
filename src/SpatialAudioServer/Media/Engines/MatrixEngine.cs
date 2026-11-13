using System.Collections.Concurrent;
using Gst;
using SpatialAudioServer.Rooms;
using static SpatialAudioServer.Media.Engines.MediaBlocks;

namespace SpatialAudioServer.Media.Engines;

/// <summary>
/// Matrix mixing: the room's sources are interleaved into one multichannel stream (<see cref="RoomInterleave"/>), and
/// each listener's stereo mix is a gain matrix over it. No per-pair elements; per-pair gain and pan (spatial rooms:
/// from positions, see <see cref="Spatial.ISpatialPolicy"/>) are matrix coefficients. One Opus encoder per listener.
/// <code>
/// room:      RoomInterleave (silence + one slot per source) → tee
/// listener:  tee → MatrixMixer (queue → audiomixmatrix; own slot off) → OpusEncoder → webrtcbin sink
/// </code>
/// </summary>
internal sealed class MatrixEngine(MixEngineContext context) : IMixEngine
{
    public const string Name = "matrix";

    private readonly ConcurrentDictionary<Guid, Member> _members = new(); // written on the worker; read by AttachSource
    private readonly RoomInterleave _room = new(context);

    private Pipeline Pipeline => context.Pipeline;

    public int MixedPairs
    {
        get
        {
            var sources = _members.Values.Where(m => m.Source is { Slot: > 0 }).Select(m => m.Id).ToList();
            var listeners = _members.Values.Where(m => m.Listener is not null).Select(m => m.Id).ToList();
            return sources.Count * listeners.Count - sources.Intersect(listeners).Count();
        }
    }

    public IReadOnlyDictionary<Guid, double> InputLevels => _room.Levels;

    public IReadOnlyDictionary<string, long> Counters => new Dictionary<string, long>
    {
        ["slots"] = _room.Channels - 1,
        ["slotsInUse"] = _room.SlotsInUse,
        ["encoders"] = _members.Values.Count(m => m.Listener is not null),
    };

    public void ParticipantAdded(ParticipantSnapshot participant) =>
        _members.TryAdd(participant.Id, new Member(participant));

    public void ParticipantUpdated(ParticipantSnapshot participant)
    {
        if (_members.TryGetValue(participant.Id, out var member))
        {
            member.Snapshot = participant;
            UpdateMatrices();
        }
    }

    public void ParticipantRemoved(Guid participantId) => _members.TryRemove(participantId, out _);

    public void AttachListener(Guid participantId, int payloadType, Pad rtcSink)
    {
        var member = _members[participantId];
        var id = ShortId(participantId);
        var listener = new ListenerPart(new MatrixMixer(id, _room.Channels, context), OpusEncoder($"egress-{id}", payloadType, context.Options));
        member.Listener = listener;
        try
        {
            var (left, right) = GainsFor(member);
            listener.Mixer.SetGains(left, right, running: false);
            listener.Mixer.AddTo(Pipeline);
            Add(Pipeline, listener.Encoder);
            Link(listener.Mixer.Src, listener.Encoder.GetStaticPad("sink"), "matrix → encoder");
            Link(listener.Encoder.GetStaticPad("src"), rtcSink, "encoder → webrtcbin");
            listener.Encoder.SyncStateWithParent();
            listener.Mixer.Sync();
            listener.TeePad = _room.RequestTeePad();
            Link(listener.TeePad, listener.Mixer.Sink, "tee → listener");
        }
        catch
        {
            Disconnect(participantId);
            Release(participantId);
            throw;
        }
    }

    public void AttachSource(Guid participantId, Pad rtpSource)
    {
        // Streaming thread: take a slot and link synchronously; only the matrix updates go to the worker.
        if (_members.TryGetValue(participantId, out var member))
        {
            member.Source = _room.Attach(participantId, rtpSource);
            context.Worker.Post(UpdateMatrices);
        }
    }

    public void Disconnect(Guid participantId)
    {
        if (!_members.TryGetValue(participantId, out var member))
        {
            return;
        }
        if (member.Listener is { TeePad: { } teePad } listener)
        {
            _room.ReleaseTeePad(teePad, listener.Mixer.Sink, $"tee → {listener.Mixer.Queue.Name}");
            listener.TeePad = null;
        }
        if (member.Source is { Slot: > 0 } source)
        {
            member.Source = _room.Detach(source);
            UpdateMatrices();
        }
    }

    public void Release(Guid participantId)
    {
        if (!_members.TryGetValue(participantId, out var member))
        {
            return;
        }
        if (member.Listener is { } listener)
        {
            Remove(Pipeline, [.. listener.Mixer.Elements, listener.Encoder]);
            member.Listener = null;
        }
        if (member.Source is { } source)
        {
            _room.Release(source);
            member.Source = null;
        }
    }

    public void Dispose()
    {
        foreach (var id in _members.Keys.ToList())
        {
            Disconnect(id);
            Release(id);
        }
        _members.Clear();
        _room.Dispose();
    }

    /// <summary>Worker: recomputes every listener's matrix.</summary>
    private void UpdateMatrices()
    {
        foreach (var member in _members.Values)
        {
            if (member.Listener is { } listener)
            {
                var (left, right) = GainsFor(member);
                listener.Mixer.SetGains(left, right);
            }
        }
    }

    /// <summary>
    /// Each other connected source as this listener hears it (gain, and position in spatial rooms); everything else
    /// (own slot, free slots, silence) off.
    /// </summary>
    private (double[] Left, double[] Right) GainsFor(Member listener) =>
        MatrixMixer.Rows(_room.Channels, _members.Values
            .Where(source => source.Id != listener.Id && source.Source is { Slot: > 0 })
            .Select(source => (source.Source!.Slot, context.Render(source.Snapshot, listener.Snapshot))));

    private sealed class Member(ParticipantSnapshot snapshot)
    {
        public Guid Id { get; } = snapshot.Id;
        public ParticipantSnapshot Snapshot { get; set; } = snapshot;
        public ListenerPart? Listener { get; set; }

        /// <summary>Set by AttachSource (streaming thread); read and changed on the worker.</summary>
        public SourceSlot? Source
        {
            get => Volatile.Read(ref _source);
            set => Volatile.Write(ref _source, value);
        }

        private SourceSlot? _source;
    }

    private sealed class ListenerPart(MatrixMixer mixer, Bin encoder)
    {
        public MatrixMixer Mixer { get; } = mixer;
        public Bin Encoder { get; } = encoder;
        public Pad? TeePad { get; set; }
    }
}
