using Microsoft.Data.Sqlite;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Serialization;

namespace Palco;

public class CatalogConfig
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";
    
    [JsonPropertyName("manifestUrl")]
    public string ManifestUrl { get; set; } = "";
    
    [JsonPropertyName("catalogId")]
    public string CatalogId { get; set; } = "";
    
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";
    
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";
    
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;
    
    [JsonPropertyName("updateIntervalHours")]
    public int UpdateIntervalHours { get; set; } = 0;
    
    [JsonPropertyName("lastUpdated")]
    public DateTime? LastUpdated { get; set; }
    
    [JsonPropertyName("status")]
    public string Status { get; set; } = "idle";
    
    [JsonPropertyName("maxItems")]
    public int MaxItems { get; set; } = 100;
    
    [JsonPropertyName("importedCount")]
    public int ImportedCount { get; set; } = 0;
    
    [JsonPropertyName("failedCount")]
    public int FailedCount { get; set; } = 0;
    
    [JsonPropertyName("collectionId")]
    public string? CollectionId { get; set; }
    
    [JsonPropertyName("collectionItemCount")]
    public int CollectionItemCount { get; set; } = 0;
}

public class CatalogsService : IDisposable
{
    private readonly string _dbPath;
    private readonly ILogger<CatalogsService> _logger;
    private SqliteConnection? _connection;
    private readonly object _lock = new();

    public CatalogsService(IApplicationPaths appPaths, ILogger<CatalogsService> logger)
    {
        _logger = logger;
        var pluginDataPath = Path.Combine(appPaths.DataPath, "Palco");
        Directory.CreateDirectory(pluginDataPath);
        _dbPath = Path.Combine(pluginDataPath, "catalogs.db");
        Initialize();
    }

    private void Initialize()
    {
        _connection = new SqliteConnection($"Data Source={_dbPath}");
        _connection.Open();

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS catalogs (
                id TEXT PRIMARY KEY,
                manifest_url TEXT NOT NULL,
                catalog_id TEXT NOT NULL,
                name TEXT NOT NULL,
                type TEXT NOT NULL,
                enabled INTEGER DEFAULT 1,
                update_interval_hours INTEGER DEFAULT 0,
                last_updated TEXT,
                status TEXT DEFAULT 'idle',
                max_items INTEGER DEFAULT 100,
                imported_count INTEGER DEFAULT 0,
                failed_count INTEGER DEFAULT 0,
                collection_id TEXT
            );
        ";
        cmd.ExecuteNonQuery();
        _logger.LogInformation("[Palco] Catalogs DB initialized at {Path}", _dbPath);
    }

    public List<CatalogConfig> GetAll()
    {
        lock (_lock)
        {
            var result = new List<CatalogConfig>();
            if (_connection == null) return result;

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT * FROM catalogs ORDER BY name";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result.Add(ReadCatalog(reader));
            }
            return result;
        }
    }

    public CatalogConfig? Get(string id)
    {
        lock (_lock)
        {
            if (_connection == null) return null;

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT * FROM catalogs WHERE id = @id";
            cmd.Parameters.AddWithValue("@id", id);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ReadCatalog(reader) : null;
        }
    }

    public void Save(CatalogConfig config)
    {
        lock (_lock)
        {
            if (_connection == null) return;

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                INSERT OR REPLACE INTO catalogs (
                    id, manifest_url, catalog_id, name, type, enabled, 
                    update_interval_hours, last_updated, status, max_items, 
                    imported_count, failed_count, collection_id
                ) VALUES (
                    @id, @manifest_url, @catalog_id, @name, @type, @enabled, 
                    @interval, @last_updated, @status, @max_items, 
                    @imported, @failed, @collection_id
                )
            ";
            
            cmd.Parameters.AddWithValue("@id", config.Id);
            cmd.Parameters.AddWithValue("@manifest_url", config.ManifestUrl);
            cmd.Parameters.AddWithValue("@catalog_id", config.CatalogId);
            cmd.Parameters.AddWithValue("@name", config.Name);
            cmd.Parameters.AddWithValue("@type", config.Type);
            cmd.Parameters.AddWithValue("@enabled", config.Enabled ? 1 : 0);
            cmd.Parameters.AddWithValue("@interval", config.UpdateIntervalHours);
            cmd.Parameters.AddWithValue("@last_updated", config.LastUpdated.HasValue ? config.LastUpdated.Value.ToString("O") : DBNull.Value);
            cmd.Parameters.AddWithValue("@status", config.Status);
            cmd.Parameters.AddWithValue("@max_items", config.MaxItems);
            cmd.Parameters.AddWithValue("@imported", config.ImportedCount);
            cmd.Parameters.AddWithValue("@failed", config.FailedCount);
            cmd.Parameters.AddWithValue("@collection_id", config.CollectionId ?? (object)DBNull.Value);

            cmd.ExecuteNonQuery();
        }
    }

    public void Delete(string id)
    {
        lock (_lock)
        {
            if (_connection == null) return;

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM catalogs WHERE id = @id";
            cmd.Parameters.AddWithValue("@id", id);
            cmd.ExecuteNonQuery();
        }
    }

    private CatalogConfig ReadCatalog(SqliteDataReader reader)
    {
        return new CatalogConfig
        {
            Id = reader.GetString(0),
            ManifestUrl = reader.GetString(1),
            CatalogId = reader.GetString(2),
            Name = reader.GetString(3),
            Type = reader.GetString(4),
            Enabled = reader.GetInt32(5) == 1,
            UpdateIntervalHours = reader.GetInt32(6),
            LastUpdated = !reader.IsDBNull(7) ? DateTime.Parse(reader.GetString(7)) : null,
            Status = !reader.IsDBNull(8) ? reader.GetString(8) : "idle",
            MaxItems = !reader.IsDBNull(9) ? reader.GetInt32(9) : 100,
            ImportedCount = !reader.IsDBNull(10) ? reader.GetInt32(10) : 0,
            FailedCount = !reader.IsDBNull(11) ? reader.GetInt32(11) : 0,
            CollectionId = !reader.IsDBNull(12) ? reader.GetString(12) : null
        };
    }

    public void Dispose()
    {
        _connection?.Close();
        _connection?.Dispose();
    }
}
