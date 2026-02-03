using System;
using System.Globalization;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Palco;

/// <summary>
/// Palco - A minimal caching plugin for Anfiteatro.
/// Provides simple key-value storage for client-side caching needs.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public const string PluginName = "Palco";
    public static Plugin? Instance { get; private set; }

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public override string Name => PluginName;

    public override Guid Id => Guid.Parse("a1b2c3d4-e5f6-7890-abcd-ef1234567890");

    public override string Description => "Minimal caching plugin for Anfiteatro. Stores arbitrary JSON data by key.";

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return Array.Empty<PluginPageInfo>();
    }
}
