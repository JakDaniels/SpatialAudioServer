using System.Globalization;
using Gst;
using SpatialAudioServer.LoadTest;
using Task = System.Threading.Tasks.Task;

namespace SpatialAudioServer.Tests;

/// <summary>
/// The load tool's room mode loops audio files through fake clients. The clip here is a sine at a test tone's
/// frequency, so the tone meter can check it is decoded, resampled, looped and paced in real time.
/// </summary>
[Collection("media")]
public sealed class AudioClipTests() : MediaTestBase(SpatialAudioServer.Media.Engines.MixEngines.Default), IDisposable
{
    private readonly TempDir _files = new();

    [Fact]
    public void A_file_is_decoded_to_48_kHz_mono()
    {
        var clip = AudioClip.Load(MakeSineWav(Tones[1], seconds: 1));

        Assert.Equal(48000 * 2, clip.Pcm.Length, tolerance: 2 * 480); // 1 s of S16 mono, give or take resampler edges
    }

    [Fact]
    public void Flite_voices_take_turns_over_one_cycle()
    {
        var voices = AudioClip.Speech(3);

        Assert.Equal(3, voices.Count);
        Assert.All(voices, v => Assert.Equal(voices[0].Pcm.Length, v.Pcm.Length));
        Assert.InRange(voices[0].Length.TotalSeconds, 1.5, 15); // three short utterances
        var samples = voices.Select(v => System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(v.Pcm).ToArray()).ToList();
        Assert.All(samples, s => Assert.True(s.Count(x => x != 0) > s.Length / 10, "every voice speaks"));
        for (var i = 0; i < samples[0].Length; i++)
        {
            Assert.True(samples.Count(s => s[i] != 0) <= 1, $"voices overlap at sample {i}");
        }
        Assert.Equal(2, AudioClip.Speech(1).Count); // a lone voice still gets pauses
    }

    [Fact]
    public void Flite_voices_together_all_start_speaking_at_once()
    {
        var voices = AudioClip.Speech(3, together: true);

        Assert.All(voices, v =>
        {
            var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(v.Pcm);
            Assert.NotEqual(0, samples[0]);
        });
    }

    [Fact]
    public void A_missing_file_is_reported()
    {
        Assert.Throws<FileNotFoundException>(() => AudioClip.Load(_files.File("missing.wav")));
    }

    [Fact]
    public async Task A_looped_clip_is_heard_by_the_others_and_not_by_itself()
    {
        var room = await CreateRoomAsync();
        // 1 s clip, measured over 3 s: the loop point must not leave gaps.
        var clip = AudioClip.Load(MakeSineWav(Tones[1], seconds: 1));
        var clients = new List<FakeClient>
        {
            await JoinAsync(room.Id, 0),
            await JoinAsync(room.Id, 1, clip: clip),
            await JoinAsync(room.Id, 2, talking: false),
        };

        var measured = await SettleAndMeasureAsync(clients, Others(clients.Where(c => c.Talking)), TimeSpan.FromSeconds(3));

        AssertHears(measured, Others(clients.Where(c => c.Talking)));
    }

    /// <summary>A full-scale sine at 44.1 kHz stereo, so loading has to resample and downmix.</summary>
    private string MakeSineWav(double hz, int seconds)
    {
        var path = _files.File($"sine-{hz}.wav");
        using var pipeline = (Pipeline)Global.ParseLaunch(
            $"audiotestsrc wave=sine volume=1.0 freq={hz.ToString(CultureInfo.InvariantCulture)} samplesperbuffer=441 " +
            $"num-buffers={seconds * 100} ! audio/x-raw,rate=44100,channels=2 ! wavenc ! filesink location=\"{path}\"");
        pipeline.SetState(State.Playing);
        using var bus = pipeline.GetBus();
        using var done = bus.TimedPopFiltered(ClockTime.FromSeconds(10), MessageType.Eos | MessageType.Error);
        pipeline.SetState(State.Null);
        Assert.True(done is { Type: MessageType.Eos }, "Could not write the test WAV.");
        return path;
    }

    public new void Dispose()
    {
        base.Dispose();
        _files.Dispose();
    }
}
