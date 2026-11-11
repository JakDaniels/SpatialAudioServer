using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using SpatialAudioServer.Configuration;

namespace SpatialAudioServer.Storage;

/// <summary>
/// SQLite connection factory. The schema is migrated on first use, so every caller (server or CLI) sees the
/// current schema.
/// </summary>
public sealed class Database
{
    private readonly string _connectionString;
    private readonly Lock _migrateLock = new();
    private bool _migrated;

    public Database(IOptions<StorageOptions> options, IHostEnvironment environment)
        : this(System.IO.Path.Combine(environment.ContentRootPath, options.Value.DatabasePath))
    {
    }

    public Database(string path)
    {
        Path = System.IO.Path.GetFullPath(path);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
        }.ToString();
    }

    public string Path { get; }

    public SqliteConnection Open()
    {
        EnsureMigrated();
        return OpenRaw();
    }

    public int SchemaVersion
    {
        get
        {
            using var connection = Open();
            return Migrations.CurrentVersion(connection);
        }
    }

    private void EnsureMigrated()
    {
        if (Volatile.Read(ref _migrated))
        {
            return;
        }
        lock (_migrateLock)
        {
            if (_migrated)
            {
                return;
            }
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            using var connection = OpenRaw();
            using (var wal = connection.CreateCommand())
            {
                wal.CommandText = "PRAGMA journal_mode=WAL;";
                wal.ExecuteNonQuery();
            }
            Migrations.Apply(connection);
            Volatile.Write(ref _migrated, true);
        }
    }

    private SqliteConnection OpenRaw()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }
}
