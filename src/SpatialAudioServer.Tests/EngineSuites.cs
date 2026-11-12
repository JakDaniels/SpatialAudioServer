using SpatialAudioServer.Media.Engines;

namespace SpatialAudioServer.Tests;

/// <summary>Names a mix engine for the shared suites (<see cref="MixSuite{TEngine}"/>, <see cref="MixChangeSuite{TEngine}"/>).</summary>
public interface IEngineUnderTest
{
    static abstract string Name { get; }
}

// To test a new engine: add a marker type and one subclass per suite. EngineCoverageTests fails until you do.

public sealed class PairChains : IEngineUnderTest
{
    public static string Name => "pairchains";
}

public sealed class PairChainsLite : IEngineUnderTest
{
    public static string Name => "pairchains-lite";
}

public sealed class Matrix : IEngineUnderTest
{
    public static string Name => "matrix";
}

[Collection("media")]
public sealed class PairChainsMix : MixSuite<PairChains>;

[Collection("media")]
public sealed class PairChainsMixChanges : MixChangeSuite<PairChains>;

[Collection("media")]
public sealed class PairChainsLiteMix : MixSuite<PairChainsLite>;

[Collection("media")]
public sealed class PairChainsLiteMixChanges : MixChangeSuite<PairChainsLite>;

[Collection("media")]
public sealed class MatrixMix : MixSuite<Matrix>;

[Collection("media")]
public sealed class MatrixMixChanges : MixChangeSuite<Matrix>;

public sealed class EngineCoverageTests
{
    [Theory]
    [InlineData(typeof(MixSuite<>))]
    [InlineData(typeof(MixChangeSuite<>))]
    public void Every_registered_engine_runs_the_suite(Type suite)
    {
        var covered = typeof(EngineCoverageTests).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && t.BaseType is { IsGenericType: true } b && b.GetGenericTypeDefinition() == suite)
            .Select(t => (string)t.BaseType!.GetGenericArguments()[0].GetProperty(nameof(IEngineUnderTest.Name))!.GetValue(null)!)
            .ToHashSet();

        var missing = MixEngines.All.Select(e => e.Name).Where(name => !covered.Contains(name)).ToList();
        var unknown = covered.Where(name => !MixEngines.Exists(name)).ToList();
        Assert.True(missing.Count == 0, $"Engines without a {suite.Name} subclass: {string.Join(", ", missing)}");
        Assert.True(unknown.Count == 0, $"{suite.Name} subclasses for unregistered engines: {string.Join(", ", unknown)}");
    }
}
