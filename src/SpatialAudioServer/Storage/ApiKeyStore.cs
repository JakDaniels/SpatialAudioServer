using static SpatialAudioServer.Storage.StoreHelpers;

namespace SpatialAudioServer.Storage;

public sealed record ApiKeyRecord(
    Guid Id, string Label, string Prefix, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt, DateTimeOffset? RevokedAt);

/// <summary>Control-API keys created at run time. Only a hash and a short display prefix are stored.</summary>
public sealed class ApiKeyStore(Database database, TimeProvider time)
{
    public const string KeyPrefix = "sas_";

    /// <summary>Creates a key and returns it in plain text; this is the only time it is available.</summary>
    public (ApiKeyRecord Record, string Key) Create(string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        var key = Secrets.NewToken(KeyPrefix);
        var record = new ApiKeyRecord(Guid.NewGuid(), label.Trim(), key[..12], time.GetUtcNow(), null, null);
        using var connection = database.Open();
        using var command = connection.Command(
            "INSERT INTO api_keys (id, label, key_hash, prefix, created_at) VALUES ($id, $label, $hash, $prefix, $created)",
            ("$id", record.Id.ToString()), ("$label", record.Label), ("$hash", Secrets.Hash(key)),
            ("$prefix", record.Prefix), ("$created", ToDb(record.CreatedAt)));
        command.ExecuteNonQuery();
        return (record, key);
    }

    public IReadOnlyList<ApiKeyRecord> List()
    {
        using var connection = database.Open();
        using var command = connection.Command(
            "SELECT id, label, prefix, created_at, last_used_at, revoked_at FROM api_keys ORDER BY created_at");
        using var reader = command.ExecuteReader();
        var keys = new List<ApiKeyRecord>();
        while (reader.Read())
        {
            keys.Add(new ApiKeyRecord(
                Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2),
                FromDb(reader.GetString(3)), FromDbNullable(reader, 4), FromDbNullable(reader, 5)));
        }
        return keys;
    }

    /// <summary>Revokes by id or display prefix. Returns the number of keys revoked.</summary>
    public int Revoke(string idOrPrefix)
    {
        using var connection = database.Open();
        using var command = connection.Command(
            "UPDATE api_keys SET revoked_at = $now WHERE revoked_at IS NULL AND (id = $value OR prefix = $value)",
            ("$now", ToDb(time.GetUtcNow())), ("$value", idOrPrefix));
        return command.ExecuteNonQuery();
    }

    /// <summary>The active key with this hash, or null.</summary>
    public ApiKeyRecord? FindActive(string keyHash)
    {
        using var connection = database.Open();
        using var command = connection.Command(
            "SELECT id, label, prefix, created_at, last_used_at FROM api_keys WHERE key_hash = $hash AND revoked_at IS NULL",
            ("$hash", keyHash));
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new ApiKeyRecord(
                Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2),
                FromDb(reader.GetString(3)), FromDbNullable(reader, 4), null)
            : null;
    }

    public void Touch(Guid id)
    {
        using var connection = database.Open();
        using var command = connection.Command(
            "UPDATE api_keys SET last_used_at = $now WHERE id = $id", ("$now", ToDb(time.GetUtcNow())), ("$id", id.ToString()));
        command.ExecuteNonQuery();
    }
}
