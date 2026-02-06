using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Mvc;
using MediaBrowser.Controller.Net;

namespace Palco.Api
{
    [ApiController]
    [Route("Palco/Catalogs")]
    public class CatalogsController : ControllerBase
    {
        private readonly CatalogsService _service;
        private readonly CatalogsImportService _importService;

        public CatalogsController(CatalogsService service, CatalogsImportService importService)
        {
            _service = service;
            _importService = importService;
        }

        [HttpGet]
        public ActionResult<List<CatalogConfig>> GetAll()
        {
            var configs = _service.GetAll();
            
            // Enrich with actual collection counts from library
            foreach (var config in configs)
            {
                if (!string.IsNullOrEmpty(config.CollectionId))
                {
                    config.CollectionItemCount = _importService.GetCollectionItemCount(config.CollectionId);
                }
            }
            
            return Ok(configs);
        }

        [HttpGet("{id}")]
        public ActionResult<CatalogConfig> Get(string id)
        {
            var config = _service.Get(id);
            if (config == null) return NotFound();
            return Ok(config);
        }

        [HttpPost]
        public ActionResult<CatalogConfig> Save([FromBody] CatalogConfig config)
        {
            if (string.IsNullOrEmpty(config.Id))
            {
                config.Id = Guid.NewGuid().ToString("N");
            }
            if (string.IsNullOrEmpty(config.Status)) config.Status = "idle";
            
            _service.Save(config);
            return Ok(config);
        }

        [HttpDelete("{id}")]
        public ActionResult Delete(string id)
        {
            _service.Delete(id);
            return Ok();
        }

        [HttpPost("{id}/Status")]
        public ActionResult UpdateStatus(string id, [FromBody] StatusUpdate update)
        {
            var config = _service.Get(id);
            if (config == null) return NotFound();

            config.Status = update.Status;
            
            if (update.ImportedCount.HasValue) config.ImportedCount = update.ImportedCount.Value;
            if (update.FailedCount.HasValue) config.FailedCount = update.FailedCount.Value;
            if (update.LastUpdated.HasValue) config.LastUpdated = update.LastUpdated.Value;

            _service.Save(config);
            return Ok(config);
        }
        
        [HttpPost("{id}/Reset")]
        public ActionResult Reset(string id)
        {
            var config = _service.Get(id);
            if (config == null) return NotFound();

            config.Status = "idle";
            config.ImportedCount = 0;
            config.FailedCount = 0;
            _service.Save(config);
            return Ok(config);
        }

        /// <summary>
        /// Trigger bulk import of catalogs. Runs in background.
        /// </summary>
        [HttpPost("BulkImport")]
        public ActionResult BulkImport([FromBody] BulkImportRequest request)
        {
            if (request.Catalogs == null || request.Catalogs.Count == 0)
            {
                return BadRequest("No catalogs specified");
            }

            // Fire and forget - returns immediately
            _ = _importService.BulkImportAsync(request);
            
            return Accepted(new { message = $"Import started for {request.Catalogs.Count} catalog(s)" });
        }

        /// <summary>
        /// Get item count in the collection linked to a catalog.
        /// </summary>
        [HttpGet("{id}/CollectionCount")]
        public ActionResult<int> GetCollectionCount(string id)
        {
            var config = _service.Get(id);
            if (config == null) return NotFound();

            if (string.IsNullOrEmpty(config.CollectionId))
            {
                return Ok(0);
            }

            var count = _importService.GetCollectionItemCount(config.CollectionId);
            return Ok(count);
        }
    }

    public class StatusUpdate
    {
        public string Status { get; set; } = "idle";
        public int? ImportedCount { get; set; }
        public int? FailedCount { get; set; }
        public DateTime? LastUpdated { get; set; }
    }
}

