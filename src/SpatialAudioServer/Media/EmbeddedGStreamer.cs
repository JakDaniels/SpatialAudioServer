using Gst.Interop;

namespace SpatialAudioServer.Media;

/// <summary>
/// The GStreamer embedded in a Windows installation (PACKAGING-PLAN.md, code change 4): when the program's folder has
/// <c>gstreamer\bin</c>, the process uses that GStreamer and only its plugins, whatever else is installed on the
/// machine; without the folder (development machines, Linux) nothing changes. Server and load tool alike, before
/// GStreamer is first loaded.
/// </summary>
/// <remarks>
/// The folder mirrors the official runtime's layout, so GStreamer's own relocation finds the rest:
/// <c>bin\</c> (GStreamer, GLib and the other libraries), <c>lib\gstreamer-1.0\</c> (the plugins) and
/// <c>libexec\gstreamer-1.0\gst-plugin-scanner.exe</c>.
/// </remarks>
public static class EmbeddedGStreamer
{
    public const string Folder = "gstreamer";

    /// <summary>
    /// The process environment that makes GStreamer use the tree under <paramref name="root"/> alone, with its plugin
    /// registry (a cache) at <paramref name="registry"/>. A null value removes the variable. Both spellings are set:
    /// GStreamer 1.x reads <c>NAME_1_0</c> first, then <c>NAME</c>.
    /// </summary>
    public static IReadOnlyList<(string Name, string? Value)> Environment(string root, string registry, string? currentPath)
    {
        var bin = Path.Combine(root, "bin");
        var plugins = Path.Combine(root, "lib", "gstreamer-1.0");
        var scanner = Path.Combine(root, "libexec", "gstreamer-1.0", "gst-plugin-scanner.exe");
        var settings = new List<(string, string?)>();
        foreach (var suffix in new[] { "_1_0", "" })
        {
            settings.Add(($"GST_PLUGIN_SYSTEM_PATH{suffix}", plugins)); // only our plugins…
            settings.Add(($"GST_PLUGIN_PATH{suffix}", null)); // …and none from elsewhere
            settings.Add(($"GST_PLUGIN_SCANNER{suffix}", scanner));
            settings.Add(($"GST_REGISTRY{suffix}", registry)); // not shared with another GStreamer's registry
        }
        // Plugins load their libraries (GLib, libnice, OpenSSL, …) from bin through the normal DLL search.
        settings.Add(("PATH", string.IsNullOrEmpty(currentPath) ? bin : bin + Path.PathSeparator + currentPath));
        return settings;
    }

    /// <summary>
    /// On Windows, if the program's folder holds an embedded GStreamer, points this process at it. Returns its folder,
    /// or null when there is none (then GStreamer is found as usual).
    /// </summary>
    public static string? UseIfPresent(string programName)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }
        var root = Path.Combine(AppContext.BaseDirectory, Folder);
        if (!Directory.Exists(Path.Combine(root, "bin")))
        {
            return null;
        }
        // The registry is a cache GStreamer rebuilds when plugins change: per account (the service's own profile for
        // the service), per program.
        var cache = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData,
            System.Environment.SpecialFolderOption.Create), "SpatialAudioServer");
        Directory.CreateDirectory(cache);
        var registry = Path.Combine(cache, $"gstreamer-registry-{programName}.bin");
        foreach (var (name, value) in Environment(root, registry, System.Environment.GetEnvironmentVariable("PATH")))
        {
            System.Environment.SetEnvironmentVariable(name, value);
        }
        NativeLoader.Configure(Path.Combine(root, "bin"), GstFlavor.Msvc);
        return root;
    }
}
