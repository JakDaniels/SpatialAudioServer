namespace SpatialAudioServer.LoadTest;

/// <summary>
/// Measures which test tones are present in received audio. Each 20 ms frame (960 samples at 48 kHz, left channel)
/// is run through a Goertzel filter per frequency; a tone counts as present in a frame when its amplitude exceeds
/// <see cref="Threshold"/>. Frequencies should be multiples of 50 Hz (exact Goertzel bins at N=960).
/// </summary>
public sealed class ToneMeter(IReadOnlyList<double> frequencies)
{
    public const int SampleRate = 48000;
    public const int FrameSamples = 960;

    /// <summary>Tone amplitude (full scale = 1) that counts as present. Fake clients send at 0.1.</summary>
    public double Threshold { get; init; } = 0.02;

    private readonly Lock _lock = new();
    private readonly double[] _frame = new double[FrameSamples];
    private readonly long[] _present = new long[frequencies.Count];
    private int _filled;
    private long _frames;

    public IReadOnlyList<double> Frequencies { get; } = frequencies;

    /// <summary>Feeds interleaved S16LE samples.</summary>
    public void Add(ReadOnlySpan<short> interleaved, int channels)
    {
        lock (_lock)
        {
            for (var i = 0; i < interleaved.Length; i += channels)
            {
                _frame[_filled++] = interleaved[i] / 32768.0;
                if (_filled == FrameSamples)
                {
                    Analyse();
                    _filled = 0;
                }
            }
        }
    }

    /// <summary>Fraction of frames since the last <see cref="Reset"/> in which each frequency was present.</summary>
    public IReadOnlyDictionary<double, double> Presence()
    {
        lock (_lock)
        {
            return Frequencies.Select((f, i) => (f, i))
                .ToDictionary(x => x.f, x => _frames == 0 ? 0 : (double)_present[x.i] / _frames);
        }
    }

    public long Frames
    {
        get
        {
            lock (_lock)
            {
                return _frames;
            }
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            Array.Clear(_present);
            _frames = 0;
        }
    }

    private void Analyse()
    {
        _frames++;
        for (var f = 0; f < Frequencies.Count; f++)
        {
            if (Amplitude(_frame, Frequencies[f]) >= Threshold)
            {
                _present[f]++;
            }
        }
    }

    /// <summary>Goertzel amplitude of one frequency over the frame.</summary>
    public static double Amplitude(ReadOnlySpan<double> samples, double frequency)
    {
        var coefficient = 2 * Math.Cos(2 * Math.PI * frequency / SampleRate);
        double s1 = 0, s2 = 0;
        foreach (var x in samples)
        {
            var s0 = x + coefficient * s1 - s2;
            s2 = s1;
            s1 = s0;
        }
        var power = s1 * s1 + s2 * s2 - coefficient * s1 * s2;
        return 2 * Math.Sqrt(Math.Max(power, 0)) / samples.Length;
    }
}
