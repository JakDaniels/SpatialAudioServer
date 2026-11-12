using SpatialAudioServer.LoadTest;

namespace SpatialAudioServer.Tests;

/// <summary>matrix-shared specifics: talkers get personal mixes, everyone else shares one encoder.</summary>
[Collection("media")]
public sealed class SharedEncoderTests() : MediaTestBase(MatrixShared.Name)
{
    [Fact]
    public async Task Silent_participants_share_one_encoder_and_talkers_hear_each_other()
    {
        var room = await CreateRoomAsync();
        var clients = new List<FakeClient>();
        for (var i = 0; i < 5; i++)
        {
            clients.Add(await JoinAsync(room.Id, i, talking: i < 2));
        }

        var talkers = TalkersHeardBy(clients);
        AssertHears(await SettleAndMeasureAsync(clients, talkers), talkers);
        Assert.Equal(3, (await Media.CountersAsync(room.Id))["encoders"]); // shared + 2 talkers
        Assert.Equal(1 + 1 + 3 * 2, await Media.MixedPairsAsync(room.Id)); // each talker hears the other; 3 listeners hear both
    }

    [Fact]
    public async Task Talkers_change_and_the_mixes_follow()
    {
        var room = await CreateRoomAsync();
        var clients = new List<FakeClient>();
        for (var i = 0; i < 4; i++)
        {
            clients.Add(await JoinAsync(room.Id, i, talking: i == 0));
        }
        AssertHears(await SettleAndMeasureAsync(clients, TalkersHeardBy(clients)), TalkersHeardBy(clients));

        // The first talker stops and two others start.
        clients[0].SetTalking(false);
        clients[2].SetTalking(true);
        clients[3].SetTalking(true);
        AssertHears(await SettleAndMeasureAsync(clients, TalkersHeardBy(clients)), TalkersHeardBy(clients));
        Assert.Equal(3, (await Media.CountersAsync(room.Id))["encoders"]);

        // Everyone stops: after the hangover only the shared encoder is left.
        clients.ForEach(c => c.SetTalking(false));
        await Task.Delay(2000);
        Assert.Equal(1, (await Media.CountersAsync(room.Id))["encoders"]);
        Assert.Equal(0, (await Media.CountersAsync(room.Id))["talkers"]);
    }

    private static Func<FakeClient, IEnumerable<double>> TalkersHeardBy(IEnumerable<FakeClient> clients) =>
        c => clients.Where(o => o != c && o.Talking).Select(o => o.ToneHz);
}
