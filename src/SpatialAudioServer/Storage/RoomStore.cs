// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using static SpatialAudioServer.Storage.StoreHelpers;

namespace SpatialAudioServer.Storage;

public sealed record RoomRecord(
    Guid Id, string Name, string? ExternalId, int MaxParticipants, string? MetadataJson, DateTimeOffset CreatedAt, string? Engine = null,
    bool Spatial = false, string? ClientPose = null, string? InWorldAvatars = null, string? SpatialProfile = null,
    string? Acoustics = null);

/// <summary>Room definitions. Rooms persist until deleted and are restored (empty) at start-up.</summary>
public sealed class RoomStore(Database database)
{
    public void Insert(RoomRecord room)
    {
        using var connection = database.Open();
        using var command = connection.Command(
            "INSERT INTO rooms (id, name, external_id, max_participants, metadata, created_at, engine, spatial, client_pose, in_world_avatars, " +
            "spatial_profile, acoustics) VALUES ($id, $name, $external, $max, $metadata, $created, $engine, $spatial, $pose, $avatars, $profile, " +
            "$acoustics)",
            ("$id", room.Id.ToString()), ("$name", room.Name), ("$external", room.ExternalId),
            ("$max", room.MaxParticipants), ("$metadata", room.MetadataJson), ("$created", ToDb(room.CreatedAt)), ("$engine", room.Engine),
            ("$spatial", room.Spatial ? 1 : 0), ("$pose", room.ClientPose), ("$avatars", room.InWorldAvatars),
            ("$profile", room.SpatialProfile), ("$acoustics", room.Acoustics));
        command.ExecuteNonQuery();
    }

    public void UpdateInWorldAvatars(Guid id, string? value)
    {
        using var connection = database.Open();
        using var command = connection.Command("UPDATE rooms SET in_world_avatars = $avatars WHERE id = $id",
            ("$id", id.ToString()), ("$avatars", value));
        command.ExecuteNonQuery();
    }

    public void UpdateAcoustics(Guid id, string? value)
    {
        using var connection = database.Open();
        using var command = connection.Command("UPDATE rooms SET acoustics = $acoustics WHERE id = $id",
            ("$id", id.ToString()), ("$acoustics", value));
        command.ExecuteNonQuery();
    }

    public void UpdateEngine(Guid id, string engine, string? spatialProfile)
    {
        using var connection = database.Open();
        using var command = connection.Command("UPDATE rooms SET engine = $engine, spatial_profile = $profile WHERE id = $id",
            ("$id", id.ToString()), ("$engine", engine), ("$profile", spatialProfile));
        command.ExecuteNonQuery();
    }

    public void UpdateMetadata(Guid id, string? json)
    {
        using var connection = database.Open();
        using var command = connection.Command("UPDATE rooms SET metadata = $metadata WHERE id = $id",
            ("$id", id.ToString()), ("$metadata", json));
        command.ExecuteNonQuery();
    }

    public bool Delete(Guid id)
    {
        using var connection = database.Open();
        using var command = connection.Command("DELETE FROM rooms WHERE id = $id", ("$id", id.ToString()));
        return command.ExecuteNonQuery() > 0;
    }

    public IReadOnlyList<RoomRecord> All()
    {
        using var connection = database.Open();
        using var command = connection.Command(
            "SELECT id, name, external_id, max_participants, metadata, created_at, engine, spatial, client_pose, in_world_avatars, spatial_profile, " +
            "acoustics FROM rooms ORDER BY created_at");
        using var reader = command.ExecuteReader();
        var rooms = new List<RoomRecord>();
        while (reader.Read())
        {
            rooms.Add(new RoomRecord(
                Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetNullableString(2),
                reader.GetInt32(3), reader.GetNullableString(4), FromDb(reader.GetString(5)), reader.GetNullableString(6),
                reader.GetInt32(7) != 0, reader.GetNullableString(8), reader.GetNullableString(9),
                reader.GetNullableString(10), reader.GetNullableString(11)));
        }
        return rooms;
    }
}
