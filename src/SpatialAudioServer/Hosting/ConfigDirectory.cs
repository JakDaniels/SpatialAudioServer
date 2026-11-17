// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.FileProviders;

namespace SpatialAudioServer.Hosting;

/// <summary>
/// A configuration directory outside the program folder (PACKAGING-PLAN.md, code change 1): when
/// <c>SAS_CONFIG_DIR</c> is set, its <c>appsettings.json</c> (and <c>appsettings.{Environment}.json</c>) apply on top of
/// the program's own, and below environment variables and the command line. Packages set it to
/// <c>/etc/spatialaudioserver</c> (Windows: <c>C:\ProgramData\SpatialAudioServer</c>), so site settings survive
/// upgrades while the program's defaults can change with them.
/// </summary>
public static class ConfigDirectory
{
    public const string Variable = "SAS_CONFIG_DIR";

    /// <summary>Adds the directory named by <see cref="Variable"/>, if set. Returns it, or null.</summary>
    public static string? AddFromEnvironment(IConfigurationBuilder configuration, string environmentName)
    {
        var directory = Environment.GetEnvironmentVariable(Variable);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }
        Add(configuration, directory, environmentName);
        return directory;
    }

    /// <summary>
    /// Inserts the directory's JSON files straight after the last JSON source already there (the program's own
    /// appsettings files), so environment variables and the command line still override them.
    /// </summary>
    public static void Add(IConfigurationBuilder configuration, string directory, string environmentName)
    {
        var full = Path.GetFullPath(directory);
        if (!Directory.Exists(full))
        {
            throw new InvalidOperationException($"{Variable} names {full}, which is not a directory.");
        }
        var sources = configuration.Sources;
        var at = 0;
        for (var i = 0; i < sources.Count; i++)
        {
            if (sources[i] is JsonConfigurationSource)
            {
                at = i + 1;
            }
        }
        var files = new PhysicalFileProvider(full);
        sources.Insert(at, Json(files, "appsettings.json"));
        sources.Insert(at + 1, Json(files, $"appsettings.{environmentName}.json"));
    }

    private static JsonConfigurationSource Json(IFileProvider files, string path) => new()
    {
        FileProvider = files,
        Path = path,
        Optional = true,
        ReloadOnChange = false,
    };
}
