using SpatialAudioServer.Hosting;

namespace SpatialAudioServer.Tests;

/// <summary>The optional environment file beside the program (the Windows installer's ProgramData paths).</summary>
public sealed class ProgramEnvironmentTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Lines_become_variables_unless_already_set_and_variables_in_values_expand()
    {
        Environment.SetEnvironmentVariable("SAS_TEST_BASE", "/base");
        var file = _dir.File("environment");
        File.WriteAllLines(file,
        [
            "# where things are",
            "",
            "SAS_CONFIG_DIR=%SAS_TEST_BASE%/config",
            "Storage__DatabasePath = %SAS_TEST_BASE%/data/spatialaudio.db",
            "ALREADY=from the file",
            "not a setting",
        ]);
        var env = new Dictionary<string, string?> { ["ALREADY"] = "from the environment" };

        var applied = ProgramEnvironment.Apply(file, n => env.GetValueOrDefault(n), (n, v) => env[n] = v);

        Assert.Equal(["SAS_CONFIG_DIR", "Storage__DatabasePath"], applied);
        Assert.Equal("/base/config", env["SAS_CONFIG_DIR"]);
        Assert.Equal("/base/data/spatialaudio.db", env["Storage__DatabasePath"]);
        Assert.Equal("from the environment", env["ALREADY"]);
        Assert.Empty(ProgramEnvironment.Apply(_dir.File("missing"), _ => null, (_, _) => throw new InvalidOperationException()));
    }
}
