// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

namespace SpatialAudioServer.Media.Spatial;

/// <summary>
/// The acoustic presets a room may choose (<c>acoustics</c>): the kind of space, for its early reflections. Only the
/// <c>acoustic</c> engine renders them (they are the acousticmultimixer element's <c>room</c> values); other engines
/// ignore the setting.
/// </summary>
public static class RoomAcoustics
{
    /// <summary>Open air, no reflections: what a room without a setting sounds like.</summary>
    public const string Outdoor = "outdoor";

    public static IReadOnlyList<string> Names { get; } = [Outdoor, "tiny-room", "small-room", "big-room", "hall", "long-hall", "cave", "cathedral"];

    public static bool IsKnown(string name) => Names.Contains(name.Trim().ToLowerInvariant());

    /// <summary>The stored form: lower case; null (and "outdoor") mean outdoor.</summary>
    public static string? Normalize(string? name) =>
        name?.Trim().ToLowerInvariant() is { } n && n != Outdoor ? n : null;
}
