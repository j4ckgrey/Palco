// using Gelato; // Removed
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Linq;

namespace Palco;

/// <summary>
/// Registers Palco services with the DI container.
/// </summary>
public class ServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection services, IServerApplicationHost applicationHost)
    {
        services.AddSingleton<CacheService>(sp =>
        {
            var appPaths = sp.GetRequiredService<IApplicationPaths>();
            var logger = sp.GetRequiredService<ILogger<CacheService>>();
            return new CacheService(appPaths, logger);
        });

        services.AddSingleton<CatalogsService>(sp =>
        {
            var appPaths = sp.GetRequiredService<IApplicationPaths>();
            var logger = sp.GetRequiredService<ILogger<CatalogsService>>();
            return new CatalogsService(appPaths, logger);
        });

        services.AddSingleton<GelatoProxy>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<GelatoProxy>>();
            object? manager = null;
            try
            {
                // Scan loaded assemblies for Gelato
                var assembly = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == "Gelato");
                    
                if (assembly != null)
                {
                    var type = assembly.GetType("Gelato.GelatoManager");
                    if (type != null)
                    {
                        manager = sp.GetService(type);
                    }
                }
            }
            catch (Exception ex)
            {
                // Likely explicitly caught if Gelato is missing or throws on resolution
                logger.LogWarning(ex, "Failed to resolve GelatoManager");
            }
            return new GelatoProxy(manager, logger);
        });

        services.AddSingleton<CatalogsImportService>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<CatalogsImportService>>();
            var catalogsService = sp.GetRequiredService<CatalogsService>();
            var gelatoProxy = sp.GetRequiredService<GelatoProxy>(); // Changed
            var libraryManager = sp.GetRequiredService<ILibraryManager>();
            var collectionManager = sp.GetRequiredService<ICollectionManager>();
            return new CatalogsImportService(logger, catalogsService, gelatoProxy, libraryManager, collectionManager);
        });

        // Register the catalog sync scheduled task
        services.AddSingleton<IScheduledTask, CatalogSyncTask>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<CatalogSyncTask>>();
            var catalogsService = sp.GetRequiredService<CatalogsService>();
            var importService = sp.GetRequiredService<CatalogsImportService>();
            var gelatoProxy = sp.GetRequiredService<GelatoProxy>();
            var libraryManager = sp.GetRequiredService<ILibraryManager>();
            var collectionManager = sp.GetRequiredService<ICollectionManager>();
            var userManager = sp.GetRequiredService<IUserManager>();
            return new CatalogSyncTask(logger, catalogsService, importService, gelatoProxy, libraryManager, collectionManager, userManager);
        });
    }
}

