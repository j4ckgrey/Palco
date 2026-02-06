using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MediaBrowser.Controller.Entities;

namespace Palco;

public class GelatoProxy
{
    private readonly dynamic? _manager;
    private readonly dynamic? _pluginInstance;
    private readonly ILogger<GelatoProxy> _logger;

    public bool IsAvailable => _manager != null && _pluginInstance != null;

    public GelatoProxy(object? manager, ILogger<GelatoProxy> logger)
    {
        _manager = manager;
        _logger = logger;
        
        // Find GelatoPlugin instance
        try 
        {
            var assembly = manager?.GetType().Assembly ?? 
                           AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Gelato");

            if (assembly != null)
            {
                var pluginType = assembly.GetType("Gelato.GelatoPlugin");
                if (pluginType != null)
                {
                    var instanceProp = pluginType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                    _pluginInstance = instanceProp?.GetValue(null);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to resolve Gelato plugin: {Message}", ex.Message);
        }
    }

    public dynamic? GetConfig(Guid userId)
    {
        if (_pluginInstance == null) return null;
        try 
        {
            return _pluginInstance.GetConfig(userId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting Gelato config");
            return null;
        }
    }

    public Folder? TryGetSeriesFolder(Guid userId)
    {
        if (_manager == null) return null;
        try
        {
            return _manager.TryGetSeriesFolder(userId) as Folder;
        }
        catch 
        { 
            return null; 
        }
    }

    public Folder? TryGetMovieFolder(Guid userId)
    {
        if (_manager == null) return null;
        try
        {
            return _manager.TryGetMovieFolder(userId) as Folder;
        }
        catch 
        { 
            return null; 
        }
    }

    public async Task<(BaseItem? Item, bool Created)> InsertMeta(
        Folder parent,
        dynamic meta,
        dynamic? user,
        bool allowRemoteRefresh,
        bool refreshItem,
        bool queueRefreshItem,
        bool queueRefreshChildren,
        CancellationToken cancellationToken)
    {
        if (_manager == null) return (null, false);

        try
        {
            // Dynamic dispatch handles correct overload resolution
            var task = (Task)_manager.InsertMeta(
                parent,
                meta,
                user,
                allowRemoteRefresh,
                refreshItem,
                queueRefreshItem,
                queueRefreshChildren,
                cancellationToken
            );

            await task.ConfigureAwait(false);

            // Reflection needed only to extract ValueTuple items from dynamic Task result
            // (dynamic await might implicitly unpack, but unwrapping ValueTuple dynamically can be tricky)
            dynamic result = ((dynamic)task).Result;
            return (result.Item1, result.Item2);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calling InsertMeta");
            return (null, false);
        }
    }
}
