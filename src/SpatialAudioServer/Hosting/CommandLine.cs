// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Data.Sqlite;

namespace SpatialAudioServer.Hosting;

/// <summary>The program's own command line: <c>--help</c>, and a plain message instead of a stack trace when the
/// configuration or data folders cannot be opened (an ordinary user running an installed server's commands).</summary>
internal static class CommandLine
{
    public static bool IsHelp(string[] args) => args is ["--help"] or ["-h"] or ["-?"] or ["/?"] or ["help"];

    public static string Usage(string program) => $"""
        Usage:
          {program}                           run the server (installed: it runs as a service instead)
          {program} apikey create <label> [--expires 90d|2027-01-31]
          {program} apikey list | revoke <id> control-API keys, stored in the server's database
          {program} admin set-password        the admin UI password (also clear-password, hash-password)
          {program} --delete-existing-rooms   run the server, deleting every stored room first (testing)
          {program} --version | --help

        Settings come from appsettings.json beside the program, then the site configuration folder (SAS_CONFIG_DIR),
        then environment variables (Section__Key=value) and switches (--Section:Key=value). See docs/configuration.md.
        """;

    /// <summary>A message for an exception (or one inside it) that means a file or folder could not be opened for lack
    /// of rights; null for anything else.</summary>
    public static string? AccessDenied(Exception exception)
    {
        for (var ex = exception; ex is not null; ex = ex.InnerException)
        {
            var what = ex switch
            {
                UnauthorizedAccessException denied => denied.Message,
                // 14 SQLITE_CANTOPEN, 8 SQLITE_READONLY: the database's folder or file is not this user's to write.
                SqliteException { SqliteErrorCode: 14 or 8 } sqlite => $"The database cannot be opened ({sqlite.Message}).",
                _ => null,
            };
            if (what is not null)
            {
                return what + Environment.NewLine + (OperatingSystem.IsWindows()
                    ? "The installed server's configuration and data are open to administrators only: run this from a "
                      + "command prompt opened with \"Run as administrator\"."
                    : "Run it as the user the server runs as (installed: sudo spatialaudioserver ...).");
            }
        }
        return null;
    }
}
