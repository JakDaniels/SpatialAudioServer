// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http.Json;
using SpatialAudioServer.Hosting;
using SpatialAudioServer.Media;
using SpatialAudioServer.Rooms;

namespace SpatialAudioServer.Tests;

/// <summary>Internet-facing settings: the public address copy of host candidates and the network allow-lists (no media).</summary>
public sealed class NetworkAccessTests : IDisposable
{
    private static readonly IPAddress Public = IPAddress.Parse("203.0.113.5");
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void A_host_candidate_gets_a_public_copy_with_its_own_foundation_and_a_lower_priority()
    {
        Assert.Equal("candidate:19 1 UDP 2015363326 203.0.113.5 40012 typ host",
            WebRtcBin.PublicCopy("candidate:1 1 UDP 2015363327 10.0.0.5 40012 typ host", Public));
        Assert.Equal("candidate:39 1 TCP 1010827518 203.0.113.5 40013 typ host tcptype passive",
            WebRtcBin.PublicCopy("candidate:3 1 TCP 1010827519 10.0.0.5 40013 typ host tcptype passive", Public));

        Assert.Null(WebRtcBin.PublicCopy("candidate:4 1 UDP 2015363583 fe80::1 40014 typ host", Public)); // other family
        Assert.Null(WebRtcBin.PublicCopy("candidate:5 1 UDP 1686052607 203.0.113.5 40015 typ srflx raddr 10.0.0.5 rport 40015", Public));
        Assert.Null(WebRtcBin.PublicCopy("candidate:6 1 UDP 2015363327 203.0.113.5 40016 typ host", Public)); // already public
        Assert.Null(WebRtcBin.PublicCopy("garbage", Public));
    }

    [Fact]
    public void Ranges_take_addresses_and_cidrs_and_an_empty_list_allows_everyone()
    {
        var ranges = NetworkAccess.ParseRanges(["10.0.0.0/8", " 192.0.2.7 ", "", "2001:db8::/32"], "test");
        Assert.True(NetworkAccess.Allowed(ranges, IPAddress.Parse("10.2.2.232")));
        Assert.True(NetworkAccess.Allowed(ranges, IPAddress.Parse("::ffff:10.1.1.1"))); // IPv4 seen as IPv6
        Assert.True(NetworkAccess.Allowed(ranges, IPAddress.Parse("192.0.2.7")));
        Assert.False(NetworkAccess.Allowed(ranges, IPAddress.Parse("192.0.2.8")));
        Assert.False(NetworkAccess.Allowed(ranges, null));
        Assert.True(NetworkAccess.Allowed([], null));
        Assert.Throws<InvalidOperationException>(() => NetworkAccess.ParseRanges(["10.0.0.0/33"], "test"));
        Assert.Throws<InvalidOperationException>(() => NetworkAccess.ParseRanges(["example.com"], "test"));

        Assert.Equal("admin", NetworkAccess.ListFor("/admin/index.html"));
        Assert.Equal("admin", NetworkAccess.ListFor("/api/admin/session"));
        Assert.Equal("api", NetworkAccess.ListFor("/api/rooms"));
        Assert.Null(NetworkAccess.ListFor("/signal/x/config"));
        Assert.Null(NetworkAccess.ListFor("/client/"));
        Assert.Null(NetworkAccess.ListFor("/apiary")); // segments, not prefixes
    }

    [Fact]
    public async Task Outside_the_allowed_networks_the_admin_ui_and_control_api_are_not_there_but_clients_still_are()
    {
        // The test server's requests have no remote address, so they are outside any list.
        using var server = new ServerFactory(_dir.File("server.db"), new Dictionary<string, string?>
        {
            ["Admin:AllowedNetworks:0"] = "192.0.2.0/24",
            ["Auth:AllowedNetworks:0"] = "192.0.2.0/24",
        });
        var api = server.CreateAuthedClient();
        Assert.Equal(HttpStatusCode.NotFound, (await api.GetAsync("/api/status")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await api.GetAsync("/api/admin/session")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await api.GetAsync("/admin/")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.GetAsync("/client/")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.GetAsync($"/signal/{Guid.NewGuid()}/config")).StatusCode); // reached

        using var open = new ServerFactory(_dir.File("open.db"));
        Assert.Equal(HttpStatusCode.OK, (await open.CreateAuthedClient().GetAsync("/api/status")).StatusCode);
    }

    [Fact]
    public void Bad_network_settings_stop_the_server_at_start_up()
    {
        using var badRange = new ServerFactory(_dir.File("a.db"), new Dictionary<string, string?> { ["Admin:AllowedNetworks:0"] = "nonsense" });
        Assert.ThrowsAny<Exception>(() => badRange.CreateClient());
        using var badPorts = new ServerFactory(_dir.File("b.db"), new Dictionary<string, string?>
        {
            ["Media:Network:MinPort"] = "50000",
            ["Media:Network:MaxPort"] = "40000",
        });
        Assert.ThrowsAny<Exception>(() => badPorts.CreateClient());
    }
}

/// <summary>The media port range and the public address, on a real connection.</summary>
[Collection("media")]
public sealed class MediaNetworkTests() : MediaTestBase(SpatialAudioServer.Media.Engines.MixEngines.Default, new Dictionary<string, string?>
{
    ["Media:Network:MinPort"] = "41000",
    ["Media:Network:MaxPort"] = "41099",
    ["Media:Network:PublicAddress"] = "203.0.113.5",
})
{
    [Fact]
    public async Task Server_candidates_use_the_port_range_and_also_come_with_the_public_address()
    {
        var room = await CreateRoomAsync();
        var client = await JoinAsync(room.Id, 0);
        var batch = await Media.WaitForLocalCandidatesAsync(room.Id, client.ParticipantId, 0, TimeSpan.FromSeconds(5), CancellationToken.None);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!batch.Complete && DateTime.UtcNow < deadline)
        {
            batch = await Media.WaitForLocalCandidatesAsync(room.Id, client.ParticipantId, 0, TimeSpan.FromSeconds(2), CancellationToken.None);
        }

        var hosts = batch.Candidates.Select(c => c.Candidate.Split(' ')).Where(p => p.Length > 7 && p[7] == "host").ToList();
        Assert.NotEmpty(hosts);
        // TCP "active" candidates carry the placeholder port 9; everything that listens is in the range.
        Assert.All(hosts.Where(p => p[5] != "9"), p => Assert.InRange(int.Parse(p[5], System.Globalization.CultureInfo.InvariantCulture), 41000, 41099));
        var local = hosts.Where(p => p[4] != "203.0.113.5" && p[4].Contains('.') && p[2] == "UDP").ToList();
        Assert.NotEmpty(local);
        Assert.All(local, p => Assert.Contains(hosts, q => q[4] == "203.0.113.5" && q[5] == p[5] && q[2] == p[2]));
    }
}
