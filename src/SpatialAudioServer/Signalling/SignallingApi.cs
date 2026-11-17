// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using SpatialAudioServer.Media;
using SpatialAudioServer.Rooms;

namespace SpatialAudioServer.Signalling;

/// <param name="Trickle">
/// false: the answer waits (up to 5 s) for the server's ICE gathering and carries every candidate, for clients that
/// never fetch candidates (Second Life viewers). Default true: answer at once and trickle via GET /ice.
/// </param>
/// <param name="DisplayName">The name the participant chose, if it may choose one (invited guests: see config).</param>
/// <param name="InWorldAvatar">Asks to be shown in-world (see config's inWorldAvatars); the integration decides.</param>
public sealed record OfferRequest(string? Sdp, bool? Trickle = null, string? DisplayName = null, bool? InWorldAvatar = null);

public sealed record OfferResponse(string Sdp, string SessionToken);

public sealed record CandidateDto(string? Candidate, uint SdpMLineIndex);

/// <summary>What a client needs before it offers. <see cref="IceServers"/> is ready to pass to RTCPeerConnection.</summary>
/// <param name="Spatial">The room mixes by position.</param>
/// <param name="ClientPose">What the participant may change about its own pose (PATCH /signal/{id}/pose).</param>
/// <param name="DisplayName">The participant's current name.</param>
/// <param name="ClientNamed">It may choose its own name (send displayName with the offer).</param>
/// <param name="InWorldAvatars">Whether the room's integration shows web participants in-world: Optional = offer a
/// choice (send inWorldAvatar with the offer), Always = it will.</param>
public sealed record ClientConfig(
    IReadOnlyList<IceServerDto> IceServers, bool Spatial = false, ClientPose ClientPose = ClientPose.None,
    string? DisplayName = null, bool ClientNamed = false, InWorldAvatars InWorldAvatars = InWorldAvatars.Off);

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

    /// <summary>Browser offers are 2–10 KB; a viewer's (audio + data channel) about the same.</summary>
    public const int MaxSdpLength = 32 * 1024;

    public const int MaxCandidatesPerRequest = 50;

    /// <summary>An ICE candidate line is ~100–250 characters.</summary>
    public const int MaxCandidateLength = 1024;

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

        signal.MapGet("/config", (Guid participantId, HttpContext http, SignalSessions sessions, RoomManager rooms,
            Microsoft.Extensions.Options.IOptions<Configuration.MediaOptions> options, TimeProvider time) =>
        {
            var roomId = sessions.AuthorizeReadOnly(participantId, Bearer(http));
            var (room, _) = rooms.GetRoom(roomId);
            var me = rooms.GetParticipant(roomId, participantId);
            var now = time.GetUtcNow();
            return new ClientConfig(options.Value.IceServers
                .Select(s => s.CredentialsFor(participantId.ToString(), now) is var (user, credential)
                    ? new IceServerDto(s.Urls, user, credential) : null!).ToList(), room.Spatial, room.ClientPose,
                me.DisplayName, me.ClientNamed, room.InWorldAvatars);
        });

        // The participant's own pose, within the room's clientPose (today: turning only). Session token.
        signal.MapPatch("/pose", (Guid participantId, SelfPoseCommand body, HttpContext http, SignalSessions sessions, RoomManager rooms) =>
        {
            var roomId = sessions.Authorize(participantId, Bearer(http));
            rooms.UpdateOwnPose(roomId, participantId, body);
            return Results.NoContent();
        });

        signal.MapPost("/offer", async (Guid participantId, OfferRequest body, HttpContext http, SignalSessions sessions,
            IRoomMedia media, RoomManager rooms) =>
        {
            if (string.IsNullOrWhiteSpace(body.Sdp))
            {
                throw new ArgumentException("sdp is required.");
            }
            if (body.Sdp.Length > MaxSdpLength)
            {
                throw new ArgumentException($"sdp must be at most {MaxSdpLength} characters.");
            }
            if (body.DisplayName is not null)
            {
                // Checked before the offer uses up the one-time join token, so a refused name can be retried.
                rooms.RenameSelf(sessions.AuthorizeReadOnly(participantId, Bearer(http)), participantId, body.DisplayName);
            }
            if (body.InWorldAvatar is { } wanted)
            {
                rooms.RequestInWorldAvatar(sessions.AuthorizeReadOnly(participantId, Bearer(http)), participantId, wanted);
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
            if (body.Candidates is { Count: > MaxCandidatesPerRequest })
            {
                throw new ArgumentException($"At most {MaxCandidatesPerRequest} candidates per request.");
            }
            if (body.Candidates?.Any(c => c.Candidate?.Length > MaxCandidateLength) == true)
            {
                throw new ArgumentException($"A candidate must be at most {MaxCandidateLength} characters.");
            }
            var candidates = (body.Candidates ?? [])
                .Where(c => !string.IsNullOrWhiteSpace(c.Candidate))
                .Select(c => new IceCandidate(c.SdpMLineIndex, c.Candidate!))
                .ToList();
            media.AddRemoteCandidates(roomId, participantId, candidates);
            return Results.NoContent();
        });

        // Long-poll for the server's candidates from index `since`; returns early when there are new ones.
        // Ends at once (503) when the server starts shutting down, so open polls do not hold up the stop.
        signal.MapGet("/ice", async (Guid participantId, int? since, int? waitMs, HttpContext http,
            SignalSessions sessions, IRoomMedia media, RoomManager rooms, IHostApplicationLifetime lifetime) =>
        {
            var roomId = sessions.Authorize(participantId, Bearer(http));
            var wait = waitMs is { } ms ? TimeSpan.FromMilliseconds(Math.Clamp(ms, 0, (int)MaxWait.TotalMilliseconds)) : DefaultWait;
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted, lifetime.ApplicationStopping);
            CandidateBatch batch;
            try
            {
                batch = await media.WaitForLocalCandidatesAsync(roomId, participantId, Math.Max(0, since ?? 0), wait, cancel.Token);
            }
            catch (OperationCanceledException) when (lifetime.ApplicationStopping.IsCancellationRequested)
            {
                return Results.Problem("The server is shutting down.", statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            var state = rooms.GetParticipant(roomId, participantId).State;
            return Results.Ok(new CandidatesResponse(
                batch.Candidates.Select(c => new CandidateDto(c.Candidate, c.SdpMLineIndex)).ToList(), batch.Next, batch.Complete, state));
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
