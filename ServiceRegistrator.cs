using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
    }
}
