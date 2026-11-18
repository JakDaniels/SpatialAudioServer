// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using SpatialAudioServer.Media.Engines;
using SpatialAudioServer.Rooms;

namespace SpatialAudioServer.Tests;

/// <summary>
/// The acoustic engine's conversion of orientations into the element's yaw, pitch and roll (its mixing is covered by
/// the shared suites in EngineSuites.cs, where the plugin is installed).
/// </summary>
public sealed class AcousticEngineTests
{
    [Theory]
    [InlineData(0, 0, 0, 1, 0, 0, 0)]                     // identity: facing east (+x)
    [InlineData(0, 0, 1, 0, 180, 0, 0)]                   // facing west
    [InlineData(0, 0, 0.70710678, 0.70710678, 90, 0, 0)]  // turned left, facing north
    [InlineData(0, -0.25881905, 0, 0.96592583, 0, 30, 0)] // looking 30° up (a negative turn about +y)
    [InlineData(0.25881905, 0, 0, 0.96592583, 0, 0, 30)]  // right ear 30° down
    public void Quaternions_become_yaw_pitch_and_roll(double x, double y, double z, double w, double yaw, double pitch, double roll)
    {
        var (gotYaw, gotPitch, gotRoll) = AcousticEngine.Euler(new Quat(x, y, z, w));
        Assert.Equal(yaw, gotYaw, 1e-6);
        Assert.Equal(pitch, gotPitch, 1e-6);
        Assert.Equal(roll, gotRoll, 1e-6);
    }

    [Fact]
    public void No_orientation_faces_east() => Assert.Equal((0.0, 0.0, 0.0), AcousticEngine.Euler(null));
}
