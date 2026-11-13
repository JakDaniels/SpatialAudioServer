using System.Runtime.InteropServices;
using Gst;
using static SpatialAudioServer.Media.Engines.MediaBlocks;

namespace SpatialAudioServer.Media.Engines;

/// <summary>
/// The room's sources as one multichannel stream: silence → slot 0, each source's decoded mono audio → its own slot,
/// <c>audiointerleave</c> → <c>tee</c>. Matrix engines hang their mixes off <see cref="RequestTeePad"/>.
/// </summary>
/// <remarks>
/// Constraints from the 1.28 sources (docs/reference/gstreamer):
/// <list type="bullet">
/// <item>audiointerleave's channel count is its number of sink pads and releasing one renumbers the rest, while
/// audiomixmatrix (manual mode) must be told the exact input channel count and reads out of bounds if buffers of
/// another width arrive. So every slot pad is requested up front and never released: nothing renegotiates.</item>
/// <item>With <c>channel-positions-from-input=false</c> every position stays NONE, so slot k is channel k (by
/// default, pads with and without caps sort differently and the channel order would shuffle).</item>
/// </list>
/// </remarks>
internal sealed class RoomInterleave : IDisposable
{
    private const string MonoCaps = "audio/x-raw,format=F32LE,layout=interleaved,rate=48000,channels=1";

    private readonly MixEngineContext _context;
    private readonly Element _silence;
    private readonly Element _silenceCaps;
    private readonly Element _interleave;
    private readonly Element _tee;
    private readonly Pad[] _slotPads; // [0] is the silence keep-alive
    private readonly Guid?[] _slotOwners; // under _slotLock
    private readonly Lock _slotLock = new();

    public RoomInterleave(MixEngineContext context)
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

    /// <summary>Channels in the interleaved stream (slot 0 included): the matrices' input width.</summary>
    public int Channels => _slotPads.Length;

    public int SlotsInUse
    {
        get
        {
            lock (_slotLock)
            {
                return _slotOwners.Count(o => o is not null);
            }
        }
    }

    public Pad RequestTeePad() => _tee.RequestPadSimple("src_%u") ?? throw new InvalidOperationException("No tee pad.");

    /// <summary>Unlinks a branch from the tee when idle and releases the tee pad.</summary>
    public void ReleaseTeePad(Pad teePad, Pad branchSink, string what)
    {
        UnlinkWhenIdle(teePad, branchSink, _context.Log, what);
        _tee.ReleaseRequestPad(teePad);
    }

    /// <summary>
    /// Streaming thread: decodes the participant's RTP into a free slot. <paramref name="onLevel"/>, if given, is
    /// called on the streaming thread with each decoded buffer's RMS (0–1). With no free slot the audio is dropped
    /// (logged) and the part has slot −1.
    /// </summary>
    public SourceSlot Attach(Guid participantId, Pad rtpSource, Action<float>? onLevel = null)
    {
        var id = ShortId(participantId);
        var slot = TakeSlot(participantId);
        if (slot < 0)
        {
            _context.Log.LogError("Room {RoomId}: no free interleave slot for {ParticipantId}; their audio is dropped",
                _context.RoomId, participantId);
            var sink = Make("fakesink", $"noslot-{id}");
            Add(_context.Pipeline, sink);
            sink.SyncStateWithParent();
            Link(rtpSource, sink.GetStaticPad("sink"), "webrtcbin → fakesink");
            return new SourceSlot(sink, -1);
        }

        var decoder = OpusDecoder($"ingress-{id}", "F32LE");
        Add(_context.Pipeline, decoder);
        var src = decoder.GetStaticPad("src");
        var meter = _levels[participantId] = new LevelMeter();
        src?.AddProbe(PadProbeType.Buffer, (_, info) =>
        {
            using var buffer = info.GetBuffer();
            if (buffer is not null)
            {
                using var map = buffer.Map(MapFlags.Read);
                var rms = Rms(MemoryMarshal.Cast<byte, float>(map.Span));
                meter.Add(rms);
                onLevel?.Invoke(rms);
            }
            return PadProbeReturn.Ok;
        });
        Link(src, _slotPads[slot], "source → interleave");
        decoder.SyncStateWithParent();
        Link(rtpSource, decoder.GetStaticPad("sink"), "webrtcbin → source");
        return new SourceSlot(decoder, slot);
    }

    /// <summary>Worker, everything running: stops the source feeding its slot and frees the slot. The slot pad stays.</summary>
    /// <summary>
    /// Each attached source's input level (RMS, 0–1 full scale), with a short peak hold so a poll every second or so
    /// sees speech rather than whichever 20 ms buffer came last. Streaming threads write; anyone may read.
    /// </summary>
    public IReadOnlyDictionary<Guid, double> Levels => _levels.ToDictionary(l => l.Key, l => l.Value.Level);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, LevelMeter> _levels = new();

    /// <summary>Peak-hold level: jumps up at once, falls by 10% per buffer (≈ −46 dB per second at 20 ms).</summary>
    private sealed class LevelMeter
    {
        private double _level;

        public double Level => Volatile.Read(ref _level);

        public void Add(double rms) => Volatile.Write(ref _level, Math.Max(rms, Volatile.Read(ref _level) * 0.9));
    }

    public SourceSlot Detach(SourceSlot source)
    {
        if (source.Slot > 0 && source.Element.GetStaticPad("src") is { } pad)
        {
            UnlinkWhenIdle(pad, _slotPads[source.Slot], _context.Log, $"{source.Element.Name} → interleave");
            lock (_slotLock)
            {
                if (_slotOwners[source.Slot] is { } owner)
                {
                    _levels.TryRemove(owner, out _);
                }
                _slotOwners[source.Slot] = null;
            }
        }
        return source with { Slot = 0 };
    }

    /// <summary>Worker, after the participant's webrtcbin has stopped: removes the source's elements.</summary>
    public void Release(SourceSlot source) => Remove(_context.Pipeline, [source.Element]);

    public void Dispose() => Remove(_context.Pipeline, [_silence, _silenceCaps, _interleave, _tee]);

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

    private static float Rms(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty)
        {
            return 0;
        }
        double sum = 0;
        foreach (var s in samples)
        {
            sum += s * s;
        }
        return (float)Math.Sqrt(sum / samples.Length);
    }
}

/// <param name="Slot">Interleave slot (≥ 1) while mixed; 0 once detached; −1 if no slot was free.</param>
internal sealed record SourceSlot(Element Element, int Slot);

/// <summary>
/// <c>queue (leaky) → audiomixmatrix</c>: one stereo mix over the room's interleaved stream.
/// </summary>
/// <remarks>
/// audiomixmatrix's <c>matrix</c> setter frees and rebuilds its tables without a lock, so changes are applied from a
/// buffer probe on its sink pad (the streaming thread, just before <c>transform</c>). It also picks a sparse or dense
/// mixing function at caps time (dense if ≥ 50% non-zero), and a later matrix on the other side of that line would
/// make the sparse one output silence; so "off" is <see cref="Off"/> (−180 dB), never 0, keeping it dense.
/// </remarks>
internal sealed class MatrixMixer
{
    public const double Off = 1e-9;

    private string? _pending;
    private int _probeInstalled;

    public MatrixMixer(string name, int channels, MixEngineContext context)
    {
        Channels = channels;
        Queue = Make("queue", $"q-{name}");
        Queue.SetProperty("max-size-buffers", 0u);
        Queue.SetProperty("max-size-bytes", 0u);
        Queue.SetProperty("max-size-time", (ulong)context.Options.ChainQueueMs * 1_000_000);
        Global.UtilSetObjectArg(Queue, "leaky", "downstream");
        Matrix = Make("audiomixmatrix", $"matrix-{name}");
        Matrix.SetProperty("in-channels", (uint)channels);
        Matrix.SetProperty("out-channels", 2u);
        Matrix.SetProperty("channel-mask", 0x3UL); // front left + right, so the encoder's audioconvert passes it through
        // In manual mode the matrix must be complete before caps negotiate.
        Global.UtilSetObjectArg(Matrix, "matrix", Serialize(Enumerable.Repeat(Off, channels).ToArray()));
    }

    public int Channels { get; }

    public Element Queue { get; }

    public Element Matrix { get; }

    public Pad Sink => Queue.GetStaticPad("sink")!;

    public Pad Src => Matrix.GetStaticPad("src")!;

    /// <summary>Adds both elements and links queue → matrix. Sync them (downstream first) once linked onwards.</summary>
    public void AddTo(Pipeline pipeline)
    {
        Add(pipeline, Queue, Matrix);
        Link(Queue.GetStaticPad("src"), Matrix.GetStaticPad("sink"), "queue → matrix");
    }

    public void Sync()
    {
        Matrix.SyncStateWithParent();
        Queue.SyncStateWithParent();
    }

    public IEnumerable<Element> Elements => [Queue, Matrix];

    /// <summary>Sets the same gain per input channel on both output channels (centred).</summary>
    public void SetGains(double[] gains, bool running = true) => SetGains(gains, gains, running);

    /// <summary>
    /// Sets the per-input-channel gains for the left and right outputs. Before the element runs this applies
    /// directly; afterwards it is applied on the streaming thread before the next buffer, and updates arriving before
    /// then collapse into the latest.
    /// </summary>
    public void SetGains(double[] left, double[] right, bool running = true)
    {
        var matrix = Serialize(left, right);
        if (!running)
        {
            Global.UtilSetObjectArg(Matrix, "matrix", matrix);
            return;
        }
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

    /// <summary>A 2 × channels matrix in GStreamer's serialised form, the same gains on both rows.</summary>
    public static string Serialize(double[] gains) => Serialize(gains, gains);

    /// <summary>A 2 × channels matrix in GStreamer's serialised form: row 0 = left output, row 1 = right.</summary>
    public static string Serialize(double[] left, double[] right)
    {
        static string Row(double[] gains) => "<" + string.Join(", ", gains.Select(g =>
            "(double)" + Math.Max(g, Off).ToString("R", System.Globalization.CultureInfo.InvariantCulture))) + ">";
        return $"<{Row(left)}, {Row(right)}>";
    }

    /// <summary>Left/right gain rows for a set of renderings by input slot.</summary>
    public static (double[] Left, double[] Right) Rows(int channels, IEnumerable<(int Slot, Spatial.Rendering Rendering)> sources)
    {
        var left = new double[channels];
        var right = new double[channels];
        foreach (var (slot, rendering) in sources)
        {
            (left[slot], right[slot]) = rendering.Stereo();
        }
        return (left, right);
    }
}
