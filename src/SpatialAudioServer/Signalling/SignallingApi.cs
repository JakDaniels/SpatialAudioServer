using SpatialAudioServer.Media;
using SpatialAudioServer.Rooms;

namespace SpatialAudioServer.Signalling;

public sealed record OfferRequest(string? Sdp);

public sealed record OfferResponse(string Sdp, string SessionToken);

public sealed record CandidateDto(string? Candidate, uint SdpMLineIndex);

public sealed record CandidatesRequest(List<CandidateDto>? Candidates, bool? Completed);

public sealed record CandidatesResponse(IReadOnlyList<CandidateDto> Candidates, int Next, bool Complete, ParticipantState State);

/// <summary>
/// Client signalling (PLAN.md "Client signalling"): HTTP offer/answer plus trickle ICE both ways. Authenticated by
/// the participant's join token (first offer) and then the session token, never by the control-API key.
/// </summary>
public static class SignallingApi
{
    private static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(30);

    public static void MapSignalling(this IEndpointRouteBuilder app)
    {
        var signal = app.MapGroup("/signal/{participantId:guid}");

        signal.MapPost("/offer", async (Guid participantId, OfferRequest body, HttpContext http, SignalSessions sessions, IRoomMedia media) =>
        {
            if (string.IsNullOrWhiteSpace(body.Sdp))
            {
                throw new ArgumentException("sdp is required.");
            }
            var (roomId, sessionToken) = sessions.AuthorizeOffer(participantId, Bearer(http));
            var answer = await media.AnswerOfferAsync(roomId, participantId, body.Sdp);
            return new OfferResponse(answer, sessionToken);
        });

        signal.MapPost("/ice", (Guid participantId, CandidatesRequest body, HttpContext http, SignalSessions sessions, IRoomMedia media) =>
        {
            var roomId = sessions.Authorize(participantId, Bearer(http));
            var candidates = (body.Candidates ?? [])
                .Where(c => !string.IsNullOrWhiteSpace(c.Candidate))
                .Select(c => new IceCandidate(c.SdpMLineIndex, c.Candidate!))
                .ToList();
            media.AddRemoteCandidates(roomId, participantId, candidates);
            return Results.NoContent();
        });

        // Long-poll for the server's candidates from index `since`; returns early when there are new ones.
        signal.MapGet("/ice", async (Guid participantId, int? since, int? waitMs, HttpContext http,
            SignalSessions sessions, IRoomMedia media, RoomManager rooms) =>
        {
            var roomId = sessions.Authorize(participantId, Bearer(http));
            var wait = waitMs is { } ms ? TimeSpan.FromMilliseconds(Math.Clamp(ms, 0, (int)MaxWait.TotalMilliseconds)) : DefaultWait;
            var batch = await media.WaitForLocalCandidatesAsync(roomId, participantId, since ?? 0, wait, http.RequestAborted);
            var state = rooms.GetParticipant(roomId, participantId).State;
            return new CandidatesResponse(
                batch.Candidates.Select(c => new CandidateDto(c.Candidate, c.SdpMLineIndex)).ToList(), batch.Next, batch.Complete, state);
        });

        signal.MapDelete("/", (Guid participantId, HttpContext http, SignalSessions sessions, RoomManager rooms) =>
        {
            var roomId = sessions.Authorize(participantId, Bearer(http));
            sessions.End(participantId);
            rooms.RemoveParticipant(roomId, participantId, "left");
            return Results.NoContent();
        });
    }

    private static string? Bearer(HttpContext http)
    {
        var header = http.Request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header["Bearer ".Length..].Trim() : null;
    }
}
