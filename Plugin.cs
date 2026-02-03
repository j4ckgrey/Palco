using System;
using System.Globalization;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace Palco;

/// <summary>
/// Palco - A minimal caching plugin for Anfiteatro.
/// Provides simple key-value storage for client-side caching needs.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public const string PluginName = "Palco";
    public static Plugin? Instance { get; private set; }
    
    private readonly IApplicationPaths _applicationPaths;
    private CacheService? _cacheService;
    private readonly object _lock = new();
    
    /// <summary>
    /// Gets the CacheService singleton instance.
    /// Uses lazy initialization to avoid startup issues.
    /// </summary>
    public CacheService CacheService
    {
        get
        {
            if (_cacheService != null) return _cacheService;
            
            lock (_lock)
            {
                if (_cacheService != null) return _cacheService;
                
                // Use a null logger - we don't have access to Jellyfin's ILogger in plugin constructor
                // but the CacheService will still work, just without detailed logs
                var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<CacheService>.Instance;
                
                _cacheService = new CacheService(_applicationPaths, logger);
                return _cacheService;
            }
        }
    }

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        _applicationPaths = applicationPaths;
    }

    public override string Name => PluginName;

    public override Guid Id => Guid.Parse("a1b2c3d4-e5f6-7890-abcd-ef1234567890");

    public override string Description => "Minimal caching plugin for Anfiteatro. Stores arbitrary JSON data by key.";

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return Array.Empty<PluginPageInfo>();
    }
    
    /// <summary>
    /// Clean up resources when plugin is unloaded.
    /// </summary>
    public void DisposeCache()
    {
        _cacheService?.Dispose();
        _cacheService = null;
    }
}
