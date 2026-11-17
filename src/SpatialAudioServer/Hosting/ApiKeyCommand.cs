// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using SpatialAudioServer.Storage;

namespace SpatialAudioServer.Hosting;

/// <summary>
/// <c>SpatialAudioServer apikey create|list|revoke</c>: manages the control-API keys stored in SQLite. A key may be
/// given an expiry (<c>--expires 90d</c> or a date); <c>list</c> flags keys that have expired or gone unused.
/// Runs against the same configuration (and so the same database) as the server.
/// </summary>
internal static class ApiKeyCommand
{
    /// <summary>A key unused this long is flagged by <c>list</c>.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(90);

    public static int Run(IServiceProvider services, string[] args, TextWriter output)
    {
        var store = services.GetRequiredService<ApiKeyStore>();
        var activity = services.GetRequiredService<ActivityLog>();
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow();

        switch (args)
        {
            case ["create", .. var rest] when rest.Length > 0:
                {
                    DateTimeOffset? expires = null;
                    var labelParts = new List<string>();
                    for (var i = 0; i < rest.Length; i++)
                    {
                        if (rest[i] == "--expires")
                        {
                            if (i + 1 >= rest.Length || ParseExpiry(rest[++i], now) is not { } when)
                            {
                                output.WriteLine("--expires needs a number of days (90d) or a date (2027-01-31), in the future.");
                                return 2;
                            }
                            expires = when;
                        }
                        else
                        {
                            labelParts.Add(rest[i]);
                        }
                    }
                    if (labelParts.Count == 0)
                    {
                        output.WriteLine("A label is needed: apikey create <label> [--expires 90d|2027-01-31]");
                        return 2;
                    }
                    var (record, key) = store.Create(string.Join(' ', labelParts), expires);
                    activity.Event("apikey.created", detail: $"{record.Label} ({record.Prefix}…)" + (expires is { } e ? $", expires {Format(e)}" : ""));
                    output.WriteLine($"Created API key '{record.Label}' (id {record.Id})" + (expires is { } x ? $", valid until {Format(x)}." : "."));
                    output.WriteLine("Store it now; it cannot be shown again:");
                    output.WriteLine(key);
                    return 0;
                }

            case ["list"]:
                {
                    var keys = store.List();
                    if (keys.Count == 0)
                    {
                        output.WriteLine("No stored API keys.");
                    }
                    foreach (var k in keys)
                    {
                        var state = k.RevokedAt is { } revoked ? $"revoked {Format(revoked)}"
                            : k.ExpiresAt is { } ended && ended <= now ? $"expired {Format(ended)}"
                            : k.ExpiresAt is { } ends ? $"active, expires {Format(ends)}"
                            : "active";
                        var used = k.LastUsedAt is { } last ? Format(last) : "never";
                        var stale = k.RevokedAt is null && (k.ExpiresAt is not { } end || end > now)
                            && now - (k.LastUsedAt ?? k.CreatedAt) > StaleAfter ? $"  (unused for {StaleAfter.TotalDays:0}+ days: revoke it?)" : "";
                        output.WriteLine($"{k.Id}  {k.Prefix}…  {k.Label,-24}  created {Format(k.CreatedAt)}  last used {used}  {state}{stale}");
                    }
                    return 0;
                }

            case ["revoke", var idOrPrefix]:
                {
                    var count = store.Revoke(idOrPrefix);
                    if (count == 0)
                    {
                        output.WriteLine($"No active key matches '{idOrPrefix}'.");
                        return 1;
                    }
                    activity.Event("apikey.revoked", detail: idOrPrefix);
                    output.WriteLine($"Revoked {count} key(s).");
                    return 0;
                }

            default:
                output.WriteLine("Usage: SpatialAudioServer apikey create <label> [--expires 90d|2027-01-31] | list | revoke <id-or-prefix>");
                return 2;
        }
    }

    /// <summary>"90d" (days from now) or a date / date and time; null if neither or not in the future.</summary>
    internal static DateTimeOffset? ParseExpiry(string text, DateTimeOffset now)
    {
        DateTimeOffset? when = null;
        if (text.EndsWith('d') && int.TryParse(text[..^1], NumberStyles.None, CultureInfo.InvariantCulture, out var days) && days > 0)
        {
            when = now.AddDays(days);
        }
        else if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var date))
        {
            when = date;
        }
        return when > now ? when : null;
    }

    private static string Format(DateTimeOffset value) =>
        value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
}
