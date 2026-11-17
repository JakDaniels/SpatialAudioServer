using Microsoft.Data.Sqlite;
using SpatialAudioServer.Hosting;

namespace SpatialAudioServer.Tests;

/// <summary>--help, and the plain message for an installed server's folders opened without the rights.</summary>
public sealed class CommandLineTests
{
    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("/?")]
    public void Help_switches_are_recognised(string arg) => Assert.True(CommandLine.IsHelp([arg]));

    [Fact]
    public void Help_is_not_mistaken_for_a_command() => Assert.False(CommandLine.IsHelp(["apikey", "--help"]));

    [Fact]
    public void Access_denied_is_found_inside_other_exceptions()
    {
        var wrapped = new InvalidOperationException("start-up", new UnauthorizedAccessException("Access to the path 'x' is denied."));
        Assert.StartsWith("Access to the path 'x' is denied.", CommandLine.AccessDenied(wrapped));
        Assert.NotNull(CommandLine.AccessDenied(new SqliteException("unable to open database file", 14)));
    }

    [Fact]
    public void Other_failures_are_left_alone()
    {
        Assert.Null(CommandLine.AccessDenied(new InvalidOperationException("no")));
        Assert.Null(CommandLine.AccessDenied(new SqliteException("constraint", 19)));
    }
}
