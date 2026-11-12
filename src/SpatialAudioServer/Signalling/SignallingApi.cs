using SpatialAudioServer.Media;
using SpatialAudioServer.Rooms;

namespace SpatialAudioServer.Signalling;

/// <param name="Trickle">
/// false: the answer waits (up to 5 s) for the server's ICE gathering and carries every candidate, for clients that
/// never fetch candidates (Second Life viewers). Default true: answer at once and trickle via GET /ice.
/// </param>
public sealed record OfferRequest(string? Sdp, bool? Trickle = null);

public sealed record OfferResponse(string Sdp, string SessionToken);

public sealed record CandidateDto(string? Candidate, uint SdpMLineIndex);

/// <summary>What a client needs before it offers. <see cref="IceServers"/> is ready to pass to RTCPeerConnection.</summary>
public sealed record ClientConfig(IReadOnlyList<IceServerDto> IceServers);

public sealed record IceServerDto(IReadOnlyList<string> Urls, string? Username, string? Credential);

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

    private static readonly TimeSpan GatheringTimeout = TimeSpan.FromSeconds(5);

    private static async Task<string> WithAllCandidatesAsync(
        IRoomMedia media, Guid roomId, Guid participantId, string answer, CancellationToken cancel)
    {
        var deadline = DateTime.UtcNow + GatheringTimeout;
        var candidates = new List<IceCandidate>();
        var complete = false;
        while (!complete && DateTime.UtcNow < deadline)
        {
            var batch = await media.WaitForLocalCandidatesAsync(roomId, participantId, candidates.Count,
                TimeSpan.FromTicks(Math.Max(0, (deadline - DateTime.UtcNow).Ticks)), cancel);
            candidates.AddRange(batch.Candidates);
            complete = batch.Complete;
        }
        return WebRtcBin.EmbedCandidates(answer, candidates, complete);
    }

    public static void MapSignalling(this IEndpointRouteBuilder app)
    {
        var signal = app.MapGroup("/signal/{participantId:guid}");

        signal.MapGet("/config", (Guid participantId, HttpContext http, SignalSessions sessions,
            Microsoft.Extensions.Options.IOptions<Configuration.MediaOptions> options) =>
        {
            sessions.AuthorizeReadOnly(participantId, Bearer(http));
            return new ClientConfig(options.Value.IceServers
                .Select(s => new IceServerDto(s.Urls, s.Username, s.Credential)).ToList());
        });

        signal.MapPost("/offer", async (Guid participantId, OfferRequest body, HttpContext http, SignalSessions sessions, IRoomMedia media) =>
        {
            if (string.IsNullOrWhiteSpace(body.Sdp))
            {
                throw new ArgumentException("sdp is required.");
            }
            var (roomId, sessionToken) = sessions.AuthorizeOffer(participantId, Bearer(http));
            var answer = await media.AnswerOfferAsync(roomId, participantId, body.Sdp);
            if (body.Trickle == false)
            {
                answer = await WithAllCandidatesAsync(media, roomId, participantId, answer, http.RequestAborted);
            }
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
