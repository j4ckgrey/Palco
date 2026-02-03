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
    
    // Keys that can be accessed without authentication (for registration)
    private static readonly HashSet<string> PublicReadableKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "registration-enabled"
    };
    
    // Namespaces/prefixes that allow anonymous write for registration requests
    private const string RegistrationNamespace = "anfiteatro-registration";

    public CacheController(CacheService cache, ILogger<CacheController> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    // ====================================================================
    // PUBLIC REGISTRATION ENDPOINTS (No Auth Required)
    // ====================================================================
    
    /// <summary>
    /// Check if registration is enabled (public endpoint).
    /// </summary>
    [HttpGet("Registration/Enabled")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> GetRegistrationEnabled()
    {
        try
        {
            var value = _cache.Get("registration-enabled", RegistrationNamespace);
            
            // Default to enabled if no value set
            if (string.IsNullOrEmpty(value))
            {
                return Ok(new { enabled = true });
            }
            
            // Try to parse as JSON boolean or check for string "true"/"false"
            try
            {
                var enabled = JsonSerializer.Deserialize<bool>(value);
                return Ok(new { enabled });
            }
            catch
            {
                // Fallback: check string value
                var enabled = value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                              value.Equals("\"true\"", StringComparison.OrdinalIgnoreCase);
                return Ok(new { enabled });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Palco] Error checking registration enabled status");
            return Ok(new { enabled = true }); // Default to enabled on error
        }
    }
    
    /// <summary>
    /// Submit a registration request (public endpoint).
    /// Only allows writing to 'request-*' keys in the registration namespace.
    /// </summary>
    [HttpPost("Registration/Request")]
    [AllowAnonymous]
    [Consumes(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult SubmitRegistrationRequest([FromBody] RegistrationRequestPayload request)
    {
        try
        {
            if (string.IsNullOrEmpty(request?.Id) || !request.Id.StartsWith("request-"))
            {
                return BadRequest(new { error = "Invalid request ID format" });
            }
            
            // Store the registration request
            _cache.Set(request.Id, request.Data, request.TtlSeconds, RegistrationNamespace);
            
            // Update the requests index
            var indexJson = _cache.Get("requests-index", RegistrationNamespace);
            var index = new List<string>();
            
            if (!string.IsNullOrEmpty(indexJson))
            {
                try
                {
                    index = JsonSerializer.Deserialize<List<string>>(indexJson) ?? new List<string>();
                }
                catch
                {
                    index = new List<string>();
                }
            }
            
            // Extract the ID without prefix for the index
            var requestId = request.Id.Replace("request-", "");
            if (!index.Contains(requestId))
            {
                index.Add(requestId);
                _cache.Set("requests-index", JsonSerializer.Serialize(index), request.TtlSeconds, RegistrationNamespace);
            }
            
            return Ok(new { success = true, requestId });
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    // ====================================================================
    // AUTHENTICATED ENDPOINTS
    // ====================================================================

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
        try
        {
            var value = _cache.Get(key, ns);
            if (value == null)
            {
                return NotFound();
            }

            return Ok(new CacheEntry { Key = key, Value = value, Namespace = ns });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Palco] Error getting cache key {Key} in namespace {Namespace}", key, ns);
            return NotFound();
        }
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
        try
        {
            _cache.Set(key, request.Value, request.TtlSeconds, ns);
            return Ok(new { success = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Palco] Error setting cache key {Key} in namespace {Namespace}", key, ns);
            return Ok(new { success = false, error = "Failed to set cache value" });
        }
    }

    /// <summary>
    /// Delete a cached value.
    /// </summary>
    [HttpDelete("Cache/{key}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult Delete([FromRoute] string key, [FromQuery] string ns = "")
    {
        try
        {
            var deleted = _cache.Delete(key, ns);
            return Ok(new { success = true, deleted });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Palco] Error deleting cache key {Key} in namespace {Namespace}", key, ns);
            return Ok(new { success = false, deleted = false });
        }
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
        try
        {
            var results = _cache.GetBulk(request.Keys, ns);
            return Ok(results);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Palco] Error getting bulk cache in namespace {Namespace}", ns);
            return Ok(new Dictionary<string, string>());
        }
    }

    /// <summary>
    /// Delete all entries in a namespace.
    /// </summary>
    [HttpDelete("Cache/Namespace/{ns}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult DeleteNamespace([FromRoute] string ns)
    {
        try
        {
            var deleted = _cache.DeleteNamespace(ns);
            return Ok(new { success = true, deleted });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Palco] Error deleting namespace {Namespace}", ns);
            return Ok(new { success = false, deleted = 0 });
        }
    }

    /// <summary>
    /// Clean expired cache entries.
    /// </summary>
    [HttpPost("Cache/Clean")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult CleanExpired()
    {
        try
        {
            var deleted = _cache.CleanExpired();
            return Ok(new { success = true, deleted });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Palco] Error cleaning expired cache entries");
            return Ok(new { success = false, deleted = 0 });
        }
    }

    /// <summary>
    /// Get cache statistics.
    /// </summary>
    [HttpGet("Cache/Stats")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<CacheStats> GetStats()
    {
        try
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Palco] Error getting cache stats");
            return Ok(new CacheStats
            {
                TotalEntries = 0,
                ExpiredEntries = 0,
                DatabaseSizeBytes = 0,
                DatabaseSizeMB = 0
            });
        }
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

public class RegistrationRequestPayload
{
    /// <summary>
    /// Request ID (must start with 'request-').
    /// </summary>
    [Required]
    public required string Id { get; set; }
    
    /// <summary>
    /// JSON data for the registration request.
    /// </summary>
    [Required]
    public required string Data { get; set; }
    
    /// <summary>
    /// Time to live in seconds.
    /// </summary>
    public int TtlSeconds { get; set; } = 2592000; // 30 days default
}

#endregion
