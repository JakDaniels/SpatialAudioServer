using System.Runtime.InteropServices;

namespace SpatialAudioServer.LoadTest;

/// <summary>
/// Text to speech through libflite, the library behind GStreamer's flite plugin (so it is installed wherever
/// <c>flitetestsrc</c> works: <c>libflite1</c>, pulled in by gstreamer1.0-plugins-bad). Calls the C API directly:
/// <c>flite_init</c>, <c>register_cmu_us_*</c> for a voice, <c>flite_text_to_wave</c>, <c>cst_wave_resample</c>.
/// Not thread-safe: synthesise from one thread.
/// </summary>
public static class FliteSpeech
{
    /// <summary>Voices that ship in libflite1, rotated so neighbouring bots sound different.</summary>
    public static readonly string[] Voices = ["kal", "slt", "awb", "rms"];

    private static readonly Lock Sync = new();
    private static readonly Dictionary<string, IntPtr> LoadedVoices = [];
    private static unsafe delegate* unmanaged<int> _init;
    private static unsafe delegate* unmanaged<byte*, IntPtr, Wave*> _textToWave;
    private static unsafe delegate* unmanaged<Wave*, int, void> _resample;
    private static unsafe delegate* unmanaged<Wave*, void> _deleteWave;

    /// <summary>Speaks <paramref name="text"/> as 48 kHz mono S16 samples, in the given voice (falls back to kal).</summary>
    public static unsafe short[] Say(string text, string voice = "kal")
    {
        lock (Sync)
        {
            var handle = Voice(voice);
            var utf8 = System.Text.Encoding.UTF8.GetBytes(text + "\0");
            Wave* wave;
            fixed (byte* p = utf8)
            {
                wave = _textToWave(p, handle);
            }
            if (wave is null)
            {
                throw new InvalidOperationException($"flite could not speak \"{text}\".");
            }
            try
            {
                _resample(wave, AudioClip.Rate);
                var samples = new short[wave->NumSamples];
                new ReadOnlySpan<short>((void*)wave->Samples, wave->NumSamples * wave->NumChannels)
                    .Slice(0, wave->NumSamples).CopyTo(samples); // flite voices are mono
                return samples;
            }
            finally
            {
                _deleteWave(wave);
            }
        }
    }

    private static unsafe IntPtr Voice(string name)
    {
        if (_init is null)
        {
            var flite = Load("libflite.so.1");
            _init = (delegate* unmanaged<int>)NativeLibrary.GetExport(flite, "flite_init");
            _textToWave = (delegate* unmanaged<byte*, IntPtr, Wave*>)NativeLibrary.GetExport(flite, "flite_text_to_wave");
            _resample = (delegate* unmanaged<Wave*, int, void>)NativeLibrary.GetExport(flite, "cst_wave_resample");
            _deleteWave = (delegate* unmanaged<Wave*, void>)NativeLibrary.GetExport(flite, "delete_wave");
            _init();
        }
        if (LoadedVoices.TryGetValue(name, out var voice))
        {
            return voice;
        }
        if (!NativeLibrary.TryLoad($"libflite_cmu_us_{name}.so.1", out var library)
            || !NativeLibrary.TryGetExport(library, $"register_cmu_us_{name}", out var register))
        {
            return name == "kal" ? throw new InvalidOperationException("flite's kal voice is missing.") : LoadedVoices[name] = Voice("kal");
        }
        voice = ((delegate* unmanaged<byte*, IntPtr>)register)(null);
        LoadedVoices[name] = voice;
        return voice;
    }

    private static IntPtr Load(string library) =>
        NativeLibrary.TryLoad(library, out var handle)
            ? handle
            : throw new InvalidOperationException(
                $"{library} not found: --audio flite needs libflite1 (installed with gstreamer1.0-plugins-bad).");

    /// <summary>flite's cst_wave.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Wave
    {
        public IntPtr Type;
        public int SampleRate;
        public int NumSamples;
        public int NumChannels;
        public IntPtr Samples;
    }
}
