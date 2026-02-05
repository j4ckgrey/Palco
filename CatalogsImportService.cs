using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Gelato;
using Gelato.Configuration;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Palco;

/// <summary>
/// Request to import a single catalog.
/// </summary>
public class CatalogImportRequest
{
    public string CatalogId { get; set; } = "";
    public string Type { get; set; } = "movie";
    public int MaxItems { get; set; } = 100;
    public string? CustomName { get; set; }
    public string? CombinedCollectionId { get; set; }
    public string ManifestUrl { get; set; } = "";
}

/// <summary>
/// Bulk import request containing multiple catalogs.
/// </summary>
public class BulkImportRequest
{
    public List<CatalogImportRequest> Catalogs { get; set; } = new();
    public Guid UserId { get; set; }
}

/// <summary>
/// Service for handling background catalog imports.
/// </summary>
public class CatalogsImportService
{
    private readonly ILogger<CatalogsImportService> _logger;
    private readonly CatalogsService _catalogsService;
    private readonly GelatoManager _gelatoManager;
    private readonly ILibraryManager _libraryManager;
    private readonly ICollectionManager _collectionManager;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeImports = new();

    public CatalogsImportService(
        ILogger<CatalogsImportService> logger,
        CatalogsService catalogsService,
        GelatoManager gelatoManager,
        ILibraryManager libraryManager,
        ICollectionManager collectionManager)
    {
        _logger = logger;
        _catalogsService = catalogsService;
        _gelatoManager = gelatoManager;
        _libraryManager = libraryManager;
        _collectionManager = collectionManager;
    }

    /// <summary>
    /// Start bulk import of catalogs in the background.
    /// </summary>
    public Task BulkImportAsync(BulkImportRequest request, CancellationToken ct = default)
    {
        // Fire and forget - but we still want to handle errors
        _ = Task.Run(async () =>
        {
            foreach (var catalogRequest in request.Catalogs)
            {
                try
                {
                    await ImportCatalogAsync(catalogRequest, request.UserId, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("[Palco] Import cancelled for {CatalogId}", catalogRequest.CatalogId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[Palco] Import failed for {CatalogId}: {Message}", 
                        catalogRequest.CatalogId, ex.Message);
                }
            }
        }, ct);

        return Task.CompletedTask;
    }

    private async Task ImportCatalogAsync(CatalogImportRequest request, Guid userId, CancellationToken ct)
    {
        var configId = $"{request.CatalogId}:{request.Type}";
        
        // Create or update catalog config
        var config = _catalogsService.Get(configId);
        if (config == null)
        {
            config = new CatalogConfig
            {
                Id = Guid.NewGuid().ToString("N"),
                ManifestUrl = request.ManifestUrl,
                CatalogId = request.CatalogId,
                Name = request.CustomName ?? request.CatalogId,
                Type = request.Type,
                MaxItems = request.MaxItems,
                Status = "importing",
                ImportedCount = 0,
                FailedCount = 0
            };
        }
        else
        {
            config.Status = "importing";
            config.MaxItems = request.MaxItems;
            if (request.CustomName != null)
                config.Name = request.CustomName;
        }
        _catalogsService.Save(config);

        _logger.LogInformation("[Palco] Starting import for {CatalogId} ({Type}), max {MaxItems} items",
            request.CatalogId, request.Type, request.MaxItems);

        try
        {
            // Get Stremio provider for this user
            var gelatoConfig = GelatoPlugin.Instance!.GetConfig(userId);
            var stremio = gelatoConfig.stremio;

            // Determine root folder
            var isSeries = request.Type.Equals("series", StringComparison.OrdinalIgnoreCase);
            var root = isSeries
                ? _gelatoManager.TryGetSeriesFolder(userId)
                : _gelatoManager.TryGetMovieFolder(userId);

            if (root == null)
            {
                _logger.LogWarning("[Palco] No {Type} folder configured for user {UserId}", 
                    request.Type, userId);
                config.Status = "error";
                _catalogsService.Save(config);
                return;
            }

            // Fetch catalog items
            var items = new List<StremioMeta>();
            var skip = 0;

            while (items.Count < request.MaxItems)
            {
                ct.ThrowIfCancellationRequested();

                var page = await stremio.GetCatalogMetasAsync(
                    request.CatalogId,
                    request.Type,
                    search: null,
                    skip: skip
                ).ConfigureAwait(false);

                if (page == null || page.Count == 0)
                    break;

                foreach (var meta in page)
                {
                    if (items.Count >= request.MaxItems)
                        break;
                    items.Add(meta);
                }

                skip += page.Count;
                if (page.Count < 20) // Likely last page
                    break;
            }

            _logger.LogInformation("[Palco] Fetched {Count} items from catalog {CatalogId}",
                items.Count, request.CatalogId);

            // Import items in parallel batches
            var importedIds = new ConcurrentBag<Guid>();
            var failedCount = 0;
            var batchSize = 4;

            var opts = new ParallelOptions
            {
                MaxDegreeOfParallelism = batchSize,
                CancellationToken = ct
            };

            await Parallel.ForEachAsync(items, opts, async (meta, token) =>
            {
                try
                {
                    var (item, created) = await _gelatoManager.InsertMeta(
                        root,
                        meta,
                        null, // user
                        true, // allowRemoteRefresh
                        true, // refreshItem
                        false, // queueRefreshItem
                        false, // queueRefreshChildren
                        token
                    ).ConfigureAwait(false);

                    if (item != null)
                    {
                        importedIds.Add(item.Id);
                        
                        // Update progress periodically
                        if (importedIds.Count % 10 == 0)
                        {
                            config.ImportedCount = importedIds.Count;
                            config.FailedCount = failedCount;
                            _catalogsService.Save(config);
                        }
                    }
                    else
                    {
                        Interlocked.Increment(ref failedCount);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("[Palco] Failed to import {MetaId}: {Message}", 
                        meta.Id, ex.Message);
                    Interlocked.Increment(ref failedCount);
                }
            });

            // Create or update collection
            string? collectionId = request.CombinedCollectionId ?? config.CollectionId;
            var collectionName = request.CustomName ?? config.Name;

            if (importedIds.Count > 0)
            {
                if (string.IsNullOrEmpty(collectionId))
                {
                    // Create new collection
                    var collection = await _collectionManager.CreateCollectionAsync(
                        new CollectionCreationOptions
                        {
                            Name = collectionName
                        }
                    ).ConfigureAwait(false);

                    collectionId = collection?.Id.ToString("N");
                    _logger.LogInformation("[Palco] Created collection '{Name}' with ID {Id}",
                        collectionName, collectionId);
                }

                // Add items to collection
                if (!string.IsNullOrEmpty(collectionId) && Guid.TryParse(collectionId, out var colGuid))
                {
                    await _collectionManager.AddToCollectionAsync(
                        colGuid,
                        importedIds.ToList()
                    ).ConfigureAwait(false);

                    _logger.LogInformation("[Palco] Added {Count} items to collection {Id}",
                        importedIds.Count, collectionId);
                }
            }

            // Final update
            config.Status = "idle";
            config.ImportedCount = importedIds.Count;
            config.FailedCount = failedCount;
            config.CollectionId = collectionId;
            config.LastUpdated = DateTime.UtcNow;
            _catalogsService.Save(config);

            _logger.LogInformation("[Palco] Import complete for {CatalogId}: {Imported} imported, {Failed} failed",
                request.CatalogId, importedIds.Count, failedCount);
        }
        catch (Exception ex)
        {
            config.Status = "error";
            _catalogsService.Save(config);
            throw;
        }
    }

    /// <summary>
    /// Get item count in a collection.
    /// </summary>
    public int GetCollectionItemCount(string collectionId)
    {
        if (!Guid.TryParse(collectionId, out var colGuid))
            return 0;

        var collection = _libraryManager.GetItemById(colGuid);
        if (collection == null)
            return 0;

        var children = _libraryManager.GetItemList(new InternalItemsQuery
        {
            Parent = collection,
            Recursive = false
        });

        return children?.Count ?? 0;
    }

    /// <summary>
    /// Cancel an active import.
    /// </summary>
    public bool CancelImport(string catalogId)
    {
        if (_activeImports.TryRemove(catalogId, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
            return true;
        }
        return false;
    }
}
