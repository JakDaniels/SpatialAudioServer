using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using SpatialAudioServer.Configuration;

namespace SpatialAudioServer.Api;

/// <summary>
/// Rate limits for the signalling endpoints, the only ones open to clients without an API key or admin session: per
/// participant and per client address (token buckets; Limits:SignalRequestsPerSecond…). Over the limit is 429 with
/// Retry-After. The control API is not limited (its callers hold a key; the region module may send many position
/// updates), and admin sign-in has its own throttle.
/// </summary>
/// <remarks>
/// The address is the TCP peer. Behind a reverse proxy every client would share the proxy's address: configure
/// forwarded headers then (not needed while the server is LAN-only).
/// </remarks>
public static class SignalRateLimits
{
    private const string SignalPrefix = "/signal/";

    public static IServiceCollection AddSignalRateLimits(this IServiceCollection services, LimitOptions limits)
    {
        var perParticipant = PartitionedRateLimiter.Create<HttpContext, string>(http =>
            limits.SignalRequestsPerSecond > 0 && ParticipantOf(http) is { } participant
                ? RateLimitPartition.GetTokenBucketLimiter(participant, _ => Bucket(limits.SignalRequestsPerSecond))
                : RateLimitPartition.GetNoLimiter(""));
        var perAddress = PartitionedRateLimiter.Create<HttpContext, string>(http =>
            limits.SignalRequestsPerSecondPerAddress > 0 && IsSignalling(http)
                ? RateLimitPartition.GetTokenBucketLimiter(AddressOf(http), _ => Bucket(limits.SignalRequestsPerSecondPerAddress))
                : RateLimitPartition.GetNoLimiter(""));

        return services.AddRateLimiter(options =>
        {
            options.GlobalLimiter = PartitionedRateLimiter.CreateChained(perAddress, perParticipant);
            options.OnRejected = (context, _) =>
            {
                var response = context.HttpContext.Response;
                response.StatusCode = StatusCodes.Status429TooManyRequests;
                response.Headers.RetryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var after)
                    ? Math.Max(1, (int)Math.Ceiling(after.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : "1";
                return ValueTask.CompletedTask;
            };
        });
    }

    private static TokenBucketRateLimiterOptions Bucket(int perSecond) => new()
    {
        TokenLimit = perSecond * 2,
        TokensPerPeriod = perSecond,
        ReplenishmentPeriod = TimeSpan.FromSeconds(1),
        QueueLimit = 0,
        AutoReplenishment = true,
    };

    private static bool IsSignalling(HttpContext http) =>
        http.Request.Path.StartsWithSegments("/signal", StringComparison.OrdinalIgnoreCase);

    /// <summary>The participant id from <c>/signal/{id}/…</c>, or null for any other path.</summary>
    private static string? ParticipantOf(HttpContext http)
    {
        var path = http.Request.Path.Value;
        if (path is null || !path.StartsWith(SignalPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        var rest = path.AsSpan(SignalPrefix.Length);
        var end = rest.IndexOf('/');
        return Guid.TryParse(end < 0 ? rest : rest[..end], out var id) ? id.ToString() : null;
    }

    private static string AddressOf(HttpContext http) =>
        http.Connection.RemoteIpAddress is { } ip ? (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToString() : IPAddress.None.ToString();
}
