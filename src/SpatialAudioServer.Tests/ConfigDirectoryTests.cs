using Microsoft.Extensions.Configuration;
using SpatialAudioServer.Hosting;

namespace SpatialAudioServer.Tests;

/// <summary>SAS_CONFIG_DIR: site settings over the program's own appsettings, under environment variables.</summary>
public sealed class ConfigDirectoryTests : IDisposable
{
    private readonly TempDir _program = new();
    private readonly TempDir _site = new();

    public void Dispose()
    {
        _program.Dispose();
        _site.Dispose();
    }

    [Fact]
    public void Site_settings_override_the_programs_and_environment_variables_override_both()
    {
        File.WriteAllText(Path.Combine(_program.Path, "appsettings.json"),
            """{ "Server": { "HttpPort": 5000, "HttpsPort": 5001 }, "Media": { "Engine": "matrix-shared" } }""");
        File.WriteAllText(Path.Combine(_site.Path, "appsettings.json"),
            """
            {
              // comments and trailing commas are allowed, as in the program's own file
              "Server": { "HttpPort": 8080, "HttpsPort": 8443, },
            }
            """);
        File.WriteAllText(Path.Combine(_site.Path, "appsettings.Production.json"), """{ "Media": { "Engine": "matrix" } }""");

        var builder = new ConfigurationBuilder()
            .SetBasePath(_program.Path)
            .AddJsonFile("appsettings.json")
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Server:HttpsPort"] = "9443" }); // as environment variables
        ConfigDirectory.Add(builder, _site.Path, "Production");
        var configuration = builder.Build();

        Assert.Equal("8080", configuration["Server:HttpPort"]); // site over program
        Assert.Equal("9443", configuration["Server:HttpsPort"]); // environment over site
        Assert.Equal("matrix", configuration["Media:Engine"]); // the site's environment-specific file
    }

    [Fact]
    public void A_missing_directory_is_an_error_and_missing_files_are_not()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ConfigDirectory.Add(new ConfigurationBuilder(), Path.Combine(_site.Path, "nope"), "Production"));

        var builder = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["A"] = "1" });
        ConfigDirectory.Add(builder, _site.Path, "Production"); // empty directory
        Assert.Equal("1", builder.Build()["A"]);
    }
}
