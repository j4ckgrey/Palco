using MediaBrowser.Model.Plugins;

namespace Palco;

/// <summary>
/// Plugin configuration (minimal - just for future extensibility).
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Default cache expiry in hours. 0 = never expire.
    /// </summary>
    public int DefaultExpiryHours { get; set; } = 168; // 7 days

    /// <summary>
    /// Maximum cache size in MB. 0 = unlimited.
    /// </summary>
    public int MaxCacheSizeMB { get; set; } = 100;
}
