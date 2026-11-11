using System.Globalization;
using SpatialAudioServer.Storage;

namespace SpatialAudioServer.Hosting;

/// <summary>
/// <c>SpatialAudioServer apikey create|list|revoke</c>: manages the control-API keys stored in SQLite.
/// Runs against the same configuration (and so the same database) as the server.
/// </summary>
internal static class ApiKeyCommand
{
    public static int Run(IServiceProvider services, string[] args, TextWriter output)
    {
        var store = services.GetRequiredService<ApiKeyStore>();
        var activity = services.GetRequiredService<ActivityLog>();

        switch (args)
        {
            case ["create", .. var labelParts] when labelParts.Length > 0:
                {
                    var (record, key) = store.Create(string.Join(' ', labelParts));
                    activity.Event("apikey.created", detail: $"{record.Label} ({record.Prefix}…)");
                    output.WriteLine($"Created API key '{record.Label}' (id {record.Id}).");
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
                        var state = k.RevokedAt is { } revoked ? $"revoked {Format(revoked)}" : "active";
                        var used = k.LastUsedAt is { } last ? Format(last) : "never";
                        output.WriteLine($"{k.Id}  {k.Prefix}…  {k.Label,-24}  created {Format(k.CreatedAt)}  last used {used}  {state}");
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
                output.WriteLine("Usage: SpatialAudioServer apikey create <label> | list | revoke <id-or-prefix>");
                return 2;
        }
    }

    private static string Format(DateTimeOffset value) =>
        value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
}
