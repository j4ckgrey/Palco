using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Palco;

/// <summary>
/// Scheduled task that syncs catalog collections with their source catalogs.
/// Removes items that no longer exist in the catalog and adds new ones.
/// </summary>
public class CatalogSyncTask : IScheduledTask, IConfigurableScheduledTask
{
    private readonly ILogger<CatalogSyncTask> _logger;
    private readonly CatalogsService _catalogsService;
    private readonly CatalogsImportService _importService;
    private readonly GelatoProxy _gelatoProxy;
    private readonly ILibraryManager _libraryManager;
    private readonly ICollectionManager _collectionManager;
    private readonly IUserManager _userManager;

    public CatalogSyncTask(
        ILogger<CatalogSyncTask> logger,
        CatalogsService catalogsService,
        CatalogsImportService importService,
        GelatoProxy gelatoProxy,
        ILibraryManager libraryManager,
        ICollectionManager collectionManager,
        IUserManager userManager)
    {
        _logger = logger;
        _catalogsService = catalogsService;
        _importService = importService;
        _gelatoProxy = gelatoProxy;
        _libraryManager = libraryManager;
        _collectionManager = collectionManager;
        _userManager = userManager;
    }

    /// <inheritdoc />
    public string Name => "Sync Catalog Collections";

    /// <inheritdoc />
    public string Key => "PalcoCatalogSync";

    /// <inheritdoc />
    public string Description => "Syncs imported catalog collections with their source catalogs. " +
        "Removes items that no longer exist and adds new ones up to the configured max.";

    /// <inheritdoc />
    public string Category => "Palco";

    /// <inheritdoc />
    public bool IsHidden => false;

    /// <inheritdoc />
    public bool IsEnabled => Plugin.Instance?.Configuration?.CatalogSyncEnabled ?? true;

    /// <inheritdoc />
    public bool IsLogged => true;

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        var intervalHours = Plugin.Instance?.Configuration?.CatalogSyncIntervalHours ?? 6;
        
        return new[]
        {
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromHours(intervalHours).Ticks
            }
        };
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        _logger.LogInformation("[Palco] Starting catalog sync task");
        
        if (!_gelatoProxy.IsAvailable)
        {
            _logger.LogWarning("[Palco] Gelato not available, skipping catalog sync");
            return;
        }

        var catalogs = _catalogsService.GetAll();
        if (catalogs.Count == 0)
        {
            _logger.LogInformation("[Palco] No catalogs configured, nothing to sync");
            return;
        }

        var totalCatalogs = catalogs.Count;
        var processedCatalogs = 0;

        foreach (var catalog in catalogs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await SyncCatalogAsync(catalog, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Palco] Failed to sync catalog {CatalogId}: {Message}", 
                    catalog.CatalogId, ex.Message);
            }

            processedCatalogs++;
            progress.Report((double)processedCatalogs / totalCatalogs * 100);
        }

        _logger.LogInformation("[Palco] Catalog sync task completed. Processed {Count} catalogs.", totalCatalogs);
    }

    private async Task SyncCatalogAsync(CatalogConfig catalog, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(catalog.CollectionId))
        {
            _logger.LogDebug("[Palco] Catalog {Id} has no collection, skipping sync", catalog.CatalogId);
            return;
        }

        if (!Guid.TryParse(catalog.CollectionId, out var collectionGuid))
        {
            _logger.LogWarning("[Palco] Invalid collection ID for catalog {Id}", catalog.CatalogId);
            return;
        }

        var collection = _libraryManager.GetItemById(collectionGuid);
        if (collection == null)
        {
            _logger.LogWarning("[Palco] Collection {Id} not found for catalog {CatalogId}", 
                catalog.CollectionId, catalog.CatalogId);
            return;
        }

        _logger.LogInformation("[Palco] Syncing catalog {Name} ({CatalogId})", catalog.Name, catalog.CatalogId);

        // Get current items in the collection
        var collectionItems = _libraryManager.GetItemList(new InternalItemsQuery
        {
            Parent = collection,
            Recursive = false
        });

        var currentItemIds = new HashSet<string>();
        foreach (var item in collectionItems)
        {
            // Try to get the Stremio ID from provider IDs
            item.ProviderIds.TryGetValue(MetadataProvider.Imdb.ToString(), out var imdbId);
            item.ProviderIds.TryGetValue(MetadataProvider.Tmdb.ToString(), out var tmdbId);
            item.ProviderIds.TryGetValue("Stremio", out var stremioId);

            var id = stremioId ?? imdbId ?? tmdbId ?? item.Id.ToString();
            currentItemIds.Add(id);
        }

        // We need a user ID for Gelato calls - try to find the first admin user
        var adminUserId = GetFirstAdminUserId();
        if (!adminUserId.HasValue)
        {
            _logger.LogWarning("[Palco] No admin user found, cannot sync catalog {Id}", catalog.CatalogId);
            return;
        }

        var gelatoConfig = _gelatoProxy.GetConfig(adminUserId.Value);
        if (gelatoConfig == null)
        {
            _logger.LogWarning("[Palco] Could not get Gelato config for admin user");
            return;
        }

        var stremio = gelatoConfig.stremio;

        // Fetch current catalog items
        var catalogItemIds = new HashSet<string>();
        var catalogItems = new List<dynamic>();
        var skip = 0;

        while (catalogItems.Count < catalog.MaxItems)
        {
            ct.ThrowIfCancellationRequested();

            var page = await stremio.GetCatalogMetasAsync(
                catalog.CatalogId,
                catalog.Type,
                search: null,
                skip: skip
            ).ConfigureAwait(false);

            if (page == null || page.Count == 0)
                break;

            foreach (var meta in page)
            {
                if (catalogItems.Count >= catalog.MaxItems)
                    break;
                
                catalogItems.Add(meta);
                
                // Get IDs from metadata
                string? metaId = null;
                try { metaId = meta.Id?.ToString(); } catch { }
                string? metaImdb = null;
                try { metaImdb = meta.ImdbId?.ToString() ?? meta.imdb_id?.ToString(); } catch { }
                
                if (!string.IsNullOrEmpty(metaId))
                    catalogItemIds.Add(metaId);
                if (!string.IsNullOrEmpty(metaImdb))
                    catalogItemIds.Add(metaImdb);
            }

            skip += page.Count;
            if (page.Count < 20)
                break;
        }

        _logger.LogDebug("[Palco] Catalog {Id} has {Count} items, collection has {CollectionCount}",
            catalog.CatalogId, catalogItemIds.Count, currentItemIds.Count);

        // Find items to remove (in collection but not in catalog)
        var itemsToRemove = new List<Guid>();
        foreach (var item in collectionItems)
        {
            item.ProviderIds.TryGetValue(MetadataProvider.Imdb.ToString(), out var imdbId);
            item.ProviderIds.TryGetValue(MetadataProvider.Tmdb.ToString(), out var tmdbId);
            item.ProviderIds.TryGetValue("Stremio", out var stremioId);
            
            // Check if any of the item's IDs exist in the catalog
            var existsInCatalog = 
                (!string.IsNullOrEmpty(stremioId) && catalogItemIds.Contains(stremioId)) ||
                (!string.IsNullOrEmpty(imdbId) && catalogItemIds.Contains(imdbId)) ||
                (!string.IsNullOrEmpty(tmdbId) && catalogItemIds.Contains(tmdbId));

            if (!existsInCatalog)
            {
                itemsToRemove.Add(item.Id);
                _logger.LogDebug("[Palco] Item {Name} ({Id}) no longer in catalog, will remove from collection",
                    item.Name, item.Id);
            }
        }

        // Remove items that no longer exist in catalog
        if (itemsToRemove.Count > 0)
        {
            _logger.LogInformation("[Palco] Removing {Count} items from collection {Name}",
                itemsToRemove.Count, catalog.Name);

            await _collectionManager.RemoveFromCollectionAsync(collectionGuid, itemsToRemove).ConfigureAwait(false);
        }

        // Find items to add (in catalog but not in collection)
        var currentCount = collectionItems.Count - itemsToRemove.Count;
        var slotsAvailable = catalog.MaxItems - currentCount;

        if (slotsAvailable > 0)
        {
            // Determine root folder
            var isSeries = catalog.Type.Equals("series", StringComparison.OrdinalIgnoreCase);
            var root = isSeries
                ? _gelatoProxy.TryGetSeriesFolder(adminUserId.Value)
                : _gelatoProxy.TryGetMovieFolder(adminUserId.Value);

            if (root != null)
            {
                var addedCount = 0;

                foreach (var meta in catalogItems)
                {
                    if (addedCount >= slotsAvailable)
                        break;

                    ct.ThrowIfCancellationRequested();

                    // Check if this item already exists in collection
                    string? metaId = null;
                    string? metaImdb = null;
                    try { metaId = meta.Id?.ToString(); } catch { }
                    try { metaImdb = meta.ImdbId?.ToString() ?? meta.imdb_id?.ToString(); } catch { }

                    var alreadyInCollection = currentItemIds.Any(id =>
                        (!string.IsNullOrEmpty(metaId) && id == metaId) ||
                        (!string.IsNullOrEmpty(metaImdb) && id == metaImdb));

                    if (alreadyInCollection)
                        continue;

                    try
                    {
                        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        timeoutCts.CancelAfter(TimeSpan.FromSeconds(60));

                        var result = await _gelatoProxy.InsertMeta(
                            root,
                            (object)meta,
                            null,
                            true,
                            true,
                            false,
                            false,
                            timeoutCts.Token
                        ).ConfigureAwait(false);

                        if (result.Item != null)
                        {
                            await _collectionManager.AddToCollectionAsync(
                                collectionGuid,
                                new List<Guid> { result.Item.Id }
                            ).ConfigureAwait(false);

                            addedCount++;
                            _logger.LogDebug("[Palco] Added {Name} to collection", result.Item.Name);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning("[Palco] Failed to add item to collection: {Message}", ex.Message);
                    }
                }

                if (addedCount > 0)
                {
                    _logger.LogInformation("[Palco] Added {Count} new items to collection {Name}",
                        addedCount, catalog.Name);
                }
            }
        }

        // Update catalog config
        catalog.LastUpdated = DateTime.UtcNow;
        catalog.CollectionItemCount = _importService.GetCollectionItemCount(catalog.CollectionId);
        _catalogsService.Save(catalog);
    }

    private Guid? GetFirstAdminUserId()
    {
        try
        {
            // Get the first user - typically the admin who set up the server
            var firstUser = _userManager.Users.FirstOrDefault();
            return firstUser?.Id;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Palco] Failed to get user ID");
        }

        return null;
    }
}
