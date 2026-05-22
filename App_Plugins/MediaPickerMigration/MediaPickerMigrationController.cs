using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.PropertyEditors;
using Umbraco.Cms.Core.Serialization;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Scoping;
using Umbraco.Cms.Web.Common.Controllers;

namespace UmbracoVO.Controllers
{
    [Route("umbraco/api/mediapickermigration/[action]")]
    public class MediaPickerMigrationController : UmbracoApiController
    {
        private static readonly string[] OldAliases = new[]
        {
            "Umbraco.MediaPicker",
            "Umbraco.MultipleMediaPicker"
        };

        private const string NewAlias = "Umbraco.MediaPicker3";

        private readonly IContentTypeService _contentTypeService;
        private readonly IDataTypeService _dataTypeService;
        private readonly IScopeProvider _scopeProvider;
        private readonly PropertyEditorCollection _propertyEditors;
        private readonly IConfigurationEditorJsonSerializer _serializer;

        public MediaPickerMigrationController(
            IContentTypeService contentTypeService,
            IDataTypeService dataTypeService,
            IScopeProvider scopeProvider,
            PropertyEditorCollection propertyEditors,
            IConfigurationEditorJsonSerializer serializer)
        {
            _contentTypeService = contentTypeService;
            _dataTypeService = dataTypeService;
            _scopeProvider = scopeProvider;
            _propertyEditors = propertyEditors;
            _serializer = serializer;
        }

        [HttpGet]
        public IActionResult Audit()
        {
            var oldDataTypes = _dataTypeService.GetAll()
                .Where(dt => OldAliases.Contains(dt.EditorAlias))
                .ToList();

            if (!oldDataTypes.Any())
                return StatusCode(500, "Geen oude Media Picker data types gevonden. Alles is al omgezet!");

            var allContentTypes = _contentTypeService.GetAll().ToList();

            var result = oldDataTypes.Select(dt =>
            {
                var usages = allContentTypes
                    .SelectMany(ct => ct.PropertyTypes
                        .Where(p => p.DataTypeId == dt.Id)
                        .Select(p => new
                        {
                            DocTypeAlias = ct.Alias,
                            DocTypeName = ct.Name,
                            PropertyAlias = p.Alias,
                            PropertyName = p.Name
                        }))
                    .ToList();

                return new
                {
                    DataTypeId = dt.Id,
                    DataTypeName = dt.Name,
                    EditorAlias = dt.EditorAlias,
                    IsMultiple = dt.EditorAlias == "Umbraco.MultipleMediaPicker",
                    IsFixed = false,
                    Usages = usages
                };
            }).ToList();

            return Ok(result);
        }

        [HttpPost]
        public IActionResult Fix([FromBody] MediaPickerFixModel model)
        {
            if (model == null || model.DataTypeId <= 0)
                return BadRequest("Ongeldig data type ID ontvangen.");

            try
            {
                var dataType = _dataTypeService.GetDataType(model.DataTypeId);
                if (dataType == null)
                    return BadRequest($"Data type met ID {model.DataTypeId} niet gevonden.");

                if (!OldAliases.Contains(dataType.EditorAlias))
                    return BadRequest($"Data type '{dataType.Name}' is geen oud Media Picker type (alias: {dataType.EditorAlias}).");

                if (!_propertyEditors.TryGet(NewAlias, out var mp3Editor) || mp3Editor == null)
                    return BadRequest("MediaPicker3 editor niet gevonden.");

                // IDataTypeService.Save() writes propertyEditorUiAlias — ensure the column exists first
                EnsurePropertyEditorUiAliasColumn();

                var isMultiple = dataType.EditorAlias == "Umbraco.MultipleMediaPicker";

                var updated = new DataType(mp3Editor, _serializer)
                {
                    Name = dataType.Name,
                    Configuration = new MediaPicker3Configuration { Multiple = isMultiple }
                };
                updated.Id = dataType.Id;
                updated.Key = dataType.Key;

                _dataTypeService.Save(updated);

                return Ok(new
                {
                    message = $"Data type '{dataType.Name}' is bijgewerkt naar {NewAlias}{(isMultiple ? " (meerdere selectie ingeschakeld)" : "")}.",
                    dataTypeId = model.DataTypeId
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Interne fout: {ex.Message}");
            }
        }

        private void EnsurePropertyEditorUiAliasColumn()
        {
            using var checkScope = _scopeProvider.CreateScope();
            var exists = checkScope.Database.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'umbracoDataType' AND COLUMN_NAME = 'propertyEditorUiAlias'") > 0;
            checkScope.Complete();

            if (exists) return;

            using var ddlScope = _scopeProvider.CreateScope();
            ddlScope.Database.Execute("ALTER TABLE umbracoDataType ADD propertyEditorUiAlias NVARCHAR(255) NULL");
            ddlScope.Complete();

            using var populateScope = _scopeProvider.CreateScope();
            populateScope.Database.Execute("UPDATE umbracoDataType SET propertyEditorUiAlias = propertyEditorAlias WHERE propertyEditorUiAlias IS NULL");
            populateScope.Complete();
        }
    }

    public class MediaPickerFixModel
    {
        [JsonProperty("dataTypeId")]
        public int DataTypeId { get; set; }
    }
}
