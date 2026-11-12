using System.Collections.Concurrent;
using Gst;
using Task = System.Threading.Tasks.Task;
using SpatialAudioServer.Rooms;
using static SpatialAudioServer.Media.Engines.MediaBlocks;

namespace SpatialAudioServer.Media.Engines;

/// <summary>
/// Matrix mixing with a shared encoder: only the current talkers are mixed, everyone who is not talking hears the
/// same mix of all talkers from one shared Opus encoder, and each talker gets a personal mix (talkers minus
/// themselves) with its own encoder. Encoders = talkers + 1 instead of one per participant.
/// <code>
/// room:      RoomInterleave → tee ─┬→ MatrixMixer (all talkers) → OpusEncodeOnly → opus tee   (the shared mix)
///                                  └→ per talker: MatrixMixer (talkers − self) → OpusEncodeOnly ┐
/// listener:  opus tee → queue ───────────────────────────────────────────→ input-selector ←────┘ → rtpopuspay → webrtcbin
/// </code>
/// </summary>
/// <remarks>
/// <para>Talkers: a source is voiced while its decoded RMS exceeds <c>Media:VadThresholdDb</c>, and stays a talker for
/// <c>Media:VadHangoverMs</c> after that; at most <c>Media:ActiveSpeakers</c> talkers (current ones keep their place).
/// Muted sources never talk. Sources that are not talking are in nobody's mix.</para>
/// <para>Switching keeps mix-minus: a new talker's personal branch is built and the listener's selector switched to
/// it on its first buffer, and only then is the talker added to the shared mix. A talker who stops is taken out of
/// every mix first, and their selector goes back to the shared mix 300 ms later (by then the shared stream no longer
/// carries them). Switching happens on Opus frames before payloading, so each listener's RTP stream (SSRC, sequence
/// numbers, timestamps) stays continuous; the receiver's decoder sees a brief discontinuity at a switch.</para>
/// <para>Spatial audio: the shared mix is one rendering for everyone, so it cannot be positioned per listener (it is
/// centred). Personal talker mixes can be. See PLAN.md for clustering / client-side spatialisation options.</para>
/// </remarks>
internal sealed class SharedMatrixEngine : IMixEngine
{
    public const string Name = "matrix-shared";

    private static readonly TimeSpan EvaluateEvery = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan SwitchBackDelay = TimeSpan.FromMilliseconds(300);

    private readonly MixEngineContext _context;
    private readonly ConcurrentDictionary<Guid, Member> _members = new(); // written on the worker; read by AttachSource
    private readonly RoomInterleave _room;
    private readonly MatrixMixer _sharedMixer;
    private readonly Bin _sharedEncoder;
    private readonly Element _opusTee;
    private readonly Pad _sharedTeePad;
    private readonly HashSet<Guid> _talkers = []; // worker: sources currently in the mixes
    private readonly float _threshold;
    private readonly Timer _timer;
    private volatile bool _disposed;

    public SharedMatrixEngine(MixEngineContext context)
    {
        _context = context;
        var id = context.RoomId.ToString("N")[..8];
        _room = new RoomInterleave(context);
        _sharedMixer = new MatrixMixer($"shared-{id}", _room.Channels, context);
        _sharedEncoder = OpusEncodeOnly($"sharedenc-{id}", context.Options);
        _opusTee = Make("tee", $"opustee-{id}");
        _opusTee.SetProperty("allow-not-linked", true);
        _sharedMixer.AddTo(Pipeline);
        Add(Pipeline, _sharedEncoder, _opusTee);
        Link(_sharedMixer.Src, _sharedEncoder.GetStaticPad("sink"), "shared matrix → encoder");
        Link(_sharedEncoder.GetStaticPad("src"), _opusTee.GetStaticPad("sink"), "shared encoder → opus tee");
        _opusTee.SyncStateWithParent();
        _sharedEncoder.SyncStateWithParent();
        _sharedMixer.Sync();
        _sharedTeePad = _room.RequestTeePad();
        Link(_sharedTeePad, _sharedMixer.Sink, "tee → shared matrix");

        _threshold = (float)Math.Pow(10, context.Options.VadThresholdDb / 20);
        _timer = new Timer(_ => PostSafe(EvaluateTalkers), null, EvaluateEvery, EvaluateEvery);
    }

    private Pipeline Pipeline => _context.Pipeline;

    private int MaxTalkers => Math.Max(1, _context.Options.ActiveSpeakers);

    public int MixedPairs =>
        _members.Values.Where(m => m.Listener is not null).Sum(l => _talkers.Count(t => t != l.Id));

    public IReadOnlyDictionary<string, long> Counters => new Dictionary<string, long>
    {
        ["slots"] = _room.Channels - 1,
        ["slotsInUse"] = _room.SlotsInUse,
        ["talkers"] = _talkers.Count,
        ["encoders"] = 1 + _members.Values.Count(m => m.Personal is not null),
    };

    public void ParticipantAdded(ParticipantSnapshot participant) =>
        _members.TryAdd(participant.Id, new Member(participant.Id) { Gain = Gain(participant) });

    public void ParticipantUpdated(ParticipantSnapshot participant)
    {
        if (_members.TryGetValue(participant.Id, out var member))
        {
            member.Gain = Gain(participant);
            if (member.Gain == 0 && _talkers.Contains(member.Id))
            {
                StopTalking(member); // muted: out of every mix now, not after the hangover
            }
            UpdateMatrices();
        }
    }

    public void ParticipantRemoved(Guid participantId) => _members.TryRemove(participantId, out _);

    public void AttachListener(Guid participantId, int payloadType, Pad rtcSink)
    {
        var member = _members[participantId];
        var id = ShortId(participantId);
        var selector = Make("input-selector", $"sel-{id}");
        selector.SetProperty("sync-streams", false); // an inactive branch's frames are just dropped
        var queue = Make("queue", $"sq-{id}");
        queue.SetProperty("max-size-buffers", 0u);
        queue.SetProperty("max-size-bytes", 0u);
        queue.SetProperty("max-size-time", (ulong)_context.Options.ChainQueueMs * 1_000_000);
        Global.UtilSetObjectArg(queue, "leaky", "downstream");
        var payloader = OpusPayloader($"pay-{id}", payloadType);
        var listener = new ListenerPart(selector, queue, payloader);
        member.Listener = listener;
        try
        {
            Add(Pipeline, selector, queue, payloader);
            listener.SharedPad = selector.RequestPadSimple("sink_%u") ?? throw new InvalidOperationException("No selector pad.");
            Link(queue.GetStaticPad("src"), listener.SharedPad, "shared queue → selector");
            Link(selector.GetStaticPad("src"), payloader.GetStaticPad("sink"), "selector → payloader");
            Link(payloader.GetStaticPad("src"), rtcSink, "payloader → webrtcbin");
            selector.SetProperty("active-pad", listener.SharedPad);
            payloader.SyncStateWithParent();
            selector.SyncStateWithParent();
            queue.SyncStateWithParent();
            listener.OpusTeePad = _opusTee.RequestPadSimple("src_%u") ?? throw new InvalidOperationException("No opus tee pad.");
            Link(listener.OpusTeePad, queue.GetStaticPad("sink"), "opus tee → listener");
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
        // Streaming thread. Not a talker yet: the level probe will make it one.
        if (_members.TryGetValue(participantId, out var member))
        {
            member.Source = _room.Attach(participantId, rtpSource, rms =>
            {
                if (rms > _threshold)
                {
                    Volatile.Write(ref member.LastVoice, Environment.TickCount64);
                }
            });
        }
    }

    public void Disconnect(Guid participantId)
    {
        if (!_members.TryGetValue(participantId, out var member))
        {
            return;
        }
        member.SwitchGeneration++; // cancel a pending switch-back
        var wasTalking = _talkers.Remove(participantId);
        if (member.Personal is not null)
        {
            RemovePersonal(member);
        }
        if (member.Listener is { OpusTeePad: { } teePad } listener)
        {
            UnlinkWhenIdle(teePad, listener.Queue.GetStaticPad("sink")!, _context.Log, $"opus tee → {listener.Queue.Name}");
            _opusTee.ReleaseRequestPad(teePad);
            listener.OpusTeePad = null;
        }
        if (member.Source is { Slot: > 0 } source)
        {
            member.Source = _room.Detach(source);
        }
        if (wasTalking)
        {
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
            Remove(Pipeline, [listener.Queue, listener.Selector, listener.Payloader]);
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
        _disposed = true;
        _timer.Dispose();
        foreach (var id in _members.Keys.ToList())
        {
            Disconnect(id);
            Release(id);
        }
        _members.Clear();
        _room.ReleaseTeePad(_sharedTeePad, _sharedMixer.Sink, "tee → shared matrix");
        Remove(Pipeline, [.. _sharedMixer.Elements, _sharedEncoder, _opusTee]);
        _room.Dispose();
    }

    private static float Gain(ParticipantSnapshot p) => p.Muted ? 0f : (float)p.Gain;

    /// <summary>Posts to the worker unless the room is going away (timers and delays outlive it briefly).</summary>
    private void PostSafe(Action action)
    {
        if (_disposed)
        {
            return;
        }
        try
        {
            _context.Worker.Post(() =>
            {
                if (!_disposed)
                {
                    action();
                }
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // The worker has shut down.
        }
    }

    /// <summary>Worker, every 100 ms: who is talking now, and the switches that follow.</summary>
    private void EvaluateTalkers()
    {
        var now = Environment.TickCount64;
        var hangover = _context.Options.VadHangoverMs;
        var wanted = _members.Values
            .Where(m => m.Source is { Slot: > 0 } && m.Gain > 0 && now - Volatile.Read(ref m.LastVoice) < hangover)
            .OrderByDescending(m => _talkers.Contains(m.Id) || m.Personal is not null) // current talkers keep their place
            .ThenByDescending(m => Volatile.Read(ref m.LastVoice))
            .Take(MaxTalkers)
            .Select(m => m.Id)
            .ToHashSet();

        var changed = false;
        foreach (var id in _talkers.Where(t => !wanted.Contains(t)).ToList())
        {
            StopTalking(_members[id]);
            changed = true;
        }
        foreach (var id in wanted.Where(w => !_talkers.Contains(w)))
        {
            changed |= StartTalking(_members[id]);
        }
        if (changed)
        {
            UpdateMatrices();
        }
    }

    /// <summary>Returns whether the talker set changed now (false while waiting for a new personal branch).</summary>
    private bool StartTalking(Member member)
    {
        if (member.Listener is null)
        {
            _talkers.Add(member.Id); // a source that hears nothing needs no personal mix
            return true;
        }
        if (member.Personal is { Active: true })
        {
            member.SwitchGeneration++; // came back within the switch-back delay: keep the personal branch
            _talkers.Add(member.Id);
            return true;
        }
        if (member.Personal is null)
        {
            BuildPersonal(member);
        }
        return false; // becomes a talker when its personal branch delivers (Activate)
    }

    private void StopTalking(Member member)
    {
        _talkers.Remove(member.Id);
        var generation = ++member.SwitchGeneration;
        _ = Task.Delay(SwitchBackDelay).ContinueWith(_ => PostSafe(() =>
        {
            if (member.SwitchGeneration == generation && !_talkers.Contains(member.Id) && member.Personal is not null)
            {
                RemovePersonal(member);
            }
        }), TaskScheduler.Default);
    }

    /// <summary>Builds the talker's personal mix (talkers minus self) and switches to it on its first frame.</summary>
    private void BuildPersonal(Member member)
    {
        var listener = member.Listener!;
        var id = ShortId(member.Id);
        var personal = new PersonalPart(new MatrixMixer($"p-{id}", _room.Channels, _context), OpusEncodeOnly($"penc-{id}", _context.Options));
        member.Personal = personal;
        personal.Mixer.SetGains(PersonalGains(member), running: false);
        personal.Mixer.AddTo(Pipeline);
        Add(Pipeline, personal.Encoder);
        Link(personal.Mixer.Src, personal.Encoder.GetStaticPad("sink"), "personal matrix → encoder");
        personal.SelectorPad = listener.Selector.RequestPadSimple("sink_%u") ?? throw new InvalidOperationException("No selector pad.");
        Link(personal.Encoder.GetStaticPad("src"), personal.SelectorPad, "personal encoder → selector");
        personal.SelectorPad.AddProbe(PadProbeType.Buffer, (_, _) =>
        {
            PostSafe(() => Activate(member, personal));
            return PadProbeReturn.Remove;
        });
        personal.Encoder.SyncStateWithParent();
        personal.Mixer.Sync();
        personal.TeePad = _room.RequestTeePad();
        Link(personal.TeePad, personal.Mixer.Sink, "tee → personal matrix");
    }

    /// <summary>Worker: the personal branch is producing; switch the listener to it, then add them to the mixes.</summary>
    private void Activate(Member member, PersonalPart personal)
    {
        if (member.Personal != personal || personal.Active || member.Listener is not { } listener)
        {
            return;
        }
        listener.Selector.SetProperty("active-pad", personal.SelectorPad);
        personal.Active = true;
        if (_talkers.Count < MaxTalkers && member.Gain > 0)
        {
            _talkers.Add(member.Id);
            UpdateMatrices();
        }
        else
        {
            StopTalking(member); // the slot went to someone else meanwhile, or they were muted
        }
    }

    private void RemovePersonal(Member member)
    {
        if (member.Personal is not { } personal)
        {
            return;
        }
        member.Personal = null;
        if (member.Listener is { SharedPad: { } sharedPad } listener)
        {
            listener.Selector.SetProperty("active-pad", sharedPad);
        }
        if (personal.TeePad is { } teePad)
        {
            _room.ReleaseTeePad(teePad, personal.Mixer.Sink, $"tee → {personal.Mixer.Queue.Name}");
        }
        Remove(Pipeline, [.. personal.Mixer.Elements, personal.Encoder]);
        if (personal.SelectorPad is { } selectorPad && member.Listener is { } owner)
        {
            owner.Selector.ReleaseRequestPad(selectorPad);
        }
    }

    /// <summary>Worker: the shared mix is every talker; a talker's personal mix is every other talker.</summary>
    private void UpdateMatrices()
    {
        _sharedMixer.SetGains(Gains(_ => true));
        foreach (var member in _members.Values)
        {
            member.Personal?.Mixer.SetGains(PersonalGains(member));
        }
    }

    private double[] PersonalGains(Member listener) => Gains(id => id != listener.Id);

    private double[] Gains(Func<Guid, bool> include)
    {
        var gains = new double[_room.Channels];
        foreach (var id in _talkers)
        {
            if (include(id) && _members.TryGetValue(id, out var talker) && talker.Source is { Slot: > 0 } source)
            {
                gains[source.Slot] = talker.Gain;
            }
        }
        return gains;
    }

    private sealed class Member(Guid id)
    {
        public long LastVoice = long.MinValue / 2; // TickCount64 of the last voiced buffer (streaming thread writes)

        private SourceSlot? _source;

        public Guid Id { get; } = id;
        public float Gain { get; set; } = 1f;
        public ListenerPart? Listener { get; set; }
        public PersonalPart? Personal { get; set; }
        public int SwitchGeneration { get; set; }

        /// <summary>Set by AttachSource (streaming thread); read and changed on the worker.</summary>
        public SourceSlot? Source
        {
            get => Volatile.Read(ref _source);
            set => Volatile.Write(ref _source, value);
        }
    }

    private sealed class ListenerPart(Element selector, Element queue, Bin payloader)
    {
        public Element Selector { get; } = selector;
        public Element Queue { get; } = queue;
        public Bin Payloader { get; } = payloader;
        public Pad? SharedPad { get; set; }
        public Pad? OpusTeePad { get; set; }
    }

    private sealed class PersonalPart(MatrixMixer mixer, Bin encoder)
    {
        public MatrixMixer Mixer { get; } = mixer;
        public Bin Encoder { get; } = encoder;
        public Pad? TeePad { get; set; }
        public Pad? SelectorPad { get; set; }
        public bool Active { get; set; }
    }
}
