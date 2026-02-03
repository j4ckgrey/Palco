using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging.Abstractions;

namespace Palco;

/// <summary>
/// Palco - Minimal cache plugin for Anfiteatro.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public static Plugin? Instance { get; private set; }
    
    private readonly IApplicationPaths _appPaths;
    private CacheService? _cacheService;

    public CacheService CacheService => _cacheService ??= new CacheService(_appPaths, NullLogger<CacheService>.Instance);

    public Plugin(IApplicationPaths appPaths, IXmlSerializer xmlSerializer) : base(appPaths, xmlSerializer)
    {
        Instance = this;
        _appPaths = appPaths;
    }

    public override string Name => "Palco";
    public override Guid Id => Guid.Parse("a1b2c3d4-e5f6-7890-abcd-ef1234567890");
    public override string Description => "Minimal cache for Anfiteatro";

    public IEnumerable<PluginPageInfo> GetPages() => Array.Empty<PluginPageInfo>();
}
