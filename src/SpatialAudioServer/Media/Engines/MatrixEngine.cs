using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Gst;
using SpatialAudioServer.Rooms;
using static SpatialAudioServer.Media.Engines.MediaBlocks;

namespace SpatialAudioServer.Media.Engines;

/// <summary>
/// Matrix mixing: the room's sources are interleaved into one multichannel stream, and each listener's stereo mix is
/// a gain matrix over it. No per-pair elements; per-pair gain (and later pan) are matrix coefficients.
/// <code>
/// room (once):     silence → interleave slot 0;  interleave (slots 0..max) → tee
/// source:          webrtcbin src → OpusDecoder (mono 48k F32) → interleave slot k (a free slot, held until release)
/// listener:        tee → queue (leaky) → audiomixmatrix (2 × slots; own slot off) → OpusEncoder → webrtcbin sink
/// </code>
/// </summary>
/// <remarks>
/// Constraints from the 1.28 sources (docs/reference/gstreamer):
/// <list type="bullet">
/// <item>audiointerleave's channel count is its number of sink pads and releasing one renumbers the rest, while
/// audiomixmatrix (manual mode) must be told the exact input channel count and reads out of bounds if buffers of
/// another width arrive. So every slot pad is requested up front and never released: nothing renegotiates.</item>
/// <item>With <c>channel-positions-from-input=false</c> every position stays NONE, so slot k is channel k (by
/// default, pads with and without caps sort differently and the channel order would shuffle).</item>
/// <item>audiomixmatrix's <c>matrix</c> setter frees and rebuilds its tables without a lock, so changes are applied
/// from a buffer probe on its sink pad (the streaming thread, just before <c>transform</c>).</item>
/// <item>It picks a sparse or dense mixing function at caps time (dense if ≥ 50% non-zero) and a later matrix on
/// the other side of that line would make the sparse one output silence. "Off" is therefore <see cref="Off"/>
/// (−180 dB), never 0, so the matrix is always dense.</item>
/// </list>
/// </remarks>
internal sealed class MatrixEngine : IMixEngine
{
    public const string Name = "matrix";

    private const double Off = 1e-9;
    private const string MonoCaps = "audio/x-raw,format=F32LE,layout=interleaved,rate=48000,channels=1";

    private readonly MixEngineContext _context;
    private readonly ConcurrentDictionary<Guid, Member> _members = new(); // written on the worker; read by AttachSource
    private readonly Element _silence;
    private readonly Element _silenceCaps;
    private readonly Element _interleave;
    private readonly Element _tee;
    private readonly Pad[] _slotPads; // [0] is the silence keep-alive
    private readonly Guid?[] _slotOwners; // under _slotLock
    private readonly Lock _slotLock = new();

    public MatrixEngine(MixEngineContext context)
    {
        _context = context;
        var id = context.RoomId.ToString("N")[..8];
        _interleave = Make("audiointerleave", $"interleave-{id}");
        _interleave.SetProperty("latency", (ulong)context.Options.MixerLatencyMs * 1_000_000);
        _interleave.SetProperty("channel-positions-from-input", false);
        _tee = Make("tee", $"roomtee-{id}");
        _tee.SetProperty("allow-not-linked", true);
        _silence = Silence($"silence-{id}");
        _silenceCaps = Make("capsfilter", $"silencecaps-{id}");
        using (var caps = Caps.FromString(MonoCaps))
        {
            _silenceCaps.SetProperty("caps", caps);
        }
        Add(context.Pipeline, _tee, _interleave, _silenceCaps, _silence);

        _slotPads = new Pad[context.MaxParticipants + 1];
        _slotOwners = new Guid?[_slotPads.Length];
        for (var slot = 0; slot < _slotPads.Length; slot++)
        {
            _slotPads[slot] = _interleave.RequestPadSimple("sink_%u") ?? throw new InvalidOperationException("No interleave pad.");
        }
        Link(_interleave.GetStaticPad("src"), _tee.GetStaticPad("sink"), "interleave → tee");
        Link(_silenceCaps.GetStaticPad("src"), _slotPads[0], "silence → interleave");
        Link(_silence.GetStaticPad("src"), _silenceCaps.GetStaticPad("sink"), "silence → caps");
        foreach (var element in new[] { _tee, _interleave, _silenceCaps, _silence })
        {
            element.SyncStateWithParent();
        }
    }

    private Pipeline Pipeline => _context.Pipeline;

    private int Slots => _slotPads.Length;

    public int MixedPairs
    {
        get
        {
            var sources = _members.Values.Where(m => m.Source is { Slot: > 0 }).Select(m => m.Id).ToList();
            var listeners = _members.Values.Where(m => m.Listener is not null).Select(m => m.Id).ToList();
            return sources.Count * listeners.Count - sources.Intersect(listeners).Count();
        }
    }

    public IReadOnlyDictionary<string, long> Counters => new Dictionary<string, long>
    {
        ["slots"] = Slots - 1,
        ["slotsInUse"] = _members.Values.Count(m => m.Source is { Slot: > 0 }),
        ["encoders"] = _members.Values.Count(m => m.Listener is not null),
    };

    public void ParticipantAdded(ParticipantSnapshot participant) =>
        _members.TryAdd(participant.Id, new Member(participant.Id) { Gain = Gain(participant) });

    public void ParticipantUpdated(ParticipantSnapshot participant)
    {
        if (_members.TryGetValue(participant.Id, out var member))
        {
            member.Gain = Gain(participant);
            UpdateMatrices();
        }
    }

    public void ParticipantRemoved(Guid participantId) => _members.TryRemove(participantId, out _);

    public void AttachListener(Guid participantId, int payloadType, Pad rtcSink)
    {
        var member = _members[participantId];
        var id = ShortId(participantId);
        var queue = Make("queue", $"lq-{id}");
        queue.SetProperty("max-size-buffers", 0u);
        queue.SetProperty("max-size-bytes", 0u);
        queue.SetProperty("max-size-time", (ulong)_context.Options.ChainQueueMs * 1_000_000);
        Global.UtilSetObjectArg(queue, "leaky", "downstream");
        var matrix = Make("audiomixmatrix", $"matrix-{id}");
        matrix.SetProperty("in-channels", (uint)Slots);
        matrix.SetProperty("out-channels", 2u);
        matrix.SetProperty("channel-mask", 0x3UL); // front left + right, so the encoder's audioconvert passes it through
        var listener = new ListenerPart(queue, matrix, OpusEncoder($"egress-{id}", payloadType, _context.Options));
        member.Listener = listener;
        try
        {
            // Before linking: in manual mode the matrix must be complete for caps to negotiate.
            Global.UtilSetObjectArg(matrix, "matrix", MatrixFor(member));
            Add(Pipeline, queue, matrix, listener.Encoder);
            Link(queue.GetStaticPad("src"), matrix.GetStaticPad("sink"), "queue → matrix");
            Link(matrix.GetStaticPad("src"), listener.Encoder.GetStaticPad("sink"), "matrix → encoder");
            Link(listener.Encoder.GetStaticPad("src"), rtcSink, "encoder → webrtcbin");
            listener.Encoder.SyncStateWithParent();
            matrix.SyncStateWithParent();
            queue.SyncStateWithParent();
            listener.TeePad = _tee.RequestPadSimple("src_%u") ?? throw new InvalidOperationException("No tee pad.");
            Link(listener.TeePad, queue.GetStaticPad("sink"), "tee → listener");
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
        if (!_members.TryGetValue(participantId, out var member))
        {
            return;
        }
        var id = ShortId(participantId);
        var slot = TakeSlot(participantId);
        if (slot < 0)
        {
            _context.Log.LogError("Room {RoomId}: no free interleave slot for {ParticipantId}; their audio is dropped",
                _context.RoomId, participantId);
            var sink = Make("fakesink", $"noslot-{id}");
            Add(Pipeline, sink);
            sink.SyncStateWithParent();
            Link(rtpSource, sink.GetStaticPad("sink"), "webrtcbin → fakesink");
            member.Source = new SourcePart(sink, -1);
            return;
        }

        var decoder = OpusDecoder($"ingress-{id}", "F32LE");
        Add(Pipeline, decoder);
        Link(decoder.GetStaticPad("src"), _slotPads[slot], "source → interleave");
        decoder.SyncStateWithParent();
        Link(rtpSource, decoder.GetStaticPad("sink"), "webrtcbin → source");
        member.Source = new SourcePart(decoder, slot);

        _context.Worker.Post(UpdateMatrices);
    }

    public void Disconnect(Guid participantId)
    {
        if (!_members.TryGetValue(participantId, out var member))
        {
            return;
        }
        if (member.Listener is { TeePad: { } teePad } listener && listener.Queue.GetStaticPad("sink") is { } queueSink)
        {
            UnlinkWhenIdle(teePad, queueSink, _context.Log, $"tee → {listener.Queue.Name}");
            _tee.ReleaseRequestPad(teePad);
            listener.TeePad = null;
        }
        if (member.Source is { Slot: > 0 } source && source.Element.GetStaticPad("src") is { } sourcePad)
        {
            // The slot pad stays (released pads renumber channels); it just stops getting data.
            UnlinkWhenIdle(sourcePad, _slotPads[source.Slot], _context.Log, $"{source.Element.Name} → interleave");
            FreeSlot(source.Slot);
            member.Source = source with { Slot = 0 };
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
            Remove(Pipeline, [listener.Queue, listener.Matrix, listener.Encoder]);
            member.Listener = null;
        }
        if (member.Source is { } source)
        {
            Remove(Pipeline, [source.Element]);
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
        Remove(Pipeline, [_silence, _silenceCaps, _interleave, _tee]);
    }

    private static float Gain(ParticipantSnapshot p) => p.Muted ? 0f : (float)p.Gain;

    private int TakeSlot(Guid owner)
    {
        lock (_slotLock)
        {
            for (var slot = 1; slot < _slotOwners.Length; slot++)
            {
                if (_slotOwners[slot] is null)
                {
                    _slotOwners[slot] = owner;
                    return slot;
                }
            }
            return -1;
        }
    }

    private void FreeSlot(int slot)
    {
        lock (_slotLock)
        {
            _slotOwners[slot] = null;
        }
    }

    /// <summary>Worker: recomputes every listener's matrix and queues it for its streaming thread.</summary>
    private void UpdateMatrices()
    {
        foreach (var member in _members.Values)
        {
            if (member.Listener is { } listener)
            {
                listener.Apply(MatrixFor(member));
            }
        }
    }

    /// <summary>
    /// The listener's 2 × slots matrix in GStreamer's serialised form: each other connected source at its gain on
    /// both channels (centre; the spatial policy will supply pan later), everything else off.
    /// </summary>
    private string MatrixFor(Member listener)
    {
        var gains = Enumerable.Repeat(Off, Slots).ToArray();
        foreach (var source in _members.Values)
        {
            if (source.Id != listener.Id && source.Source is { Slot: > 0 } part)
            {
                gains[part.Slot] = Math.Max(source.Gain, Off);
            }
        }
        var row = "<" + string.Join(", ", gains.Select(g => "(double)" + g.ToString("R", CultureInfo.InvariantCulture))) + ">";
        return new StringBuilder("<").Append(row).Append(", ").Append(row).Append('>').ToString();
    }

    private sealed class Member(Guid id)
    {
        public Guid Id { get; } = id;
        public float Gain { get; set; } = 1f;
        public ListenerPart? Listener { get; set; }

        /// <summary>Set by AttachSource (streaming thread); read and changed on the worker.</summary>
        public SourcePart? Source
        {
            get => Volatile.Read(ref _source);
            set => Volatile.Write(ref _source, value);
        }

        private SourcePart? _source;
    }

    /// <param name="Slot">Interleave slot (≥ 1) while mixed; 0 once disconnected; −1 if no slot was free.</param>
    private sealed record SourcePart(Element Element, int Slot);

    private sealed class ListenerPart(Element queue, Element matrix, Bin encoder)
    {
        private string? _pending;
        private int _probeInstalled;

        public Element Queue { get; } = queue;
        public Element Matrix { get; } = matrix;
        public Bin Encoder { get; } = encoder;
        public Pad? TeePad { get; set; }

        /// <summary>
        /// Sets the matrix from the streaming thread, just before the next buffer is transformed (the element's
        /// setter is not thread-safe). Updates arriving before then collapse into the latest.
        /// </summary>
        public void Apply(string matrix)
        {
            Volatile.Write(ref _pending, matrix);
            if (Interlocked.Exchange(ref _probeInstalled, 1) == 1)
            {
                return;
            }
            Matrix.GetStaticPad("sink")!.AddProbe(PadProbeType.Buffer, (_, _) =>
            {
                Volatile.Write(ref _probeInstalled, 0);
                if (Interlocked.Exchange(ref _pending, null) is { } latest)
                {
                    Global.UtilSetObjectArg(Matrix, "matrix", latest);
                }
                return PadProbeReturn.Remove;
            });
        }
    }
}
