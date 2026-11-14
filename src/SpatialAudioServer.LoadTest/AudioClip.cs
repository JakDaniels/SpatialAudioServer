using Gst;
using Gst.GLib;

namespace SpatialAudioServer.LoadTest;

/// <summary>
/// An audio file decoded once to 48 kHz mono S16LE in memory, for fake clients to loop instead of a tone.
/// Several clients share one clip, each starting at its own offset so they don't speak in unison.
/// </summary>
public sealed class AudioClip
{
    public const int Rate = 48000;
    public const string Caps = "audio/x-raw,format=S16LE,rate=48000,channels=1,layout=interleaved";

    private AudioClip(string path, byte[] pcm)
    {
        Path = path;
        Pcm = pcm;
    }

    public string Path { get; }

    /// <summary>Mono S16LE samples at 48 kHz.</summary>
    public byte[] Pcm { get; }

    public TimeSpan Length => TimeSpan.FromSeconds(Pcm.Length / 2.0 / Rate);

    /// <summary>Decodes any file GStreamer can play (WAV, OGG/Opus, FLAC, MP3, …) via a temporary raw file.</summary>
    public static AudioClip Load(string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        if (!File.Exists(full))
        {
            throw new FileNotFoundException($"Audio file not found: {full}", full);
        }
        var raw = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"sas-clip-{Guid.NewGuid():N}.raw");
        try
        {
            using var pipeline = (Pipeline)Global.ParseLaunch(
                $"filesrc location=\"{Escape(full)}\" ! decodebin ! audioconvert ! audioresample ! {Caps} ! " +
                $"filesink location=\"{Escape(raw)}\"");
            pipeline.SetState(State.Playing);
            using var bus = pipeline.GetBus();
            using var message = bus.TimedPopFiltered(ClockTime.FromSeconds(120), MessageType.Eos | MessageType.Error);
            pipeline.SetState(State.Null);
            if (message is null)
            {
                throw new InvalidOperationException($"Decoding {full} timed out.");
            }
            if (message.Type == MessageType.Error)
            {
                (GException error, string? debug) = message.ParseError();
                throw new InvalidOperationException($"Cannot decode {full}: {error.Message} ({debug})");
            }
            var pcm = File.ReadAllBytes(raw);
            if (pcm.Length < Rate * 2 / 10)
            {
                throw new InvalidOperationException($"{full} decoded to less than 100 ms of audio.");
            }
            return new AudioClip(full, pcm[..(pcm.Length & ~1)]);
        }
        finally
        {
            File.Delete(raw);
        }
    }

    /// <summary>
    /// Synthesised speech, no file needed: one clip per phrase, spoken by flite (<see cref="FliteSpeech"/>) in
    /// rotating voices. Every clip is one cycle long. Taking turns, each phrase sits in its own slot of the cycle (so
    /// clients given clips 0, 1, 2… speak one after another, like a conversation); <paramref name="together"/>, they
    /// all start at once. A pause follows each phrase.
    /// </summary>
    public static IReadOnlyList<AudioClip> Speech(IReadOnlyList<string> phrases, bool together = false)
    {
        if (phrases.Count == 0)
        {
            throw new ArgumentException("No phrases to speak.", nameof(phrases));
        }
        var spoken = phrases.Select((text, i) => FliteSpeech.Say(text, FliteSpeech.Voices[i % FliteSpeech.Voices.Length])).ToList();
        return Arrange(spoken, phrases.Select(p => $"\"{p}\"").ToList(), together);
    }

    /// <summary>
    /// One clip per spoken line (48 kHz mono samples), every clip one cycle long. Taking turns, each line sits in its
    /// own slot of the cycle, so clients given clips 0, 1, 2… speak one after another; <paramref name="together"/>, they
    /// all start at once. A pause follows each line.
    /// </summary>
    public static IReadOnlyList<AudioClip> Arrange(IReadOnlyList<short[]> spoken, IReadOnlyList<string> labels, bool together = false)
    {
        if (spoken.Count == 0)
        {
            throw new ArgumentException("Nothing to speak.", nameof(spoken));
        }
        var gap = Rate * 4 / 10; // 0.4 s after each line
        var cycle = together ? spoken.Max(s => s.Length) + gap : spoken.Sum(s => s.Length + gap);
        var clips = new List<AudioClip>();
        var offset = 0;
        for (var i = 0; i < spoken.Count; i++)
        {
            var pcm = new byte[cycle * 2];
            spoken[i].AsSpan().CopyTo(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(pcm.AsSpan()).Slice(offset));
            clips.Add(new AudioClip(labels[i], pcm));
            if (!together)
            {
                offset += spoken[i].Length + gap;
            }
        }
        return clips;
    }

    private static string Escape(string path) => path.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
