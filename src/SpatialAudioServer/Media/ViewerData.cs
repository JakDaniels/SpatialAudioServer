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
/// <para>Participants are keyed by their external id when it is a UUID (an avatar), otherwise by our participant
/// id (a browser or bot). A viewer looks every UUID up with its simulator to show a name, so the OpenSim module
/// registers those participants' names there (polling the room); they are announced <see cref="GuestAnnounceDelay"/>
/// after they connect, so the name is there before the viewer asks (if not, it shows "(loading)" and asks again a
/// minute later). Avatars are announced as soon as they are added. Neighbouring regions (a viewer connected as a
/// non-primary listener) are not handled yet: everyone is announced as primary.</para>
/// </remarks>
internal sealed class ViewerData : IDisposable
{
    /// <summary>How often levels and speaking flags go out (only changes are sent).</summary>
    public static readonly TimeSpan LevelInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>How long after connecting a participant that is not an avatar is announced (see the remarks).</summary>
    public static readonly TimeSpan GuestAnnounceDelay = TimeSpan.FromSeconds(2);

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
    private readonly Timer _timer;

    public ViewerData(RoomWorker worker, MediaOptions options, Func<IReadOnlyDictionary<Guid, double>> levels, Func<Guid, WebRtcBin?> rtcOf)
    {
        _worker = worker;
        _levels = levels;
        _rtcOf = rtcOf;
        _speakingDb = options.VadThresholdDb;
        _hangoverMs = options.VadHangoverMs;
        _timer = new Timer(_ => _worker.Post(Tick), null, LevelInterval, LevelInterval);
    }

    /// <summary>Messages sent, for tests and stats.</summary>
    public long Sent { get; private set; }

    /// <summary>A participant joined the room: viewers already listening are told.</summary>
    public void Added(ParticipantSnapshot participant)
    {
        var avatar = Guid.TryParse(participant.ExternalId, out var avatarId);
        var member = new Member(avatar ? avatarId.ToString() : participant.Id.ToString());
        _members[participant.Id] = member;
        if (avatar)
        {
            Announce(participant.Id, member);
        }
    }

    /// <summary>The participant's connection is up: one that is not an avatar is announced shortly.</summary>
    public void Connected(Guid participantId)
    {
        if (!_members.TryGetValue(participantId, out var member) || member.Announced)
        {
            return;
        }
        _ = Task.Delay(GuestAnnounceDelay).ContinueWith(_ =>
        {
            try
            {
                _worker.Post(() =>
                {
                    if (_members.TryGetValue(participantId, out var current) && current == member && !member.Announced)
                    {
                        Announce(participantId, member);
                    }
                });
            }
            catch (ObjectDisposedException)
            {
                // The room's media stopped meanwhile.
            }
        }, TaskScheduler.Default);
    }

    /// <summary>A participant left the room.</summary>
    public void Removed(Guid participantId)
    {
        if (_members.Remove(participantId, out var member) && member.Announced)
        {
            Broadcast(new JsonObject { [member.Key] = new JsonObject { ["l"] = true } }, except: participantId);
        }
    }

    private void Announce(Guid participantId, Member member)
    {
        member.Announced = true;
        Broadcast(new JsonObject { [member.Key] = Join() }, except: participantId);
    }

    /// <summary>The participant's endpoint went (re-offer or leaving): it gets nothing until it joins again.</summary>
    public void EndpointRemoved(Guid participantId)
    {
        if (_members.TryGetValue(participantId, out var member))
        {
            member.Listening = false;
        }
    }

    /// <summary>The viewer sent its <c>j</c>: it hears about everyone in the room (itself included) and gets our version.</summary>
    public void ViewerJoined(Guid participantId)
    {
        if (!_members.TryGetValue(participantId, out var member))
        {
            return;
        }
        member.Listening = true;
        member.Announced = true; // a viewer is in its own list from now on
        var message = new JsonObject();
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
        ((JsonObject)message[member.Key]!)["V"] = ServerVersion;
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

    private sealed class Member(string key)
    {
        public string Key { get; } = key;

        /// <summary>Viewers have been told about this participant (a <c>j</c> went out).</summary>
        public bool Announced { get; set; }

        /// <summary>A viewer that has joined over its data channel.</summary>
        public bool Listening { get; set; }

        public int SentLevel { get; set; }

        public bool Speaking { get; set; }

        public long LastLoud { get; set; } = long.MinValue / 2;
    }
}
