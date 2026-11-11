using System.Reflection;
using Microsoft.Extensions.Options;
using SpatialAudioServer.Configuration;
using SpatialAudioServer.Media;
using SpatialAudioServer.Rooms;

namespace SpatialAudioServer.Api;

public sealed record RoomDetails(
    Guid Id, string Name, string? ExternalId, int MaxParticipants, System.Text.Json.Nodes.JsonObject? Metadata,
    DateTimeOffset CreatedAt, int ParticipantCount, IReadOnlyList<ParticipantSnapshot> Participants);

public sealed record JoinResponse(Guid ParticipantId, Guid RoomId, string JoinToken, string SignalUrl, DateTimeOffset ExpiresAt);

public sealed record StatusResponse(
    string Version, DateTimeOffset StartedAt, double UptimeSeconds, GstStatusDto Gstreamer, int Rooms, int Participants, LimitOptions Limits);

public sealed record GstStatusDto(bool Enabled, bool Ok, string? Version, IReadOnlyList<string> MissingElements, string? Error);

/// <summary>The control API (PLAN.md "JSON API"). Every endpoint requires an API key.</summary>
public static class ControlApi
{
    private static readonly string Version =
        typeof(ControlApi).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    public static void MapControlApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();

        api.MapGet("/status", (RoomManager rooms, GstRuntime gst, IOptions<LimitOptions> limits, ServerClock clock) =>
        {
            var g = gst.Status;
            return new StatusResponse(
                Version, clock.StartedAt, Math.Round(clock.Uptime.TotalSeconds, 1),
                new GstStatusDto(g.Enabled, g.Ok, g.Version, g.MissingElements, g.Error),
                rooms.RoomCount, rooms.ParticipantCount, limits.Value);
        });

        var roomsApi = api.MapGroup("/rooms");

        roomsApi.MapPost("/", (CreateRoomCommand body, RoomManager rooms) =>
        {
            var room = rooms.CreateRoom(body);
            return Results.Created($"/api/rooms/{room.Id}", room);
        });

        roomsApi.MapGet("/", (string? externalId, RoomManager rooms) => rooms.ListRooms(externalId));

        roomsApi.MapGet("/{roomId:guid}", (Guid roomId, RoomManager rooms) =>
        {
            var (r, participants) = rooms.GetRoom(roomId);
            return new RoomDetails(r.Id, r.Name, r.ExternalId, r.MaxParticipants, r.Metadata, r.CreatedAt, r.ParticipantCount, participants);
        });

        roomsApi.MapDelete("/{roomId:guid}", (Guid roomId, RoomManager rooms) =>
        {
            rooms.DeleteRoom(roomId);
            return Results.NoContent();
        });

        var participantsApi = roomsApi.MapGroup("/{roomId:guid}/participants");

        participantsApi.MapPost("/", (Guid roomId, AddParticipantCommand body, RoomManager rooms) =>
        {
            var join = rooms.AddParticipant(roomId, body);
            var id = join.Participant.Id;
            return Results.Created(
                $"/api/rooms/{roomId}/participants/{id}",
                new JoinResponse(id, roomId, join.JoinToken, $"/signal/{id}", join.ExpiresAt));
        });

        participantsApi.MapGet("/", (Guid roomId, RoomManager rooms) => rooms.ListParticipants(roomId));

        participantsApi.MapGet("/{participantId:guid}", (Guid roomId, Guid participantId, RoomManager rooms) =>
            rooms.GetParticipant(roomId, participantId));

        participantsApi.MapPatch("/{participantId:guid}",
            (Guid roomId, Guid participantId, UpdateParticipantCommand body, RoomManager rooms) =>
                rooms.UpdateParticipant(roomId, participantId, body));

        participantsApi.MapDelete("/{participantId:guid}", (Guid roomId, Guid participantId, RoomManager rooms) =>
        {
            rooms.RemoveParticipant(roomId, participantId);
            return Results.NoContent();
        });
    }
}

/// <summary>When the process started, for <c>/api/status</c>.</summary>
public sealed class ServerClock(TimeProvider time)
{
    public DateTimeOffset StartedAt { get; } = time.GetUtcNow();

    public TimeSpan Uptime => time.GetUtcNow() - StartedAt;
}
