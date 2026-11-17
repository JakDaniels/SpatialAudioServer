// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using SpatialAudioServer.Storage;

namespace SpatialAudioServer.Tests;

/// <summary>Stopping the server ends everyone's session and stops the pipelines promptly.</summary>
[Collection("media")]
public sealed class ShutdownTests() : MediaTestBase(SpatialAudioServer.Media.Engines.MixEngines.Default)
{
    [Fact]
    public async Task Stopping_closes_sessions_as_server_shutdown_and_does_not_hang()
    {
        var room = await CreateRoomAsync();
        var clients = new List<LoadTest.FakeClient> { await JoinAsync(room.Id, 0), await JoinAsync(room.Id, 1), await JoinAsync(room.Id, 2) };
        await SettleAndMeasureAsync(clients, Others(clients));
        var databasePath = Server.Services.GetRequiredService<Database>().Path;

        var clock = Stopwatch.StartNew();
        await Server.DisposeAsync();
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15), $"stopping took {clock.Elapsed.TotalSeconds:F1} s");

        var sessions = new ActivityLog(new Database(databasePath), TimeProvider.System).Sessions(room.Id);
        Assert.Equal(3, sessions.Count);
        Assert.All(sessions, s =>
        {
            Assert.NotNull(s.LeftAt);
            Assert.Equal("server-shutdown", s.LeaveReason);
        });
    }
}
