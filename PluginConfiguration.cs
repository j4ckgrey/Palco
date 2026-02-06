using MediaBrowser.Model.Plugins;

namespace Palco;

/// <summary>
/// Palco plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the catalog sync interval in hours. Default is 6 hours.
    /// </summary>
    public int CatalogSyncIntervalHours { get; set; } = 6;

    /// <summary>
    /// Gets or sets whether catalog sync is enabled.
    /// </summary>
    public bool CatalogSyncEnabled { get; set; } = true;
}
