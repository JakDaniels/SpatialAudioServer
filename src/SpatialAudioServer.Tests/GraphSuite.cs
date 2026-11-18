// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using SpatialAudioServer.LoadTest;
using SpatialAudioServer.Media;

namespace SpatialAudioServer.Tests;

/// <summary>
/// The admin pipeline view's snapshot of a real room: every element is placed in a summary block, links join listed
/// pads, bins expand, and reading every element's properties (webrtcbin's insides included) leaves the room working.
/// Runs per engine (EngineSuites.cs), since the blocks come from the names each engine gives its elements.
/// </summary>
public abstract class GraphSuite<TEngine>() : MediaTestBase(TEngine.Name) where TEngine : IEngineUnderTest
{
    [SkippableFact]
    public async Task Every_element_is_in_a_block_and_links_join_listed_pads()
    {
        var room = await CreateRoomAsync();
        var clients = new List<FakeClient> { await JoinAsync(room.Id, 0), await JoinAsync(room.Id, 1), await JoinAsync(room.Id, 2, talking: false) };
        await SettleAndMeasureAsync(clients, Others(clients.Take(2)));

        var graph = (await Media.PipelineGraphAsync(room.Id, new HashSet<string>()))!;
        Assert.Equal(TEngine.Name, graph.Engine);
        Assert.Equal(3, graph.Participants.Count);
        Assert.All(graph.Elements, e =>
        {
            Assert.Null(e.Parent);
            Assert.True(e.Block is not null && e.Block.Kind != "other", $"{e.Name} ({e.Factory}) is in no block");
        });
        foreach (var participant in graph.Participants.Values)
        {
            foreach (var kind in new[] { "webrtc", "decode", "listener" })
            {
                Assert.Contains(graph.Elements, e => e.Block!.Kind == kind && e.Block.Participant == participant);
            }
        }
        if (TEngine.Name == "matrix-shared")
        {
            Assert.Contains(graph.Elements, e => e.Block!.Kind == "cluster");
        }
        AssertLinksJoinListedPads(graph);
        var webrtc = graph.Elements.First(e => e.Factory == "webrtcbin");
        Assert.True(webrtc.IsBin && webrtc.Children > 0);
        Assert.Equal("playing", webrtc.State);
        Assert.Contains("connection-state", webrtc.Live.Keys);

        // Expanding every bin, level by level, lists their insides with ghost-pad links into them.
        var expand = new HashSet<string>();
        PipelineGraph full;
        do
        {
            full = (await Media.PipelineGraphAsync(room.Id, expand))!;
        }
        while (full.Elements.Where(e => e.IsBin).Select(e => e.Path).Count(expand.Add) > 0);
        Assert.Contains(full.Elements, e => e.Parent == webrtc.Path && e.Factory == "rtpbin");
        Assert.Contains(full.Links, l => l.From.StartsWith(webrtc.Path + ":", StringComparison.Ordinal)
            && l.To.StartsWith(webrtc.Path + "/", StringComparison.Ordinal)
            || l.To.StartsWith(webrtc.Path + ":", StringComparison.Ordinal) && l.From.StartsWith(webrtc.Path + "/", StringComparison.Ordinal));
        AssertLinksJoinListedPads(full);

        // Every property of every element, then the room still mixes.
        foreach (var element in full.Elements)
        {
            var details = await Media.ElementDetailsAsync(room.Id, element.Path);
            Assert.NotNull(details);
            Assert.Contains(details.Properties, p => p.Name == "name");
        }
        Assert.Null(await Media.ElementDetailsAsync(room.Id, "no-such-element"));
        AssertHears(await SettleAndMeasureAsync(clients, Others(clients.Take(2))), Others(clients.Take(2)));
    }

    private static void AssertLinksJoinListedPads(PipelineGraph graph)
    {
        var pads = graph.Elements.SelectMany(e => e.Pads.Select(p => (Pad: $"{e.Path}:{p.Name}", p.Direction))).ToDictionary(p => p.Pad, p => p.Direction);
        Assert.NotEmpty(graph.Links);
        Assert.All(graph.Links, l =>
        {
            Assert.True(pads.ContainsKey(l.From), $"link from unlisted pad {l.From}");
            Assert.True(pads.ContainsKey(l.To), $"link to unlisted pad {l.To}");
        });
    }
}
