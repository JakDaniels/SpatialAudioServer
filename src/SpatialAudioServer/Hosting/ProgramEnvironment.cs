// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

namespace SpatialAudioServer.Hosting;

/// <summary>
/// An optional <c>environment</c> file beside the program, read at start-up: <c>KEY=value</c> lines that become
/// environment variables unless the variable is already set. The Windows installer puts one there to say where
/// configuration and data live (<c>%ProgramData%\SpatialAudioServer</c>), so the service and an administrator's
/// <c>SpatialAudioServer.exe apikey …</c> agree, as the Linux packages do with /etc/spatialaudioserver/environment and
/// their wrapper. Same syntax: one per line, <c>#</c> comments, no quotes; values may use <c>%NAME%</c> variables.
/// </summary>
public static class ProgramEnvironment
{
    public const string FileName = "environment";

    /// <summary>Applies the file beside the program, if there is one. Returns the variables it set.</summary>
    public static IReadOnlyList<string> ApplyFromProgramFolder() =>
        Apply(Path.Combine(AppContext.BaseDirectory, FileName), Environment.GetEnvironmentVariable, Environment.SetEnvironmentVariable);

    public static IReadOnlyList<string> Apply(string file, Func<string, string?> get, Action<string, string?> set)
    {
        if (!File.Exists(file))
        {
            return [];
        }
        var applied = new List<string>();
        foreach (var raw in File.ReadAllLines(file))
        {
            var line = raw.Trim();
            var equals = line.IndexOf('=');
            if (line.Length == 0 || line.StartsWith('#') || equals <= 0)
            {
                continue;
            }
            var name = line[..equals].Trim();
            if (!string.IsNullOrEmpty(get(name)))
            {
                continue; // the real environment (or a service's own settings) wins
            }
            set(name, Environment.ExpandEnvironmentVariables(line[(equals + 1)..].Trim()));
            applied.Add(name);
        }
        return applied;
    }
}
