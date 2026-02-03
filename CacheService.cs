using Microsoft.Data.Sqlite;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Palco;

/// <summary>
/// SQLite-backed cache service. Simple key-value storage with optional TTL.
/// </summary>
public class CacheService : IDisposable
{
    private readonly string _dbPath;
    private readonly ILogger<CacheService> _logger;
    private SqliteConnection? _connection;
    private readonly object _lock = new();

    public CacheService(IApplicationPaths appPaths, ILogger<CacheService> logger)
    {
        _logger = logger;
        var pluginDataPath = Path.Combine(appPaths.PluginsPath, "Palco");
        Directory.CreateDirectory(pluginDataPath);
        _dbPath = Path.Combine(pluginDataPath, "cache.db");
        InitializeDatabase();
    }

    private void InitializeDatabase()
    {
        lock (_lock)
        {
            try
            {
                _connection = new SqliteConnection($"Data Source={_dbPath}");
                _connection.Open();

                using var cmd = _connection.CreateCommand();
                cmd.CommandText = @"
                    CREATE TABLE IF NOT EXISTS cache (
                        key TEXT PRIMARY KEY,
                        value TEXT NOT NULL,
                        created_at INTEGER NOT NULL,
                        expires_at INTEGER,
                        namespace TEXT DEFAULT ''
                    );
                    CREATE INDEX IF NOT EXISTS idx_namespace ON cache(namespace);
                    CREATE INDEX IF NOT EXISTS idx_expires ON cache(expires_at);
                ";
                cmd.ExecuteNonQuery();

                _logger.LogInformation("[Palco] Cache database initialized at {Path}", _dbPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Palco] Failed to initialize cache database at {Path}", _dbPath);
                _connection = null;
            }
        }
    }

    /// <summary>
    /// Get a cached value by key.
    /// </summary>
    public string? Get(string key, string ns = "")
    {
        lock (_lock)
        {
            if (_connection == null) return null;

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                SELECT value, expires_at FROM cache 
                WHERE key = @key AND namespace = @ns
            ";
            cmd.Parameters.AddWithValue("@key", key);
            cmd.Parameters.AddWithValue("@ns", ns);

            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                var expiresAt = reader.IsDBNull(1) ? (long?)null : reader.GetInt64(1);
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                // Check expiry
                if (expiresAt.HasValue && expiresAt.Value < now)
                {
                    // Expired - delete and return null
                    Delete(key, ns);
                    return null;
                }

                return reader.GetString(0);
            }

            return null;
        }
    }

    /// <summary>
    /// Set a cached value.
    /// </summary>
    /// <param name="key">Cache key</param>
    /// <param name="value">JSON value to store</param>
    /// <param name="ttlSeconds">Time to live in seconds. 0 = never expire.</param>
    /// <param name="ns">Optional namespace for grouping</param>
    public void Set(string key, string value, int ttlSeconds = 0, string ns = "")
    {
        lock (_lock)
        {
            if (_connection == null) return;

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long? expiresAt = ttlSeconds > 0 ? now + ttlSeconds : null;

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                INSERT OR REPLACE INTO cache (key, value, created_at, expires_at, namespace)
                VALUES (@key, @value, @created, @expires, @ns)
            ";
            cmd.Parameters.AddWithValue("@key", key);
            cmd.Parameters.AddWithValue("@value", value);
            cmd.Parameters.AddWithValue("@created", now);
            cmd.Parameters.AddWithValue("@expires", expiresAt.HasValue ? expiresAt.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("@ns", ns);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Delete a cached value.
    /// </summary>
    public bool Delete(string key, string ns = "")
    {
        lock (_lock)
        {
            if (_connection == null) return false;

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM cache WHERE key = @key AND namespace = @ns";
            cmd.Parameters.AddWithValue("@key", key);
            cmd.Parameters.AddWithValue("@ns", ns);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>
    /// Get multiple cached values by keys.
    /// </summary>
    public Dictionary<string, string> GetBulk(IEnumerable<string> keys, string ns = "")
    {
        var result = new Dictionary<string, string>();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        lock (_lock)
        {
            if (_connection == null) return result;

            foreach (var key in keys)
            {
                var value = Get(key, ns);
                if (value != null)
                {
                    result[key] = value;
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Delete all entries in a namespace.
    /// </summary>
    public int DeleteNamespace(string ns)
    {
        lock (_lock)
        {
            if (_connection == null) return 0;

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM cache WHERE namespace = @ns";
            cmd.Parameters.AddWithValue("@ns", ns);
            return cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Clean up expired entries.
    /// </summary>
    public int CleanExpired()
    {
        lock (_lock)
        {
            if (_connection == null) return 0;

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM cache WHERE expires_at IS NOT NULL AND expires_at < @now";
            cmd.Parameters.AddWithValue("@now", now);
            var deleted = cmd.ExecuteNonQuery();

            if (deleted > 0)
            {
                _logger.LogInformation("[Palco] Cleaned {Count} expired cache entries", deleted);
            }

            return deleted;
        }
    }

    /// <summary>
    /// Get cache statistics.
    /// </summary>
    public (int totalEntries, int expiredEntries, long dbSizeBytes) GetStats()
    {
        lock (_lock)
        {
            if (_connection == null) return (0, 0, 0);

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            using var cmd1 = _connection.CreateCommand();
            cmd1.CommandText = "SELECT COUNT(*) FROM cache";
            var total = Convert.ToInt32(cmd1.ExecuteScalar());

            using var cmd2 = _connection.CreateCommand();
            cmd2.CommandText = "SELECT COUNT(*) FROM cache WHERE expires_at IS NOT NULL AND expires_at < @now";
            cmd2.Parameters.AddWithValue("@now", now);
            var expired = Convert.ToInt32(cmd2.ExecuteScalar());

            var dbSize = File.Exists(_dbPath) ? new FileInfo(_dbPath).Length : 0;

            return (total, expired, dbSize);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _connection?.Close();
            _connection?.Dispose();
            _connection = null;
        }
    }
}
