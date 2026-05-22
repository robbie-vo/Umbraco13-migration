using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
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
using Umbraco.Cms.Infrastructure.Scoping;
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
        private readonly IScopeProvider _scopeProvider;
        private readonly ILogger<MigrationController> _logger;

        // Known columns that may be missing when migrations were bypassed
        private static readonly List<SchemaColumnCheck> KnownColumnChecks = new()
        {
            new SchemaColumnCheck
            {
                Table = "umbracoDataType",
                Column = "propertyEditorUiAlias",
                DataType = "NVARCHAR(255)",
                Nullable = true,
                PopulateSql = "UPDATE umbracoDataType SET propertyEditorUiAlias = propertyEditorAlias WHERE propertyEditorUiAlias IS NULL"
            }
        };

        public MigrationController(
        IContentService contentService,
        IContentTypeService contentTypeService,
        IDataTypeService dataTypeService,
        IConfigurationEditorJsonSerializer serializer,
        IShortStringHelper shortStringHelper,
        PropertyEditorCollection propertyEditors,
        IScopeProvider scopeProvider,
        ILogger<MigrationController> logger)
        {
            _contentService = contentService;
            _contentTypeService = contentTypeService;
            _dataTypeService = dataTypeService;
            _serializer = serializer;
            _shortStringHelper = shortStringHelper;
            _propertyEditors = propertyEditors;
            _scopeProvider = scopeProvider;
            _logger = logger;
        }


        private IActionResult RunMigrationCore(string docTypeAlias, string oldPropertyAlias, string newPropertyAlias, string newPropertyName)
        {
            var contentType = _contentTypeService.Get(docTypeAlias);
            if (contentType == null)
            {
                _logger.LogError("NC migratie mislukt: DocType '{DocTypeAlias}' niet gevonden.", docTypeAlias);
                return BadRequest($"DocType {docTypeAlias} niet gevonden.");
            }

            var oldProperty = contentType.PropertyTypes.FirstOrDefault(x => x.Alias == oldPropertyAlias);
            if (oldProperty == null)
            {
                _logger.LogError("NC migratie mislukt: property '{OldAlias}' niet gevonden op '{DocTypeAlias}'.", oldPropertyAlias, docTypeAlias);
                return BadRequest($"Oude property {oldPropertyAlias} niet gevonden op {docTypeAlias}.");
            }

            var oldDataType = _dataTypeService.GetDataType(oldProperty.DataTypeId);
            if (oldDataType == null || oldDataType.EditorAlias != "Umbraco.NestedContent")
            {
                _logger.LogError("NC migratie mislukt: property '{OldAlias}' op '{DocTypeAlias}' is geen Nested Content (editor: {EditorAlias}).", oldPropertyAlias, docTypeAlias, oldDataType?.EditorAlias ?? "null");
                return BadRequest("De bron-property is geen Nested Content type.");
            }

            _logger.LogInformation("NC migratie gestart: {DocTypeAlias} [{OldAlias} → {NewAlias}].", docTypeAlias, oldPropertyAlias, newPropertyAlias);

            ConvertOEmbedInElementTypes(oldDataType, new HashSet<string>());
            MigrateNestedElementTypes(oldDataType, new HashSet<string>());

            var newListDataType = CreateBlockListDataType(oldDataType);
            EnsurePropertyExists(contentType, newListDataType, newPropertyAlias, newPropertyName, oldPropertyAlias);

            int count = 0;
            int errorCount = 0;
            bool oldPropVariesByCulture = oldProperty.Variations.HasFlag(ContentVariation.Culture);
            bool newPropVariesByCulture = oldPropVariesByCulture;
            var contentNodes = _contentService.GetPagedOfType(contentType.Id, 0, int.MaxValue, out long totalRecords, null, null);

            foreach (var content in contentNodes)
            {
                if (!content.HasProperty(oldPropertyAlias)) continue;

                try
                {
                    bool changed = false;

                    if (oldPropVariesByCulture)
                    {
                        foreach (var culture in content.AvailableCultures)
                        {
                            var ncValue = content.GetValue<string>(oldPropertyAlias, culture);
                            if (string.IsNullOrWhiteSpace(ncValue) || !ncValue.StartsWith("[")) continue;
                            var existing = content.GetValue<string>(newPropertyAlias, culture);
                            if (!string.IsNullOrWhiteSpace(existing)) continue;
                            content.SetValue(newPropertyAlias, ConvertToBlockList(ncValue), culture);
                            changed = true;
                        }
                    }
                    else if (newPropVariesByCulture)
                    {
                        var ncValue = content.GetValue<string>(oldPropertyAlias);
                        if (string.IsNullOrWhiteSpace(ncValue) || !ncValue.StartsWith("[")) continue;
                        var newListJson = ConvertToBlockList(ncValue);
                        foreach (var culture in content.AvailableCultures)
                        {
                            var existing = content.GetValue<string>(newPropertyAlias, culture);
                            if (!string.IsNullOrWhiteSpace(existing)) continue;
                            content.SetValue(newPropertyAlias, newListJson, culture);
                            changed = true;
                        }
                    }
                    else
                    {
                        var ncValue = content.GetValue<string>(oldPropertyAlias);
                        if (string.IsNullOrWhiteSpace(ncValue) || !ncValue.StartsWith("[")) continue;
                        var existing = content.GetValue<string>(newPropertyAlias);
                        if (!string.IsNullOrWhiteSpace(existing)) continue;
                        content.SetValue(newPropertyAlias, ConvertToBlockList(ncValue));
                        changed = true;
                    }

                    if (changed)
                    {
                        if (content.Published)
                            _contentService.SaveAndPublish(content);
                        else
                            _contentService.Save(content);
                        count++;
                    }
                }
                catch (Exception ex)
                {
                    errorCount++;
                    _logger.LogError(ex, "NC migratie fout bij node {NodeId} '{NodeName}' ({DocTypeAlias}, {OldAlias} → {NewAlias}).", content.Id, content.Name, docTypeAlias, oldPropertyAlias, newPropertyAlias);
                }
            }

            _logger.LogInformation("NC migratie voltooid: {Count}/{Total} pagina's gemigreerd, {ErrorCount} fouten. ({DocTypeAlias})", count, totalRecords, errorCount, docTypeAlias);

            var message = errorCount > 0
                ? $"Migratie voltooid met waarschuwingen. {count} pagina's gemigreerd, {errorCount} fouten (zie Umbraco logboek)."
                : $"Succes! Property '{newPropertyAlias}' gecontroleerd. {count} pagina's gemigreerd.";

            return Ok(new { message, count, errorCount });
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
                    .Select(p =>
                    {
                        // Check of er al een property bestaat met dezelfde Name maar andere Alias
                        // Dit geeft aan of de migratie al is uitgevoerd
                        var duplicateProperty = ct.PropertyTypes
                            .FirstOrDefault(x => x.Name == p.Name && x.Alias != p.Alias);

                        return new
                        {
                            DocTypeAlias = ct.Alias,
                            PropertyAlias = p.Alias,
                            PropertyName = p.Name,
                            HasDuplicate = duplicateProperty != null,
                            DuplicateAlias = duplicateProperty?.Alias,
                            DuplicateEditorAlias = duplicateProperty?.PropertyEditorAlias
                        };
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
                _logger.LogError(ex, "NC migratie onverwachte fout voor {DocTypeAlias}.{PropertyAlias} → {NewAlias}.", model.DocTypeAlias, model.PropertyAlias, model.NewAlias);
                return StatusCode(500, $"Interne fout: {ex.Message}");
            }
        }

        /// <summary>
        /// Walks all element types referenced by an NC data type (recursively) and converts any OEmbed
        /// properties to Textstring. OEmbed was removed in Umbraco 13 and causes Save/SaveAndPublish to
        /// throw on content types and content nodes that still reference the editor.
        /// </summary>
        private void ConvertOEmbedInElementTypes(IDataType ncDataType, HashSet<string> visited)
        {
            var ncConfig = ncDataType.ConfigurationAs<NestedContentConfiguration>();
            if (ncConfig?.ContentTypes == null) return;

            // Get the built-in Textstring data type once
            var textstringDt = _dataTypeService.GetDataType("Textstring")
                ?? _dataTypeService.GetAll().FirstOrDefault(d => d.EditorAlias == "Umbraco.TextBox" || d.EditorAlias == "Umbraco.Textbox");
            if (textstringDt == null) return; // nothing we can do without a target data type

            foreach (var ncType in ncConfig.ContentTypes)
            {
                if (!visited.Add(ncType.Alias)) continue;

                var elementType = _contentTypeService.Get(ncType.Alias);
                if (elementType == null) continue;

                // Also recurse into nested NC properties on this element type
                foreach (var innerProp in elementType.PropertyTypes.Where(p => p.PropertyEditorAlias == "Umbraco.NestedContent").ToList())
                {
                    var innerDt = _dataTypeService.GetDataType(innerProp.DataTypeId);
                    if (innerDt != null) ConvertOEmbedInElementTypes(innerDt, visited);
                }

                var oembedProps = elementType.PropertyTypes
                    .Where(p => p.PropertyEditorAlias.IndexOf("oembed", StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToList();

                if (!oembedProps.Any()) continue;

                foreach (var prop in oembedProps)
                {
                    var group = elementType.PropertyGroups
                        .FirstOrDefault(g => g.PropertyTypes.Any(p => p.Alias == prop.Alias));
                    var groupAlias = group?.Alias ?? elementType.PropertyGroups.FirstOrDefault()?.Alias;
                    if (groupAlias == null) continue;

                    // Preserve all settings then swap the data type by removing and re-adding
                    var alias = prop.Alias;
                    var name = prop.Name;
                    var mandatory = prop.Mandatory;
                    var description = prop.Description;
                    var sortOrder = prop.SortOrder;

                    elementType.RemovePropertyType(alias);

                    var replacement = new PropertyType(_shortStringHelper, textstringDt, alias)
                    {
                        Name = name,
                        Mandatory = mandatory,
                        Description = description,
                        SortOrder = sortOrder
                    };
                    elementType.AddPropertyType(replacement, groupAlias);
                }

                _contentTypeService.Save(elementType);
            }
        }

        /// <summary>
        /// Depth-first: for every element type referenced by an NC data type, check whether that element type
        /// itself contains NC properties and migrate those first. Prevents top-level migration from running
        /// before inner NC properties have a BlockList counterpart.
        /// </summary>
        private void MigrateNestedElementTypes(IDataType ncDataType, HashSet<string> visited)
        {
            var ncConfig = ncDataType.ConfigurationAs<NestedContentConfiguration>();
            if (ncConfig?.ContentTypes == null) return;

            foreach (var ncType in ncConfig.ContentTypes)
            {
                if (!visited.Add(ncType.Alias)) continue; // skip already-processed types (avoids infinite loops)

                var elementType = _contentTypeService.Get(ncType.Alias);
                if (elementType == null) continue;

                foreach (var prop in elementType.PropertyTypes.Where(p => p.PropertyEditorAlias == "Umbraco.NestedContent").ToList())
                {
                    var innerDataType = _dataTypeService.GetDataType(prop.DataTypeId);
                    if (innerDataType == null) continue;

                    // Go deeper first
                    MigrateNestedElementTypes(innerDataType, visited);

                    // Only add the BlockList property if it does not exist yet
                    bool alreadyMigrated = elementType.PropertyTypes
                        .Any(x => x.Name == prop.Name && x.Alias != prop.Alias && x.PropertyEditorAlias == "Umbraco.BlockList");

                    if (!alreadyMigrated)
                    {
                        var newInnerDataType = CreateBlockListDataType(innerDataType);
                        var newAlias = prop.Alias + "BlockList";
                        EnsurePropertyExists(elementType, newInnerDataType, newAlias, prop.Name, prop.Alias);
                    }
                }
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

            var oldProperty = contentType.PropertyTypes.FirstOrDefault(p => p.Alias == oldPropertyAlias);
            var propertyType = new PropertyType(_shortStringHelper, dataType, alias)
            {
                Name = name,
                Variations = oldProperty?.Variations ?? ContentVariation.Nothing
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
                if (string.IsNullOrEmpty(alias))
                {
                    _logger.LogWarning("NC conversie: NC item overgeslagen — geen contentTypeAlias gevonden in item: {Item}", item.ToString(Formatting.None));
                    continue;
                }

                var contentType = _contentTypeService.Get(alias);
                if (contentType == null)
                {
                    _logger.LogWarning("NC conversie: NC item overgeslagen — element type '{Alias}' niet gevonden.", alias);
                    continue;
                }

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

                // 3. Recursively convert any nested NC properties on this element type
                foreach (var ncProp in contentType.PropertyTypes.Where(p => p.PropertyEditorAlias == "Umbraco.NestedContent"))
                {
                    if (!item.ContainsKey(ncProp.Alias)) continue;

                    var innerValue = item[ncProp.Alias]?.ToString();
                    if (string.IsNullOrWhiteSpace(innerValue) || !innerValue.TrimStart().StartsWith("[")) continue;

                    var convertedInner = ConvertToBlockList(innerValue);

                    // If a corresponding BlockList property already exists (same Name, different Alias), use that alias
                    var blockListCounterpart = contentType.PropertyTypes
                        .FirstOrDefault(x => x.Name == ncProp.Name && x.Alias != ncProp.Alias && x.PropertyEditorAlias == "Umbraco.BlockList");

                    var targetAlias = blockListCounterpart?.Alias ?? (ncProp.Alias + "BlockList");
                    item[targetAlias] = convertedInner;
                    item.Remove(ncProp.Alias);
                }

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
        private void EnsureSchemaColumns()
        {
            foreach (var check in KnownColumnChecks)
            {
                using var checkScope = _scopeProvider.CreateScope();
                var exists = checkScope.Database.ExecuteScalar<int>(
                    "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = @0 AND COLUMN_NAME = @1",
                    check.Table, check.Column) > 0;
                checkScope.Complete();

                if (exists) continue;

                using var ddlScope = _scopeProvider.CreateScope();
                var nullable = check.Nullable ? "NULL" : "NOT NULL";
                ddlScope.Database.Execute($"ALTER TABLE {check.Table} ADD {check.Column} {check.DataType} {nullable}");
                ddlScope.Complete();

                if (!string.IsNullOrWhiteSpace(check.PopulateSql))
                {
                    using var populateScope = _scopeProvider.CreateScope();
                    populateScope.Database.Execute(check.PopulateSql);
                    populateScope.Complete();
                }
            }
        }

        [HttpGet]
        public IActionResult ContentCheck([FromQuery] string docTypeAlias, [FromQuery] string ncAlias, [FromQuery] string blAlias)
        {
            if (string.IsNullOrWhiteSpace(docTypeAlias) || string.IsNullOrWhiteSpace(ncAlias) || string.IsNullOrWhiteSpace(blAlias))
                return BadRequest("docTypeAlias, ncAlias en blAlias zijn verplicht.");

            var contentType = _contentTypeService.Get(docTypeAlias);
            if (contentType == null) return BadRequest($"DocType {docTypeAlias} niet gevonden.");

            var ncProp = contentType.PropertyTypes.FirstOrDefault(p => p.Alias == ncAlias);
            bool variesByCulture = ncProp != null && ncProp.Variations.HasFlag(ContentVariation.Culture);

            var contentNodes = _contentService.GetPagedOfType(contentType.Id, 0, int.MaxValue, out _, null, null);
            var results = new List<object>();

            foreach (var content in contentNodes)
            {
                if (!content.HasProperty(ncAlias)) continue;

                string nodeName = content.Name ?? "(naamloos)";
                int nodeId = content.Id;

                if (variesByCulture)
                {
                    foreach (var culture in content.AvailableCultures)
                    {
                        int ncCount = CountNcItems(content.GetValue<string>(ncAlias, culture));
                        int blCount = CountBlItems(content.GetValue<string>(blAlias, culture));
                        results.Add(new { NodeName = $"{nodeName} [{culture}]", NodeId = nodeId, NcCount = ncCount, BlCount = blCount, Match = ncCount == blCount });
                    }
                }
                else
                {
                    int ncCount = CountNcItems(content.GetValue<string>(ncAlias));
                    int blCount = CountBlItems(content.GetValue<string>(blAlias));
                    results.Add(new { NodeName = nodeName, NodeId = nodeId, NcCount = ncCount, BlCount = blCount, Match = ncCount == blCount });
                }
            }

            return Ok(results);
        }

        private static int CountNcItems(string? json)
        {
            if (string.IsNullOrWhiteSpace(json) || !json.TrimStart().StartsWith("[")) return 0;
            try { return JArray.Parse(json).Count; }
            catch { return 0; }
        }

        private static int CountBlItems(string? json)
        {
            if (string.IsNullOrWhiteSpace(json) || !json.TrimStart().StartsWith("{")) return 0;
            try { return (JObject.Parse(json)["contentData"] as JArray)?.Count ?? 0; }
            catch { return 0; }
        }

        [HttpGet]
        public IActionResult SchemaCheck()
        {
            var results = new List<object>();

            using var scope = _scopeProvider.CreateScope();
            foreach (var check in KnownColumnChecks)
            {
                var count = scope.Database.ExecuteScalar<int>(
                    "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = @0 AND COLUMN_NAME = @1",
                    check.Table, check.Column);

                results.Add(new
                {
                    check.Table,
                    check.Column,
                    check.DataType,
                    Exists = count > 0
                });
            }
            scope.Complete();

            return Ok(results);
        }

        [HttpPost]
        public IActionResult SchemaFix()
        {
            var fixed_ = new List<string>();
            var skipped = new List<string>();

            foreach (var check in KnownColumnChecks)
            {
                using var checkScope = _scopeProvider.CreateScope();
                var exists = checkScope.Database.ExecuteScalar<int>(
                    "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = @0 AND COLUMN_NAME = @1",
                    check.Table, check.Column) > 0;
                checkScope.Complete();

                if (exists) { skipped.Add($"{check.Table}.{check.Column}"); continue; }

                fixed_.Add($"{check.Table}.{check.Column}");
            }

            EnsureSchemaColumns();

            return Ok(new { fixed_, skipped });
        }

        [HttpGet]
        public IActionResult ForceMultiplePicker()
        {
            int affected;
            using (var scope = _scopeProvider.CreateScope())
            {
                affected = scope.Database.Execute(
                    "UPDATE umbracoDataType SET config = REPLACE(config, '\"multiple\":false', '\"multiple\":true') " +
                    "WHERE propertyEditorAlias = 'Umbraco.MediaPicker3' " +
                    "AND nodeId IN (SELECT id FROM umbracoNode WHERE LOWER(text) LIKE '%multiple%' OR LOWER(text) LIKE '%meerdere%')");
                scope.Complete();
            }
            return Ok(new { affected, message = $"{affected} rij(en) bijgewerkt. Herstart de app pool om de cache te wissen." });
        }

        [HttpGet]
        public IActionResult MediaPickerDbConfig()
        {
            using var scope = _scopeProvider.CreateScope();
            var rows = scope.Database.Query<MediaPickerConfigRow>(
                "SELECT n.id AS Id, n.text AS Name, dt.config AS Config " +
                "FROM umbracoDataType dt JOIN umbracoNode n ON dt.nodeId = n.id " +
                "WHERE dt.propertyEditorAlias = 'Umbraco.MediaPicker3'"
            ).ToList();
            scope.Complete();
            return Ok(rows.Select(r => new { r.Id, r.Name, r.Config }));
        }

        [HttpGet]
        public IActionResult MediaPickerAudit()
        {
            using var scope = _scopeProvider.CreateScope();

            // Old alias data types — need full migration
            var oldAliasRows = scope.Database.Query<MediaPickerDataTypeRow>(
                "SELECT n.id AS Id, n.text AS Name, dt.propertyEditorAlias AS EditorAlias " +
                "FROM umbracoDataType dt JOIN umbracoNode n ON dt.nodeId = n.id " +
                "WHERE dt.propertyEditorAlias IN ('Umbraco.MediaPicker', 'Umbraco.MultipleMediaPicker')"
            ).ToList();

            // All MediaPicker3 data types — check config to find stale ones
            var mp3Rows = scope.Database.Query<MediaPickerConfigRow>(
                "SELECT n.id AS Id, n.text AS Name, dt.config AS Config " +
                "FROM umbracoDataType dt JOIN umbracoNode n ON dt.nodeId = n.id " +
                "WHERE dt.propertyEditorAlias = 'Umbraco.MediaPicker3'"
            ).ToList();

            scope.Complete();

            var allContentTypes = _contentTypeService.GetAll().ToList();
            var processedIds = new HashSet<int>(oldAliasRows.Select(r => r.Id));

            var report = new List<object>();

            foreach (var dt in oldAliasRows)
            {
                report.Add(new
                {
                    dt.Id,
                    dt.Name,
                    dt.EditorAlias,
                    NeedsConfigFix = false,
                    Usages = allContentTypes
                        .SelectMany(ct => ct.PropertyTypes
                            .Where(p => p.DataTypeId == dt.Id)
                            .Select(p => new { ContentTypeAlias = ct.Alias, PropertyAlias = p.Alias, PropertyName = p.Name }))
                        .ToList()
                });
            }

            foreach (var row in mp3Rows)
            {
                if (processedIds.Contains(row.Id)) continue;

                var configJson = JObject.Parse(row.Config ?? "{}");
                bool hasMultiple = configJson["multiple"]?.Value<bool>() == true;

                if (hasMultiple) continue; // config is already correct

                // Stale: either had multiPicker:true (raw SQL conversion) or name suggests multiple
                bool hadMultiPicker = configJson["multiPicker"]?.Value<bool>() == true;
                bool nameImpliesMultiple = row.Name.IndexOf("multiple", StringComparison.OrdinalIgnoreCase) >= 0
                                        || row.Name.IndexOf("meerdere", StringComparison.OrdinalIgnoreCase) >= 0;

                if (!hadMultiPicker && !nameImpliesMultiple) continue;

                report.Add(new
                {
                    row.Id,
                    row.Name,
                    EditorAlias = "Umbraco.MediaPicker3",
                    NeedsConfigFix = true,
                    Usages = allContentTypes
                        .SelectMany(ct => ct.PropertyTypes
                            .Where(p => p.DataTypeId == row.Id)
                            .Select(p => new { ContentTypeAlias = ct.Alias, PropertyAlias = p.Alias, PropertyName = p.Name }))
                        .ToList()
                });
            }

            return Ok(report);
        }

        [HttpPost]
        public IActionResult MediaPickerFix()
        {
            if (!_propertyEditors.TryGet("Umbraco.MediaPicker3", out var mp3Editor) || mp3Editor == null)
                return BadRequest("MediaPicker3 editor niet gevonden.");

            EnsureSchemaColumns();

            List<MediaPickerDataTypeRow> oldAliasRows;
            using (var scope = _scopeProvider.CreateScope())
            {
                oldAliasRows = scope.Database.Query<MediaPickerDataTypeRow>(
                    "SELECT n.id AS Id, n.text AS Name, dt.propertyEditorAlias AS EditorAlias " +
                    "FROM umbracoDataType dt JOIN umbracoNode n ON dt.nodeId = n.id " +
                    "WHERE dt.propertyEditorAlias IN ('Umbraco.MediaPicker', 'Umbraco.MultipleMediaPicker')"
                ).ToList();
                scope.Complete();
            }

            int count = 0;

            // Fix old alias data types — editor must change so reconstruct with new editor
            foreach (var row in oldAliasRows)
            {
                var existing = _dataTypeService.GetDataType(row.Id);
                if (existing == null) continue;

                var updated = new DataType(mp3Editor, _serializer)
                {
                    Name = existing.Name,
                    Configuration = new MediaPicker3Configuration
                    {
                        Multiple = row.EditorAlias == "Umbraco.MultipleMediaPicker"
                    }
                };
                updated.Id = existing.Id;
                updated.Key = existing.Key;
                _dataTypeService.Save(updated);
                count++;
            }

            // Fix already-MediaPicker3 types whose name implies multiple but config still has multiple:false
            // Uses SQL REPLACE — the only approach that reliably writes through Umbraco's config layer
            using (var fixScope = _scopeProvider.CreateScope())
            {
                int sqlFixed = fixScope.Database.Execute(
                    "UPDATE umbracoDataType " +
                    "SET config = REPLACE(config, '\"multiple\":false', '\"multiple\":true') " +
                    "WHERE propertyEditorAlias = 'Umbraco.MediaPicker3' " +
                    "AND config LIKE '%\"multiple\":false%' " +
                    "AND nodeId IN (" +
                    "  SELECT id FROM umbracoNode" +
                    "  WHERE LOWER(text) LIKE '%multiple%' OR LOWER(text) LIKE '%meerdere%'" +
                    ")");
                fixScope.Complete();
                count += sqlFixed;
            }

            return Ok(new { message = $"{count} media picker(s) bijgewerkt.", count });
        }
    }

    public class SchemaColumnCheck
    {
        public string Table { get; set; } = string.Empty;
        public string Column { get; set; } = string.Empty;
        public string DataType { get; set; } = string.Empty;
        public bool Nullable { get; set; } = true;
        public string? PopulateSql { get; set; }
    }

    public class MediaPickerDataTypeRow
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string EditorAlias { get; set; } = string.Empty;
    }

    public class MediaPickerConfigRow
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Config { get; set; }
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

