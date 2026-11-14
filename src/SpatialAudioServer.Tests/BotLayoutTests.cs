using SpatialAudioServer.LoadTest;

namespace SpatialAudioServer.Tests;

/// <summary>Where room-mode bots stand in spatial rooms: exact compass points on rings 10 m apart.</summary>
public sealed class BotLayoutTests
{
    [Fact]
    public void Four_bots_stand_east_north_west_and_south_on_the_first_ring()
    {
        var places = Enumerable.Range(0, 4).Select(i => BotLayout.Place(i, 4, radius: 5)).ToList();

        Assert.Equal([0, 2, 4, 6], places.Select(p => p.Octant));
        Assert.All(places, p => Assert.Equal(5, p.Distance));
        Assert.Equal(new Rooms.Vec3(5, 0, 0), places[0].Position);
        Assert.Equal(new Rooms.Vec3(0, 5, 0), places[1].Position); // north: on the left of a listener facing east
    }

    [Fact]
    public void Bots_beyond_eight_go_on_rings_further_out()
    {
        var places = Enumerable.Range(0, 12).Select(i => BotLayout.Place(i, 12, radius: 5)).ToList();

        Assert.Equal(Enumerable.Range(0, 8), places.Take(8).Select(p => p.Octant)); // a full ring: every compass point
        Assert.All(places.Take(8), p => Assert.Equal(5, p.Distance));
        Assert.Equal([0, 2, 4, 6], places.Skip(8).Select(p => p.Octant)); // 4 on the second ring, spread evenly
        Assert.All(places.Skip(8), p => Assert.Equal(15, p.Distance));
        Assert.Equal(35, BotLayout.Place(31, 32, radius: 5).Distance);
    }

    [Fact]
    public void Each_bot_describes_its_compass_point_relative_to_a_listener_facing_east()
    {
        Assert.Equal("East, ahead of you", BotLayout.Spoken(0));
        Assert.Equal("North-west, behind, to your left", BotLayout.Spoken(3));
        Assert.Equal("South, on your right", BotLayout.Spoken(6));
    }
}
