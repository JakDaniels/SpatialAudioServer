using System.Net.Http.Json;
using SpatialAudioServer.Api;
using Microsoft.Extensions.DependencyInjection;
using SpatialAudioServer.LoadTest;
using SpatialAudioServer.Media;
using SpatialAudioServer.Rooms;

namespace SpatialAudioServer.Tests;

/// <summary>Media tests share the CPU and loopback network, so they never run in parallel.</summary>
[CollectionDefinition("media", DisableParallelization = true)]
public sealed class MediaCollection;

/// <summary>
/// A real server with GStreamer, plus fake clients that join rooms through the HTTP APIs. Rooms are created with
/// <see cref="Engine"/>, so the same tests run against every mix engine (see EngineSuites.cs).
/// </summary>
public abstract class MediaTestBase : IDisposable
{
    /// <summary>Exact Goertzel bins at 20 ms, 200 Hz apart. Index i is participant i's tone.</summary>
    protected static readonly double[] Tones = [400, 600, 800, 1000, 1200, 1400, 1600, 1800];

    private readonly TempDir _dir = new();
    private readonly List<FakeClient> _clients = [];

    protected MediaTestBase(string engine)
    {
        Engine = engine;
        Server = new ServerFactory(_dir.File("server.db"), new Dictionary<string, string?> { ["Media:Enabled"] = "true" });
        Api = Server.CreateAuthedClient();
        Signal = Server.CreateClient();
    }

    protected string Engine { get; }

    protected ServerFactory Server { get; }

    protected GstRoomMedia Media => Server.Services.GetRequiredService<GstRoomMedia>();

    protected HttpClient Api { get; }

    protected HttpClient Signal { get; }

    protected async Task<RoomSnapshot> CreateRoomAsync() =>
        await (await Api.PostAsJsonAsync("/api/rooms", new { name = "media test", engine = Engine })).Read<RoomSnapshot>();

    /// <summary>Adds a participant via the control API and connects a fake client sending <c>Tones[index]</c>.</summary>
    protected async Task<FakeClient> JoinAsync(Guid roomId, int index, bool talking = true, bool viewerStyle = false)
    {
        var response = await Api.PostAsJsonAsync($"/api/rooms/{roomId}/participants", new { displayName = $"fake{index}" });
        response.EnsureSuccessStatusCode();
        var join = await response.Read<JoinResponse>();
        var client = new FakeClient($"fake{index}", Tones[index], Tones, talking: talking, viewerStyle: viewerStyle);
        _clients.Add(client);
        await client.ConnectAsync(Signal, join.ParticipantId, join.JoinToken);
        return client;
    }

    /// <summary>
    /// Waits until every client hears exactly the expected tones (connections up, chains linked, jitterbuffers
    /// filled), then measures for <paramref name="window"/> and returns each client's tone presence.
    /// </summary>
    protected static async Task<Dictionary<FakeClient, IReadOnlyDictionary<double, double>>> SettleAndMeasureAsync(
        IReadOnlyList<FakeClient> clients, Func<FakeClient, IEnumerable<double>> expected, TimeSpan? window = null)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (true)
        {
            clients.ToList().ForEach(c => c.Meter.Reset());
            await Task.Delay(500);
            var settled = clients.All(c =>
            {
                var presence = c.Meter.Presence();
                var want = expected(c).ToHashSet();
                return c.Meter.Frames > 10 && presence.All(p => want.Contains(p.Key) ? p.Value > 0.5 : p.Value < 0.1);
            });
            if (settled)
            {
                break;
            }
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Mix never settled: " + Describe(clients));
            }
        }

        // Both directions of trickle ICE must work: on one machine ICE also connects from the client's candidates
        // alone, which once hid a client that could not read the server's.
        Assert.All(clients.Where(c => !c.ViewerStyle), c => // viewer-style clients take candidates from the answer only
        {
            Assert.Null(c.IceError);
            Assert.True(c.RemoteCandidates > 0, $"{c.Name} never received a server candidate.");
        });

        clients.ToList().ForEach(c => c.Meter.Reset());
        await Task.Delay(window ?? TimeSpan.FromSeconds(2));
        return clients.ToDictionary(c => c, c => c.Meter.Presence());
    }

    /// <summary>Asserts each client hears every expected tone in most frames and no other tone at all.</summary>
    protected static void AssertHears(
        Dictionary<FakeClient, IReadOnlyDictionary<double, double>> measured, Func<FakeClient, IEnumerable<double>> expected)
    {
        foreach (var (client, presence) in measured)
        {
            var want = expected(client).ToHashSet();
            foreach (var (tone, fraction) in presence)
            {
                if (want.Contains(tone))
                {
                    Assert.True(fraction > 0.9, $"{client.Name} should hear {tone} Hz but heard it in {fraction:P0} of frames. {Describe(measured)}");
                }
                else
                {
                    Assert.True(fraction < 0.02, $"{client.Name} should not hear {tone} Hz but did in {fraction:P0} of frames. {Describe(measured)}");
                }
            }
        }
    }

    /// <summary>Everyone else's tone: the mix-minus rule.</summary>
    protected static Func<FakeClient, IEnumerable<double>> Others(IEnumerable<FakeClient> clients) =>
        c => clients.Where(o => o != c).Select(o => o.ToneHz);

    protected static string Describe(IEnumerable<FakeClient> clients) =>
        string.Join("; ", clients.Select(c =>
            $"{c.Name}({c.ToneHz}) rx={c.Receiving} frames={c.Meter.Frames} hears " +
            string.Join(",", c.Meter.Presence().Where(p => p.Value > 0).Select(p => $"{p.Key}:{p.Value:P0}"))));

    private static string Describe(Dictionary<FakeClient, IReadOnlyDictionary<double, double>> measured) =>
        string.Join("; ", measured.Select(m =>
            $"{m.Key.Name}({m.Key.ToneHz}) hears " + string.Join(",", m.Value.Where(p => p.Value > 0).Select(p => $"{p.Key}:{p.Value:P0}"))));

    public void Dispose()
    {
        foreach (var client in _clients)
        {
            client.Dispose();
        }
        Api.Dispose();
        Signal.Dispose();
        Server.Dispose();
        _dir.Dispose();
        GC.SuppressFinalize(this);
    }
}
