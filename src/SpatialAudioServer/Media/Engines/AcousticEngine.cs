// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Gst;
using SpatialAudioServer.Rooms;
using static SpatialAudioServer.Media.Engines.MediaBlocks;

namespace SpatialAudioServer.Media.Engines;

/// <summary>
/// The whole room's mix in one <c>acousticmultimixer</c> element (its own plugin and repository,
/// github.com/JakDaniels/acousticmultimixer): each participant has a slot N, their voice goes into its <c>sink_N</c>
/// and what they hear comes out of its <c>mix_N</c>, stereo, everyone else placed by distance and direction. Gains,
/// mutes, poses and a viewer's own mutes and volumes are the slot pad's properties; the element computes the
/// rendering itself with the same formulas as <see cref="Spatial.DistancePolicy"/>. One Opus encoder per listener.
/// <code>
/// room:      silence → acousticmultimixer.sink_0 (muted: keeps the live mixer running when nobody talks)
/// source:    webrtcbin → OpusDecoder → acousticmultimixer.sink_N
/// listener:  acousticmultimixer.mix_N → queue → OpusEncoder → webrtcbin
/// </code>
/// Available only when the plugin is installed (<see cref="MixEngines"/>).
/// </summary>
internal sealed class AcousticEngine : IMixEngine
{
    public const string Name = "acoustic";

    /// <summary>The GStreamer element this engine needs.</summary>
    public const string Element = "acousticmultimixer";

    private const string MonoCaps = "audio/x-raw,format=F32LE,layout=interleaved,rate=48000,channels=1";

    private readonly MixEngineContext _context;
    private readonly ConcurrentDictionary<Guid, Member> _members = new();
    private readonly ConcurrentDictionary<Guid, LevelMeter> _levels = new();
    private readonly Element _mixer;
    private readonly Element _silence;
    private readonly Element _silenceCaps;
    private readonly Member?[] _slots; // [0] is the silence; under _slotLock
    private readonly Lock _slotLock = new();
    private readonly Lock _applyLock = new();
    private bool _updatePosted; // worker: an update is queued

    public AcousticEngine(MixEngineContext context)
    {
        _context = context;
        var id = context.RoomId.ToString("N")[..8];
        _slots = new Member?[context.MaxParticipants + 1];
        _mixer = ElementFactory.MakeWithProperties(Element, new Dictionary<string, object?>
        {
            ["name"] = $"acoustic-{id}",
            ["max-participants"] = (uint)_slots.Length,
        }) ?? throw new InvalidOperationException($"GStreamer element {Element} is missing.");
        _mixer.SetProperty("latency", (ulong)context.Options.MixerLatencyMs * 1_000_000);
        if (context.SpatialSettings is { } spatial)
        {
            Global.UtilSetObjectArg(_mixer, "distance-model", spatial.DistanceModel.ToString().ToLowerInvariant());
            _mixer.SetProperty("reference-distance", spatial.ReferenceDistance);
            _mixer.SetProperty("rolloff", spatial.Rolloff);
            _mixer.SetProperty("max-range", spatial.MaxRange);
        }

        SetRoom(context.Acoustics);

        _silence = Silence($"silence-{id}");
        _silenceCaps = Make("capsfilter", $"silencecaps-{id}");
        using (var caps = Caps.FromString(MonoCaps))
        {
            _silenceCaps.SetProperty("caps", caps);
        }
        Add(context.Pipeline, _mixer, _silenceCaps, _silence);
        var silencePad = _mixer.RequestPadSimple("sink_0") ?? throw new InvalidOperationException("No mixer pad for the silence.");
        silencePad.SetProperty("mute", true);
        Link(_silenceCaps.GetStaticPad("src"), silencePad, "silence → mixer");
        Link(_silence.GetStaticPad("src"), _silenceCaps.GetStaticPad("sink"), "silence → caps");
        foreach (var element in new[] { _mixer, _silenceCaps, _silence })
        {
            element.SyncStateWithParent();
        }
    }

    private Pipeline Pipeline => _context.Pipeline;

    public int MixedPairs
    {
        get
        {
            var sources = _members.Values.Where(m => m.SourceLinked).Select(m => m.Id).ToList();
            var listeners = _members.Values.Where(m => m.Listener is { Linked: true }).Select(m => m.Id).ToList();
            return sources.Count * listeners.Count - sources.Intersect(listeners).Count();
        }
    }

    public IReadOnlyDictionary<string, long> Counters
    {
        get
        {
            lock (_slotLock)
            {
                return new Dictionary<string, long>
                {
                    ["slots"] = _slots.Length - 1,
                    ["slotsInUse"] = _slots.Skip(1).Count(m => m is not null),
                    ["encoders"] = _members.Values.Count(m => m.Listener is not null),
                };
            }
        }
    }

    public IReadOnlyDictionary<Guid, double> InputLevels => _levels.ToDictionary(l => l.Key, l => l.Value.Level);

    public void RoomUpdated(RoomSnapshot room) => SetRoom(room.Acoustics);

    /// <summary>
    /// The element's <c>room</c> (its early reflections) from the room's acoustics; null is outdoor. A plugin from
    /// before rooms existed has no such property, and every room sounds outdoor.
    /// </summary>
    private void SetRoom(string? acoustics)
    {
        if (NativeGObject.HasProperty(_mixer.Handle, "room"))
        {
            Global.UtilSetObjectArg(_mixer, "room", acoustics ?? Spatial.RoomAcoustics.Outdoor);
        }
        else if (acoustics is not null)
        {
            _context.Log.LogWarning("Room {RoomId}: acoustics {Acoustics} needs a newer acousticmultimixer plugin; it sounds outdoor",
                _context.RoomId, acoustics);
        }
    }

    public void ParticipantAdded(ParticipantSnapshot participant) => _members.TryAdd(participant.Id, new Member(participant));

    public void ParticipantUpdated(ParticipantSnapshot participant)
    {
        if (!_members.TryGetValue(participant.Id, out var member))
        {
            return;
        }
        member.Snapshot = participant;
        // Poses arrive often (each viewer up to 10/s, the simulator for everyone at once): one update per burst.
        if (!_updatePosted)
        {
            _updatePosted = true;
            _context.Worker.Post(() =>
            {
                _updatePosted = false;
                ApplyAll();
            });
        }
    }

    public void ParticipantRemoved(Guid participantId) => _members.TryRemove(participantId, out _);

    public void AttachListener(Guid participantId, int payloadType, Pad rtcSink)
    {
        var member = _members[participantId];
        var id = ShortId(participantId);
        var queue = Make("queue", $"q-{id}");
        queue.SetProperty("max-size-buffers", 0u);
        queue.SetProperty("max-size-bytes", 0u);
        queue.SetProperty("max-size-time", (ulong)_context.Options.ChainQueueMs * 1_000_000);
        Global.UtilSetObjectArg(queue, "leaky", "downstream");
        var listener = new ListenerPart(queue, OpusEncoder($"egress-{id}", payloadType, _context.Options));
        member.Listener = listener;
        try
        {
            var slot = TakeSlot(member);
            Add(Pipeline, queue, listener.Encoder);
            Link(queue.GetStaticPad("src"), listener.Encoder.GetStaticPad("sink"), "queue → encoder");
            Link(listener.Encoder.GetStaticPad("src"), rtcSink, "encoder → webrtcbin");
            listener.Encoder.SyncStateWithParent();
            queue.SyncStateWithParent();
            // Linking the output is what makes the mixer mix it.
            Link(slot.MixPad, queue.GetStaticPad("sink"), "mixer → listener");
            listener.Linked = true;
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
        // Streaming thread: link synchronously (webrtcbin pushes as soon as this returns).
        if (!_members.TryGetValue(participantId, out var member))
        {
            return;
        }
        var id = ShortId(participantId);
        var decoder = OpusDecoder($"ingress-{id}", "F32LE");
        Add(Pipeline, decoder);
        var meter = _levels[participantId] = new LevelMeter();
        var src = decoder.GetStaticPad("src");
        AddLevelProbe(src, rms => meter.Add(rms));
        member.Source = decoder;
        var slot = TakeSlot(member);
        Link(src, slot.SinkPad, "source → mixer");
        member.SourceLinked = true;
        decoder.SyncStateWithParent();
        Link(rtpSource, decoder.GetStaticPad("sink"), "webrtcbin → source");
    }

    public void Disconnect(Guid participantId)
    {
        if (!_members.TryGetValue(participantId, out var member) || member.Slot is not { } slot)
        {
            return;
        }
        if (member.Listener is { Linked: true } listener)
        {
            if (listener.Queue.GetStaticPad("sink") is { } queueSink)
            {
                UnlinkWhenIdle(slot.MixPad, queueSink, _context.Log, $"{slot.MixPad.Name} → {listener.Queue.Name}");
            }
            listener.Linked = false;
        }
        if (member.Source is { } source && source.GetStaticPad("src") is { } pad && pad.IsLinked())
        {
            UnlinkWhenIdle(pad, slot.SinkPad, _context.Log, $"{source.Name} → {slot.SinkPad.Name}");
        }
        member.SourceLinked = false;
    }

    public void Release(Guid participantId)
    {
        if (!_members.TryGetValue(participantId, out var member))
        {
            return;
        }
        if (member.Listener is { } listener)
        {
            Remove(Pipeline, [listener.Queue, listener.Encoder]);
            member.Listener = null;
        }
        if (member.Source is { } source)
        {
            Remove(Pipeline, [source]);
            member.Source = null;
            _levels.TryRemove(participantId, out _);
        }
        FreeSlot(member);
    }

    public void Dispose()
    {
        foreach (var id in _members.Keys.ToList())
        {
            Disconnect(id);
            Release(id);
        }
        _members.Clear();
        Remove(Pipeline, [_silence, _silenceCaps, _mixer]);
    }

    /// <summary>The participant's slot, taking a free one (and its pads) on first use.</summary>
    private Slot TakeSlot(Member member)
    {
        lock (_slotLock)
        {
            if (member.Slot is { } existing)
            {
                return existing;
            }
            var n = Array.FindIndex(_slots, 1, m => m is null);
            if (n < 0)
            {
                throw new InvalidOperationException($"Room {_context.RoomId}: no free mixer slot for {member.Id}.");
            }
            var sink = _mixer.RequestPadSimple($"sink_{n}") ?? throw new InvalidOperationException($"No mixer pad sink_{n}.");
            var mix = _mixer.GetStaticPad($"mix_{n}") ?? throw new InvalidOperationException($"No mixer pad mix_{n}.");
            _slots[n] = member;
            member.Slot = new Slot(n, sink, mix);
        }
        // Its settings before its audio, and its number in everyone's trims.
        ApplyAll();
        return member.Slot;
    }

    private void FreeSlot(Member member)
    {
        lock (_applyLock) // the same order as ApplyAll
        {
            lock (_slotLock)
            {
                if (member.Slot is not { } slot)
                {
                    return;
                }
                _mixer.ReleaseRequestPad(slot.SinkPad); // also removes mix_N and the number from everyone's trims
                _slots[slot.Number] = null;
                member.Slot = null;
                member.Applied = null;
            }
        }
    }

    /// <summary>Any thread: brings every slot pad's properties up to date with the participants' snapshots.</summary>
    private void ApplyAll()
    {
        lock (_applyLock)
        {
            List<Member> placed;
            lock (_slotLock)
            {
                placed = _slots.Skip(1).OfType<Member>().ToList();
            }
            foreach (var member in placed)
            {
                if (member.Slot is { } slot)
                {
                    var wanted = Wanted(member, placed);
                    Apply(slot.SinkPad, member.Applied, wanted);
                    member.Applied = wanted;
                }
            }
        }
    }

    /// <summary>What the participant's pad properties should be.</summary>
    private PadSettings Wanted(Member member, List<Member> placed)
    {
        var p = member.Snapshot;

        // A viewer's own mute list and volumes (SLData), by the other participants' slots.
        var trims = new StringBuilder("trims");
        if (p.Peers is { Count: > 0 } peers)
        {
            foreach (var other in placed.Where(o => o != member && o.Slot is not null).OrderBy(o => o.Slot!.Number))
            {
                if (peers.TryGetValue(other.Snapshot.ViewerKey, out var peer))
                {
                    var gain = peer.Muted ? 0 : peer.Gain;
                    if (gain != 1)
                    {
                        trims.Append(CultureInfo.InvariantCulture, $",sink_{other.Slot!.Number}=(double){gain:R}");
                    }
                }
            }
        }

        var settings = new PadSettings(p.Gain, p.Muted, null, null, Trims: trims.ToString());
        if (_context.SpatialSettings is not { } spatial)
        {
            return settings; // not spatial: everyone centred
        }
        var pose = Spatial.EffectivePose.Of(p, spatial);
        return settings with
        {
            Speaker = pose.Speaker,
            Listener = pose.Listener is { } at ? new ListenerPose(at, Euler(pose.ListenerOrientation)) : null,
        };
    }

    /// <summary>Sets the properties that differ from what was last set (everything, the first time).</summary>
    private static void Apply(Pad pad, PadSettings? was, PadSettings now)
    {
        if (was?.Gain != now.Gain)
        {
            pad.SetProperty("gain", now.Gain);
        }
        if (was?.Muted != now.Muted)
        {
            pad.SetProperty("mute", now.Muted);
        }
        if (was is null || was.Speaker != now.Speaker)
        {
            if (now.Speaker is { } s)
            {
                pad.SetProperty("x", s.X); // each also sets "positioned"
                pad.SetProperty("y", s.Y);
                pad.SetProperty("z", s.Z);
            }
            else
            {
                pad.SetProperty("positioned", false);
            }
        }
        if (was is null || was.Listener != now.Listener)
        {
            if (now.Listener is { } l)
            {
                pad.SetProperty("listener-x", l.Position.X); // each also sets "listener-separate"
                pad.SetProperty("listener-y", l.Position.Y);
                pad.SetProperty("listener-z", l.Position.Z);
                pad.SetProperty("listener-yaw", l.Yaw);
                pad.SetProperty("listener-pitch", l.Pitch);
                pad.SetProperty("listener-roll", l.Roll);
            }
            else
            {
                pad.SetProperty("listener-separate", false);
            }
        }
        if (was?.Trims != now.Trims)
        {
            Global.UtilSetObjectArg(pad, "trims", now.Trims);
        }
    }

    /// <summary>
    /// A quaternion as the element's yaw, pitch and roll in degrees (its rotation Rz(yaw) · Ry(−pitch) · Rx(roll);
    /// acousticmultimixer's PLAN.md section 3). No orientation: facing +x.
    /// </summary>
    internal static (double Yaw, double Pitch, double Roll) Euler(Quat? q)
    {
        if (q is null)
        {
            return (0, 0, 0);
        }
        var norm = Math.Sqrt(q.X * q.X + q.Y * q.Y + q.Z * q.Z + q.W * q.W);
        if (norm < 1e-9)
        {
            return (0, 0, 0);
        }
        double x = q.X / norm, y = q.Y / norm, z = q.Z / norm, w = q.W / norm;
        var yaw = Math.Atan2(2 * (w * z + x * y), 1 - 2 * (y * y + z * z));
        var pitch = -Math.Asin(Math.Clamp(2 * (w * y - z * x), -1, 1));
        var roll = Math.Atan2(2 * (w * x + y * z), 1 - 2 * (x * x + y * y));
        const double degrees = 180 / Math.PI;
        return (yaw * degrees, pitch * degrees, roll * degrees);
    }

    private sealed record ListenerPose(Vec3 Position, double Yaw, double Pitch, double Roll)
    {
        public ListenerPose(Vec3 position, (double Yaw, double Pitch, double Roll) angles)
            : this(position, angles.Yaw, angles.Pitch, angles.Roll)
        {
        }
    }

    /// <param name="Speaker">Where the voice is (null: heard centred at full volume).</param>
    /// <param name="Listener">Where they hear from (null: everyone centred).</param>
    /// <param name="Trims">This listener's own gains for others, as the element's structure.</param>
    private sealed record PadSettings(double Gain, bool Muted, Vec3? Speaker, ListenerPose? Listener, string Trims);

    private sealed record Slot(int Number, Pad SinkPad, Pad MixPad);

    private sealed class Member(ParticipantSnapshot snapshot)
    {
        public Guid Id { get; } = snapshot.Id;

        public ParticipantSnapshot Snapshot
        {
            get => Volatile.Read(ref _snapshot);
            set => Volatile.Write(ref _snapshot, value);
        }

        private ParticipantSnapshot _snapshot = snapshot;

        /// <summary>Set under the slot lock; read anywhere.</summary>
        public Slot? Slot
        {
            get => Volatile.Read(ref _slot);
            set => Volatile.Write(ref _slot, value);
        }

        private Slot? _slot;

        /// <summary>What was last set on the slot pad; under the apply lock.</summary>
        public PadSettings? Applied { get; set; }

        public ListenerPart? Listener { get; set; }

        /// <summary>The decoder feeding the slot (set on the streaming thread).</summary>
        public Element? Source
        {
            get => Volatile.Read(ref _source);
            set => Volatile.Write(ref _source, value);
        }

        private Element? _source;

        /// <summary>The decoder is linked into the slot (between AttachSource and Disconnect).</summary>
        public bool SourceLinked
        {
            get => Volatile.Read(ref _sourceLinked);
            set => Volatile.Write(ref _sourceLinked, value);
        }

        private bool _sourceLinked;
    }

    private sealed class ListenerPart(Element queue, Bin encoder)
    {
        public Element Queue { get; } = queue;
        public Bin Encoder { get; } = encoder;
        public bool Linked { get; set; }
    }
}
