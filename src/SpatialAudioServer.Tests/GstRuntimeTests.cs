using System.Net.Http.Json;
using SpatialAudioServer.Api;

namespace SpatialAudioServer.Tests;

/// <summary>Needs GStreamer and the plugins from scripts/setup-ubuntu.sh installed on the test machine.</summary>
[Collection("media")]
public sealed class GstRuntimeTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task With_media_enabled_the_server_loads_GStreamer_and_finds_every_required_element()
    {
        using var server = new ServerFactory(_dir.File("server.db"), new Dictionary<string, string?> { ["Media:Enabled"] = "true" });

        var status = await server.CreateAuthedClient().GetFromJsonAsync<StatusResponse>("/api/status", Json.Options);

        Assert.True(status!.Gstreamer.Enabled);
        Assert.True(status.Gstreamer.Ok, $"Missing: {string.Join(", ", status.Gstreamer.MissingElements)} {status.Gstreamer.Error}");
        Assert.StartsWith("GStreamer 1.", status.Gstreamer.Version);
    }
}
