// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;
using SpatialAudioServer.Configuration;
using SpatialAudioServer.Storage;

namespace SpatialAudioServer.Api;

/// <summary>
/// The admin password: PBKDF2-SHA256 hashes in the form <c>pbkdf2-sha256$iterations$salt$hash</c> (base64). Stored with
/// <c>admin set-password</c> (SQLite settings) or given as <c>Admin:PasswordHash</c>, which wins when set.
/// </summary>
public sealed class AdminPassword(SettingsStore settings, IOptions<AdminOptions> options)
{
    private const int Iterations = 210_000;
    private const string Scheme = "pbkdf2-sha256";

    /// <summary>The hash in force, from configuration or the database; null when no password is set.</summary>
    public string? CurrentHash => options.Value.PasswordHash is { Length: > 0 } configured ? configured : settings.Get(SettingsStore.AdminPasswordHash);

    public bool IsSet => CurrentHash is not null;

    public bool FromConfiguration => options.Value.PasswordHash is { Length: > 0 };

    /// <summary>
    /// A short fingerprint of the password hash in force, kept in each admin session: when the password changes (or is
    /// cleared) it no longer matches, so every session made with the old one ends.
    /// </summary>
    public string Fingerprint => CurrentHash is { } hash
        ? Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(hash)))[..16]
        : "none";

    public bool Verify(string password) => CurrentHash is { } hash && Verify(password, hash);

    public void Set(string password) => settings.Set(SettingsStore.AdminPasswordHash, Hash(password));

    public bool Clear() => settings.Delete(SettingsStore.AdminPasswordHash);

    public static string Hash(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"{Scheme}${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != Scheme || !int.TryParse(parts[1], out var iterations) || iterations < 1)
        {
            return false;
        }
        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

/// <summary>
/// Throttles admin logins: after <see cref="MaxFailures"/> failures from one address within the window, that
/// address gets 429 until the window has passed since its last failure; and after <see cref="MaxFailuresOverall"/>
/// failures from anywhere within the window, everyone does (a guesser spread over many addresses). Old entries are
/// dropped, so the table cannot grow without bound.
/// </summary>
public sealed class LoginThrottle(TimeProvider time)
{
    public const int MaxFailures = 5;
    public const int MaxFailuresOverall = 30;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, (int Count, DateTimeOffset Last)> _failures = new();
    private readonly Queue<DateTimeOffset> _recent = new();

    public bool Blocked(string address)
    {
        var now = time.GetUtcNow();
        lock (_recent)
        {
            Expire(now);
            if (_recent.Count >= MaxFailuresOverall)
            {
                return true;
            }
        }
        return _failures.TryGetValue(address, out var f) && f.Count >= MaxFailures && now - f.Last < Window;
    }

    public void Failed(string address)
    {
        var now = time.GetUtcNow();
        _failures.AddOrUpdate(address, _ => (1, now), (_, f) => (now - f.Last < Window ? f.Count + 1 : 1, now));
        lock (_recent)
        {
            _recent.Enqueue(now);
            Expire(now);
        }
        if (_failures.Count > 1000)
        {
            foreach (var (key, f) in _failures)
            {
                if (now - f.Last >= Window)
                {
                    _failures.TryRemove(key, out _);
                }
            }
        }
    }

    public void Succeeded(string address) => _failures.TryRemove(address, out _);

    private void Expire(DateTimeOffset now)
    {
        while (_recent.Count > 0 && now - _recent.Peek() >= Window)
        {
            _recent.Dequeue();
        }
    }
}

/// <summary>Admin sign-in (cookie) and the authentication wiring shared by the control API and the admin UI.</summary>
public static class AdminAuthentication
{
    public const string CookieScheme = "AdminCookie";
    public const string SelectorScheme = "ApiKeyOrAdmin";
    public const string AdminPolicy = "Admin";
    public const string AdminRole = "admin";
    public const string CookieName = "sas_admin";

    /// <summary>The session's <see cref="AdminPassword.Fingerprint"/>.</summary>
    public const string PasswordClaim = "sas:password";

    public sealed record LoginRequest(string? Password);

    public sealed record SessionResponse(bool LoggedIn, bool PasswordSet);

    /// <summary>
    /// API keys and the admin cookie side by side: requests that carry a key (X-Api-Key or Authorization) use the
    /// key scheme, anything else the cookie. <c>/api</c> accepts either; <see cref="AdminPolicy"/> needs the cookie.
    /// </summary>
    public static void AddApiKeyAndAdminAuthentication(this IServiceCollection services)
    {
        services.AddAuthentication(SelectorScheme)
            .AddPolicyScheme(SelectorScheme, "API key or admin session", o => o.ForwardDefaultSelector = context =>
                context.Request.Headers.ContainsKey(ApiKeyAuthenticationHandler.HeaderName)
                || context.Request.Headers.Authorization.Count > 0
                    ? ApiKeyAuthenticationHandler.SchemeName
                    : CookieScheme)
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, null)
            .AddCookie(CookieScheme, _ => { });
        services.AddOptions<CookieAuthenticationOptions>(CookieScheme).Configure<IOptions<AdminOptions>>((cookie, admin) =>
        {
            cookie.Cookie.Name = CookieName;
            cookie.Cookie.HttpOnly = true;
            cookie.Cookie.SameSite = SameSiteMode.Strict; // with JSON-only endpoints, this is the CSRF protection
            // LAN set-ups use plain HTTP too; Admin:RequireHttps marks it Secure.
            cookie.Cookie.SecurePolicy = admin.Value.RequireHttps ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
            // A session made with an older password (changed or cleared since) ends.
            cookie.Events.OnValidatePrincipal = context =>
            {
                var password = context.HttpContext.RequestServices.GetRequiredService<AdminPassword>();
                if (context.Principal?.FindFirst(PasswordClaim)?.Value != password.Fingerprint)
                {
                    context.RejectPrincipal();
                    return context.HttpContext.SignOutAsync(CookieScheme);
                }
                return Task.CompletedTask;
            };
            cookie.ExpireTimeSpan = admin.Value.SessionLifetime;
            cookie.SlidingExpiration = true;
            // An API: answer 401/403 instead of redirecting to a login page.
            cookie.Events.OnRedirectToLogin = context =>
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            };
            cookie.Events.OnRedirectToAccessDenied = context =>
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            };
        });
        services.AddAuthorizationBuilder()
            .AddPolicy(AdminPolicy, policy => policy.AddAuthenticationSchemes(CookieScheme).RequireRole(AdminRole));
        services.AddSingleton<AdminPassword>();
        services.AddSingleton<LoginThrottle>();
    }

    /// <summary>
    /// Changes made with the admin cookie (not an API key) must come from this server's own pages: a request with an
    /// <c>Origin</c> header naming another host is refused. <c>SameSite=Strict</c> and JSON-only bodies already stop
    /// cross-site requests; this also covers a hostile site on a sibling subdomain, which counts as "same site".
    /// </summary>
    public static void UseSameOriginAdminChanges(this WebApplication app) => app.Use((http, next) =>
    {
        var request = http.Request;
        if (request.Path.StartsWithSegments("/api")
            && !HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method) && !HttpMethods.IsOptions(request.Method)
            && !request.Headers.ContainsKey(ApiKeyAuthenticationHandler.HeaderName) && request.Headers.Authorization.Count == 0
            && request.Headers.Origin is [{ } origin] && !SameHost(origin, request.Host))
        {
            http.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        }
        return next(http);

        static bool SameHost(string origin, HostString host) =>
            Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            && string.Equals(uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}", host.Value, StringComparison.OrdinalIgnoreCase);
    });

    /// <summary>
    /// A request made on this machine itself (loopback, not through a proxy): the one exception to
    /// <see cref="AdminOptions.RequireHttps"/>. A request through a proxy on the same host also comes from loopback,
    /// but still carries forwarding headers unless the proxy is trusted (then they have been applied and removed,
    /// and the address is the real client's), so it does not count.
    /// </summary>
    private static bool FromThisMachine(HttpContext http) =>
        http.Connection.RemoteIpAddress is { } ip && System.Net.IPAddress.IsLoopback(ip) && !Proxied(http);

    private static bool Proxied(HttpContext http) =>
        http.Request.Headers.ContainsKey("X-Forwarded-For") || http.Request.Headers.ContainsKey("Forwarded");

    /// <summary>POST /api/admin/login, POST /api/admin/logout, GET /api/admin/session (all anonymous).</summary>
    public static void MapAdminSession(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/admin").AllowAnonymous();

        admin.MapPost("/login", async (LoginRequest body, HttpContext http, AdminPassword password, LoginThrottle throttle,
            ActivityLog activity, IOptions<AdminOptions> options) =>
        {
            var address = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (options.Value.RequireHttps && !http.Request.IsHttps && !FromThisMachine(http))
            {
                return Results.Problem("Admin login needs HTTPS here." + (Proxied(http)
                    ? " This request came through a proxy that is not in Server:TrustedProxies, so its HTTPS is not known."
                    : ""), statusCode: StatusCodes.Status403Forbidden);
            }
            if (throttle.Blocked(address))
            {
                return Results.Problem("Too many failed logins; wait a minute.", statusCode: StatusCodes.Status429TooManyRequests);
            }
            if (!password.IsSet)
            {
                return Results.Problem("No admin password is set: run 'SpatialAudioServer admin set-password' on the server.",
                    statusCode: StatusCodes.Status409Conflict);
            }
            if (body.Password is not { Length: > 0 } given || !password.Verify(given))
            {
                throttle.Failed(address);
                activity.Event("admin.login-failed", detail: address);
                return Results.Problem("Wrong password.", statusCode: StatusCodes.Status401Unauthorized);
            }
            throttle.Succeeded(address);
            activity.Event("admin.login", detail: address);
            var identity = new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, "admin"), new Claim(ClaimTypes.Role, AdminRole), new Claim(PasswordClaim, password.Fingerprint)],
                CookieScheme);
            await http.SignInAsync(CookieScheme, new ClaimsPrincipal(identity));
            return Results.Ok(new SessionResponse(true, true));
        });

        admin.MapPost("/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync(CookieScheme);
            return Results.NoContent();
        });

        admin.MapGet("/session", async (HttpContext http, AdminPassword password) =>
        {
            var result = await http.AuthenticateAsync(CookieScheme);
            return new SessionResponse(result.Succeeded && result.Principal.IsInRole(AdminRole), password.IsSet);
        });
    }
}
