using static SpatialAudioServer.Storage.StoreHelpers;

namespace SpatialAudioServer.Storage;

public sealed record RoomRecord(
    Guid Id, string Name, string? ExternalId, int MaxParticipants, string? MetadataJson, DateTimeOffset CreatedAt, string? Engine = null);

/// <summary>Room definitions. Rooms persist until deleted and are restored (empty) at start-up.</summary>
public sealed class RoomStore(Database database)
{
    public void Insert(RoomRecord room)
    {
        using var connection = database.Open();
        using var command = connection.Command(
            "INSERT INTO rooms (id, name, external_id, max_participants, metadata, created_at, engine) " +
            "VALUES ($id, $name, $external, $max, $metadata, $created, $engine)",
            ("$id", room.Id.ToString()), ("$name", room.Name), ("$external", room.ExternalId),
            ("$max", room.MaxParticipants), ("$metadata", room.MetadataJson), ("$created", ToDb(room.CreatedAt)), ("$engine", room.Engine));
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
            "SELECT id, name, external_id, max_participants, metadata, created_at, engine FROM rooms ORDER BY created_at");
        using var reader = command.ExecuteReader();
        var rooms = new List<RoomRecord>();
        while (reader.Read())
        {
            rooms.Add(new RoomRecord(
                Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetNullableString(2),
                reader.GetInt32(3), reader.GetNullableString(4), FromDb(reader.GetString(5)), reader.GetNullableString(6)));
        }
        return rooms;
    }
}
