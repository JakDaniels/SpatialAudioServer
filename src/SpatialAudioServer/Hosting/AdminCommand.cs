// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using SpatialAudioServer.Api;
using SpatialAudioServer.Storage;

namespace SpatialAudioServer.Hosting;

/// <summary>
/// <c>SpatialAudioServer admin set-password | clear-password | hash-password</c>: the admin UI password.
/// The password is read from the terminal without echo (twice), or from standard input when redirected.
/// </summary>
internal static class AdminCommand
{
    public static int Run(IServiceProvider services, string[] args, TextWriter output)
    {
        var password = services.GetRequiredService<AdminPassword>();
        var activity = services.GetRequiredService<ActivityLog>();
        switch (args)
        {
            case ["set-password"]:
                {
                    if (ReadNew(output) is not { } value)
                    {
                        return 1;
                    }
                    password.Set(value);
                    activity.Event("admin.password-set");
                    output.WriteLine("Admin password stored.");
                    if (password.FromConfiguration)
                    {
                        output.WriteLine("Note: Admin:PasswordHash is set in configuration and takes precedence over it.");
                    }
                    return 0;
                }
            case ["clear-password"]:
                output.WriteLine(password.Clear() ? "Stored admin password removed." : "No stored admin password.");
                activity.Event("admin.password-cleared");
                return 0;
            case ["hash-password"]:
                {
                    if (ReadNew(output) is not { } value)
                    {
                        return 1;
                    }
                    output.WriteLine("Put this in configuration as Admin:PasswordHash (env Admin__PasswordHash):");
                    output.WriteLine(AdminPassword.Hash(value));
                    return 0;
                }
            default:
                output.WriteLine("Usage: SpatialAudioServer admin set-password | clear-password | hash-password");
                return 2;
        }
    }

    private static string? ReadNew(TextWriter output)
    {
        if (Console.IsInputRedirected)
        {
            var piped = Console.In.ReadLine();
            return string.IsNullOrEmpty(piped) ? Fail(output, "No password on standard input.") : piped;
        }
        var first = ReadHidden(output, "New admin password: ");
        if (first.Length < 8)
        {
            return Fail(output, "Use at least 8 characters.");
        }
        return ReadHidden(output, "Again: ") == first ? first : Fail(output, "The passwords differ.");
    }

    private static string? Fail(TextWriter output, string message)
    {
        output.WriteLine(message);
        return null;
    }

    private static string ReadHidden(TextWriter output, string prompt)
    {
        output.Write(prompt);
        var text = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                output.WriteLine();
                return text.ToString();
            }
            if (key.Key == ConsoleKey.Backspace)
            {
                if (text.Length > 0)
                {
                    text.Length--;
                }
            }
            else if (!char.IsControl(key.KeyChar))
            {
                text.Append(key.KeyChar);
            }
        }
    }
}
