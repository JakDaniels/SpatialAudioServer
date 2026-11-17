// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using SpatialAudioServer.Rooms;

namespace SpatialAudioServer.Media.Spatial;

/// <summary>How volume falls with distance beyond the reference distance (the OpenAL / Web Audio models).</summary>
public enum DistanceModel
{
    /// <summary>reference / (reference + rolloff × (d − reference)); rolloff 1 is reference/d, −6 dB per doubling.</summary>
    Inverse,

    /// <summary>1 − rolloff × (d − reference) / (max range − reference), down to silence (Vivox's SL default).</summary>
    Linear,

    /// <summary>(d / reference)^−rolloff.</summary>
    Exponential,
}

/// <summary>A distance curve: the part of <see cref="SpatialOptions"/> a room can choose by name (its spatial profile).</summary>
public sealed class SpatialProfile
{
    public DistanceModel DistanceModel { get; set; } = DistanceModel.Exponential;
    public double ReferenceDistance { get; set; } = 2;
    public double Rolloff { get; set; } = 1;
    public double MaxRange { get; set; } = 60;
}

/// <summary>Spatial settings (<c>Media:Spatial</c>). Distances are in room units: metres for SL/OpenSim regions.</summary>
public sealed class SpatialOptions
{
    /// <summary>"Outdoor": physical fall-off, −6 dB per doubling from 2 m (5 m −8 dB, 20 m −20 dB, 50 m −28 dB). The default.</summary>
    public const string Outdoor = "outdoor";

    /// <summary>"Meeting": SL's Vivox settings, full volume to 10 m, then linear to silence at 35 m (rolloff 2, 60 m range).</summary>
    public const string Meeting = "meeting";

    /// <summary>Sources at or within this distance are heard at full volume.</summary>
    public double ReferenceDistance { get; set; } = 2;

    /// <summary>The fall-off beyond <see cref="ReferenceDistance"/>.</summary>
    public DistanceModel DistanceModel { get; set; } = DistanceModel.Exponential;

    /// <summary>
    /// How fast the volume falls (see <see cref="DistanceModel"/>). The default (exponential, 2 m, rolloff 1) is the
    /// physical free-field fall-off; OpenSim's Vivox module (SL's settings) uses linear, 10 m, rolloff 2 (the
    /// <see cref="Meeting"/> profile).
    /// </summary>
    public double Rolloff { get; set; } = 1;

    /// <summary>
    /// Named distance curves a room may choose (<c>spatialProfile</c>); a room without one uses the settings above.
    /// Built in: <see cref="Outdoor"/> and <see cref="Meeting"/>; configuration can change them or add more
    /// (<c>Media:Spatial:Profiles:{name}:DistanceModel</c> …).
    /// </summary>
    public Dictionary<string, SpatialProfile> Profiles { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        [Outdoor] = new SpatialProfile(),
        [Meeting] = new SpatialProfile { DistanceModel = DistanceModel.Linear, ReferenceDistance = 10, Rolloff = 2, MaxRange = 60 },
    };

    /// <summary>These settings with the named profile's distance curve (null: unchanged).</summary>
    public SpatialOptions WithProfile(string? name)
    {
        if (name is null || !Profiles.TryGetValue(name, out var profile))
        {
            return this;
        }
        var copy = (SpatialOptions)MemberwiseClone();
        copy.DistanceModel = profile.DistanceModel;
        copy.ReferenceDistance = profile.ReferenceDistance;
        copy.Rolloff = profile.Rolloff;
        copy.MaxRange = profile.MaxRange;
        return copy;
    }

    /// <summary>Beyond this distance a source is silent and left out of the mix.</summary>
    public double MaxRange { get; set; } = 60;

    /// <summary>
    /// Tethered rooms: a viewer's own avatar position is used while it is within this many metres of the simulator's
    /// (which lags: it is pushed a few times a second); further away, the simulator's position is used.
    /// </summary>
    public double SpeakerTether { get; set; } = 10;

    /// <summary>Tethered rooms: how far from the simulator's avatar position a viewer's camera may listen (SL: 50 m).</summary>
    public double ListenerTether { get; set; } = 50;

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

/// <summary>Where a participant is heard from and hears from, after the tether rules (<see cref="ClientPose.Tethered"/>).</summary>
public readonly record struct EffectivePose(Vec3? Speaker, Vec3? Listener, Quat? ListenerOrientation)
{
    public static EffectivePose Of(ParticipantSnapshot p, SpatialOptions options)
    {
        if (p.ClientReport is not { } report || p.Position is not { } anchor)
        {
            // No viewer report, or nothing to check it against: the control API's pose only.
            return new(p.Position, p.ListenerPosition ?? p.Position, p.ListenerOrientation ?? p.Orientation);
        }
        var speaker = Distance(report.SpeakerPosition, anchor) <= options.SpeakerTether ? report.SpeakerPosition : anchor;
        return new(speaker, Clamp(report.ListenerPosition, anchor, options.ListenerTether), report.ListenerOrientation);
    }

    private static double Distance(Vec3 a, Vec3 b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    /// <summary><paramref name="point"/>, or the nearest point to it within <paramref name="radius"/> of <paramref name="centre"/>.</summary>
    private static Vec3 Clamp(Vec3 point, Vec3 centre, double radius)
    {
        var distance = Distance(point, centre);
        if (distance <= radius)
        {
            return point;
        }
        var k = radius / distance;
        return new Vec3(centre.X + (point.X - centre.X) * k, centre.Y + (point.Y - centre.Y) * k, centre.Z + (point.Z - centre.Z) * k);
    }
}

/// <summary>Computes each (source, listener) rendering from their positions.</summary>
public interface ISpatialPolicy
{
    Rendering Render(Vec3? source, Vec3? listener, Quat? listenerOrientation);
}

/// <summary>
/// The default policy. Distance: full volume up to <see cref="SpatialOptions.ReferenceDistance"/>, then the
/// <see cref="SpatialOptions.DistanceModel"/> (by default reference/distance, −6 dB per doubling), silent beyond
/// <see cref="SpatialOptions.MaxRange"/>. Direction: pan is
/// the sine of the source's horizontal angle from the listener's facing, so straight ahead or behind is centred and
/// directly left or right is hard left or right (stereo cannot tell front from back). Elevation only affects distance.
/// Either side without a position hears or is heard centred at full volume.
/// </summary>
/// <remarks>
/// Coordinates follow SL/OpenSim: x east, y north, z up; an avatar with identity rotation faces +x, so its left is +y.
/// </remarks>
public sealed class DistancePolicy(SpatialOptions options) : ISpatialPolicy
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
        var gain = Attenuation(distance);
        if (gain <= 0)
        {
            return Rendering.Silent;
        }

        // Into the listener's frame: local x = forward, local y = left.
        var (forward, left, _) = listenerOrientation is { } q ? InverseRotate(q, dx, dy, dz) : (dx, dy, dz);
        var horizontal = Math.Sqrt(forward * forward + left * left);
        var pan = horizontal < 1e-6 ? 0 : -left / horizontal;
        return new Rendering(gain, pan);
    }

    /// <summary>The distance model's gain, 0 … 1.</summary>
    public double Attenuation(double distance)
    {
        double reference = options.ReferenceDistance, rolloff = options.Rolloff;
        if (distance <= reference)
        {
            return 1;
        }
        return options.DistanceModel switch
        {
            DistanceModel.Linear => options.MaxRange <= reference ? 0
                : Math.Clamp(1 - rolloff * (distance - reference) / (options.MaxRange - reference), 0, 1),
            DistanceModel.Exponential => Math.Pow(distance / reference, -rolloff),
            _ => reference / (reference + rolloff * (distance - reference)),
        };
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
