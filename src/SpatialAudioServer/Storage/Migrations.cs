using Microsoft.Data.Sqlite;

namespace SpatialAudioServer.Storage;

/// <summary>
/// Numbered schema migrations, tracked in <c>PRAGMA user_version</c>. Append new steps; never edit applied ones.
/// </summary>
internal static class Migrations
{
    private static readonly string[] Steps =
    [
        // 1: initial schema.
        """
        CREATE TABLE rooms (
            id               TEXT PRIMARY KEY,
            name             TEXT NOT NULL,
            external_id      TEXT,
            max_participants INTEGER NOT NULL,
            metadata         TEXT,
            created_at       TEXT NOT NULL
        );
        CREATE INDEX ix_rooms_external_id ON rooms(external_id);

        CREATE TABLE api_keys (
            id           TEXT PRIMARY KEY,
            label        TEXT NOT NULL,
            key_hash     TEXT NOT NULL UNIQUE,
            prefix       TEXT NOT NULL,
            created_at   TEXT NOT NULL,
            last_used_at TEXT,
            revoked_at   TEXT
        );

        CREATE TABLE join_tokens (
            token_hash     TEXT PRIMARY KEY,
            participant_id TEXT NOT NULL,
            room_id        TEXT NOT NULL,
            expires_at     TEXT NOT NULL,
            used_at        TEXT
        );
        CREATE INDEX ix_join_tokens_participant ON join_tokens(participant_id);

        CREATE TABLE sessions (
            participant_id TEXT PRIMARY KEY,
            room_id        TEXT NOT NULL,
            external_id    TEXT,
            display_name   TEXT NOT NULL,
            joined_at      TEXT NOT NULL,
            left_at        TEXT,
            leave_reason   TEXT
        );
        CREATE INDEX ix_sessions_room ON sessions(room_id);

        CREATE TABLE events (
            id             INTEGER PRIMARY KEY AUTOINCREMENT,
            at             TEXT NOT NULL,
            kind           TEXT NOT NULL,
            room_id        TEXT,
            participant_id TEXT,
            detail         TEXT
        );
        CREATE INDEX ix_events_at ON events(at);
        """,

        // 2: a room's externalId (e.g. OpenSim region/parcel UUID) is unique. NULLs stay allowed and distinct.
        // Fails if a database already holds duplicates; delete one of the rooms first.
        """
        DROP INDEX ix_rooms_external_id;
        CREATE UNIQUE INDEX ux_rooms_external_id ON rooms(external_id);
        """,

        // 3: each room's mix engine. NULL (rooms created before engines existed) means the server default.
        """
        ALTER TABLE rooms ADD COLUMN engine TEXT;
        """,

        // 4: spatial rooms mix by participant position (off for rooms created before).
        """
        ALTER TABLE rooms ADD COLUMN spatial INTEGER NOT NULL DEFAULT 0;
        """,

        // 5: server settings (key/value), e.g. the admin UI password hash.
        """
        CREATE TABLE settings (
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );
        """,

        // 6: what participants may change about their own pose (ClientPose name); NULL = the default for the room kind.
        """
        ALTER TABLE rooms ADD COLUMN client_pose TEXT;
        """,

        // 7: whether the integration shows web and bot participants in-world (InWorldAvatars name); NULL = Off.
        """
        ALTER TABLE rooms ADD COLUMN in_world_avatars TEXT;
        """,
    ];

    public static int LatestVersion => Steps.Length;

    public static int CurrentVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    public static void Apply(SqliteConnection connection)
    {
        var version = CurrentVersion(connection);
        if (version > Steps.Length)
        {
            throw new InvalidOperationException(
                $"Database schema version {version} is newer than this server ({Steps.Length}).");
        }

        for (var i = version; i < Steps.Length; i++)
        {
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = Steps[i] + $"\nPRAGMA user_version = {i + 1};";
            command.ExecuteNonQuery();
            transaction.Commit();
        }
    }
}
