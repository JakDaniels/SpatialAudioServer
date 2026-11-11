using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace SpatialAudioServer.Storage;

internal static class StoreHelpers
{
    /// <summary>Timestamps are stored as round-trip UTC strings, which also sort correctly as text.</summary>
    public static string ToDb(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    public static DateTimeOffset FromDb(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    public static DateTimeOffset? FromDbNullable(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : FromDb(reader.GetString(ordinal));

    public static string? GetNullableString(this SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    public static SqliteCommand Command(this SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
        return command;
    }
}

/// <summary>Random secrets and their stored form.</summary>
public static class Secrets
{
    /// <summary>A 256-bit random value, base64url without padding.</summary>
    public static string NewToken(string prefix = "") =>
        prefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>
    /// SHA-256 hex. Adequate for stored API keys and join tokens because they are high-entropy random values,
    /// not user-chosen passwords (the admin password will use a slow KDF).
    /// </summary>
    public static string Hash(string secret) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
}
