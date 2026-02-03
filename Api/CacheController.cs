using System.ComponentModel.DataAnnotations;
using System.Net.Mime;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Palco.Api;

/// <summary>
/// Palco Cache API - Simple key-value storage for Anfiteatro.
/// </summary>
[ApiController]
[Route("Palco")]
[Authorize]
public class CacheController : ControllerBase
{
    private readonly CacheService _cache;
    private readonly ILogger<CacheController> _logger;

    public CacheController(CacheService cache, ILogger<CacheController> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    /// <summary>
    /// Get a cached value by key.
    /// </summary>
    /// <param name="key">Cache key</param>
    /// <param name="ns">Optional namespace</param>
    [HttpGet("Cache/{key}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<CacheEntry> Get([FromRoute] string key, [FromQuery] string ns = "")
    {
        var value = _cache.Get(key, ns);
        if (value == null)
        {
            return NotFound();
        }

        return Ok(new CacheEntry { Key = key, Value = value, Namespace = ns });
    }

    /// <summary>
    /// Set a cached value.
    /// </summary>
    [HttpPost("Cache/{key}")]
    [Consumes(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult Set(
        [FromRoute] string key,
        [FromBody] SetCacheRequest request,
        [FromQuery] string ns = "")
    {
        _cache.Set(key, request.Value, request.TtlSeconds, ns);
        return Ok(new { success = true });
    }

    /// <summary>
    /// Delete a cached value.
    /// </summary>
    [HttpDelete("Cache/{key}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult Delete([FromRoute] string key, [FromQuery] string ns = "")
    {
        var deleted = _cache.Delete(key, ns);
        return Ok(new { success = true, deleted });
    }

    /// <summary>
    /// Get multiple cached values.
    /// </summary>
    [HttpPost("Cache/Bulk")]
    [Consumes(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<Dictionary<string, string>> GetBulk(
        [FromBody] BulkGetRequest request,
        [FromQuery] string ns = "")
    {
        var results = _cache.GetBulk(request.Keys, ns);
        return Ok(results);
    }

    /// <summary>
    /// Delete all entries in a namespace.
    /// </summary>
    [HttpDelete("Cache/Namespace/{ns}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult DeleteNamespace([FromRoute] string ns)
    {
        var deleted = _cache.DeleteNamespace(ns);
        return Ok(new { success = true, deleted });
    }

    /// <summary>
    /// Clean expired cache entries.
    /// </summary>
    [HttpPost("Cache/Clean")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult CleanExpired()
    {
        var deleted = _cache.CleanExpired();
        return Ok(new { success = true, deleted });
    }

    /// <summary>
    /// Get cache statistics.
    /// </summary>
    [HttpGet("Cache/Stats")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<CacheStats> GetStats()
    {
        var (total, expired, size) = _cache.GetStats();
        return Ok(new CacheStats
        {
            TotalEntries = total,
            ExpiredEntries = expired,
            DatabaseSizeBytes = size,
            DatabaseSizeMB = Math.Round(size / 1024.0 / 1024.0, 2)
        });
    }
}

#region DTOs

public class CacheEntry
{
    public required string Key { get; set; }
    public required string Value { get; set; }
    public string Namespace { get; set; } = "";
}

public class SetCacheRequest
{
    /// <summary>
    /// JSON value to cache (stored as-is).
    /// </summary>
    [Required]
    public required string Value { get; set; }

    /// <summary>
    /// Time to live in seconds. 0 = never expire.
    /// </summary>
    public int TtlSeconds { get; set; } = 0;
}

public class BulkGetRequest
{
    [Required]
    public required IEnumerable<string> Keys { get; set; }
}

public class CacheStats
{
    public int TotalEntries { get; set; }
    public int ExpiredEntries { get; set; }
    public long DatabaseSizeBytes { get; set; }
    public double DatabaseSizeMB { get; set; }
}

#endregion
