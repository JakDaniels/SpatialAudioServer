using SpatialAudioServer.Media;
using SpatialAudioServer.Rooms;
using SpatialAudioServer.Storage;

namespace SpatialAudioServer.Api;

/// <summary>
/// Admin-only endpoints (admin session cookie; API keys are not enough): the event log, session history, live
/// media statistics and the pipeline graph. Everything else the admin UI does goes through the control API, which
/// an admin session may also use.
/// </summary>
public static class AdminApi
{
    public static void MapAdminApi(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/admin").RequireAuthorization(AdminAuthentication.AdminPolicy);

        // Newest first. ?room= one room's events, ?before= older than that event id (paging), ?limit= 1–500.
        admin.MapGet("/events", (ActivityLog activity, Guid? room, long? before, int? limit) =>
            activity.RecentEvents(Math.Clamp(limit ?? 100, 1, 500), room, before));

        admin.MapGet("/rooms/{roomId:guid}/sessions", (Guid roomId, RoomManager rooms, ActivityLog activity) =>
        {
            rooms.GetRoom(roomId); // 404 for an unknown room
            return activity.Sessions(roomId);
        });

        // Engine counters plus each participant's input level and connection statistics (poll it: ~1/s is fine).
        admin.MapGet("/rooms/{roomId:guid}/live", async (Guid roomId, RoomManager rooms, IRoomMedia media) =>
        {
            rooms.GetRoom(roomId);
            return await media.LiveStatsAsync(roomId) is { } live ? Results.Ok(live) : MediaDisabled();
        });

        admin.MapGet("/rooms/{roomId:guid}/graph.dot", async (Guid roomId, RoomManager rooms, IRoomMedia media) =>
        {
            rooms.GetRoom(roomId);
            return await media.PipelineDotAsync(roomId) is { } dot
                ? Results.File(System.Text.Encoding.UTF8.GetBytes(dot), "text/vnd.graphviz", $"room-{roomId.ToString()[..8]}.dot")
                : NoPipeline();
        });

        // The pipeline view (/admin/graph.html): elements, pads and links, top-level elements tagged with their summary
        // block. ?expand=path (repeatable) also lists that bin's children.
        admin.MapGet("/rooms/{roomId:guid}/graph", async (Guid roomId, string[]? expand, RoomManager rooms, IRoomMedia media) =>
        {
            rooms.GetRoom(roomId);
            if (expand is { Length: > MaxExpanded })
            {
                return Results.Problem($"At most {MaxExpanded} bins can be expanded.", statusCode: StatusCodes.Status400BadRequest);
            }
            return await media.PipelineGraphAsync(roomId, (expand ?? []).ToHashSet(StringComparer.Ordinal)) is { } graph
                ? Results.Ok(graph)
                : NoPipeline();
        });

        // Every readable property of one element (?path= as in the graph).
        admin.MapGet("/rooms/{roomId:guid}/graph/element", async (Guid roomId, string path, RoomManager rooms, IRoomMedia media) =>
        {
            rooms.GetRoom(roomId);
            return await media.ElementDetailsAsync(roomId, path) is { } details
                ? Results.Ok(details)
                : Results.Problem($"No element '{path}' (or the room has no pipeline).", statusCode: StatusCodes.Status404NotFound);
        });
    }

    private const int MaxExpanded = 200;

    private static IResult NoPipeline() =>
        Results.Problem("The room has no pipeline: nobody is in it (media runs only while it has participants), or media is " +
            "disabled on this server.", statusCode: StatusCodes.Status404NotFound);

    private static IResult MediaDisabled() =>
        Results.Problem("Media is disabled on this server (Media:Enabled=false).", statusCode: StatusCodes.Status404NotFound);
}
