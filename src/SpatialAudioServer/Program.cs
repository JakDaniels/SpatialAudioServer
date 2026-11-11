using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using SpatialAudioServer.Api;
using SpatialAudioServer.Configuration;
using SpatialAudioServer.Hosting;
using SpatialAudioServer.Media;
using SpatialAudioServer.Rooms;
using SpatialAudioServer.Storage;

// SpatialAudioServer                      run the server
// SpatialAudioServer apikey <command>     manage stored control-API keys (create <label> | list | revoke <id>)
var cliCommand = args is ["apikey", ..];
var builder = WebApplication.CreateBuilder(cliCommand ? [] : args);

builder.Services.Configure<ServerOptions>(builder.Configuration.GetSection(ServerOptions.Section));
builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection(StorageOptions.Section));
builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection(AuthOptions.Section));
builder.Services.Configure<LimitOptions>(builder.Configuration.GetSection(LimitOptions.Section));
builder.Services.Configure<MediaOptions>(builder.Configuration.GetSection(MediaOptions.Section));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ServerClock>();
builder.Services.AddSingleton<Database>();
builder.Services.AddSingleton<RoomStore>();
builder.Services.AddSingleton<ApiKeyStore>();
builder.Services.AddSingleton<JoinTokenStore>();
builder.Services.AddSingleton<ActivityLog>();
builder.Services.AddSingleton<GstRuntime>();
builder.Services.AddSingleton<IRoomMedia, NullRoomMedia>(); // Phase 2 replaces this with the GStreamer implementation.
builder.Services.AddSingleton<RoomManager>();
builder.Services.AddSingleton<ApiKeyValidator>();
builder.Services.AddHostedService<StartupService>();

builder.Services.AddAuthentication(ApiKeyAuthenticationHandler.SchemeName)
    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
        ApiKeyAuthenticationHandler.SchemeName, null);
builder.Services.AddAuthorization();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<DomainExceptionHandler>();
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase)));

// Listeners. Only consulted when Kestrel actually starts, so the test host never generates a certificate.
builder.WebHost.ConfigureKestrel((context, kestrel) =>
{
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
    return ApiKeyCommand.Run(app.Services, args[1..], Console.Out);
}

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();
app.MapControlApi();

app.Run();
return 0;

/// <summary>Visible to the integration tests' WebApplicationFactory.</summary>
public partial class Program;
