// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using SpatialAudioServer.Rooms;
using SpatialAudioServer.Storage;

namespace SpatialAudioServer.Signalling;

/// <summary>
/// Signalling authorisation. The client's first offer presents the one-time join token (issued when the participant
/// was added), which is redeemed and exchanged for a session token; every later signalling call (ICE, re-offer,
/// leave) presents the session token. Session tokens live in memory only: participants do not survive a restart.
/// </summary>
public sealed class SignalSessions(JoinTokenStore joinTokens, RoomManager rooms)
{
    private readonly ConcurrentDictionary<Guid, Session> _sessions = new();

    /// <summary>
    /// Authorises an offer by join token or session token. Returns the room and the session token the client must
    /// use from now on.
    /// </summary>
    public (Guid RoomId, string SessionToken) AuthorizeOffer(Guid participantId, string? bearer)
    {
        if (string.IsNullOrEmpty(bearer))
        {
            throw new UnauthorizedAccessException("A join token or session token is required.");
        }
        if (TryAuthorizeSession(participantId, bearer) is { } existing)
        {
            return (existing.RoomId, bearer);
        }

        var claim = joinTokens.Redeem(bearer);
        if (claim is null || claim.ParticipantId != participantId)
        {
            throw new UnauthorizedAccessException("The token is not valid for this participant (unknown, expired or already used).");
        }
        rooms.GetParticipant(claim.RoomId, participantId); // 404 if removed meanwhile

        var token = Secrets.NewToken();
        _sessions[participantId] = new Session(claim.RoomId, Hash(token));
        return (claim.RoomId, token);
    }

    /// <summary>Authorises a call by session token; returns the participant's room.</summary>
    public Guid Authorize(Guid participantId, string? bearer) =>
        (string.IsNullOrEmpty(bearer) ? null : TryAuthorizeSession(participantId, bearer))?.RoomId
        ?? throw new UnauthorizedAccessException("A valid session token is required (from the offer response).");

    /// <summary>
    /// Authorises a read-only call before or after the first offer: an unused join token (not consumed) or the
    /// session token. Returns the participant's room.
    /// </summary>
    public Guid AuthorizeReadOnly(Guid participantId, string? bearer)
    {
        if (!string.IsNullOrEmpty(bearer))
        {
            if (TryAuthorizeSession(participantId, bearer) is { } session)
            {
                return session.RoomId;
            }
            if (joinTokens.Peek(bearer) is { } claim && claim.ParticipantId == participantId)
            {
                return claim.RoomId;
            }
        }
        throw new UnauthorizedAccessException("A join token or session token is required.");
    }

    public void End(Guid participantId) => _sessions.TryRemove(participantId, out _);

    private Session? TryAuthorizeSession(Guid participantId, string bearer)
    {
        if (!_sessions.TryGetValue(participantId, out var session)
            || !CryptographicOperations.FixedTimeEquals(session.TokenHash, Hash(bearer)))
        {
            return null;
        }
        try
        {
            rooms.GetParticipant(session.RoomId, participantId);
        }
        catch (NotFoundException)
        {
            // The participant was removed or replaced: the session is over.
            End(participantId);
            throw;
        }
        return session;
    }

    private static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    private sealed record Session(Guid RoomId, byte[] TokenHash);
}
