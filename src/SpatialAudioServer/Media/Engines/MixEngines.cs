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
    public const string Default = PairChainsEngine.Name;

    private static readonly Dictionary<string, (string Description, Func<MixEngineContext, IMixEngine> Create)> Registry =
        new(StringComparer.Ordinal)
        {
            [PairChainsEngine.Name] = (
                "Per (source, listener) chain: tee → queue → audioamplify → audiopanorama → listener audiomixer → opusenc.",
                ctx => new PairChainsEngine(ctx, PairChainsEngine.Variant.Queued)),
            [PairChainsEngine.LiteName] = (
                "Like pairchains without the per-chain queue and audioamplify: tee → audiopanorama → audiomixer pad volume. " +
                "Threads grow with participants instead of pairs.",
                ctx => new PairChainsEngine(ctx, PairChainsEngine.Variant.Lite)),
            [MatrixEngine.Name] = (
                "One audiointerleave per room (a fixed channel slot per participant) → tee → per listener queue → " +
                "audiomixmatrix (slots × stereo gain matrix; own slot zeroed) → opusenc. No per-pair elements.",
                ctx => new MatrixEngine(ctx)),
        };

    public static IReadOnlyList<MixEngineInfo> All { get; } =
        Registry.Select(e => new MixEngineInfo(e.Key, e.Value.Description)).OrderBy(e => e.Name, StringComparer.Ordinal).ToList();

    public static bool Exists(string name) => Registry.ContainsKey(name);

    internal static IMixEngine Create(string name, MixEngineContext context) =>
        Registry.TryGetValue(name, out var engine)
            ? engine.Create(context)
            : throw new ArgumentException($"Unknown mix engine '{name}'. Available: {string.Join(", ", Registry.Keys)}.");
}
