// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using System.Net.Http.Json;
using SpatialAudioServer.Api;
using SpatialAudioServer.LoadTest;
using SpatialAudioServer.Rooms;

namespace SpatialAudioServer.Tests;

/// <summary>
/// End-to-end mixing through real WebRTC: fake clients send tones and check what comes back. Abstract: each mix
/// engine runs it through a concrete subclass in EngineSuites.cs.
/// </summary>
public abstract class MixSuite<TEngine>() : MediaTestBase(TEngine.Name) where TEngine : IEngineUnderTest
{
    [SkippableFact]
    public async Task Two_participants_hear_each_other_but_not_themselves()
    {
        var room = await CreateRoomAsync();
        FakeClient[] clients = [await JoinAsync(room.Id, 0), await JoinAsync(room.Id, 1)];

        var measured = await SettleAndMeasureAsync(clients, Others(clients));

        AssertHears(measured, Others(clients));
        var details = await Api.GetFromJsonAsync<RoomDetails>($"/api/rooms/{room.Id}", Json.Options);
        Assert.All(details!.Participants, p => Assert.Equal(ParticipantState.Connected, p.State));
        Assert.Equal(TEngine.Name, details.Engine);
        Assert.Equal(2, await Media.MixedPairsAsync(room.Id));
    }

    [SkippableFact]
    public async Task Four_participants_each_hear_the_other_three()
    {
        var room = await CreateRoomAsync();
        var clients = new List<FakeClient>();
        for (var i = 0; i < 4; i++)
        {
            clients.Add(await JoinAsync(room.Id, i));
        }

        AssertHears(await SettleAndMeasureAsync(clients, Others(clients)), Others(clients));
        Assert.Equal(12, await Media.MixedPairsAsync(room.Id));
    }
}
