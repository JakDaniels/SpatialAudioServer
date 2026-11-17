using SpatialAudioServer.Media;

namespace SpatialAudioServer.Tests;

/// <summary>The environment that points a Windows installation at its embedded GStreamer (code change 4).</summary>
public sealed class EmbeddedGStreamerTests
{
    [Fact]
    public void Only_the_embedded_plugins_scanner_and_libraries_are_used()
    {
        var root = Path.Combine("C:", "sas", "gstreamer");
        var settings = EmbeddedGStreamer.Environment(root, "reg.bin", "existing").ToDictionary(s => s.Name, s => s.Value);

        foreach (var suffix in new[] { "_1_0", "" })
        {
            Assert.Equal(Path.Combine(root, "lib", "gstreamer-1.0"), settings[$"GST_PLUGIN_SYSTEM_PATH{suffix}"]);
            Assert.Null(settings[$"GST_PLUGIN_PATH{suffix}"]); // no plugins from another installation
            Assert.Equal(Path.Combine(root, "libexec", "gstreamer-1.0", "gst-plugin-scanner.exe"), settings[$"GST_PLUGIN_SCANNER{suffix}"]);
            Assert.Equal("reg.bin", settings[$"GST_REGISTRY{suffix}"]);
        }
        Assert.Equal(Path.Combine(root, "bin") + Path.PathSeparator + "existing", settings["PATH"]); // ours first
        Assert.Equal(Path.Combine(root, "bin"), EmbeddedGStreamer.Environment(root, "reg.bin", null).Single(s => s.Name == "PATH").Value);
    }

    [Fact]
    public void Nothing_changes_off_windows_or_without_the_folder()
    {
        // On Linux (where the tests run) it never applies; on Windows without gstreamer\bin it would not either.
        Assert.Null(EmbeddedGStreamer.UseIfPresent("tests"));
    }
}
