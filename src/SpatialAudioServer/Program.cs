using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using SpatialAudioServer.Api;
using SpatialAudioServer.Configuration;
using SpatialAudioServer.Hosting;
using SpatialAudioServer.Media;
using SpatialAudioServer.Rooms;
using SpatialAudioServer.Signalling;
using SpatialAudioServer.Storage;

// SpatialAudioServer                          run the server
// SpatialAudioServer --delete-existing-rooms  run it, deleting every stored room first (testing: a clean, idle server)
// SpatialAudioServer apikey <command>         manage stored control-API keys (create <label> | list | revoke <id>)
// SpatialAudioServer admin <command>          the admin UI password (set-password | clear-password | hash-password)
var cliCommand = args is ["apikey", ..] or ["admin", ..];
const string DeleteRoomsFlag = "--delete-existing-rooms";
var deleteRooms = !cliCommand && args.Contains(DeleteRoomsFlag);
args = [.. args.Where(a => a != DeleteRoomsFlag)]; // not a configuration switch: keep it away from the config parser
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = cliCommand ? [] : args,
    ContentRootPath = ContentRoot.Resolve(),
});
builder.Services.AddSingleton(new StartupFlags(deleteRooms));
// No effect unless started by systemd (Type=notify, journal-friendly logs) or as a Windows service (Event Log).
builder.Host.UseSystemd();
builder.Host.UseWindowsService(o => o.ServiceName = "SpatialAudioServer");

builder.Services.Configure<ServerOptions>(builder.Configuration.GetSection(ServerOptions.Section));
builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection(StorageOptions.Section));
builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection(AuthOptions.Section));
builder.Services.Configure<LimitOptions>(builder.Configuration.GetSection(LimitOptions.Section));
builder.Services.Configure<MediaOptions>(builder.Configuration.GetSection(MediaOptions.Section));
builder.Services.Configure<AdminOptions>(builder.Configuration.GetSection(AdminOptions.Section));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ServerClock>();
builder.Services.AddSingleton<Database>();
builder.Services.AddSingleton<RoomStore>();
builder.Services.AddSingleton<ApiKeyStore>();
builder.Services.AddSingleton<SettingsStore>();
builder.Services.AddSingleton<JoinTokenStore>();
builder.Services.AddSingleton<ActivityLog>();
builder.Services.AddSingleton<GstRuntime>();
if (builder.Configuration.GetValue($"{MediaOptions.Section}:{nameof(MediaOptions.Enabled)}", true))
{
    builder.Services.AddSingleton<GstRoomMedia>();
    builder.Services.AddSingleton<IRoomMedia>(sp => sp.GetRequiredService<GstRoomMedia>());
}
else
{
    builder.Services.AddSingleton<IRoomMedia, NullRoomMedia>();
}
builder.Services.AddSingleton<SignalSessions>();
builder.Services.AddSingleton<RoomManager>();
builder.Services.AddSingleton<ApiKeyValidator>();
builder.Services.AddHostedService<StartupService>();
builder.Services.AddHostedService<ParticipantSweeper>();

builder.Services.AddApiKeyAndAdminAuthentication();
var limitOptions = builder.Configuration.GetSection(LimitOptions.Section).Get<LimitOptions>() ?? new LimitOptions();
builder.Services.AddSignalRateLimits(limitOptions);
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<DomainExceptionHandler>();
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase)));

// Listeners. Only consulted when Kestrel actually starts, so the test host never generates a certificate.
builder.WebHost.ConfigureKestrel((context, kestrel) =>
{
    kestrel.Limits.MaxRequestBodySize = limitOptions.MaxRequestBodyBytes;
    var server = context.Configuration.GetSection(ServerOptions.Section).Get<ServerOptions>() ?? new ServerOptions();
    if (server.HttpPort > 0)
    {
        kestrel.ListenAnyIP(server.HttpPort);
    }
    if (server.HttpsPort > 0)
    {
        var log = kestrel.ApplicationServices.GetRequiredService<ILoggerFactory>().CreateLogger("Certificate");
        var certificate = server.CertificatePath is { Length: > 0 } path
            ? System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12FromFile(
                Path.Combine(context.HostingEnvironment.ContentRootPath, path), server.CertificatePassword)
            : DevCertificate.LoadOrCreate(Path.Combine(context.HostingEnvironment.ContentRootPath, server.DevCertificatePath), log);
        kestrel.ListenAnyIP(server.HttpsPort, listen => listen.UseHttps(certificate));
    }
});

var app = builder.Build();

if (cliCommand)
{
    return args[0] == "admin"
        ? AdminCommand.Run(app.Services, args[1..], Console.Out)
        : ApiKeyCommand.Run(app.Services, args[1..], Console.Out);
}

app.UseExceptionHandler();
app.Use((http, next) =>
{
    // The UIs load only their own scripts and styles; join links carry a token in the query, so no Referer.
    var headers = http.Response.Headers;
    headers.XContentTypeOptions = "nosniff";
    headers["Referrer-Policy"] = "no-referrer";
    headers.ContentSecurityPolicy = "default-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; " +
        "object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'";
    return next(http);
});
// wwwroot: the test web client at /client/ (and later the admin UI). Static, no build step, no auth.
app.UseDefaultFiles();
// no-cache: browsers revalidate (ETag, a 304 when unchanged), so after an upgrade they never mix new pages with old scripts.
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context => context.Context.Response.Headers.CacheControl = "no-cache",
});
app.MapGet("/", () => Results.Redirect("/client/")).ExcludeFromDescription();
app.UseStatusCodePages();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapAdminSession();
app.MapAdminApi();
app.MapControlApi();
app.MapSignalling();

app.Run();
return 0;

/// <summary>
/// Where wwwroot, appsettings.json and data/ are. `dotnet run` (and the tests) start in the project directory; a published
/// build may be started from anywhere (a Windows service starts in System32), so it uses its own directory instead.
/// </summary>
internal static class ContentRoot
{
    public static string? Resolve() =>
        !Directory.Exists(Path.Combine(Directory.GetCurrentDirectory(), "wwwroot"))
        && Directory.Exists(Path.Combine(AppContext.BaseDirectory, "wwwroot"))
            ? AppContext.BaseDirectory
            : null;
}

/// <summary>Visible to the integration tests' WebApplicationFactory.</summary>
public partial class Program;
