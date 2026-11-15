using System.Reflection;
using System.Text.Json.Nodes;
using SpatialAudioServer.Configuration;
using SpatialAudioServer.Rooms;

namespace SpatialAudioServer.Media;

/// <summary>
/// What a room tells SL viewers on their "SLData" data channels, as Second Life's voice server does (the viewer's
/// <c>LLVoiceWebRTCConnection::OnDataReceivedImpl</c>): one JSON object keyed by avatar UUID, with <c>j</c>
/// <c>{p}</c> when someone joins (the viewer adds them to its voice list; in a spatial session only with
/// <c>p: true</c>, their primary region), <c>l: true</c> when they leave, <c>p</c> (level × 128) and <c>v</c>
/// (speaking) while they talk, and <c>V</c> (the server's version) keyed by the viewer's own UUID.
/// </summary>
/// <remarks>
/// <para>Worker only, except <see cref="Dispose"/>. A viewer gets messages once it has sent its own <c>j</c>; it
/// is then told about everyone already in the room. Browsers and bots never open a data channel, so they only appear
/// in viewers' lists.</para>
/// <para>Neighbouring regions: a viewer near a border is connected to each nearby region's room, and its <c>j</c>
/// says <c>p: true</c> only in the room of the region its avatar is in (it sends <c>j</c> again when that changes;
/// its microphone is live only there). A viewer adds people to its list only from <c>j</c> with <c>p: true</c>, so
/// an avatar is announced only in its primary room, and withdrawn (<c>l</c>) when it stops being primary there; its
/// neighbours see it through their own connection to that room. Browsers and bots are always primary.</para>
/// <para>A viewer keeps one list for all its connections, remembering the region each person was added from; it
/// ignores <c>j</c> for someone already listed and takes <c>l</c> only from that region. When an avatar crosses a
/// border, the new room's <c>j</c> and the old room's <c>l</c> reach a neighbour over different connections in
/// either order, and with <c>j</c> first the avatar would vanish from its list. So every announcement is repeated
/// <see cref="ReannounceDelay"/> later, which adds it back from the right region.</para>
/// <para>A viewer keeps its list across a lost connection, so one that joins (or rejoins, e.g. after a server restart)
/// is also told about recent departures (<see cref="ViewerDepartures"/>) that are not in the room now: those that left
/// while it was away, and those the integration reports gone (<see cref="Gone"/>).</para>
/// <para>Participants are keyed by their external id when it is a UUID (an avatar), otherwise by our participant
/// id (a browser or bot). A viewer looks every UUID up with its simulator to show a name, so the OpenSim module
/// registers those participants' names there (polling the room); they are announced <see cref="GuestAnnounceDelay"/>
/// after they connect, so the name is there before the viewer asks (if not, it shows "(loading)" and asks again a
/// minute later). Avatars are announced once their viewer says this is their primary region (below).</para>
/// </remarks>
internal sealed class ViewerData : IDisposable
{
    /// <summary>How often levels and speaking flags go out (only changes are sent).</summary>
    public static readonly TimeSpan LevelInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>How long after connecting a participant that is not an avatar is announced (see the remarks).</summary>
    public static readonly TimeSpan GuestAnnounceDelay = TimeSpan.FromSeconds(2);

    /// <summary>How long after an announcement it is sent again (see the remarks).</summary>
    public static readonly TimeSpan ReannounceDelay = TimeSpan.FromSeconds(2);

    /// <summary>The level sent as p = 0 … 128 maps −60 dBFS … 0 dBFS (speech is around −30 … −15).</summary>
    private const double FloorDb = -60;

    private static readonly string ServerVersion = "SpatialAudioServer " +
        (typeof(ViewerData).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown");

    private readonly RoomWorker _worker;
    private readonly Func<IReadOnlyDictionary<Guid, double>> _levels;
    private readonly Func<Guid, WebRtcBin?> _rtcOf;
    private readonly double _speakingDb;
    private readonly long _hangoverMs;
    private readonly Dictionary<Guid, Member> _members = [];
    private readonly ViewerDepartures _departures;
    private readonly Timer _timer;

    public ViewerData(RoomWorker worker, MediaOptions options, Func<IReadOnlyDictionary<Guid, double>> levels, Func<Guid, WebRtcBin?> rtcOf,
        ViewerDepartures? departures = null)
    {
        _departures = departures ?? new ViewerDepartures();
        _worker = worker;
        _levels = levels;
        _rtcOf = rtcOf;
        _speakingDb = options.VadThresholdDb;
        _hangoverMs = options.VadHangoverMs;
        _timer = new Timer(_ => _worker.Post(Tick), null, LevelInterval, LevelInterval);
    }

    /// <summary>Messages sent, for tests and stats.</summary>
    public long Sent { get; private set; }

    /// <summary>A participant joined the room. An avatar is announced when its viewer joins as primary, anyone else
    /// shortly after connecting (<see cref="Connected"/>).</summary>
    public void Added(ParticipantSnapshot participant) =>
        _members[participant.Id] = new Member(participant.ViewerKey, Guid.TryParse(participant.ExternalId, out _));

    /// <summary>The participant's connection is up: one that is not an avatar is announced shortly.</summary>
    public void Connected(Guid participantId)
    {
        if (!_members.TryGetValue(participantId, out var member) || member.Avatar || member.Announced)
        {
            return;
        }
        Later(GuestAnnounceDelay, participantId, member, () =>
        {
            if (!member.Announced)
            {
                Announce(participantId, member);
            }
        });
    }

    /// <summary>Runs <paramref name="action"/> on the worker after <paramref name="delay"/> if the member is still here.</summary>
    private void Later(TimeSpan delay, Guid participantId, Member member, Action action) =>
        _ = Task.Delay(delay).ContinueWith(_ =>
        {
            try
            {
                _worker.Post(() =>
                {
                    if (_members.TryGetValue(participantId, out var current) && current == member)
                    {
                        action();
                    }
                });
            }
            catch (ObjectDisposedException)
            {
                // The room's media stopped meanwhile.
            }
        }, TaskScheduler.Default);

    /// <summary>A participant left the room.</summary>
    public void Removed(Guid participantId)
    {
        if (_members.Remove(participantId, out var member))
        {
            if (member.Announced)
            {
                Broadcast(new JsonObject { [member.Key] = new JsonObject { ["l"] = true } }, except: participantId);
            }
            if (_members.Values.All(m => m.Key != member.Key))
            {
                _departures.Add(member.Key);
            }
        }
    }

    /// <summary>
    /// The integration says these viewer keys are gone (e.g. NPCs it removed while the server was restarting):
    /// listening viewers are told, and joining ones later. Keys in the room now are left alone.
    /// </summary>
    public void Gone(IReadOnlyList<string> keys)
    {
        var message = new JsonObject();
        foreach (var key in keys.Where(k => _members.Values.All(m => !string.Equals(m.Key, k, StringComparison.OrdinalIgnoreCase))))
        {
            message[key.ToLowerInvariant()] = new JsonObject { ["l"] = true };
        }
        if (message.Count > 0)
        {
            Broadcast(message, except: null);
        }
    }

    private void Announce(Guid participantId, Member member)
    {
        member.Announced = true;
        var generation = ++member.Generation;
        Broadcast(new JsonObject { [member.Key] = Join() }, except: participantId);
        Later(ReannounceDelay, participantId, member, () =>
        {
            if (member.Announced && member.Generation == generation)
            {
                Broadcast(new JsonObject { [member.Key] = Join() }, except: participantId);
            }
        });
    }

    /// <summary>The participant is no longer primary here: viewers drop it from this region's list.</summary>
    private void Withdraw(Guid participantId, Member member)
    {
        member.Announced = false;
        member.Generation++;
        member.Speaking = false;
        member.SentLevel = 0;
        Broadcast(new JsonObject { [member.Key] = new JsonObject { ["l"] = true } }, except: participantId);
    }

    /// <summary>
    /// The participant changed: if viewers now know it by another key (the integration gave it an in-world avatar),
    /// it leaves under the old key and is announced under the new one.
    /// </summary>
    public void Updated(ParticipantSnapshot participant)
    {
        if (!_members.TryGetValue(participant.Id, out var member) || member.Key == participant.ViewerKey)
        {
            return;
        }
        var announced = member.Announced;
        if (announced)
        {
            Withdraw(participant.Id, member);
        }
        _departures.Add(member.Key);
        member.Key = participant.ViewerKey;
        if (announced)
        {
            Announce(participant.Id, member);
        }
    }

    /// <summary>The participant's endpoint went (re-offer or leaving): it gets nothing until it joins again.</summary>
    public void EndpointRemoved(Guid participantId)
    {
        if (_members.TryGetValue(participantId, out var member))
        {
            member.Listening = false;
        }
    }

    /// <summary>
    /// The viewer sent its <c>j</c> (on joining, and again when its primary region changes): it is announced to the
    /// others or withdrawn, and hears about everyone announced (itself included when primary) and gets our version.
    /// </summary>
    public void ViewerJoined(Guid participantId, bool primary)
    {
        if (!_members.TryGetValue(participantId, out var member))
        {
            return;
        }
        member.Listening = true;
        if (primary && !member.Announced)
        {
            Announce(participantId, member);
        }
        else if (!primary && member.Announced)
        {
            Withdraw(participantId, member);
        }
        var message = new JsonObject();
        foreach (var key in _departures.Recent())
        {
            if (_members.Values.All(m => !string.Equals(m.Key, key, StringComparison.OrdinalIgnoreCase)))
            {
                message[key.ToLowerInvariant()] = new JsonObject { ["l"] = true };
            }
        }
        foreach (var other in _members.Values.Where(m => m.Announced))
        {
            var entry = Join();
            if (other.Speaking)
            {
                entry["p"] = other.SentLevel;
                entry["v"] = true;
            }
            message[other.Key] = entry;
        }
        if (message[member.Key] is not JsonObject own)
        {
            message[member.Key] = own = new JsonObject();
        }
        own["V"] = ServerVersion;
        Send(participantId, message.ToJsonString());
    }

    /// <summary>Stops the level timer and waits for a running callback, so nothing is posted to the worker after.</summary>
    public void Dispose()
    {
        using var stopped = new ManualResetEvent(false);
        if (_timer.Dispose(stopped))
        {
            stopped.WaitOne(TimeSpan.FromSeconds(5));
        }
    }

    /// <summary>Worker, every <see cref="LevelInterval"/>: levels and speaking flags that changed.</summary>
    private void Tick()
    {
        if (!_members.Values.Any(m => m.Listening))
        {
            return;
        }
        var levels = _levels();
        var now = Environment.TickCount64;
        var message = new JsonObject();
        foreach (var (id, member) in _members.Where(m => m.Value.Announced))
        {
            var db = levels.TryGetValue(id, out var rms) ? 20 * Math.Log10(Math.Max(rms, 1e-6)) : double.NegativeInfinity;
            if (db > _speakingDb)
            {
                member.LastLoud = now;
            }
            var speaking = now - member.LastLoud < _hangoverMs;
            var level = speaking ? (int)Math.Round(Math.Clamp((db - FloorDb) / -FloorDb, 0, 1) * 128) : 0;
            var entry = new JsonObject();
            if (Math.Abs(level - member.SentLevel) >= 4 || (level == 0) != (member.SentLevel == 0))
            {
                entry["p"] = level;
                member.SentLevel = level;
            }
            if (speaking != member.Speaking)
            {
                entry["v"] = speaking;
                member.Speaking = speaking;
            }
            if (entry.Count > 0)
            {
                message[member.Key] = entry;
            }
        }
        if (message.Count > 0)
        {
            Broadcast(message, except: null);
        }
    }

    private void Broadcast(JsonObject message, Guid? except)
    {
        string? text = null;
        foreach (var (id, member) in _members)
        {
            if (member.Listening && id != except)
            {
                Send(id, text ??= message.ToJsonString());
            }
        }
    }

    private void Send(Guid participantId, string text)
    {
        if (_rtcOf(participantId)?.SendData(text) == true)
        {
            Sent++;
        }
    }

    private static JsonObject Join() => new() { ["j"] = new JsonObject { ["p"] = true } };

    private sealed class Member(string key, bool avatar)
    {
        public string Key { get; set; } = key;

        /// <summary>An SL avatar (external id is a UUID): announced only where its viewer is primary.</summary>
        public bool Avatar { get; } = avatar;

        /// <summary>Viewers have been told about this participant (a <c>j</c> went out).</summary>
        public bool Announced { get; set; }

        /// <summary>A viewer that has joined over its data channel.</summary>
        public bool Listening { get; set; }

        /// <summary>Bumped on each announcement and withdrawal, so a stale repeat is dropped.</summary>
        public int Generation { get; set; }

        public int SentLevel { get; set; }

        public bool Speaking { get; set; }

        public long LastLoud { get; set; } = long.MinValue / 2;
    }
}
