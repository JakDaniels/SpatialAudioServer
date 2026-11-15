using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using IPNetwork = System.Net.IPNetwork;
using Microsoft.Extensions.Options;
using SpatialAudioServer.Configuration;

namespace SpatialAudioServer.Hosting;

/// <summary>
/// For internet-facing hosts: believing forwarded headers only from trusted reverse proxies
/// (<c>Server:TrustedProxies</c>), and network allow-lists for the admin UI (<c>Admin:AllowedNetworks</c>) and the
/// control API (<c>Auth:AllowedNetworks</c>). Everything is open when the lists are empty, as on a LAN.
/// </summary>
public static class NetworkAccess
{
    /// <summary>Parses addresses and CIDR ranges ("10.0.0.0/8", "192.0.2.7", "2001:db8::/32"); a bad entry throws.</summary>
    public static IReadOnlyList<IPNetwork> ParseRanges(IEnumerable<string> entries, string setting)
    {
        var ranges = new List<IPNetwork>();
        foreach (var raw in entries)
        {
            var entry = raw.Trim();
            if (entry.Length == 0)
            {
                continue;
            }
            if (IPNetwork.TryParse(entry, out var range))
            {
                ranges.Add(range);
            }
            else if (IPAddress.TryParse(entry, out var address))
            {
                ranges.Add(new IPNetwork(Plain(address), address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128));
            }
            else
            {
                throw new InvalidOperationException($"{setting}: '{entry}' is not an address or a CIDR range (e.g. 10.0.0.0/8).");
            }
        }
        return ranges;
    }

    /// <summary>Whether <paramref name="address"/> is in one of the ranges; any address when there are none.</summary>
    public static bool Allowed(IReadOnlyList<IPNetwork> ranges, IPAddress? address) =>
        ranges.Count == 0 || (address is not null && ranges.Any(r => r.Contains(Plain(address))));

    /// <summary>Which allow-list a request path falls under: the admin UI's, the control API's, or none.</summary>
    public static string? ListFor(PathString path) =>
        path.StartsWithSegments("/admin") || path.StartsWithSegments("/api/admin") ? "admin"
        : path.StartsWithSegments("/api") ? "api"
        : null;

    /// <summary>Forwarded headers from trusted proxies (first, so everything after sees the client), then the allow-lists.</summary>
    public static void UseNetworkAccess(this WebApplication app)
    {
        var server = app.Services.GetRequiredService<IOptions<ServerOptions>>().Value;
        var proxies = ParseRanges(server.TrustedProxies, "Server:TrustedProxies");
        if (proxies.Count > 0)
        {
            var forwarded = new ForwardedHeadersOptions
            {
                ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
            };
            forwarded.KnownIPNetworks.Clear();
            forwarded.KnownProxies.Clear();
            foreach (var range in proxies)
            {
                forwarded.KnownIPNetworks.Add(range);
            }
            app.UseForwardedHeaders(forwarded);
        }

        var admin = ParseRanges(app.Services.GetRequiredService<IOptions<AdminOptions>>().Value.AllowedNetworks, "Admin:AllowedNetworks");
        var api = ParseRanges(app.Services.GetRequiredService<IOptions<AuthOptions>>().Value.AllowedNetworks, "Auth:AllowedNetworks");
        if (admin.Count == 0 && api.Count == 0)
        {
            return;
        }
        var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(NetworkAccess));
        app.Use((http, next) =>
        {
            var ranges = ListFor(http.Request.Path) switch
            {
                "admin" => admin,
                "api" => api,
                _ => null,
            };
            if (ranges is null || Allowed(ranges, http.Connection.RemoteIpAddress))
            {
                return next(http);
            }
            log.LogInformation("Refused {Path} from {Address}: not in the allowed networks", http.Request.Path, http.Connection.RemoteIpAddress);
            http.Response.StatusCode = StatusCodes.Status404NotFound; // as if it were not there
            return Task.CompletedTask;
        });
    }

    private static IPAddress Plain(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}
