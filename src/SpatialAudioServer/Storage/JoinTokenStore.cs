using static SpatialAudioServer.Storage.StoreHelpers;

namespace SpatialAudioServer.Storage;

public sealed record JoinTokenClaim(Guid ParticipantId, Guid RoomId);

/// <summary>
/// One-time join tokens handed to a participant's client for signalling. Stored hashed. Issued in Phase 1;
/// redeemed by the signalling endpoints in Phase 3.
/// </summary>
public sealed class JoinTokenStore(Database database, TimeProvider time)
{
    public (string Token, DateTimeOffset ExpiresAt) Issue(Guid participantId, Guid roomId, TimeSpan lifetime)
    {
        var token = Secrets.NewToken();
        var expires = time.GetUtcNow() + lifetime;
        using var connection = database.Open();
        using var command = connection.Command(
            "INSERT INTO join_tokens (token_hash, participant_id, room_id, expires_at) VALUES ($hash, $pid, $room, $expires)",
            ("$hash", Secrets.Hash(token)), ("$pid", participantId.ToString()), ("$room", roomId.ToString()),
            ("$expires", ToDb(expires)));
        command.ExecuteNonQuery();
        return (token, expires);
    }

    /// <summary>
    /// Marks the token used and returns who it was issued to; null if unknown, expired or already used.
    /// The update is a single statement, so two concurrent redemptions cannot both succeed.
    /// </summary>
    public JoinTokenClaim? Redeem(string token)
    {
        using var connection = database.Open();
        using var command = connection.Command(
            "UPDATE join_tokens SET used_at = $now " +
            "WHERE token_hash = $hash AND used_at IS NULL AND expires_at > $now " +
            "RETURNING participant_id, room_id",
            ("$now", ToDb(time.GetUtcNow())), ("$hash", Secrets.Hash(token)));
        using var reader = command.ExecuteReader();
        return reader.Read() ? new JoinTokenClaim(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1))) : null;
    }

    /// <summary>Who the token was issued to, if it is still redeemable; does not use it up.</summary>
    public JoinTokenClaim? Peek(string token)
    {
        using var connection = database.Open();
        using var command = connection.Command(
            "SELECT participant_id, room_id FROM join_tokens WHERE token_hash = $hash AND used_at IS NULL AND expires_at > $now",
            ("$now", ToDb(time.GetUtcNow())), ("$hash", Secrets.Hash(token)));
        using var reader = command.ExecuteReader();
        return reader.Read() ? new JoinTokenClaim(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1))) : null;
    }

    public void DeleteForParticipant(Guid participantId)
    {
        using var connection = database.Open();
        using var command = connection.Command(
            "DELETE FROM join_tokens WHERE participant_id = $pid", ("$pid", participantId.ToString()));
        command.ExecuteNonQuery();
    }

    /// <summary>Removes every token: participants are not restored after a restart, so their tokens are dead.</summary>
    public void DeleteAll()
    {
        using var connection = database.Open();
        using var command = connection.Command("DELETE FROM join_tokens");
        command.ExecuteNonQuery();
    }
}
