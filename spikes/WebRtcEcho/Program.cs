// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using WebRtcEcho;
using WebRtcEcho.Media;

// Phase 0 spike. Usage:
//   dotnet run                  serve the echo test page on http://*:5000 and https://*:5001 (self-signed)
//   dotnet run -- --selftest    serve on a free port, run the GStreamer fake client against it, exit 0/1
var selfTest = args.Contains("--selftest");
var builder = WebApplication.CreateBuilder(args.Where(a => a != "--selftest").ToArray());
if (selfTest)
{
    builder.WebHost.UseUrls("http://127.0.0.1:0");
}
else
{
    var httpPort = builder.Configuration.GetValue("HttpPort", 5000);
    var httpsPort = builder.Configuration.GetValue("HttpsPort", 5001);
    var certPath = builder.Configuration["DevCertificate"] ?? Path.Combine(builder.Environment.ContentRootPath, ".devcert", "devcert.pfx");
    using var startupLog = LoggerFactory.Create(b => b.AddConsole());
    var certificate = DevCertificate.LoadOrCreate(certPath, startupLog.CreateLogger("DevCertificate"));
    builder.WebHost.ConfigureKestrel(k =>
    {
        k.ListenAnyIP(httpPort);
        k.ListenAnyIP(httpsPort, l => l.UseHttps(certificate));
    });
}

var app = builder.Build();
GstRuntime.Initialize(app.Logger);

var sessions = new ConcurrentDictionary<string, EchoSession>();
app.Lifetime.ApplicationStopping.Register(() =>
{
    foreach (var s in sessions.Values)
    {
        s.Dispose();
    }
});

app.UseDefaultFiles();
app.UseStaticFiles();

// Offer in, answer out. The promise waits block, so the work runs off the request thread's context.
app.MapPost("/api/sessions", async (OfferRequest request, ILoggerFactory logs) =>
{
    try
    {
        var session = await Task.Run(() => EchoSession.Create(request.Sdp, logs.CreateLogger<EchoSession>()));
        sessions[session.Id] = session;
        return Results.Ok(new OfferResponse(session.Id, session.Answer));
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(ex.Message);
    }
});

app.MapPost("/api/sessions/{id}/candidates", (string id, CandidateDto c) =>
{
    if (!sessions.TryGetValue(id, out var session))
    {
        return Results.NotFound();
    }
    session.AddRemoteCandidate(new IceCandidate(c.SdpMLineIndex, c.Candidate));
    return Results.NoContent();
});

// Trickle ICE the other way: the client polls for the server's candidates.
app.MapGet("/api/sessions/{id}/candidates", (string id, int? since) =>
    sessions.TryGetValue(id, out var session)
        ? Results.Ok(new CandidatesResponse(
            session.LocalCandidates(since ?? 0).Select(c => new CandidateDto(c.Candidate, c.SdpMLineIndex)).ToList(),
            session.GatheringComplete,
            session.ConnectionState))
        : Results.NotFound());

app.MapDelete("/api/sessions/{id}", (string id) =>
{
    if (sessions.TryRemove(id, out var session))
    {
        session.Dispose();
    }
    return Results.NoContent();
});

if (!selfTest)
{
    app.Run();
    return 0;
}

await app.StartAsync();
var url = new Uri(app.Urls.First().TrimEnd('/') + "/");
app.Logger.LogInformation("Self-test against {Url}", url);
int status;
using (var client = new FakeClient(app.Services.GetRequiredService<ILoggerFactory>().CreateLogger<FakeClient>()))
{
    status = await client.RunAsync(url, TimeSpan.FromSeconds(6), CancellationToken.None);
}
await app.StopAsync();
app.Logger.LogInformation("Self-test {Result}", status == 0 ? "PASSED" : "FAILED");
return status;

namespace WebRtcEcho
{
    public sealed record OfferRequest(string Sdp);

    public sealed record OfferResponse(string Id, string Sdp);

    public sealed record CandidateDto(string Candidate, uint SdpMLineIndex);

    public sealed record CandidatesResponse(List<CandidateDto> Candidates, bool Complete, string ConnectionState);
}
