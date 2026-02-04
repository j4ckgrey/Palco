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

        public CatalogsController(CatalogsService service)
        {
            _service = service;
        }

        [HttpGet]
        public ActionResult<List<CatalogConfig>> GetAll()
        {
            return Ok(_service.GetAll());
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
    }

    public class StatusUpdate
    {
        public string Status { get; set; } = "idle";
        public int? ImportedCount { get; set; }
        public int? FailedCount { get; set; }
        public DateTime? LastUpdated { get; set; }
    }
}
