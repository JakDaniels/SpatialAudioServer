using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using SpatialAudioServer.Configuration;
using SpatialAudioServer.Storage;

namespace SpatialAudioServer.Api;

/// <summary>Checks a presented control-API key against the configured bootstrap keys, then the SQLite keys.</summary>
public sealed class ApiKeyValidator
{
    private static readonly TimeSpan TouchInterval = TimeSpan.FromMinutes(1);

    private readonly byte[][] _bootstrapHashes;
    private readonly ApiKeyStore _store;
    private readonly TimeProvider _time;

    public ApiKeyValidator(IOptions<AuthOptions> options, ApiKeyStore store, TimeProvider time)
    {
        _bootstrapHashes = options.Value.ApiKeys
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => SHA256.HashData(Encoding.UTF8.GetBytes(k)))
            .ToArray();
        _store = store;
        _time = time;
    }

    /// <summary>The key's label if it is valid, otherwise null.</summary>
    public string? Validate(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return null;
        }

        // Compare hashes in constant time so response timing does not reveal how much of a key matched.
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        var bootstrap = false;
        foreach (var candidate in _bootstrapHashes)
        {
            bootstrap |= CryptographicOperations.FixedTimeEquals(hash, candidate);
        }
        if (bootstrap)
        {
            return "bootstrap";
        }

        var record = _store.FindActive(Convert.ToHexStringLower(hash));
        if (record is null)
        {
            return null;
        }
        if (record.LastUsedAt is not { } used || _time.GetUtcNow() - used > TouchInterval)
        {
            _store.Touch(record.Id);
        }
        return record.Label;
    }
}

/// <summary>Authentication scheme for the control API: <c>X-Api-Key: key</c> or <c>Authorization: Bearer key</c>.</summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ApiKeyValidator validator)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";
    public const string HeaderName = "X-Api-Key";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? key = Request.Headers[HeaderName];
        if (string.IsNullOrEmpty(key))
        {
            var authorization = Request.Headers.Authorization.ToString();
            if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                key = authorization["Bearer ".Length..].Trim();
            }
        }
        if (string.IsNullOrEmpty(key))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var label = validator.Validate(key);
        if (label is null)
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid API key."));
        }

        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, label)], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer";
        return Task.CompletedTask;
    }
}
