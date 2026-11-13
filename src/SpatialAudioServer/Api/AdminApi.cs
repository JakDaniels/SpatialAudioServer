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
                : MediaDisabled();
        });

        // Rendered with Graphviz on the server when it is installed (501 otherwise: use graph.dot).
        admin.MapGet("/rooms/{roomId:guid}/graph.svg", async (Guid roomId, RoomManager rooms, IRoomMedia media, CancellationToken cancel) =>
        {
            rooms.GetRoom(roomId);
            if (await media.PipelineDotAsync(roomId) is not { } dot)
            {
                return MediaDisabled();
            }
            var svg = await Graphviz.RenderSvgAsync(dot, cancel);
            return svg is null
                ? Results.Problem("Graphviz ('dot') is not installed on the server; download graph.dot instead.",
                    statusCode: StatusCodes.Status501NotImplemented)
                : Results.Text(svg, "image/svg+xml", System.Text.Encoding.UTF8);
        });
    }

    private static IResult MediaDisabled() =>
        Results.Problem("Media is disabled on this server (Media:Enabled=false).", statusCode: StatusCodes.Status404NotFound);
}

/// <summary>Renders .dot to SVG with Graphviz's <c>dot</c>, if it is on the PATH.</summary>
public static class Graphviz
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>The SVG, or null when <c>dot</c> is not installed.</summary>
    public static async Task<string?> RenderSvgAsync(string dot, CancellationToken cancel)
    {
        System.Diagnostics.Process process;
        try
        {
            process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("dot", "-Tsvg")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            }) ?? throw new InvalidOperationException("dot did not start.");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null; // not installed
        }
        using (process)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(Timeout);
            try
            {
                var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
                var errors = process.StandardError.ReadToEndAsync(timeout.Token);
                await process.StandardInput.WriteAsync(dot.AsMemory(), timeout.Token);
                process.StandardInput.Close();
                await process.WaitForExitAsync(timeout.Token);
                return process.ExitCode == 0
                    ? await output
                    : throw new InvalidOperationException($"dot failed ({process.ExitCode}): {await errors}");
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException($"dot did not finish within {Timeout.TotalSeconds:F0} s.");
            }
        }
    }
}
