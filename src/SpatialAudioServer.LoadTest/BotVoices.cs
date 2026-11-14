using System.Runtime.InteropServices;
using System.Text.Json;

namespace SpatialAudioServer.LoadTest;

/// <summary>
/// The speaking bots' pre-rendered voices (<c>scripts/loadtest-linux-x64/voices</c>, made with
/// <c>scripts/render-voices.py</c>): per voice, name clips ("Bot, 3."), direction clips ("North-west, behind, to your
/// left.") and distance clips ("15 metres."). A bot's line is its voice's clips joined, so no speech engine is needed
/// where the load tool runs, on Linux or Windows. Bot N speaks in voice (N − 1) mod the number of voices.
/// </summary>
public sealed class BotVoices
{
    private const string Manifest = "voices.json";
    private static readonly int Pause = AudioClip.Rate / 5; // 0.2 s between the parts of a line

    private readonly string _directory;
    private readonly Dictionary<string, short[]> _decoded = [];

    private BotVoices(string directory, ManifestFile manifest)
    {
        _directory = directory;
        Voices = manifest.Voices.Select(v => v.Id).ToList();
        Names = manifest.Names;
        Distances = manifest.Distances.ToHashSet();
    }

    public string Directory => _directory;

    public IReadOnlyList<string> Voices { get; }

    /// <summary>Name clips exist for bots 1 to this.</summary>
    public int Names { get; }

    public IReadOnlySet<int> Distances { get; }

    /// <summary>
    /// The voices in <paramref name="directory"/>, or else the first found of: <c>voices</c> next to the load tool, then
    /// <c>scripts/loadtest-linux-x64/voices</c> above the load tool or the current directory (running from the repo).
    /// </summary>
    public static BotVoices Load(string? directory = null)
    {
        var found = directory is not null
            ? Path.GetFullPath(directory)
            : Candidates().FirstOrDefault(d => File.Exists(Path.Combine(d, Manifest)))
              ?? throw new FileNotFoundException(
                  "No bot voices found: expected voices/voices.json next to the load tool or scripts/loadtest-linux-x64/voices " +
                  "in the repository (or give --voices DIR).");
        var manifest = JsonSerializer.Deserialize<ManifestFile>(File.ReadAllText(Path.Combine(found, Manifest)),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new InvalidDataException($"Empty {Manifest} in {found}.");
        if (manifest.Format != 1 || manifest.Voices.Count == 0)
        {
            throw new InvalidDataException($"{Path.Combine(found, Manifest)}: unsupported format {manifest.Format} or no voices.");
        }
        return new BotVoices(found, manifest);
    }

    /// <summary>
    /// Bot <paramref name="index"/>'s line (48 kHz mono samples): its name, and where it stands if <paramref name="place"/>
    /// is given ("Bot, 12. North-west, behind, to your left. 15 metres."). A distance without a clip is left out.
    /// </summary>
    public short[] Line(int index, BotPlace? place)
    {
        var number = index + 1;
        if (number > Names)
        {
            throw new ArgumentOutOfRangeException(nameof(index), $"The voices have names for {Names} bots only.");
        }
        var voice = Voices[index % Voices.Count];
        var parts = new List<short[]> { Clip(voice, $"name-{number:00}") };
        if (place is not null)
        {
            parts.Add(Clip(voice, $"dir-{BotLayout.Keys[place.Octant]}"));
            var metres = (int)Math.Round(place.Distance);
            if (Math.Abs(place.Distance - metres) < 0.01 && Distances.Contains(metres))
            {
                parts.Add(Clip(voice, $"dist-{metres:00}"));
            }
        }
        var line = new short[parts.Sum(p => p.Length) + Pause * (parts.Count - 1)];
        var at = 0;
        foreach (var part in parts)
        {
            part.CopyTo(line, at);
            at += part.Length + Pause;
        }
        return line;
    }

    private short[] Clip(string voice, string name)
    {
        var path = Path.Combine(_directory, voice, name + ".ogg");
        if (!_decoded.TryGetValue(path, out var samples))
        {
            samples = MemoryMarshal.Cast<byte, short>(AudioClip.Load(path).Pcm).ToArray();
            _decoded[path] = samples;
        }
        return samples;
    }

    private static IEnumerable<string> Candidates()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "voices");
        foreach (var start in new[] { AppContext.BaseDirectory, System.IO.Directory.GetCurrentDirectory() })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                yield return Path.Combine(dir.FullName, "scripts", "loadtest-linux-x64", "voices");
            }
        }
    }

    private sealed record ManifestFile(int Format, int Names, List<string> Directions, List<int> Distances, List<VoiceEntry> Voices);

    private sealed record VoiceEntry(string Id, string Model, int? Speaker, string Credit);
}
