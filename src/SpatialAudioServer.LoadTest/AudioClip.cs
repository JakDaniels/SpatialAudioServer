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
    /// Synthesised speech with no file needed: GStreamer's flitetestsrc (plugins-bad, "flite" plugin) speaks each
    /// channel's number and position in turn ("zero, front left", "one, front right", …). Each channel becomes one
    /// clip, cut to exactly one cycle, so clients given clips 0, 1, 2… take turns like a conversation and each says
    /// its own number. At most 8 voices (the element's channel limit); at least 2, so a lone voice gets pauses.
    /// </summary>
    /// <param name="voices">How many distinct voices (clamped to 2–8).</param>
    /// <param name="together">Rotate each voice's clip to start with its own utterance, so they all speak at once
    /// (and keep doing so, with a pause while the others' turns would have been).</param>
    public static IReadOnlyList<AudioClip> Speech(int voices, bool together = false)
    {
        var channels = Math.Clamp(voices, 2, 8);
        if (ElementFactory.Find("flitetestsrc") is null)
        {
            throw new InvalidOperationException(
                "--audio flite needs GStreamer's flite plugin (flitetestsrc, in gstreamer1.0-plugins-bad).");
        }
        var raw = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"sas-flite-{Guid.NewGuid():N}.raw");
        try
        {
            // About 2 s per utterance, so 60 s covers a full cycle of 8 with room to spare. No audioconvert: it would
            // downmix; the channels are wanted separately.
            using var pipeline = (Pipeline)Global.ParseLaunch(
                $"flitetestsrc samplesperbuffer=4800 num-buffers=600 ! " +
                $"audio/x-raw,format=S16LE,rate=48000,channels={channels},layout=interleaved ! filesink location=\"{Escape(raw)}\"");
            pipeline.SetState(State.Playing);
            using var bus = pipeline.GetBus();
            using var message = bus.TimedPopFiltered(ClockTime.FromSeconds(120), MessageType.Eos | MessageType.Error);
            pipeline.SetState(State.Null);
            if (message is not { Type: MessageType.Eos })
            {
                var why = message is null ? "timed out" : message.ParseError().Item1.Message;
                throw new InvalidOperationException($"flitetestsrc: {why}");
            }
            var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(File.ReadAllBytes(raw)).ToArray();
            var frames = samples.Length / channels;

            // One cycle: from channel 0's first sound to its first sound after the last channel has spoken.
            int First(int channel, int from)
            {
                for (var f = from; f < frames; f++)
                {
                    if (samples[f * channels + channel] != 0)
                    {
                        return f;
                    }
                }
                throw new InvalidOperationException("flitetestsrc output has no full cycle.");
            }
            var start = First(0, 0);
            var end = First(0, First(channels - 1, start));

            var clips = new List<AudioClip>();
            for (var c = 0; c < channels; c++)
            {
                var pcm = new byte[(end - start) * 2];
                var mono = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(pcm.AsSpan());
                for (var f = start; f < end; f++)
                {
                    mono[f - start] = samples[f * channels + c];
                }
                if (together)
                {
                    var own = mono.IndexOfAnyExcept((short)0);
                    pcm = [.. pcm.AsSpan(own * 2), .. pcm.AsSpan(0, own * 2)];
                }
                clips.Add(new AudioClip($"flite voice {c}", pcm));
            }
            return clips;
        }
        finally
        {
            File.Delete(raw);
        }
    }

    private static string Escape(string path) => path.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
