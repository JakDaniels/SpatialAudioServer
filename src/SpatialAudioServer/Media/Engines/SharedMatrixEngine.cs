using System.Collections.Concurrent;
using System.Text;
using Gst;
using Task = System.Threading.Tasks.Task;
using SpatialAudioServer.Media.Spatial;
using SpatialAudioServer.Rooms;
using static SpatialAudioServer.Media.Engines.MediaBlocks;

namespace SpatialAudioServer.Media.Engines;

/// <summary>
/// Matrix mixing with shared encoders: only the current talkers are mixed. Each talker gets a personal mix (talkers
/// minus themselves) with its own encoder; everyone else hears a <b>cluster</b> mix shared by every listener who
/// hears the talkers the same way. In a non-spatial room that is one cluster (one shared mix, centred). In a spatial
/// room listeners are grouped by their rendering of the talkers, rounded to a grid (gain in dB steps, pan steps:
/// <see cref="SpatialOptions"/>), so encoders = talkers + distinct renderings rather than one per participant.
/// <code>
/// room:      RoomInterleave → tee ─┬→ per cluster: MatrixMixer (talkers, as the cluster hears them) → OpusEncodeOnly → opus tee
///                                  └→ per talker:  MatrixMixer (talkers − self) → OpusEncodeOnly ──────────────┐
/// listener:  a cluster's opus tee → queue → input-selector ←────────────────────────────────────────────────┘ → rtpopuspay → webrtcbin
/// </code>
/// </summary>
/// <remarks>
/// <para>Talkers: a source is voiced while its decoded RMS exceeds <c>Media:VadThresholdDb</c>, and stays a talker for
/// <c>Media:VadHangoverMs</c> after that; at most <c>Media:ActiveSpeakers</c> talkers (current ones keep their place).
/// Muted sources never talk. Sources that are not talking are in nobody's mix.</para>
/// <para>Every switch of a listener's input (to a personal mix, to a cluster, between clusters) links the new branch
/// first and moves the selector on its first frame, so the listener never hears a gap or a branch they should not.
/// Mix-minus: a new talker is switched to their personal branch before they are added to any mix; a talker who stops
/// is taken out of every mix and only joins a cluster 300 ms later (by then no cluster stream carries them). Switching
/// happens on Opus frames before payloading, so each listener's RTP stream (SSRC, sequence numbers, timestamps) stays
/// continuous; the receiver's decoder sees a brief discontinuity at a switch.</para>
/// <para>Clusters follow the talkers and positions (re-evaluated every 100 ms): when all of a cluster's listeners
/// get the same new rendering the cluster is re-keyed in place (no switch); only listeners whose rendering differs
/// from their cluster's move. Empty clusters are removed after a second.</para>
/// </remarks>
internal sealed class SharedMatrixEngine : IMixEngine
{
    public const string Name = "matrix-shared";

    private static readonly TimeSpan EvaluateEvery = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan SwitchBackDelay = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan EmptyClusterGrace = TimeSpan.FromSeconds(1);

    private readonly MixEngineContext _context;
    private readonly ConcurrentDictionary<Guid, Member> _members = new(); // written on the worker; read by AttachSource
    private readonly RoomInterleave _room;
    private readonly List<Cluster> _clusters = []; // worker
    private readonly HashSet<Guid> _talkers = []; // worker: sources currently in the mixes
    private readonly RenderingQuantizer _quantizer;
    private readonly float _threshold;
    private readonly Timer _timer;
    private readonly string _roomTag;
    private int _clusterCount;
    private bool _dirty; // worker: positions or gains changed since the last rebalance
    private volatile bool _disposed;

    public SharedMatrixEngine(MixEngineContext context)
    {
        _context = context;
        _roomTag = context.RoomId.ToString("N")[..8];
        _room = new RoomInterleave(context);
        _quantizer = new RenderingQuantizer(context.Options.Spatial);
        _threshold = (float)Math.Pow(10, context.Options.VadThresholdDb / 20);
        _timer = new Timer(_ => PostSafe(Tick), null, EvaluateEvery, EvaluateEvery);
    }

    private Pipeline Pipeline => _context.Pipeline;

    private int MaxTalkers => Math.Max(1, _context.Options.ActiveSpeakers);

    public int MixedPairs =>
        _members.Values.Where(m => m.Listener is not null).Sum(l => _talkers.Count(t => t != l.Id));

    public IReadOnlyDictionary<Guid, double> InputLevels => _room.Levels;

    public IReadOnlyDictionary<string, long> Counters => new Dictionary<string, long>
    {
        ["slots"] = _room.Channels - 1,
        ["slotsInUse"] = _room.SlotsInUse,
        ["talkers"] = _talkers.Count,
        ["clusters"] = _clusters.Count,
        ["encoders"] = _clusters.Count + _members.Values.Count(m => m.Personal is not null),
    };

    public void ParticipantAdded(ParticipantSnapshot participant) =>
        _members.TryAdd(participant.Id, new Member(participant));

    public void ParticipantUpdated(ParticipantSnapshot participant)
    {
        if (_members.TryGetValue(participant.Id, out var member))
        {
            member.Snapshot = participant;
            if (!member.Audible && _talkers.Contains(member.Id))
            {
                StopTalking(member); // muted: out of every mix now, not after the hangover
                Rebalance();
            }
            _dirty = true; // positions arrive often (the viewer sends ≤ 10/s): applied on the next tick
        }
    }

    public void ParticipantRemoved(Guid participantId) => _members.TryRemove(participantId, out _);

    public void AttachListener(Guid participantId, int payloadType, Pad rtcSink)
    {
        var member = _members[participantId];
        var id = ShortId(participantId);
        var selector = Make("input-selector", $"sel-{id}");
        selector.SetProperty("sync-streams", false); // an inactive branch's frames are just dropped
        var payloader = OpusPayloader($"pay-{id}", payloadType);
        member.Listener = new ListenerPart(selector, payloader);
        try
        {
            Add(Pipeline, selector, payloader);
            Link(selector.GetStaticPad("src"), payloader.GetStaticPad("sink"), "selector → payloader");
            Link(payloader.GetStaticPad("src"), rtcSink, "payloader → webrtcbin");
            payloader.SyncStateWithParent();
            selector.SyncStateWithParent();
            member.Clustered = true;
            Rebalance(); // joins a cluster; the selector takes it on its first frame
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
        member.Clustered = false;
        var wasTalking = _talkers.Remove(participantId);
        DetachLink(member, member.Pending);
        DetachLink(member, member.Current);
        if (member.Personal is { TeePad: { } personalTeePad } personal)
        {
            // Out of the room's tee only: its elements may be blocked pushing into webrtcbin, so Release removes them.
            _room.ReleaseTeePad(personalTeePad, personal.Mixer.Sink, $"tee → {personal.Mixer.Queue.Name}");
            personal.TeePad = null;
        }
        if (member.Source is { Slot: > 0 } source)
        {
            member.Source = _room.Detach(source);
        }
        if (wasTalking)
        {
            Rebalance();
        }
    }

    public void Release(Guid participantId)
    {
        if (!_members.TryGetValue(participantId, out var member))
        {
            return;
        }
        // webrtcbin has stopped, so nothing downstream can block these any more.
        foreach (var link in member.Retired.ToList())
        {
            RemoveRetired(member, link);
        }
        if (member.Personal is not null)
        {
            RemovePersonal(member);
        }
        if (member.Listener is { } listener)
        {
            Remove(Pipeline, [listener.Selector, listener.Payloader]);
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
        foreach (var cluster in _clusters.ToList())
        {
            RemoveCluster(cluster);
        }
        _room.Dispose();
    }

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

    /// <summary>Worker, every 100 ms: who is talking now, and the switches and mix changes that follow.</summary>
    private void Tick()
    {
        var changed = EvaluateTalkers();
        if (changed || _dirty)
        {
            _dirty = false;
            Rebalance();
        }
    }

    private bool EvaluateTalkers()
    {
        var now = Environment.TickCount64;
        var hangover = _context.Options.VadHangoverMs;
        var wanted = _members.Values
            .Where(m => m.Source is { Slot: > 0 } && m.Audible && now - Volatile.Read(ref m.LastVoice) < hangover)
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
        return changed;
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
            member.Clustered = false;
            DetachLink(member, member.Pending); // a switch back to a cluster may have started
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
            if (member.SwitchGeneration == generation && !_talkers.Contains(member.Id) && member.Listener is not null)
            {
                member.Clustered = true; // by now no cluster stream carries them: join one, then drop the personal mix
                Rebalance();
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
        var (left, right) = PersonalGains(member);
        personal.Mixer.SetGains(left, right, running: false);
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
        member.Clustered = false;
        DetachLink(member, member.Pending);
        DetachLink(member, member.Current);
        if (_talkers.Count < MaxTalkers && member.Audible)
        {
            _talkers.Add(member.Id);
        }
        else
        {
            StopTalking(member); // the slot went to someone else meanwhile, or they were muted
        }
        Rebalance();
    }

    private void RemovePersonal(Member member)
    {
        if (member.Personal is not { } personal)
        {
            return;
        }
        member.Personal = null;
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

    /// <summary>
    /// Worker: brings every mix up to date with the talkers, gains and positions. Personal mixes are exact; each
    /// clustered listener goes to the cluster for its (quantised) rendering, re-keying clusters in place where all
    /// their listeners agree so that as few listeners as possible switch.
    /// </summary>
    private void Rebalance()
    {
        if (_disposed)
        {
            return; // tearing down: no new clusters or switches
        }
        foreach (var member in _members.Values)
        {
            if (member.Personal is { } personal)
            {
                var (left, right) = PersonalGains(member);
                personal.Mixer.SetGains(left, right);
            }
        }

        var wanted = _members.Values
            .Where(m => m.Clustered && m.Listener is not null)
            .ToDictionary(m => m, ClusterKey);

        // Re-key: each cluster takes the rendering most of its listeners now want, unless a bigger cluster took it.
        var byKey = new Dictionary<string, Cluster>();
        foreach (var cluster in _clusters.OrderByDescending(c => ListenersOf(c).Count()))
        {
            var majority = ListenersOf(cluster)
                .Where(wanted.ContainsKey)
                .GroupBy(m => wanted[m].Key)
                .OrderByDescending(g => g.Count())
                .FirstOrDefault()?.Key;
            if (majority is not null && !byKey.ContainsKey(majority))
            {
                if (cluster.Key != majority)
                {
                    cluster.Key = majority;
                    var (left, right) = ClusterGains(wanted.First(w => w.Value.Key == majority).Value.Renderings);
                    cluster.Mixer.SetGains(left, right);
                }
                byKey[majority] = cluster;
            }
        }

        foreach (var (member, (key, renderings)) in wanted)
        {
            if (!byKey.TryGetValue(key, out var target))
            {
                target = CreateCluster(key, renderings);
                byKey[key] = target;
            }
            if ((member.Pending ?? member.Current)?.Cluster != target)
            {
                MoveTo(member, target);
            }
        }

        foreach (var cluster in _clusters.Where(c => !ListenersOf(c).Any()).ToList())
        {
            ScheduleRemoval(cluster);
        }
    }

    /// <summary>Listeners on the cluster's stream or switching to it (derived from the links, so it cannot drift).</summary>
    private IEnumerable<Member> ListenersOf(Cluster cluster) =>
        _members.Values.Where(m => m.Current?.Cluster == cluster || m.Pending?.Cluster == cluster);

    /// <summary>Exact renderings of every other talker, as this talker hears them.</summary>
    private (double[] Left, double[] Right) PersonalGains(Member listener) =>
        MatrixMixer.Rows(_room.Channels, _talkers
            .Where(id => id != listener.Id)
            .Select(id => _members.TryGetValue(id, out var t) ? t : null)
            .Where(t => t?.Source is { Slot: > 0 })
            .Select(t => (t!.Source!.Slot, _context.Render(t.Snapshot, listener.Snapshot))));

    private (double[] Left, double[] Right) ClusterGains(IReadOnlyDictionary<Guid, Rendering> renderings) =>
        MatrixMixer.Rows(_room.Channels, renderings
            .Select(r => _members.TryGetValue(r.Key, out var t) && t.Source is { Slot: > 0 } source ? (source.Slot, r.Value) : (0, Rendering.Silent))
            .Where(r => r.Item1 > 0));

    /// <summary>
    /// The talkers as this listener hears them, rounded to the cluster grid; the key names that rendering. In a
    /// non-spatial room every listener gets the same key.
    /// </summary>
    private (string Key, Dictionary<Guid, Rendering> Renderings) ClusterKey(Member listener)
    {
        var key = new StringBuilder();
        var renderings = new Dictionary<Guid, Rendering>();
        foreach (var id in _talkers.Order())
        {
            if (id == listener.Id || !_members.TryGetValue(id, out var talker) || talker.Source is not { Slot: > 0 })
            {
                continue;
            }
            if (_quantizer.Quantize(_context.Render(talker.Snapshot, listener.Snapshot)) is { } q)
            {
                key.Append(id.ToString("N")[..8]).Append(':').Append(q.GainStep).Append(',').Append(q.PanStep).Append(';');
                renderings[id] = _quantizer.Dequantize(q);
            }
        }
        return (key.ToString(), renderings);
    }

    private Cluster CreateCluster(string key, IReadOnlyDictionary<Guid, Rendering> renderings)
    {
        var name = $"c{++_clusterCount}-{_roomTag}";
        var cluster = new Cluster(key, new MatrixMixer(name, _room.Channels, _context), OpusEncodeOnly($"cenc-{name}", _context.Options),
            Make("tee", $"ctee-{name}"));
        cluster.OpusTee.SetProperty("allow-not-linked", true);
        var (left, right) = ClusterGains(renderings);
        cluster.Mixer.SetGains(left, right, running: false);
        cluster.Mixer.AddTo(Pipeline);
        Add(Pipeline, cluster.Encoder, cluster.OpusTee);
        Link(cluster.Mixer.Src, cluster.Encoder.GetStaticPad("sink"), "cluster matrix → encoder");
        Link(cluster.Encoder.GetStaticPad("src"), cluster.OpusTee.GetStaticPad("sink"), "cluster encoder → opus tee");
        cluster.OpusTee.SyncStateWithParent();
        cluster.Encoder.SyncStateWithParent();
        cluster.Mixer.Sync();
        cluster.RoomTeePad = _room.RequestTeePad();
        Link(cluster.RoomTeePad, cluster.Mixer.Sink, "tee → cluster matrix");
        _clusters.Add(cluster);
        return cluster;
    }

    private void ScheduleRemoval(Cluster cluster)
    {
        if (cluster.RemovalScheduled)
        {
            return;
        }
        cluster.RemovalScheduled = true;
        _ = Task.Delay(EmptyClusterGrace).ContinueWith(_ => PostSafe(() =>
        {
            cluster.RemovalScheduled = false;
            if (!ListenersOf(cluster).Any() && _clusters.Contains(cluster))
            {
                RemoveCluster(cluster);
            }
        }), TaskScheduler.Default);
    }

    private void RemoveCluster(Cluster cluster)
    {
        _clusters.Remove(cluster);
        if (cluster.RoomTeePad is { } teePad)
        {
            _room.ReleaseTeePad(teePad, cluster.Mixer.Sink, $"tee → {cluster.Mixer.Queue.Name}");
            cluster.RoomTeePad = null;
        }
        Remove(Pipeline, [.. cluster.Mixer.Elements, cluster.Encoder, cluster.OpusTee]);
    }

    /// <summary>Links the listener to the cluster's stream; the selector moves to it on its first frame.</summary>
    private void MoveTo(Member member, Cluster target)
    {
        DetachLink(member, member.Pending);
        if (member.Current?.Cluster == target)
        {
            return; // already on it: the pending move away is simply cancelled
        }
        var listener = member.Listener!;
        var queue = Make("queue", $"cq-{ShortId(member.Id)}-{++member.LinkCount}");
        queue.SetProperty("max-size-buffers", 0u);
        queue.SetProperty("max-size-bytes", 0u);
        queue.SetProperty("max-size-time", (ulong)_context.Options.ChainQueueMs * 1_000_000);
        Global.UtilSetObjectArg(queue, "leaky", "downstream");
        var link = new ClusterLink(target, queue);
        member.Pending = link;
        Add(Pipeline, queue);
        link.SelectorPad = listener.Selector.RequestPadSimple("sink_%u") ?? throw new InvalidOperationException("No selector pad.");
        Link(queue.GetStaticPad("src"), link.SelectorPad, "cluster queue → selector");
        link.SelectorPad.AddProbe(PadProbeType.Buffer, (_, _) =>
        {
            PostSafe(() => CompleteMove(member, link));
            return PadProbeReturn.Remove;
        });
        queue.SyncStateWithParent();
        link.TeePad = target.OpusTee.RequestPadSimple("src_%u") ?? throw new InvalidOperationException("No opus tee pad.");
        Link(link.TeePad, queue.GetStaticPad("sink"), "cluster opus tee → listener");
    }

    /// <summary>Worker: the new cluster stream reached the selector; switch to it and drop what it replaces.</summary>
    private void CompleteMove(Member member, ClusterLink link)
    {
        if (member.Pending != link || member.Listener is not { } listener)
        {
            return;
        }
        listener.Selector.SetProperty("active-pad", link.SelectorPad);
        var previous = member.Current;
        member.Current = link;
        member.Pending = null;
        DetachLink(member, previous);
        if (member.Personal is not null && !_talkers.Contains(member.Id))
        {
            RemovePersonal(member); // back from talking
        }
    }

    private void DetachLink(Member member, ClusterLink? link)
    {
        if (link is null)
        {
            return;
        }
        if (member.Pending == link)
        {
            member.Pending = null;
        }
        if (member.Current == link)
        {
            member.Current = null;
        }
        if (link.TeePad is { } teePad)
        {
            UnlinkWhenIdle(teePad, link.Queue.GetStaticPad("sink")!, _context.Log, $"cluster tee → {link.Queue.Name}");
            link.Cluster.OpusTee.ReleaseRequestPad(teePad);
            link.TeePad = null;
        }
        Retire(member, link);
        if (!ListenersOf(link.Cluster).Any())
        {
            ScheduleRemoval(link.Cluster);
        }
    }

    /// <summary>
    /// Removes a link's queue and selector pad once the queue is not pushing. Stopping it straight away can deadlock:
    /// its thread may be blocked pushing into webrtcbin (before the connection is up, or while the room tears down),
    /// and stopping waits for that thread. Links still not idle are removed by <see cref="Release"/>, after webrtcbin
    /// has stopped.
    /// </summary>
    private void Retire(Member member, ClusterLink link)
    {
        member.Retired.Add(link);
        if (_disposed)
        {
            return; // Release takes them
        }
        link.Queue.GetStaticPad("src")!.AddProbe(PadProbeType.Idle, (_, _) =>
        {
            PostSafe(() => RemoveRetired(member, link));
            return PadProbeReturn.Remove;
        });
    }

    private void RemoveRetired(Member member, ClusterLink link)
    {
        if (!member.Retired.Remove(link))
        {
            return; // already removed (Release)
        }
        Remove(Pipeline, [link.Queue]);
        if (link.SelectorPad is { } selectorPad && member.Listener is { } listener)
        {
            listener.Selector.ReleaseRequestPad(selectorPad);
            link.SelectorPad = null;
        }
    }

    private sealed class Member(ParticipantSnapshot snapshot)
    {
        public long LastVoice = long.MinValue / 2; // TickCount64 of the last voiced buffer (streaming thread writes)

        private SourceSlot? _source;

        public Guid Id { get; } = snapshot.Id;
        public ParticipantSnapshot Snapshot { get; set; } = snapshot;
        public bool Audible => !Snapshot.Muted && Snapshot.Gain > 0;
        public ListenerPart? Listener { get; set; }
        public PersonalPart? Personal { get; set; }
        public int SwitchGeneration { get; set; }

        /// <summary>Hears a cluster mix (not talking, and not within the switch-back delay after talking).</summary>
        public bool Clustered { get; set; }

        /// <summary>The cluster stream the selector is on, and one being switched to.</summary>
        public ClusterLink? Current { get; set; }
        public ClusterLink? Pending { get; set; }
        public int LinkCount { get; set; }

        /// <summary>Links cut from their cluster whose queue and selector pad are still to be removed (worker).</summary>
        public List<ClusterLink> Retired { get; } = [];

        /// <summary>Set by AttachSource (streaming thread); read and changed on the worker.</summary>
        public SourceSlot? Source
        {
            get => Volatile.Read(ref _source);
            set => Volatile.Write(ref _source, value);
        }
    }

    private sealed class ListenerPart(Element selector, Bin payloader)
    {
        public Element Selector { get; } = selector;
        public Bin Payloader { get; } = payloader;
    }

    private sealed class PersonalPart(MatrixMixer mixer, Bin encoder)
    {
        public MatrixMixer Mixer { get; } = mixer;
        public Bin Encoder { get; } = encoder;
        public Pad? TeePad { get; set; }
        public Pad? SelectorPad { get; set; }
        public bool Active { get; set; }
    }

    private sealed class Cluster(string key, MatrixMixer mixer, Bin encoder, Element opusTee)
    {
        public string Key { get; set; } = key;
        public MatrixMixer Mixer { get; } = mixer;
        public Bin Encoder { get; } = encoder;
        public Element OpusTee { get; } = opusTee;
        public Pad? RoomTeePad { get; set; }
        public bool RemovalScheduled { get; set; }
    }

    private sealed class ClusterLink(Cluster cluster, Element queue)
    {
        public Cluster Cluster { get; } = cluster;
        public Element Queue { get; } = queue;
        public Pad? TeePad { get; set; }
        public Pad? SelectorPad { get; set; }
    }
}
