using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.PropertyEditors;
using Umbraco.Cms.Core.Serialization;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Strings;
using Umbraco.Cms.Web.Common.Controllers;
using Umbraco.Extensions;

namespace UmbracoVO.Controllers
{
    [Route("umbraco/api/migration/[action]")]
    public class MigrationController : UmbracoApiController
    {
        private readonly IContentService _contentService;
        private readonly IContentTypeService _contentTypeService;
        private readonly IDataTypeService _dataTypeService;
        private readonly IConfigurationEditorJsonSerializer _serializer;
        private readonly IShortStringHelper _shortStringHelper;
        private readonly PropertyEditorCollection _propertyEditors;

        public MigrationController(
        IContentService contentService,
        IContentTypeService contentTypeService,
        IDataTypeService dataTypeService,
        IConfigurationEditorJsonSerializer serializer,
        IShortStringHelper shortStringHelper,
        PropertyEditorCollection propertyEditors)
        {
            _contentService = contentService;
            _contentTypeService = contentTypeService;
            _dataTypeService = dataTypeService;
            _serializer = serializer;
            _shortStringHelper = shortStringHelper;
            _propertyEditors = propertyEditors;
        }


        //[HttpGet]
        //public IActionResult RunFullMigration()
        //{
        //    string docTypeAlias = "Pagina";
        //    string oldPropertyAlias = "contentBlocks";
        //    string newPropertyAlias = "contentBlockList";
        //    string newPropertyName = "Content Blokken";
        //    return RunMigrationCore(docTypeAlias, oldPropertyAlias, newPropertyAlias, newPropertyName);
        //}

        private IActionResult RunMigrationCore(string docTypeAlias, string oldPropertyAlias, string newPropertyAlias, string newPropertyName)
        {
            var contentType = _contentTypeService.Get(docTypeAlias);
            if (contentType == null) return BadRequest($"DocType {docTypeAlias} niet gevonden.");

            var oldProperty = contentType.PropertyTypes.FirstOrDefault(x => x.Alias == oldPropertyAlias);
            if (oldProperty == null) return BadRequest($"Oude property {oldPropertyAlias} niet gevonden op {docTypeAlias}.");

            var oldDataType = _dataTypeService.GetDataType(oldProperty.DataTypeId);
            if (oldDataType == null || oldDataType.EditorAlias != "Umbraco.NestedContent")
                return BadRequest("De bron-property is geen Nested Content type.");

            var newListDataType = CreateBlockListDataType(oldDataType);
            EnsurePropertyExists(contentType, newListDataType, newPropertyAlias, newPropertyName, oldPropertyAlias);

            int count = 0;
            var contentNodes = _contentService.GetPagedOfType(contentType.Id, 0, int.MaxValue, out long totalRecords, null, null);

            foreach (var content in contentNodes)
            {
                if (content.HasProperty(oldPropertyAlias))
                {
                    var ncValue = content.GetValue<string>(oldPropertyAlias);
                    if (!string.IsNullOrWhiteSpace(ncValue) && ncValue.StartsWith("["))
                    {
                        var currentNewValue = content.GetValue<string>(newPropertyAlias);
                        if (string.IsNullOrWhiteSpace(currentNewValue))
                        {
                            var newListJson = ConvertToBlockList(ncValue);
                            content.SetValue(newPropertyAlias, newListJson);
                            _contentService.SaveAndPublish(content);
                            count++;
                        }
                    }
                }
            }

            return Ok(new { message = $"Succes! Property '{newPropertyAlias}' gecontroleerd. {count} pagina's gemigreerd.", count });
        }


        [HttpGet]
        public IActionResult Audit()
        {
            // De interne alias voor Nested Content
            const string nestedContentAlias = "Umbraco.NestedContent";

            // Haal alle Document Types (ContentTypes) op
            var allContentTypes = _contentTypeService.GetAll();

            var report = allContentTypes
                .SelectMany(ct => ct.PropertyTypes
                    .Where(p => p.PropertyEditorAlias == nestedContentAlias)
                    .Select(p => new
                    {
                        DocTypeAlias = ct.Alias,
                        PropertyAlias = p.Alias,
                        PropertyName = p.Name
                    }))
                .ToList();

            if (!report.Any())
            {
                return StatusCode(500, "Geen Nested Content velden gevonden! Alles is al omgezet.");
                //return Ok("Geen Nested Content velden gevonden! Alles is al omgezet.");
            }

            return Ok(report);
        }

        [HttpPost]
        public IActionResult Update([FromBody] MigrationUpdateModel model)
        {
            if (model == null ||
                string.IsNullOrWhiteSpace(model.DocTypeAlias) ||
                string.IsNullOrWhiteSpace(model.PropertyAlias) ||
                string.IsNullOrWhiteSpace(model.NewAlias))
            {
                return BadRequest("Ongeldige data ontvangen. DocTypeAlias, PropertyAlias en NewAlias zijn verplicht.");
            }

            try
            {
                var contentType = _contentTypeService.Get(model.DocTypeAlias);
                if (contentType == null) return BadRequest($"DocType {model.DocTypeAlias} niet gevonden.");

                var oldProperty = contentType.PropertyTypes.FirstOrDefault(x => x.Alias == model.PropertyAlias);
                if (oldProperty == null) return BadRequest($"Oude property {model.PropertyAlias} niet gevonden op {model.DocTypeAlias}.");

                string newPropertyName = oldProperty.Name;
                return RunMigrationCore(model.DocTypeAlias, model.PropertyAlias, model.NewAlias, newPropertyName);
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Interne fout: {ex.Message}");
            }
        }

        private IDataType CreateBlockListDataType(IDataType oldNcDataType)
        {
            string newName = "VO Blocklist - " + oldNcDataType.Name;
            var existing = _dataTypeService.GetDataType(newName);
            if (existing != null) return existing;

            // 1. Haal de NC configuratie op (hiervoor is 'using Umbraco.Cms.Core.PropertyEditors' nodig)
            var ncConfig = oldNcDataType.ConfigurationAs<NestedContentConfiguration>();

            // 2. Haal de Block List Property Editor op
            // We hebben de editor zelf nodig (IDataEditor), niet een bestaand DataType
            if (!_propertyEditors.TryGet("Umbraco.BlockList", out var blockListEditor))
            {
                throw new Exception("Block List editor niet gevonden.");
            }
            if (blockListEditor == null) throw new Exception("Block List editor niet gevonden.");

            // 3. Maak het nieuwe DataType aan
            var newDataType = new DataType(blockListEditor, _serializer)
            {
                Name = newName
            };

            var blockConfigs = new List<BlockListConfiguration.BlockConfiguration>();
            foreach (var ncType in ncConfig.ContentTypes)
            {
                var element = _contentTypeService.Get(ncType.Alias);
                if (element != null)
                {
                    blockConfigs.Add(new BlockListConfiguration.BlockConfiguration
                    {
                        ContentElementTypeKey = element.Key,
                        Label = ncType.TabAlias ?? "{{name}}"
                    });
                }
            }

            newDataType.Configuration = new BlockListConfiguration
            {
                Blocks = blockConfigs.ToArray()
            };

            _dataTypeService.Save(newDataType);
            return newDataType;
        }

        private void EnsurePropertyExists(IContentType contentType, IDataType dataType, string alias, string name, string oldPropertyAlias)
        {
            if (contentType.PropertyTypeExists(alias)) return;

            // We zoeken de groep waar de OUDE property in zit, 
            // zodat de nieuwe Block List direct daaronder verschijnt.
            var group = contentType.PropertyGroups
                .FirstOrDefault(x => x.PropertyTypes.Any(p => p.Alias == oldPropertyAlias))
                ?? contentType.PropertyGroups.First();

            var propertyType = new PropertyType(_shortStringHelper, dataType, alias)
            {
                Name = name
                //Description = "Automatisch gemigreerd van Nested Content"
            };

            contentType.AddPropertyType(propertyType, group.Alias);
            _contentTypeService.Save(contentType);
        }

        private string ConvertToBlockList(string ncJson)
        {
            var ncArray = JsonConvert.DeserializeObject<List<JObject>>(ncJson);
            var layoutItems = new List<object>();
            var contentData = new List<JObject>();

            foreach (var item in ncArray)
            {
                string? alias = item["ncContentTypeAlias"]?.ToString() ?? item["contentTypeAlias"]?.ToString();
                if (string.IsNullOrEmpty(alias)) continue;

                var contentType = _contentTypeService.Get(alias);
                if (contentType == null) continue;

                var contentGuid = Guid.NewGuid();
                var udi = Udi.Create(Constants.UdiEntityType.Element, contentGuid).ToString();

                layoutItems.Add(new { contentUdi = udi });

                // 1. Voeg de verplichte technische velden toe
                item["udi"] = udi;
                item["contentTypeKey"] = contentType.Key.ToString();

                // 2. VERWIJDER de velden die warnings veroorzaken
                // Deze velden zitten in de NC data maar horen niet in een Block List
                item.Remove("ncContentTypeAlias");
                item.Remove("contentTypeAlias");
                item.Remove("name");
                item.Remove("key");
                item.Remove("PropType");
                item.Remove("controlId");

                contentData.Add(item);
            }

            var finalResult = new
            {
                layout = new Dictionary<string, object>
        {
            { "Umbraco.BlockList", layoutItems }
        },
                contentData = contentData,
                settingsData = new List<object>()
            };

            return JsonConvert.SerializeObject(finalResult);
        }
    }

    public class MigrationUpdateModel
    {
        [JsonProperty("docTypeAlias")]
        public string DocTypeAlias { get; set; } = string.Empty;

        [JsonProperty("propertyAlias")]
        public string PropertyAlias { get; set; } = string.Empty;

        [JsonProperty("newAlias")]
        public string NewAlias { get; set; } = string.Empty;
    }
}

