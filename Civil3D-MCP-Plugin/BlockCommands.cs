using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace Civil3DMcpPlugin;

/// <summary>
/// Blocks from another drawing (a symbol library or the template): list the block definitions of a .dwg,
/// import them through Database.WblockCloneObjects, and insert a block reference with its attributes in
/// model space or a layout. The source path goes through FileBoundary (import roots, .dwg/.dwt).
/// </summary>
public static class BlockCommands
{
  internal static Database ReadSource(string path)
  {
    var source = new Database(false, true);
    try
    {
      source.ReadDwgFile(path, FileOpenMode.OpenForReadAndAllShare, true, "");
      source.CloseInput(true);
      return source;
    }
    catch (Autodesk.AutoCAD.Runtime.Exception ex)
    {
      source.Dispose();
      throw new JsonRpcDispatchException("CIVIL3D.FILE_IO_ERROR", $"Could not read '{path}': {ex.ErrorStatus}. Nothing was changed.");
    }
  }

  private static string? SkipReason(BlockTableRecord record) =>
    record.IsLayout ? "layout"
    : record.IsAnonymous ? "anonymous"
    : record.IsFromExternalReference || record.IsFromOverlayReference ? "xref"
    : record.IsDependent ? "xref-dependent"
    : null;

  public static Task<object?> ListExternalBlocksAsync(JsonObject? parameters)
  {
    var path = FileBoundary.ResolveImportPath(PluginRuntime.GetRequiredString(parameters, "sourcePath"), ".dwg", ".dwt");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      using var source = ReadSource(path);
      using var sourceTransaction = source.TransactionManager.StartTransaction();
      var table = (BlockTable)sourceTransaction.GetObject(source.BlockTableId, OpenMode.ForRead);
      var destination = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
      var blocks = new List<Dictionary<string, object?>>();
      var skipped = new Dictionary<string, int>();
      var layouts = new List<string>();
      foreach (ObjectId id in table)
      {
        var record = (BlockTableRecord)sourceTransaction.GetObject(id, OpenMode.ForRead);
        var reason = SkipReason(record);
        if (reason != null)
        {
          skipped[reason] = skipped.GetValueOrDefault(reason) + 1;
          if (reason == "layout") layouts.Add(record.Name);
          continue;
        }

        var attributes = new List<Dictionary<string, object?>>();
        var entityCount = 0;
        foreach (ObjectId entityId in record)
        {
          entityCount++;
          if (sourceTransaction.GetObject(entityId, OpenMode.ForRead) is AttributeDefinition definition)
          {
            attributes.Add(new Dictionary<string, object?>
            {
              ["tag"] = definition.Tag,
              ["prompt"] = definition.Prompt,
              ["default"] = definition.TextString,
              ["constant"] = definition.Constant,
              ["invisible"] = definition.Invisible,
            });
          }
        }

        blocks.Add(new Dictionary<string, object?>
        {
          ["name"] = record.Name,
          ["basePoint"] = new[] { record.Origin.X, record.Origin.Y, record.Origin.Z }.Select(v => Math.Round(v, 4)).ToArray(),
          ["attributes"] = attributes,
          ["entityCount"] = entityCount,
          ["dynamic"] = record.IsDynamicBlock,
          ["annotative"] = record.Annotative.ToString(),
          ["units"] = record.Units.ToString(),
          ["description"] = string.IsNullOrWhiteSpace(record.Comments) ? null : record.Comments,
          ["existsInDrawing"] = destination.Has(record.Name),
        });
      }

      sourceTransaction.Commit();
      return new Dictionary<string, object?>
      {
        ["sourcePath"] = path,
        ["blocks"] = blocks.OrderBy(b => (string)b["name"]!, StringComparer.OrdinalIgnoreCase).ToList(),
        ["skipped"] = skipped,
        ["layouts"] = layouts,
      };
    });
  }

  /// <summary>Imports block definitions by name (all importable ones when none are given). Existing names are
  /// skipped unless overwrite, which redefines them with DuplicateRecordCloning.Replace.</summary>
  public static Task<object?> ImportBlocksAsync(JsonObject? parameters)
  {
    var path = FileBoundary.ResolveImportPath(PluginRuntime.GetRequiredString(parameters, "sourcePath"), ".dwg", ".dwt");
    var requested = (PluginRuntime.GetParameter(parameters, "blockNames") as JsonArray)?.Select(n => n?.GetValue<string>()).OfType<string>()
      .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    var overwrite = PluginRuntime.GetOptionalBool(parameters, "overwrite") ?? false;
    if (requested is { Count: 0 })
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "blockNames must list at least one block when given. Nothing was imported.");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      using var source = ReadSource(path);
      // Names in the drawing, read through an open/close transaction so that the block table is closed again
      // before WblockCloneObjects opens it for write.
      var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      using (var check = database.TransactionManager.StartOpenCloseTransaction())
      {
        foreach (ObjectId id in (BlockTable)check.GetObject(database.BlockTableId, OpenMode.ForRead))
          existing.Add(((BlockTableRecord)check.GetObject(id, OpenMode.ForRead)).Name);
        check.Commit();
      }

      var ids = new ObjectIdCollection();
      var imported = new List<string>();
      var replaced = new List<string>();
      var skipped = new List<Dictionary<string, object?>>();
      var missing = new List<string>();
      using (var sourceTransaction = source.TransactionManager.StartTransaction())
      {
        var table = (BlockTable)sourceTransaction.GetObject(source.BlockTableId, OpenMode.ForRead);
        var candidates = new List<BlockTableRecord>();
        if (requested == null)
        {
          foreach (ObjectId id in table)
          {
            var record = (BlockTableRecord)sourceTransaction.GetObject(id, OpenMode.ForRead);
            if (SkipReason(record) == null) candidates.Add(record);
          }
        }
        else
        {
          foreach (var name in requested)
          {
            if (!table.Has(name)) { missing.Add(name); continue; }
            var record = (BlockTableRecord)sourceTransaction.GetObject(table[name], OpenMode.ForRead);
            var reason = SkipReason(record);
            if (reason != null) skipped.Add(new Dictionary<string, object?> { ["name"] = record.Name, ["reason"] = reason });
            else candidates.Add(record);
          }
        }

        foreach (var record in candidates)
        {
          if (existing.Contains(record.Name))
          {
            if (!overwrite)
            {
              skipped.Add(new Dictionary<string, object?> { ["name"] = record.Name, ["reason"] = "exists" });
              continue;
            }

            replaced.Add(record.Name);
          }
          else
          {
            imported.Add(record.Name);
          }

          ids.Add(record.ObjectId);
        }

        sourceTransaction.Commit();
      }

      if (ids.Count > 0)
      {
        var mapping = new IdMapping();
        source.WblockCloneObjects(ids, database.BlockTableId, mapping,
          overwrite ? DuplicateRecordCloning.Replace : DuplicateRecordCloning.Ignore, false);
      }

      return new Dictionary<string, object?>
      {
        ["sourcePath"] = path,
        ["imported"] = imported,
        ["replaced"] = replaced,
        ["skipped"] = skipped,
        ["missing"] = missing,
      };
    });
  }

  /// <summary>Inserts a block reference in model space (default) or a layout, with attribute references made
  /// from the definition's attribute definitions; given values replace the defaults.</summary>
  public static Task<object?> InsertBlockAsync(JsonObject? parameters)
  {
    var blockName = PluginRuntime.GetRequiredString(parameters, "blockName");
    var x = PluginRuntime.GetRequiredDouble(parameters, "x");
    var y = PluginRuntime.GetRequiredDouble(parameters, "y");
    var z = PluginRuntime.GetOptionalDouble(parameters, "z") ?? 0.0;
    var rotation = (PluginRuntime.GetOptionalDouble(parameters, "rotationDegrees") ?? 0.0) * Math.PI / 180.0;
    var scale = PluginRuntime.GetOptionalDouble(parameters, "scale") ?? 1.0;
    var layer = PluginRuntime.GetOptionalString(parameters, "layer");
    var createLayer = PluginRuntime.GetOptionalBool(parameters, "createLayer") ?? false;
    var layoutName = PluginRuntime.GetOptionalString(parameters, "layout");
    var values = (PluginRuntime.GetParameter(parameters, "attributes") as JsonObject)?.Where(kv => kv.Value != null)
      .ToDictionary(kv => kv.Key, kv => kv.Value!.GetValue<string>(), StringComparer.OrdinalIgnoreCase)
      ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    if (scale <= 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "scale must be > 0. Nothing was inserted.");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var table = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
      if (!table.Has(blockName))
        throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Block '{blockName}' is not defined in the drawing (import it with block_import). Nothing was inserted.");
      var definition = (BlockTableRecord)transaction.GetObject(table[blockName], OpenMode.ForRead);
      if (definition.IsLayout || definition.IsFromExternalReference)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"'{blockName}' is a layout or xref, not a block. Nothing was inserted.");

      var spaceId = string.IsNullOrWhiteSpace(layoutName) || string.Equals(layoutName, "Model", StringComparison.OrdinalIgnoreCase)
        ? SymbolUtilityServices.GetBlockModelSpaceId(database)
        : LayoutCommands.FindLayout(database, transaction, layoutName!).BlockTableRecordId;
      var layerId = string.IsNullOrWhiteSpace(layer) ? ObjectId.Null : LookupUtils.GetOrCreateLayerId(database, transaction, layer!, createLayer);

      var space = (BlockTableRecord)transaction.GetObject(spaceId, OpenMode.ForWrite);
      var reference = new BlockReference(new Point3d(x, y, z), definition.ObjectId)
      {
        Rotation = rotation,
        ScaleFactors = new Scale3d(scale),
      };
      reference.SetDatabaseDefaults(database);
      if (!layerId.IsNull) reference.LayerId = layerId;
      space.AppendEntity(reference);
      transaction.AddNewlyCreatedDBObject(reference, true);

      var attributes = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
      foreach (ObjectId id in definition)
      {
        if (transaction.GetObject(id, OpenMode.ForRead) is not AttributeDefinition attributeDefinition || attributeDefinition.Constant) continue;
        var attribute = new AttributeReference();
        attribute.SetAttributeFromBlock(attributeDefinition, reference.BlockTransform);
        if (values.TryGetValue(attributeDefinition.Tag, out var value)) MultilineAttributes.SetValue(attribute, value);
        if (attributeDefinition.Justify != AttachmentPoint.BaseLeft) attribute.AdjustAlignment(database);
        if (attribute.IsMTextAttribute) attribute.UpdateMTextAttribute();
        reference.AttributeCollection.AppendAttribute(attribute);
        transaction.AddNewlyCreatedDBObject(attribute, true);
        attributes[attribute.Tag] = MultilineAttributes.Value(attribute);
      }

      return new Dictionary<string, object?>
      {
        ["handle"] = reference.Handle.ToString(),
        ["blockName"] = definition.Name,
        ["space"] = spaceId == SymbolUtilityServices.GetBlockModelSpaceId(database) ? "Model" : layoutName,
        ["layer"] = reference.Layer,
        ["position"] = new[] { x, y, z },
        ["rotationDegrees"] = Math.Round(rotation * 180.0 / Math.PI, 6),
        ["scale"] = scale,
        ["attributes"] = attributes,
        ["unknownTags"] = values.Keys.Where(k => !attributes.ContainsKey(k)).ToList(),
        ["annotative"] = definition.Annotative.ToString(),
      };
    });
  }
}
