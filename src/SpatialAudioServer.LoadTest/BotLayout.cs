using SpatialAudioServer.Rooms;

namespace SpatialAudioServer.LoadTest;

/// <summary>Where room-mode bot <see cref="Index"/> stands: compass point <see cref="Octant"/> on ring <see cref="Ring"/>.</summary>
/// <param name="Octant">0 = east, then anticlockwise: 1 north-east, 2 north, … 7 south-east.</param>
public sealed record BotPlace(int Index, int Ring, int Octant, double Distance)
{
    public double Angle => Octant * Math.PI / 4;

    public Vec3 Position => new(Math.Round(Distance * Math.Cos(Angle), 3), Math.Round(Distance * Math.Sin(Angle), 3), 0);
}

/// <summary>
/// Spatial room mode: bots stand on the 8 compass points of rings round the centre (where the browser client puts
/// you, facing east). Bots 1–8 are on the first ring (<c>--radius</c>, default 5 m), 9–16 on the next, 10 m further out,
/// and so on, so each bot's direction is exact and the outer rings test distance. A ring with fewer than 8 bots
/// spreads them evenly (4 bots: east, north, west, south).
/// </summary>
public static class BotLayout
{
    public const int PerRing = 8;
    public const double RingSpacing = 10;

    /// <summary>The compass point names, by octant (as the voice clips are named: dir-&lt;key&gt;.ogg).</summary>
    public static readonly string[] Keys = ["east", "north-east", "north", "north-west", "west", "south-west", "south", "south-east"];

    private static readonly string[] Compass = ["East", "North-east", "North", "North-west", "West", "South-west", "South", "South-east"];

    private static readonly string[] Relative =
        ["in front of you", "in front, to your left", "on your left", "behind, to your left", "behind you",
         "behind, to your right", "on your right", "in front, to your right"];

    private static readonly string[] Short =
        ["in front (east)", "front-left (north-east)", "left (north)", "behind-left (north-west)", "behind (west)",
         "behind-right (south-west)", "right (south)", "front-right (south-east)"];

    public static BotPlace Place(int index, int count, double radius)
    {
        var ring = index / PerRing;
        var inRing = Math.Min(PerRing, count - ring * PerRing);
        var octant = (int)Math.Round(index % PerRing * (double)PerRing / inRing) % PerRing;
        return new BotPlace(index, ring, octant, radius + ring * RingSpacing);
    }

    /// <summary>What a bot says about where it is (as rendered in the voice clips): "North-west, behind, to your left".</summary>
    public static string Spoken(int octant) => $"{Compass[octant]}, {Relative[octant]}";

    /// <summary>For the console: "behind-left (north-west)".</summary>
    public static string Describe(int octant) => Short[octant];
}
