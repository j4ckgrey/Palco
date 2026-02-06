using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
    private readonly GelatoProxy _gelatoProxy; // Changed from GelatoManager
    private readonly ILibraryManager _libraryManager;
    private readonly ICollectionManager _collectionManager;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeImports = new();

    public CatalogsImportService(
        ILogger<CatalogsImportService> logger,
        CatalogsService catalogsService,
        GelatoProxy gelatoProxy,
        ILibraryManager libraryManager,
        ICollectionManager collectionManager)
    {
        _logger = logger;
        _catalogsService = catalogsService;
        _gelatoProxy = gelatoProxy;
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
        var configId = request.CatalogId; // Use CatalogId as primary key
        
        // Create or update catalog config
        var config = _catalogsService.Get(configId);
        if (config == null)
        {
            config = new CatalogConfig
            {
                Id = configId, // Set ID to CatalogId
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
            if (!_gelatoProxy.IsAvailable)
            {
                _logger.LogWarning("[Palco] Gelato plugin is not available. Skipping import.");
                config.Status = "error";
                _catalogsService.Save(config);
                return;
            }

            // Get Stremio provider for this user
            var gelatoConfig = _gelatoProxy.GetConfig(userId); // Changed
            if (gelatoConfig == null)
            {
                _logger.LogWarning("[Palco] Could not get Gelato config for user {UserId}", userId);
                config.Status = "error";
                _catalogsService.Save(config);
                return;
            }

            var stremio = gelatoConfig.stremio;

            // Determine root folder
            var isSeries = request.Type.Equals("series", StringComparison.OrdinalIgnoreCase);
            var root = isSeries
                ? _gelatoProxy.TryGetSeriesFolder(userId) // Changed
                : _gelatoProxy.TryGetMovieFolder(userId); // Changed

            if (root == null)
            {
                _logger.LogWarning("[Palco] No {Type} folder configured for user {UserId}", 
                    request.Type, userId);
                config.Status = "error";
                _catalogsService.Save(config);
                return;
            }

            // Fetch catalog items
            var items = new List<dynamic>(); // Changed from StremioMeta
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

            // First ensure we have a collection to add to
            string? collectionId = request.CombinedCollectionId ?? config.CollectionId;
            var collectionName = request.CustomName ?? config.Name;

            // Verify if collection actually exists in library
            if (!string.IsNullOrEmpty(collectionId))
            {
                if (Guid.TryParse(collectionId, out var existingGuid))
                {
                    var existingCollection = _libraryManager.GetItemById(existingGuid);
                    if (existingCollection == null)
                    {
                        _logger.LogWarning("[Palco] Collection {Id} not found in library (deleted?), creating new one.", collectionId);
                        collectionId = null; // Reset so we create a new one
                    }
                }
                else
                {
                    collectionId = null;
                }
            }

            if (string.IsNullOrEmpty(collectionId))
            {
                // Create new collection upfront
                try 
                {
                    var collection = await _collectionManager.CreateCollectionAsync(
                        new CollectionCreationOptions
                        {
                            Name = collectionName
                        }
                    ).ConfigureAwait(false);

                    collectionId = collection?.Id.ToString("N");
                    
                    if (collection != null)
                    {
                        _logger.LogInformation("[Palco] Created collection '{Name}' with ID {Id}",
                            collectionName, collectionId);
                        
                        // Update config immediately so we don't lose the association
                        config.CollectionId = collectionId;
                        _catalogsService.Save(config);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[Palco] Failed to create collection '{Name}'", collectionName);
                    // Decide if we should continue? We can try to import anyway but they won't be collected.
                    // For now, let's proceed but maybe log a warning.
                }
            }

            // Import items in parallel batches
            var importedIds = new ConcurrentBag<Guid>();
            var failedCount = 0;
            var batchSize = 1; // Sequential execution to prevent DB locks/race conditions

            var opts = new ParallelOptions
            {
                MaxDegreeOfParallelism = batchSize,
                CancellationToken = ct
            };

            Guid? validCollectionGuid = null;
            if (!string.IsNullOrEmpty(collectionId) && Guid.TryParse(collectionId, out var parsedGuid))
            {
                validCollectionGuid = parsedGuid;
            }

            await Parallel.ForEachAsync(items, opts, async (meta, token) =>
            {
                try
                {
                    // Create a timeout token for this specific item
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                    timeoutCts.CancelAfter(TimeSpan.FromSeconds(60));

                    // meta is dynamic here
                    var result = await _gelatoProxy.InsertMeta(
                        root,
                        (object)meta,
                        null, // user
                        true, // allowRemoteRefresh
                        true, // refreshItem
                        false, // queueRefreshItem
                        false, // queueRefreshChildren
                        timeoutCts.Token
                    ).ConfigureAwait(false);
                    var item = result.Item;
                    var created = result.Created;

                    if (item != null)
                    {
                        importedIds.Add(item.Id);
                        
                        _logger.LogDebug("[Palco] Imported item {Id} at {Path}", item.Id, item.Path);

                        // Add to collection IMMEDIATELY
                        if (validCollectionGuid.HasValue)
                        {
                            try 
                            {
                                await _collectionManager.AddToCollectionAsync(
                                    validCollectionGuid.Value,
                                    new List<Guid> { item.Id }
                                ).ConfigureAwait(false);
                            }
                            catch (Exception ex) 
                            {
                                _logger.LogError(ex, "[Palco] Failed to add item {Id} to collection {ColId}", item.Id, validCollectionGuid);
                            }
                        }

                        // Update progress periodically - MOVED TO FINALLY BLOCK
                        // if (importedIds.Count % 10 == 0) ...
                    }
                    else
                    {
                        Interlocked.Increment(ref failedCount);
                    }
                }
                catch (Exception ex)
                {
                    // Accessing dynamic properties might throw if not present, but StremioMeta should have Id
                    var id = "unknown";
                    try { id = meta.Id.ToString(); } catch {}
                    
                    _logger.LogWarning("[Palco] Failed to import {MetaId}: {Message}", 
                        id, ex.Message);
                    Interlocked.Increment(ref failedCount);
                }
                finally
                {
                    // Update progress frequently (every item) to give immediate feedback to user
                    // Lock to ensure thread safety even though we are running sequentially (good practice)
                    lock (config) 
                    {
                        config.ImportedCount = importedIds.Count;
                        config.FailedCount = failedCount;
                        // Use a throttle or just save every time? 
                        // With sequential execution, saving every time is fine and gives best UX.
                        _catalogsService.Save(config);
                    }
                }
            });

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
