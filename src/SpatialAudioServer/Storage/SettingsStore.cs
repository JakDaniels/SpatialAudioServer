// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using static SpatialAudioServer.Storage.StoreHelpers;

namespace SpatialAudioServer.Storage;

/// <summary>Server settings kept in SQLite (key/value).</summary>
public sealed class SettingsStore(Database database)
{
    public const string AdminPasswordHash = "admin.password_hash";

    public string? Get(string key)
    {
        using var connection = database.Open();
        using var command = connection.Command("SELECT value FROM settings WHERE key = $key", ("$key", key));
        return command.ExecuteScalar() as string;
    }

    public void Set(string key, string value)
    {
        using var connection = database.Open();
        using var command = connection.Command(
            "INSERT INTO settings (key, value) VALUES ($key, $value) ON CONFLICT(key) DO UPDATE SET value = excluded.value",
            ("$key", key), ("$value", value));
        command.ExecuteNonQuery();
    }

    public bool Delete(string key)
    {
        using var connection = database.Open();
        using var command = connection.Command("DELETE FROM settings WHERE key = $key", ("$key", key));
        return command.ExecuteNonQuery() > 0;
    }
}
