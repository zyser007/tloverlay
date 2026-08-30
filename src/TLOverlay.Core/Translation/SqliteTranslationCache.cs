using Microsoft.Data.Sqlite;

namespace TLOverlay.Core.Translation;

/// <summary>
/// Persistent translation cache.
///
/// Worth the disk: a local model costs hundreds of milliseconds to seconds per
/// line, and story dialogue is re-read constantly (replays, menus, NPCs you walk
/// past twice). Surviving a restart means the second playthrough is instant.
/// </summary>
public sealed class SqliteTranslationCache : ITranslationCache, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly object _gate = new();
    private bool _disposed;

    public SqliteTranslationCache(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        string? directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString());

        _connection.Open();
        Initialize(_connection);
    }

    private SqliteTranslationCache()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        Initialize(_connection);
    }

    /// <summary>Creates an in-memory instance, used by tests.</summary>
    public static SqliteTranslationCache CreateInMemory() => new();

    public bool TryGet(string key, out string value)
    {
        ArgumentNullException.ThrowIfNull(key);
        value = string.Empty;

        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT target FROM translations WHERE key = $key LIMIT 1;";
            command.Parameters.AddWithValue("$key", key);

            object? result = command.ExecuteScalar();
            if (result is string text)
            {
                value = text;
                return true;
            }

            return false;
        }
    }

    public void Set(string key, string value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            using var command = _connection.CreateCommand();
            command.CommandText =
                "INSERT INTO translations (key, target, created_utc) " +
                "VALUES ($key, $target, $created) " +
                "ON CONFLICT(key) DO UPDATE SET target = excluded.target, created_utc = excluded.created_utc;";
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$target", value);
            command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            command.ExecuteNonQuery();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _connection.Dispose();
        }
    }

    private static void Initialize(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "CREATE TABLE IF NOT EXISTS translations (" +
            "  key         TEXT PRIMARY KEY," +
            "  target      TEXT NOT NULL," +
            "  created_utc INTEGER NOT NULL);";
        command.ExecuteNonQuery();
    }
}
