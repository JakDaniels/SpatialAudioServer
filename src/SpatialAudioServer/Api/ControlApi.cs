using System.Reflection;
using Microsoft.Extensions.Options;
using SpatialAudioServer.Configuration;
using SpatialAudioServer.Media;
using SpatialAudioServer.Media.Engines;
using SpatialAudioServer.Rooms;

namespace SpatialAudioServer.Api;

public sealed record RoomDetails(
    Guid Id, string Name, string? ExternalId, int MaxParticipants, System.Text.Json.Nodes.JsonObject? Metadata,
    DateTimeOffset CreatedAt, int ParticipantCount, string Engine, IReadOnlyList<ParticipantSnapshot> Participants);

public sealed record JoinResponse(
    Guid ParticipantId, Guid RoomId, string JoinToken, string SignalUrl, DateTimeOffset ExpiresAt, Guid? ReplacedParticipantId);

public sealed record StatusResponse(
    string Version, DateTimeOffset StartedAt, double UptimeSeconds, GstStatusDto Gstreamer, int Rooms, int Participants,
    LimitOptions Limits, ProcessStatus Process, string DefaultEngine, IReadOnlyList<MixEngineInfo> Engines);

/// <summary>Server process resources; the load tool samples CpuSeconds twice to get CPU use over a window.</summary>
public sealed record ProcessStatus(double CpuSeconds, int ProcessorCount, long WorkingSetBytes, int Threads);

public sealed record GstStatusDto(bool Enabled, bool Ok, string? Version, IReadOnlyList<string> MissingElements, string? Error);

/// <summary>The control API (PLAN.md "JSON API"). Every endpoint requires an API key.</summary>
public static class ControlApi
{
    private static readonly string Version =
        typeof(ControlApi).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    public static void MapControlApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();

        api.MapGet("/status", (RoomManager rooms, GstRuntime gst, IOptions<LimitOptions> limits, IOptions<MediaOptions> media, ServerClock clock) =>
        {
            var g = gst.Status;
            return new StatusResponse(
                Version, clock.StartedAt, Math.Round(clock.Uptime.TotalSeconds, 1),
                new GstStatusDto(g.Enabled, g.Ok, g.Version, g.MissingElements, g.Error),
                rooms.RoomCount, rooms.ParticipantCount, limits.Value, CurrentProcess(),
                media.Value.Engine, MixEngines.All);
        });

        var roomsApi = api.MapGroup("/rooms");

        roomsApi.MapPost("/", (CreateRoomCommand body, RoomManager rooms) =>
        {
            var room = rooms.CreateRoom(body);
            return Results.Created($"/api/rooms/{room.Id}", room);
        });

        roomsApi.MapGet("/", (string? externalId, RoomManager rooms) => rooms.ListRooms(externalId));

        // Get-or-create by external id: 201 when created, 200 with the existing room otherwise.
        roomsApi.MapPut($"/by-external/{{{RoomExternalId}}}", (string roomExternalId, EnsureRoomCommand? body, RoomManager rooms) =>
        {
            var (room, created) = rooms.EnsureRoom(roomExternalId, body);
            return created ? Results.Created($"/api/rooms/{room.Id}", room) : Results.Ok(room);
        });

        // Every room route exists twice: by our id and by external id (OpenSim region/parcel UUID). Likewise for
        // participants (OpenSim avatar UUID), so the region module never has to store our ids.
        MapRoom(roomsApi.MapGroup($"/{{{RoomId}:guid}}"));
        MapRoom(roomsApi.MapGroup($"/by-external/{{{RoomExternalId}}}"));
    }

    private static ProcessStatus CurrentProcess()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        return new ProcessStatus(
            process.TotalProcessorTime.TotalSeconds, Environment.ProcessorCount, process.WorkingSet64, process.Threads.Count);
    }

    private const string RoomId = "roomId";
    private const string RoomExternalId = "roomExternalId";
    private const string ParticipantId = "participantId";
    private const string ParticipantExternalId = "participantExternalId";

    private static void MapRoom(RouteGroupBuilder room)
    {
        room.MapGet("/", (HttpContext http, RoomManager rooms) =>
        {
            var (r, participants) = rooms.GetRoom(ResolveRoom(http, rooms));
            return new RoomDetails(r.Id, r.Name, r.ExternalId, r.MaxParticipants, r.Metadata, r.CreatedAt, r.ParticipantCount, r.Engine, participants);
        });

        room.MapDelete("/", (HttpContext http, RoomManager rooms) =>
        {
            rooms.DeleteRoom(ResolveRoom(http, rooms));
            return Results.NoContent();
        });

        var participants = room.MapGroup("/participants");

        participants.MapPost("/", (HttpContext http, AddParticipantCommand body, RoomManager rooms) =>
        {
            var roomId = ResolveRoom(http, rooms);
            var join = rooms.AddParticipant(roomId, body);
            var id = join.Participant.Id;
            return Results.Created(
                $"/api/rooms/{roomId}/participants/{id}",
                new JoinResponse(id, roomId, join.JoinToken, $"/signal/{id}", join.ExpiresAt, join.ReplacedParticipantId));
        });

        participants.MapGet("/", (HttpContext http, RoomManager rooms) => rooms.ListParticipants(ResolveRoom(http, rooms)));

        MapParticipant(participants.MapGroup($"/{{{ParticipantId}:guid}}"));
        MapParticipant(participants.MapGroup($"/by-external/{{{ParticipantExternalId}}}"));
    }

    private static void MapParticipant(RouteGroupBuilder participant)
    {
        participant.MapGet("/", (HttpContext http, RoomManager rooms) =>
        {
            var (roomId, participantId) = ResolveParticipant(http, rooms);
            return rooms.GetParticipant(roomId, participantId);
        });

        participant.MapPatch("/", (HttpContext http, UpdateParticipantCommand body, RoomManager rooms) =>
        {
            var (roomId, participantId) = ResolveParticipant(http, rooms);
            return rooms.UpdateParticipant(roomId, participantId, body);
        });

        participant.MapDelete("/", (HttpContext http, RoomManager rooms) =>
        {
            var (roomId, participantId) = ResolveParticipant(http, rooms);
            rooms.RemoveParticipant(roomId, participantId);
            return Results.NoContent();
        });
    }

    private static Guid ResolveRoom(HttpContext http, RoomManager rooms)
    {
        var values = http.Request.RouteValues;
        return values.TryGetValue(RoomId, out var id)
            ? Guid.Parse((string)id!)
            : rooms.ResolveRoom((string)values[RoomExternalId]!);
    }

    private static (Guid RoomId, Guid ParticipantId) ResolveParticipant(HttpContext http, RoomManager rooms)
    {
        var roomId = ResolveRoom(http, rooms);
        var values = http.Request.RouteValues;
        var participantId = values.TryGetValue(ParticipantId, out var id)
            ? Guid.Parse((string)id!)
            : rooms.ResolveParticipant(roomId, (string)values[ParticipantExternalId]!);
        return (roomId, participantId);
    }
}

/// <summary>When the process started, for <c>/api/status</c>.</summary>
public sealed class ServerClock(TimeProvider time)
{
    public DateTimeOffset StartedAt { get; } = time.GetUtcNow();

    public TimeSpan Uptime => time.GetUtcNow() - StartedAt;
}
