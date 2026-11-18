// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

namespace SpatialAudioServer.Media.Engines;

/// <summary>An available mix engine, as listed by <c>GET /api/status</c>.</summary>
public sealed record MixEngineInfo(string Name, string Description);

/// <summary>
/// The catalogue of mix engines. A room picks one by name when it is created (default: <c>Media:Engine</c>).
/// To add an engine: implement <see cref="IMixEngine"/>, register it here, and add a test class deriving from
/// the shared engine suites in the tests project (see <c>EngineSuites.cs</c>).
/// </summary>
public static class MixEngines
{
    public const string Default = SharedMatrixEngine.Name;

    private sealed record Entry(string Description, Func<MixEngineContext, IMixEngine> Create, string? NeedsElement = null);

    private static readonly Dictionary<string, Entry> Registry = new(StringComparer.Ordinal)
    {
        [PairChainsEngine.Name] = new(
            "Per (source, listener) chain: tee → queue → audioamplify → audiopanorama → listener audiomixer → opusenc.",
            ctx => new PairChainsEngine(ctx)),
        [MatrixEngine.Name] = new(
            "One audiointerleave per room (a fixed channel slot per participant) → tee → per listener queue → " +
            "audiomixmatrix (slots × stereo gain matrix; own slot zeroed) → opusenc. No per-pair elements.",
            ctx => new MatrixEngine(ctx)),
        [SharedMatrixEngine.Name] = new(
            "Like matrix, but only current talkers (voice activity, up to Media:ActiveSpeakers) are mixed: everyone " +
            "else hears one shared mix from one encoder, each talker a personal mix-minus. Encoders = talkers + 1. " +
            "The shared mix is not spatial (centred).",
            ctx => new SharedMatrixEngine(ctx)),
        [AcousticEngine.Name] = new(
            "The whole room in one acousticmultimixer element (its own GStreamer plugin, installed separately): a slot " +
            "per participant, voice in, stereo mix-minus out, gains and positions as pad properties → per listener " +
            "queue → opusenc.",
            ctx => new AcousticEngine(ctx),
            AcousticEngine.Element),
    };

    private static volatile Func<string, bool>? _elementPresent;

    /// <summary>
    /// Called once GStreamer is loaded: engines that need an element from an optional plugin are only offered when
    /// it is installed. Until then (or with media disabled) they are not.
    /// </summary>
    internal static void CheckElements(Func<string, bool> elementPresent) => _elementPresent = elementPresent;

    private static bool Available(Entry entry) => entry.NeedsElement is not { } element || _elementPresent?.Invoke(element) == true;

    /// <summary>The engines this server can run.</summary>
    public static IReadOnlyList<MixEngineInfo> All =>
        Registry.Where(e => Available(e.Value)).Select(e => new MixEngineInfo(e.Key, e.Value.Description))
            .OrderBy(e => e.Name, StringComparer.Ordinal).ToList();

    /// <summary>Every engine this build knows, available or not (tests use it).</summary>
    internal static IReadOnlyList<string> Known => [.. Registry.Keys.Order(StringComparer.Ordinal)];

    /// <summary>The engine exists and can run here.</summary>
    public static bool Exists(string name) => Registry.TryGetValue(name, out var entry) && Available(entry);

    /// <summary>Why <paramref name="name"/> cannot be used, for error messages.</summary>
    public static string Unavailable(string name) =>
        Registry.TryGetValue(name, out var entry) && entry.NeedsElement is { } element
            ? $"Mix engine '{name}' needs the GStreamer element {element}, which is not installed."
            : $"'{name}' is not a mix engine. Available: {string.Join(", ", All.Select(e => e.Name))}.";

    internal static IMixEngine Create(string name, MixEngineContext context) =>
        Registry.TryGetValue(name, out var entry) && Available(entry)
            ? entry.Create(context)
            : throw new ArgumentException(Unavailable(name));
}
