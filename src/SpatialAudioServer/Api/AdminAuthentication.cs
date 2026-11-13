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
/// address gets 429 until the window has passed since its last failure.
/// </summary>
public sealed class LoginThrottle(TimeProvider time)
{
    public const int MaxFailures = 5;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, (int Count, DateTimeOffset Last)> _failures = new();

    public bool Blocked(string address) =>
        _failures.TryGetValue(address, out var f) && f.Count >= MaxFailures && time.GetUtcNow() - f.Last < Window;

    public void Failed(string address) =>
        _failures.AddOrUpdate(address, _ => (1, time.GetUtcNow()),
            (_, f) => (time.GetUtcNow() - f.Last < Window ? f.Count + 1 : 1, time.GetUtcNow()));

    public void Succeeded(string address) => _failures.TryRemove(address, out _);
}

/// <summary>Admin sign-in (cookie) and the authentication wiring shared by the control API and the admin UI.</summary>
public static class AdminAuthentication
{
    public const string CookieScheme = "AdminCookie";
    public const string SelectorScheme = "ApiKeyOrAdmin";
    public const string AdminPolicy = "Admin";
    public const string AdminRole = "admin";
    public const string CookieName = "sas_admin";

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
            cookie.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest; // LAN development uses plain HTTP too
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

    /// <summary>POST /api/admin/login, POST /api/admin/logout, GET /api/admin/session (all anonymous).</summary>
    public static void MapAdminSession(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/admin").AllowAnonymous();

        admin.MapPost("/login", async (LoginRequest body, HttpContext http, AdminPassword password, LoginThrottle throttle,
            ActivityLog activity) =>
        {
            var address = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
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
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "admin"), new Claim(ClaimTypes.Role, AdminRole)], CookieScheme);
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
