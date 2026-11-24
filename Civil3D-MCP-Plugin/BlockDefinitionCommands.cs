using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.Geometry;

namespace Civil3DMcpPlugin;

/// <summary>
/// Block definitions (C68): read a definition's geometry, texts and attribute definitions with its references, and
/// change attribute definitions (tag, position in block units, height, style, justification, prompt, default,
/// visibility; add or remove), replace text in the definition and sync the references.
/// Use case: the company title block "Stamp" has attributes outside their fields, fields without attributes and texts
/// with a "?" in place of a missing character; a project moved the attributes on every sheet instead.
/// Syncing only touches the tags that were changed, created, renamed or removed, so attributes moved by hand on a
/// sheet keep their place; the values of existing attributes are kept.
/// </summary>
public static class BlockDefinitionCommands
{
  private sealed record AttributeSpec(string Tag, string? NewTag, bool Create, Point3d? At, double? Height, double? WidthFactor,
    string? TextStyle, AttachmentPoint? Justify, string? Prompt, string? DefaultValue, bool? Invisible, Color? Color, string? Layer,
    bool? Multiline = null, double? MTextWidth = null);

  public static Task<object?> GetBlockDefinitionAsync(JsonObject? parameters)
  {
    var blockName = PluginRuntime.GetRequiredString(parameters, "blockName");
    var includeGeometry = PluginRuntime.GetOptionalBool(parameters, "includeGeometry") ?? true;
    var maxEntities = PluginRuntime.GetOptionalInt(parameters, "maxEntities") ?? 500;
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var record = FindBlock(database, transaction, blockName, OpenMode.ForRead);
      var entities = new List<Dictionary<string, object?>>();
      var attributes = new List<Dictionary<string, object?>>();
      var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
      Extents3d? extents = null;
      var truncated = false;
      foreach (ObjectId id in record)
      {
        if (transaction.GetObject(id, OpenMode.ForRead) is not Entity entity) continue;
        var type = entity.GetType().Name;
        counts[type] = counts.TryGetValue(type, out var n) ? n + 1 : 1;
        try
        {
          var e = entity.GeometricExtents;
          if (extents == null) extents = e;
          else { var grown = extents.Value; grown.AddExtents(e); extents = grown; }
        }
        catch (Exception)
        {
          // Empty text and some proxies have no extents.
        }

        if (entity is AttributeDefinition definition) { attributes.Add(DescribeAttribute(definition, transaction)); continue; }
        if (!includeGeometry) continue;
        if (entities.Count >= maxEntities) { truncated = true; continue; }
        entities.Add(DescribeEntity(entity, transaction));
      }

      return new Dictionary<string, object?>
      {
        ["name"] = record.Name,
        ["origin"] = Xy(record.Origin),
        ["units"] = record.Units.ToString(),
        ["annotative"] = record.Annotative == AnnotativeStates.True,
        ["explodable"] = record.Explodable,
        ["entityCounts"] = counts,
        ["extents"] = extents == null ? null : new Dictionary<string, object?>
        {
          ["minX"] = R(extents.Value.MinPoint.X), ["minY"] = R(extents.Value.MinPoint.Y),
          ["maxX"] = R(extents.Value.MaxPoint.X), ["maxY"] = R(extents.Value.MaxPoint.Y),
        },
        ["attributeDefinitions"] = attributes,
        ["entities"] = includeGeometry ? entities : null,
        ["truncated"] = truncated,
        ["references"] = References(record, transaction),
      };
    });
  }

  public static Task<object?> SetBlockDefinitionAsync(JsonObject? parameters)
  {
    var blockName = PluginRuntime.GetRequiredString(parameters, "blockName");
    var specs = ParseSpecs(PluginRuntime.GetParameter(parameters, "attributeDefinitions") as JsonArray);
    var removeTags = (PluginRuntime.GetParameter(parameters, "removeAttributes") as JsonArray ?? new JsonArray())
      .Select(n => n?.GetValue<string>()?.Trim() ?? "").Where(t => t.Length > 0).ToList();
    var texts = (PluginRuntime.GetParameter(parameters, "texts") as JsonArray ?? new JsonArray()).Select(n =>
    {
      var find = n?["find"]?.GetValue<string>() ?? "";
      if (find.Length == 0) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "texts[].find must not be empty. Nothing was changed.");
      return (Find: find, Replace: n?["replace"]?.GetValue<string>() ?? "");
    }).ToList();
    var sync = PluginRuntime.GetOptionalBool(parameters, "syncReferences") ?? true;
    var attsync = PluginRuntime.GetOptionalBool(parameters, "attsync") ?? false;
    if (specs.Count + removeTags.Count + texts.Count == 0 && !attsync)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Give attributeDefinitions, removeAttributes, texts and/or attsync: true. Nothing was changed.");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var record = FindBlock(database, transaction, blockName, OpenMode.ForWrite);
      var definitions = new Dictionary<string, AttributeDefinition>(StringComparer.OrdinalIgnoreCase);
      foreach (ObjectId id in record)
        if (transaction.GetObject(id, OpenMode.ForRead) is AttributeDefinition d && !definitions.ContainsKey(d.Tag))
          definitions[d.Tag] = d;

      // Validate everything before the first change.
      var styles = (TextStyleTable)transaction.GetObject(database.TextStyleTableId, OpenMode.ForRead);
      var layers = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
      foreach (var spec in specs)
      {
        if (spec.Layer != null && !layers.Has(spec.Layer))
          throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"{spec.Tag}: layer '{spec.Layer}' does not exist. Nothing was changed.");
        if (!definitions.ContainsKey(spec.Tag) && !spec.Create)
          throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND",
            $"Block '{blockName}' has no attribute '{spec.Tag}' (pass create: true to add it). Tags: {string.Join(", ", definitions.Keys)}. Nothing was changed.");
        if (spec.TextStyle != null && !styles.Has(spec.TextStyle))
          throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Text style '{spec.TextStyle}' was not found. Nothing was changed.");
        if (spec.NewTag != null && !string.Equals(spec.NewTag, spec.Tag, StringComparison.OrdinalIgnoreCase) && definitions.ContainsKey(spec.NewTag))
          throw new JsonRpcDispatchException("CIVIL3D.CONFLICT", $"Block '{blockName}' already has an attribute '{spec.NewTag}'. Nothing was changed.");
      }
      foreach (var tag in removeTags)
        if (!definitions.ContainsKey(tag))
          throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Block '{blockName}' has no attribute '{tag}' to remove. Nothing was changed.");

      var changed = new List<Dictionary<string, object?>>();
      var touched = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);   // final tag -> tag on the references before
      foreach (var spec in specs)
      {
        var created = false;
        if (!definitions.TryGetValue(spec.Tag, out var definition))
        {
          definition = new AttributeDefinition();
          definition.SetDatabaseDefaults(database);
          definition.Tag = spec.Tag;
          definition.Prompt = spec.Prompt ?? spec.Tag;
          definition.TextString = spec.DefaultValue ?? "";
          definition.Height = spec.Height!.Value;
          definition.Position = spec.At!.Value;
          record.AppendEntity(definition);
          transaction.AddNewlyCreatedDBObject(definition, true);
          definitions[spec.Tag] = definition;
          created = true;
        }
        else
        {
          definition.UpgradeOpen();
        }

        var anchor = definition.Justify == AttachmentPoint.BaseLeft ? definition.Position : definition.AlignmentPoint;
        if (spec.TextStyle != null) definition.TextStyleId = styles[spec.TextStyle];
        if (spec.Height != null) definition.Height = spec.Height.Value;
        if (spec.WidthFactor != null) definition.WidthFactor = spec.WidthFactor.Value;
        if (spec.Prompt != null) definition.Prompt = spec.Prompt;
        if (spec.DefaultValue != null) definition.TextString = spec.DefaultValue;
        if (spec.Invisible != null) definition.Invisible = spec.Invisible.Value;
        if (spec.Color != null) definition.Color = spec.Color;
        if (spec.Layer != null) definition.Layer = spec.Layer;
        if (spec.Justify != null && spec.Multiline != true && !definition.IsMTextAttributeDefinition) definition.Justify = spec.Justify.Value;
        var at = spec.At ?? anchor;
        if (definition.IsMTextAttributeDefinition || spec.Multiline == true)
        {
          // a multiline definition is justified (top left): AutoCAD rebuilds Position from AlignmentPoint when the object
          // closes, so both are set (C116; setting only Position looked right in the reply and was lost afterwards)
          definition.Position = at;
          if (definition.Justify != AttachmentPoint.BaseLeft) definition.AlignmentPoint = at;
        }
        else if (definition.Justify == AttachmentPoint.BaseLeft) definition.Position = at;
        else definition.AlignmentPoint = at;
        // C116: multiline attribute (top left at the position, width in block units, \P line breaks in the default)
        if (spec.Multiline == true || definition.IsMTextAttributeDefinition)
        {
          MultilineAttributes.Apply(definition, spec.MTextWidth, spec.DefaultValue, spec.At);
        }
        else
        {
          Realign(definition, database);
        }

        var before = created ? null : spec.Tag;
        if (spec.NewTag != null) definition.Tag = spec.NewTag;
        touched[definition.Tag] = before;
        changed.Add(new Dictionary<string, object?>
        {
          ["tag"] = definition.Tag,
          ["created"] = created,
          ["renamedFrom"] = spec.NewTag != null && !created ? spec.Tag : null,
        });
      }

      foreach (var tag in removeTags)
      {
        definitions[tag].UpgradeOpen();
        definitions[tag].Erase();
      }

      var textResults = new List<Dictionary<string, object?>>();
      foreach (var (find, replace) in texts)
      {
        var count = 0;
        foreach (ObjectId id in record)
        {
          var entity = transaction.GetObject(id, OpenMode.ForRead);
          if (entity is DBText text && entity is not AttributeDefinition && text.TextString.Contains(find, StringComparison.Ordinal))
          {
            text.UpgradeOpen();
            text.TextString = text.TextString.Replace(find, replace, StringComparison.Ordinal);
            count++;
          }
          else if (entity is MText mtext && mtext.Contents.Contains(find, StringComparison.Ordinal))
          {
            mtext.UpgradeOpen();
            mtext.Contents = mtext.Contents.Replace(find, replace, StringComparison.Ordinal);
            count++;
          }
        }

        textResults.Add(new Dictionary<string, object?> { ["find"] = find, ["replace"] = replace, ["replaced"] = count });
      }

      var synced = sync && touched.Count + removeTags.Count > 0 ? SyncReferences(record, transaction, database, touched, removeTags) : 0;
      var full = attsync ? FullSync(record, transaction, database) : null;
      var after = new List<Dictionary<string, object?>>();
      foreach (ObjectId id in record)
        if (transaction.GetObject(id, OpenMode.ForRead) is AttributeDefinition d) after.Add(DescribeAttribute(d, transaction));

      return new Dictionary<string, object?>
      {
        ["blockName"] = record.Name,
        ["changed"] = changed,
        ["removed"] = removeTags,
        ["texts"] = textResults,
        ["referencesSynced"] = synced,
        ["attsync"] = full,
        ["attributeDefinitions"] = after,
      };
    });
  }

  /// <summary>Brings the touched tags of every reference in line with the definition (position, height, style,
  /// justification, tag), keeps their values, adds created ones with the default and erases removed ones.</summary>
  private static int SyncReferences(BlockTableRecord record, Transaction transaction, Database database,
    IReadOnlyDictionary<string, string?> touched, IReadOnlyCollection<string> removed)
  {
    var definitions = new List<AttributeDefinition>();
    foreach (ObjectId id in record)
      if (transaction.GetObject(id, OpenMode.ForRead) is AttributeDefinition d && !d.Constant && touched.ContainsKey(d.Tag))
        definitions.Add(d);

    var count = 0;
    foreach (ObjectId referenceId in record.GetBlockReferenceIds(true, false))
    {
      var reference = (BlockReference)transaction.GetObject(referenceId, OpenMode.ForWrite);
      var existing = new Dictionary<string, AttributeReference>(StringComparer.OrdinalIgnoreCase);
      foreach (ObjectId attributeId in reference.AttributeCollection)
        if (!attributeId.IsErased && transaction.GetObject(attributeId, OpenMode.ForWrite) is AttributeReference a && !existing.ContainsKey(a.Tag))
          existing[a.Tag] = a;

      foreach (var tag in removed)
        if (existing.TryGetValue(tag, out var gone)) gone.Erase();

      foreach (var definition in definitions)
      {
        var previous = touched[definition.Tag];
        if ((previous != null && existing.TryGetValue(previous, out var attribute)) || existing.TryGetValue(definition.Tag, out attribute))
        {
          var value = attribute.TextString;
          attribute.SetAttributeFromBlock(definition, reference.BlockTransform);
          attribute.TextString = value;
          attribute.Color = definition.Color;
        }
        else
        {
          attribute = new AttributeReference();
          attribute.SetAttributeFromBlock(definition, reference.BlockTransform);
          attribute.TextString = definition.TextString;
          attribute.Color = definition.Color;
          reference.AttributeCollection.AppendAttribute(attribute);
          transaction.AddNewlyCreatedDBObject(attribute, true);
        }

        Realign(attribute, database);
      }

      count++;
    }

    return count;
  }

  /// <summary>Full ATTSYNC (C68b): every reference gets exactly the definition's non-constant attributes, with position,
  /// height, style and justification from the definition; values of tags that exist are kept, stale tags (left by an
  /// earlier definition) are erased and missing ones added with the default. Need: the template's Stamp references
  /// still carried an older stamp's tags (RITAD, PROJNR, INRAD1…) after the definition had been replaced.</summary>
  private static Dictionary<string, object?> FullSync(BlockTableRecord record, Transaction transaction, Database database)
  {
    var definitions = new List<AttributeDefinition>();
    foreach (ObjectId id in record)
      if (transaction.GetObject(id, OpenMode.ForRead) is AttributeDefinition d && !d.Constant) definitions.Add(d);

    var references = 0;
    var erased = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
    var added = 0;
    foreach (ObjectId referenceId in record.GetBlockReferenceIds(true, false))
    {
      var reference = (BlockReference)transaction.GetObject(referenceId, OpenMode.ForWrite);
      var (gone, addedTags) = SyncReferenceAttributes(reference, definitions, transaction, database);
      erased.UnionWith(gone);
      added += addedTags.Count;
      references++;
    }

    return new Dictionary<string, object?> { ["references"] = references, ["staleTagsErased"] = erased.ToList(), ["attributesAdded"] = added };
  }

  /// <summary>One reference gets exactly the given (non-constant) attribute definitions: position, height, style and
  /// justification from the definition, values of tags it has kept, stale or duplicate tags erased, missing ones added
  /// with the default. Used by the full ATTSYNC (C68b) and by C87b when a reference is pointed at another block.</summary>
  internal static (List<string> Erased, List<string> Added) SyncReferenceAttributes(BlockReference reference,
    IReadOnlyList<AttributeDefinition> definitions, Transaction transaction, Database database)
  {
    var tags = new HashSet<string>(definitions.Select(d => d.Tag), StringComparer.OrdinalIgnoreCase);
    var erased = new List<string>();
    var added = new List<string>();
    var existing = new Dictionary<string, AttributeReference>(StringComparer.OrdinalIgnoreCase);
    foreach (ObjectId attributeId in reference.AttributeCollection)
    {
      if (attributeId.IsErased) continue;   // erased by an attribute sync in this drawing
      if (transaction.GetObject(attributeId, OpenMode.ForWrite) is not AttributeReference a) continue;
      if (!tags.Contains(a.Tag) || existing.ContainsKey(a.Tag))
      {
        erased.Add(a.Tag);
        a.Erase();
        continue;
      }
      existing[a.Tag] = a;
    }

    foreach (var definition in definitions)
    {
      if (existing.TryGetValue(definition.Tag, out var attribute))
      {
        var value = attribute.TextString;
        attribute.SetAttributeFromBlock(definition, reference.BlockTransform);
        attribute.TextString = value;
        attribute.Color = definition.Color;
      }
      else
      {
        attribute = new AttributeReference();
        attribute.SetAttributeFromBlock(definition, reference.BlockTransform);
        if (!definition.IsMTextAttributeDefinition) attribute.TextString = definition.TextString;   // a multiline one gets its MText from the definition
        attribute.Color = definition.Color;
        reference.AttributeCollection.AppendAttribute(attribute);
        transaction.AddNewlyCreatedDBObject(attribute, true);
        added.Add(definition.Tag);
      }

      Realign(attribute, database);
    }

    return (erased, added);
  }

  private static List<AttributeSpec> ParseSpecs(JsonArray? array)
  {
    var specs = new List<AttributeSpec>();
    foreach (var node in array ?? new JsonArray())
    {
      if (node is not JsonObject o) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "attributeDefinitions[] must be objects. Nothing was changed.");
      var tag = o["tag"]?.GetValue<string>()?.Trim() ?? "";
      var newTag = o["newTag"]?.GetValue<string>()?.Trim();
      foreach (var t in new[] { tag, newTag })
        if (t != null && (t.Length == 0 || t.Any(char.IsWhiteSpace)))
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Attribute tag '{t}' must be non-empty without spaces. Nothing was changed.");
      double? Num(string key) => o[key] == null ? null : o[key]!.GetValue<double>();
      var x = Num("x");
      var y = Num("y");
      if ((x == null) != (y == null)) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{tag}: give x and y together. Nothing was changed.");
      var height = Num("height");
      if (height is <= 0) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{tag}: height must be > 0. Nothing was changed.");
      var width = Num("widthFactor");
      if (width is <= 0) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{tag}: widthFactor must be > 0. Nothing was changed.");
      var create = o["create"]?.GetValue<bool>() ?? false;
      if (create && (x == null || height == null))
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{tag}: create needs x, y and height. Nothing was changed.");
      AttachmentPoint? justify = null;
      var justifyText = o["justify"]?.GetValue<string>();
      if (justifyText != null)
        justify = Enum.TryParse<AttachmentPoint>(justifyText, false, out var parsed) ? parsed
          : throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{tag}: justify '{justifyText}' is not an AutoCAD attachment point. Nothing was changed.");
      var mtextWidth = Num("width");
      if (mtextWidth is < 0) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{tag}: width must be >= 0 (0 = no wrapping). Nothing was changed.");
      var multiline = o["multiline"]?.GetValue<bool>();
      if (multiline == false) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{tag}: multiline can only be turned on (C116); recreate the attribute to make it single-line. Nothing was changed.");
      specs.Add(new AttributeSpec(tag, newTag, create, x == null ? null : new Point3d(x.Value, y!.Value, 0.0), height, width,
        o["textStyle"]?.GetValue<string>(), justify, o["prompt"]?.GetValue<string>(), o["defaultValue"]?.GetValue<string>(),
        o["invisible"]?.GetValue<bool>(), ParseColor(o["color"], tag), o["layer"]?.GetValue<string>(), multiline, mtextWidth));
    }

    var duplicate = specs.GroupBy(s => s.Tag, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
    if (duplicate != null) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Attribute '{duplicate.Key}' is given twice. Nothing was changed.");
    return specs;
  }

  /// <summary>
  /// C84: one colour for the drawn entities of a block definition (attribute definitions are left alone) and, for a
  /// dynamic block, of its anonymous copies (the references show those). Optionally only entities with a given
  /// colour (fromColor). Use case: the company template's scale bar plotted green in colour sheets; its entities carry an
  /// explicit green, so the insert's layer did not matter (the scale bar should be black).
  /// </summary>
  public static Task<object?> SetBlockEntitiesColorAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "blockName");
    var color = ParseColor(PluginRuntime.GetParameter(parameters, "color") as JsonNode, "color")
      ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Give color (ACI, ByLayer or ByBlock). Nothing was changed.");
    var fromColor = ParseColor(PluginRuntime.GetParameter(parameters, "fromColor") as JsonNode, "fromColor");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var master = FindBlock(database, transaction, name, OpenMode.ForRead);
      var records = new List<ObjectId> { master.ObjectId };
      if (master.IsDynamicBlock) records.AddRange(master.GetAnonymousBlockIds().Cast<ObjectId>());
      var changed = 0;
      var before = new Dictionary<string, int>();
      foreach (var recordId in records)
      {
        var record = (BlockTableRecord)transaction.GetObject(recordId, OpenMode.ForRead);
        foreach (ObjectId id in record)
        {
          if (transaction.GetObject(id, OpenMode.ForRead) is not Entity entity || entity is AttributeDefinition) continue;
          var key = entity.Color.ColorMethod == ColorMethod.ByAci ? $"ACI {entity.Color.ColorIndex}" : entity.Color.ColorMethod.ToString();
          if (recordId == master.ObjectId) before[key] = before.GetValueOrDefault(key) + 1;
          if (fromColor != null && !entity.Color.Equals(fromColor)) continue;
          if (entity.Color.Equals(color)) continue;
          entity.UpgradeOpen();
          entity.Color = color;
          changed++;
        }
      }
      return new Dictionary<string, object?>
      {
        ["block"] = master.Name,
        ["definitions"] = records.Count,
        ["entitiesChanged"] = changed,
        ["colorsBefore"] = before,
        ["note"] = "References show the change after a regen.",
      };
    });
  }

  /// <summary>ACI 1-255, 0 = ByBlock, 256 = ByLayer, or the words ByLayer/ByBlock.</summary>
  internal static Color? ParseColor(JsonNode? node, string tag)
  {
    if (node == null) return null;
    if (node is JsonValue v && v.TryGetValue<string>(out var word))
    {
      if (string.Equals(word, "ByLayer", StringComparison.OrdinalIgnoreCase)) return Color.FromColorIndex(ColorMethod.ByLayer, 256);
      if (string.Equals(word, "ByBlock", StringComparison.OrdinalIgnoreCase)) return Color.FromColorIndex(ColorMethod.ByBlock, 0);
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{tag}: color '{word}' must be an ACI number, ByLayer or ByBlock. Nothing was changed.");
    }
    var index = node.GetValue<int>();
    return index switch
    {
      0 => Color.FromColorIndex(ColorMethod.ByBlock, 0),
      256 => Color.FromColorIndex(ColorMethod.ByLayer, 256),
      >= 1 and <= 255 => Color.FromColorIndex(ColorMethod.ByAci, (short)index),
      _ => throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{tag}: color {index} must lie between 0 and 256. Nothing was changed."),
    };
  }

  internal static BlockTableRecord FindBlock(Database database, Transaction transaction, string name, OpenMode mode)
  {
    var table = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
    if (!table.Has(name)) throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Block '{name}' was not found. Nothing was changed.");
    var record = (BlockTableRecord)transaction.GetObject(table[name], mode);
    if (record.IsLayout || record.IsFromExternalReference || record.IsAnonymous)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"'{name}' is a layout, an external reference or an anonymous block, not a block definition. Nothing was changed.");
    return record;
  }

  private static List<Dictionary<string, object?>> References(BlockTableRecord record, Transaction transaction)
  {
    var references = new List<Dictionary<string, object?>>();
    foreach (ObjectId id in record.GetBlockReferenceIds(true, false))
    {
      var reference = (BlockReference)transaction.GetObject(id, OpenMode.ForRead);
      var owner = (BlockTableRecord)transaction.GetObject(reference.OwnerId, OpenMode.ForRead);
      var space = owner.IsLayout ? ((Layout)transaction.GetObject(owner.LayoutId, OpenMode.ForRead)).LayoutName : owner.Name;
      references.Add(new Dictionary<string, object?>
      {
        ["handle"] = reference.Handle.ToString(),
        ["space"] = space,
        ["position"] = Xy(reference.Position),
        ["scale"] = new[] { R(reference.ScaleFactors.X), R(reference.ScaleFactors.Y) },
        ["rotationDegrees"] = R(reference.Rotation * 180.0 / Math.PI),
      });
    }

    return references;
  }

  private static Dictionary<string, object?> DescribeAttribute(AttributeDefinition d, Transaction transaction) => new()
  {
    ["tag"] = d.Tag,
    ["prompt"] = d.Prompt,
    ["defaultValue"] = MultilineAttributes.Value(d),
    ["position"] = Xy(d.Position),
    ["alignmentPoint"] = d.Justify == AttachmentPoint.BaseLeft ? null : Xy(d.AlignmentPoint),
    ["justify"] = d.Justify.ToString(),
    ["height"] = R(d.Height),
    ["widthFactor"] = R(d.WidthFactor),
    ["rotationDegrees"] = R(d.Rotation * 180.0 / Math.PI),
    ["textStyle"] = StyleName(d.TextStyleId, transaction),
    ["layer"] = d.Layer,
    ["color"] = d.Color.IsByLayer ? "ByLayer" : d.Color.IsByBlock ? "ByBlock" : d.Color.ColorIndex,
    ["invisible"] = d.Invisible,
    ["constant"] = d.Constant,
    ["preset"] = d.Preset,
    ["lockPosition"] = d.LockPositionInBlock,
    ["multiline"] = d.IsMTextAttributeDefinition,
    ["width"] = d.IsMTextAttributeDefinition ? Math.Round(d.MTextAttributeDefinition.Width, 4) : null,
    ["handle"] = d.Handle.ToString(),
  };

  private static Dictionary<string, object?> DescribeEntity(Entity entity, Transaction transaction)
  {
    var item = new Dictionary<string, object?>
    {
      ["type"] = entity.GetType().Name, ["handle"] = entity.Handle.ToString(), ["layer"] = entity.Layer,
      ["color"] = entity.Color.IsByLayer ? "ByLayer" : entity.Color.IsByBlock ? "ByBlock" : entity.Color.ColorIndex,
      ["lineweight"] = entity.LineWeight.ToString(),
      ["transparency"] = LayerStandardCommands.TransparencyText(entity.Transparency),   // C99j
      ["linetype"] = entity.Linetype,                                                  // C147
      ["linetypeScale"] = Math.Round(entity.LinetypeScale, 4),
    };
    switch (entity)
    {
      case Line line:
        item["type"] = "line"; item["start"] = Xy(line.StartPoint); item["end"] = Xy(line.EndPoint);
        break;
      case Polyline polyline:
        item["type"] = "polyline"; item["closed"] = polyline.Closed;
        item["vertices"] = Enumerable.Range(0, polyline.NumberOfVertices).Select(i => Xy(polyline.GetPoint3dAt(i))).ToList();
        break;
      case Circle circle:
        item["type"] = "circle"; item["center"] = Xy(circle.Center); item["radius"] = R(circle.Radius);
        break;
      case Arc arc:
        item["type"] = "arc"; item["center"] = Xy(arc.Center); item["radius"] = R(arc.Radius);
        item["startDegrees"] = R(arc.StartAngle * 180.0 / Math.PI); item["endDegrees"] = R(arc.EndAngle * 180.0 / Math.PI);
        break;
      case DBText text:
        item["type"] = "text"; item["text"] = text.TextString; item["position"] = Xy(text.Position);
        item["alignmentPoint"] = text.Justify == AttachmentPoint.BaseLeft ? null : Xy(text.AlignmentPoint);
        item["justify"] = text.Justify.ToString(); item["height"] = R(text.Height); item["widthFactor"] = R(text.WidthFactor);
        item["rotationDegrees"] = R(text.Rotation * 180.0 / Math.PI); item["textStyle"] = StyleName(text.TextStyleId, transaction);
        item["codepoints"] = NonAscii(text.TextString);
        break;
      case MText mtext:
        item["type"] = "mtext"; item["text"] = mtext.Text; item["contents"] = mtext.Contents; item["location"] = Xy(mtext.Location);
        item["height"] = R(mtext.TextHeight); item["width"] = R(mtext.Width); item["attachment"] = mtext.Attachment.ToString();
        item["textStyle"] = StyleName(mtext.TextStyleId, transaction); item["codepoints"] = NonAscii(mtext.Contents);
        break;
      case BlockReference reference:
        item["type"] = "insert"; item["name"] = reference.Name; item["position"] = Xy(reference.Position);
        break;
      default:
        try
        {
          var e = entity.GeometricExtents;
          item["extents"] = new[] { R(e.MinPoint.X), R(e.MinPoint.Y), R(e.MaxPoint.X), R(e.MaxPoint.Y) };
        }
        catch (Exception)
        {
          item["extents"] = null;
        }
        break;
    }

    return item;
  }

  /// <summary>Characters outside ASCII and the question mark, as U+XXXX, so a "?" stored in the text can be told
  /// apart from a glyph missing in the font.</summary>
  private static List<string>? NonAscii(string text)
  {
    var codes = text.Where(c => c > 127 || c == '?').Select(c => $"U+{(int)c:X4}").Distinct().ToList();
    return codes.Count == 0 ? null : codes;
  }

  internal static void Realign(DBText text, Database database)
  {
    if (text is AttributeReference { IsMTextAttribute: true } multiline) { multiline.UpdateMTextAttribute(); return; }
    if (text is AttributeDefinition { IsMTextAttributeDefinition: true } multilineDefinition) { multilineDefinition.UpdateMTextAttributeDefinition(); return; }
    if (text.Justify == AttachmentPoint.BaseLeft) return;
    var working = HostApplicationServices.WorkingDatabase;
    try
    {
      HostApplicationServices.WorkingDatabase = database;
      text.AdjustAlignment(database);
    }
    finally
    {
      HostApplicationServices.WorkingDatabase = working;
    }
  }

  private static string? StyleName(ObjectId id, Transaction transaction) =>
    id.IsNull ? null : ((TextStyleTableRecord)transaction.GetObject(id, OpenMode.ForRead)).Name;

  private static double[] Xy(Point3d p) => new[] { R(p.X), R(p.Y) };

  private static double R(double v) => Math.Round(v, 4);
}
