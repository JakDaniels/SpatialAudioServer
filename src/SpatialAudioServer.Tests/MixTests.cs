using System.Net.Http.Json;
using SpatialAudioServer.Api;
using SpatialAudioServer.LoadTest;
using SpatialAudioServer.Rooms;

namespace SpatialAudioServer.Tests;

/// <summary>End-to-end mixing through real WebRTC: fake clients send tones and check what comes back.</summary>
[Collection("media")]
public sealed class MixTests : MediaTestBase
{
    [Fact]
    public async Task Two_participants_hear_each_other_but_not_themselves()
    {
        var room = await CreateRoomAsync();
        FakeClient[] clients = [await JoinAsync(room.Id, 0), await JoinAsync(room.Id, 1)];

        var measured = await SettleAndMeasureAsync(clients, Others(clients));

        AssertHears(measured, Others(clients));
        var details = await Api.GetFromJsonAsync<RoomDetails>($"/api/rooms/{room.Id}", Json.Options);
        Assert.All(details!.Participants, p => Assert.Equal(ParticipantState.Connected, p.State));
    }

    [Fact]
    public async Task Four_participants_each_hear_the_other_three()
    {
        var room = await CreateRoomAsync();
        var clients = new List<FakeClient>();
        for (var i = 0; i < 4; i++)
        {
            clients.Add(await JoinAsync(room.Id, i));
        }

        AssertHears(await SettleAndMeasureAsync(clients, Others(clients)), Others(clients));
    }
}
