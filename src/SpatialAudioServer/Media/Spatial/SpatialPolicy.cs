using SpatialAudioServer.Rooms;

namespace SpatialAudioServer.Media.Spatial;

/// <summary>Spatial settings (<c>Media:Spatial</c>). Distances are in room units: metres for SL/OpenSim regions.</summary>
public sealed class SpatialOptions
{
    /// <summary>Sources at or within this distance are heard at full volume.</summary>
    public double ReferenceDistance { get; set; } = 10;

    /// <summary>Beyond this distance a source is silent and left out of the mix.</summary>
    public double MaxRange { get; set; } = 60;

    /// <summary>matrix-shared clusters: listeners whose talker levels match to this many dB share a mix.</summary>
    public double ClusterGainStepDb { get; set; } = 3;

    /// <summary>matrix-shared clusters: pan positions per side (pan −1…1 is quantised to steps of 1/PanSteps).</summary>
    public int ClusterPanSteps { get; set; } = 8;
}

/// <summary>How one source sounds to one listener: a level (0 = silent, out of the mix) and a pan (−1 left … 1 right).</summary>
public readonly record struct Rendering(double Gain, double Pan)
{
    public static readonly Rendering Silent = new(0, 0);

    /// <summary>Full volume, centred: a non-spatial room, or a participant without a position.</summary>
    public static readonly Rendering Centre = new(1, 0);

    public bool IsSilent => Gain <= 0;

    public Rendering Scale(double gain) => this with { Gain = Gain * gain };

    /// <summary>
    /// Left/right channel gains. Constant power with the centre at unity on both channels (so a centred source is
    /// exactly as loud as in a non-spatial room), capped at unity so panning never boosts: centre 1/1, hard right 0/1.
    /// </summary>
    public (double Left, double Right) Stereo()
    {
        var angle = (Math.Clamp(Pan, -1, 1) + 1) * Math.PI / 4; // 0 = hard left, π/4 = centre, π/2 = hard right
        return (Gain * Math.Min(1, Math.Sqrt(2) * Math.Cos(angle)), Gain * Math.Min(1, Math.Sqrt(2) * Math.Sin(angle)));
    }
}

/// <summary>Computes each (source, listener) rendering from their positions.</summary>
public interface ISpatialPolicy
{
    Rendering Render(Vec3? source, Vec3? listener, Quat? listenerOrientation);
}

/// <summary>
/// The default policy. Distance: full volume up to <see cref="SpatialOptions.ReferenceDistance"/>, then
/// reference/distance (−6 dB per doubling), silent beyond <see cref="SpatialOptions.MaxRange"/>. Direction: pan is
/// the sine of the source's horizontal angle from the listener's facing, so straight ahead or behind is centred and
/// directly left or right is hard left or right (stereo cannot tell front from back). Elevation only affects distance.
/// Either side without a position hears or is heard centred at full volume.
/// </summary>
/// <remarks>
/// Coordinates follow SL/OpenSim: x east, y north, z up; an avatar with identity rotation faces +x, so its left is +y.
/// </remarks>
public sealed class InverseDistancePolicy(SpatialOptions options) : ISpatialPolicy
{
    public Rendering Render(Vec3? source, Vec3? listener, Quat? listenerOrientation)
    {
        if (source is null || listener is null)
        {
            return Rendering.Centre;
        }
        double dx = source.X - listener.X, dy = source.Y - listener.Y, dz = source.Z - listener.Z;
        var distance = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        if (distance > options.MaxRange)
        {
            return Rendering.Silent;
        }
        var gain = distance <= options.ReferenceDistance ? 1 : options.ReferenceDistance / distance;

        // Into the listener's frame: local x = forward, local y = left.
        var (forward, left, _) = listenerOrientation is { } q ? InverseRotate(q, dx, dy, dz) : (dx, dy, dz);
        var horizontal = Math.Sqrt(forward * forward + left * left);
        var pan = horizontal < 1e-6 ? 0 : -left / horizontal;
        return new Rendering(gain, pan);
    }

    /// <summary>Rotates a world vector by the inverse of <paramref name="q"/> (normalised first).</summary>
    private static (double X, double Y, double Z) InverseRotate(Quat q, double x, double y, double z)
    {
        var norm = Math.Sqrt(q.X * q.X + q.Y * q.Y + q.Z * q.Z + q.W * q.W);
        if (norm < 1e-9)
        {
            return (x, y, z);
        }
        // Conjugate of the unit quaternion: (-x, -y, -z, w).
        double qx = -q.X / norm, qy = -q.Y / norm, qz = -q.Z / norm, qw = q.W / norm;
        // v' = v + 2w(u × v) + 2u × (u × v), with u = (qx, qy, qz).
        double cx = qy * z - qz * y, cy = qz * x - qx * z, cz = qx * y - qy * x;
        double ccx = qy * cz - qz * cy, ccy = qz * cx - qx * cz, ccz = qx * cy - qy * cx;
        return (x + 2 * (qw * cx + ccx), y + 2 * (qw * cy + ccy), z + 2 * (qw * cz + ccz));
    }
}

/// <summary>
/// Rounds a rendering to the cluster grid (gain in dB steps, pan in 1/steps), so listeners who hear the talkers
/// nearly the same way can share one mix and encoder.
/// </summary>
public sealed class RenderingQuantizer(SpatialOptions options)
{
    /// <summary>Below this a source is treated as silent (−40 dB).</summary>
    public const double Floor = 0.01;

    public (int GainStep, int PanStep)? Quantize(Rendering r)
    {
        if (r.Gain < Floor)
        {
            return null;
        }
        var db = 20 * Math.Log10(r.Gain);
        return ((int)Math.Round(db / options.ClusterGainStepDb), (int)Math.Round(Math.Clamp(r.Pan, -1, 1) * options.ClusterPanSteps));
    }

    public Rendering Dequantize((int GainStep, int PanStep) q) =>
        new(Math.Pow(10, q.GainStep * options.ClusterGainStepDb / 20), (double)q.PanStep / options.ClusterPanSteps);
}
