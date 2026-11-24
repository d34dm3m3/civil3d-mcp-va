using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using CivilEntity = Autodesk.Civil.DatabaseServices.Entity;

namespace Civil3DMcpPlugin;

/// <summary>
/// Display control while working: the style of surfaces, corridors, alignments, profiles and pipe network
/// parts (e.g. a "no display" surface style to hide a corridor surface over pipes), and layer on/off and freeze.
/// </summary>
public static class DisplayCommands
{
  public static Task<object?> SetObjectStyleAsync(JsonObject? parameters)
  {
    var objectType = PluginRuntime.GetRequiredString(parameters, "objectType").Trim().ToLowerInvariant();
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var style = PluginRuntime.GetOptionalString(parameters, "style");
    var alignmentName = PluginRuntime.GetOptionalString(parameters, "alignmentName");
    var pipeStyle = PluginRuntime.GetOptionalString(parameters, "pipeStyle");
    var structureStyle = PluginRuntime.GetOptionalString(parameters, "structureStyle");
    var prefix = PluginRuntime.GetOptionalString(parameters, "namePrefix");
    var codeSetStyle = PluginRuntime.GetOptionalString(parameters, "codeSetStyle");
    var partFamily = PluginRuntime.GetOptionalString(parameters, "partFamily");
    // C189: exact part names (any form, e.g. "DNB1"), combined with namePrefix/partFamily when given
    var partNames = parameters?["names"] is JsonArray namesArray
      ? namesArray.Select(n => n?.ToString() ?? "").Where(n => n.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase)
      : null;
    var layer = PluginRuntime.GetOptionalString(parameters, "layer");
    var createLayer = PluginRuntime.GetOptionalBool(parameters, "createLayer") ?? false;

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var cdoc = (CivilDocument)civilDoc;
      string RequiredStyle() => string.IsNullOrWhiteSpace(style)
        ? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"style is required for objectType '{objectType}'.")
        : style!;

      Dictionary<string, object?> SetOne(ObjectId objectId, object styles, string kind)
      {
        var styleId = LookupUtils.GetStyleIdStrict(styles, transaction, RequiredStyle(), kind);
        var entity = CivilObjectUtils.GetRequiredObject<CivilEntity>(transaction, objectId, OpenMode.ForWrite);
        var previous = StyleName(transaction, entity.StyleId);
        entity.StyleId = styleId;
        return new Dictionary<string, object?>
        {
          ["objectType"] = objectType,
          ["name"] = name,
          ["previousStyle"] = previous,
          ["style"] = RequiredStyle(),
        };
      }

      switch (objectType)
      {
        case "surface":
          return SetOne(CivilObjectUtils.FindSurfaceByName(cdoc, transaction, name, OpenMode.ForRead).ObjectId, cdoc.Styles.SurfaceStyles, "Surface style");
        case "corridor":
        {
          // Corridor feature lines, links and points follow the code set style, not the corridor style.
          var corridorId = CivilObjectUtils.FindCorridorByName(cdoc, transaction, name, OpenMode.ForRead).ObjectId;
          var result = string.IsNullOrWhiteSpace(style)
            ? new Dictionary<string, object?> { ["objectType"] = objectType, ["name"] = name }
            : SetOne(corridorId, cdoc.Styles.CorridorStyles, "Corridor style");
          if (!string.IsNullOrWhiteSpace(codeSetStyle))
          {
            var codeSetId = LookupUtils.GetStyleIdStrict(cdoc.Styles.CodeSetStyles, transaction, codeSetStyle, "Code set style");
            var corridor = CivilObjectUtils.GetRequiredObject<Corridor>(transaction, corridorId, OpenMode.ForWrite);
            result["previousCodeSetStyle"] = StyleName(transaction, corridor.CodeSetStyleId);
            corridor.CodeSetStyleId = codeSetId;
            result["codeSetStyle"] = codeSetStyle;
          }
          else if (string.IsNullOrWhiteSpace(style))
          {
            throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Give style and/or codeSetStyle for objectType 'corridor'.");
          }

          return result;
        }
        case "alignment":
          return SetOne(CivilObjectUtils.FindAlignmentByName(cdoc, transaction, name).ObjectId, cdoc.Styles.AlignmentStyles, "Alignment style");
        case "profile":
        {
          if (string.IsNullOrWhiteSpace(alignmentName))
            throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "alignmentName is required for objectType 'profile'.");
          var alignment = CivilObjectUtils.FindAlignmentByName(cdoc, transaction, alignmentName!);
          return SetOne(CivilObjectUtils.FindProfileByName(alignment, transaction, name, OpenMode.ForRead).ObjectId, cdoc.Styles.ProfileStyles, "Profile style");
        }
        case "pipe_network":
          return SetNetworkPartStyles(cdoc, database, transaction, name, pipeStyle, structureStyle, prefix, partFamily, layer, createLayer, partNames);
        default:
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
            $"objectType must be surface, corridor, alignment, profile or pipe_network, not '{objectType}'.");
      }
    });
  }

  /// <summary>Styles and/or layer for the parts of a network, optionally only those named "{prefix}-…" and/or
  /// of one part family. A layer moves the parts whose style is set, or all matching parts when no style is given.</summary>
  private static Dictionary<string, object?> SetNetworkPartStyles(CivilDocument cdoc, Database database, Transaction transaction,
    string networkName, string? pipeStyle, string? structureStyle, string? prefix, string? partFamily, string? layer, bool createLayer,
    HashSet<string>? partNames = null)
  {
    if (string.IsNullOrWhiteSpace(pipeStyle) && string.IsNullOrWhiteSpace(structureStyle) && string.IsNullOrWhiteSpace(layer))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Give pipeStyle, structureStyle and/or layer for objectType 'pipe_network'.");

    var network = PipeNetworkCommands.FindPipeNetworkByName(cdoc, transaction, networkName, OpenMode.ForRead);
    if (!string.IsNullOrWhiteSpace(layer)) EnsureLayer(database, transaction, layer!, createLayer);
    bool Matches(Part part) =>
      (string.IsNullOrWhiteSpace(prefix) || (part.Name ?? "").StartsWith(prefix + "-", StringComparison.OrdinalIgnoreCase))
      && (string.IsNullOrWhiteSpace(partFamily) || string.Equals(part.PartFamilyName, partFamily, StringComparison.OrdinalIgnoreCase))
      && (partNames == null || partNames.Contains(part.Name ?? ""));

    (int Styled, int Moved) Apply(IEnumerable<ObjectId> ids, ObjectId styleId)
    {
      int styled = 0, moved = 0;
      foreach (var id in ids)
      {
        var part = CivilObjectUtils.GetRequiredObject<Part>(transaction, id, OpenMode.ForRead);
        if (!Matches(part)) continue;
        part.UpgradeOpen();
        if (!styleId.IsNull)
        {
          part.StyleId = styleId;
          styled++;
        }

        if (!string.IsNullOrWhiteSpace(layer))
        {
          part.Layer = layer;
          moved++;
        }
      }

      return (styled, moved);
    }

    var pipeStyleId = string.IsNullOrWhiteSpace(pipeStyle) ? ObjectId.Null
      : LookupUtils.GetStyleIdStrict(cdoc.Styles.PipeStyles, transaction, pipeStyle, "Pipe style");
    var structureStyleId = string.IsNullOrWhiteSpace(structureStyle) ? ObjectId.Null
      : LookupUtils.GetStyleIdStrict(cdoc.Styles.StructureStyles, transaction, structureStyle, "Structure style");
    var onlyLayer = pipeStyleId.IsNull && structureStyleId.IsNull;
    var touchPipes = !pipeStyleId.IsNull || onlyLayer;
    var touchStructures = !structureStyleId.IsNull || onlyLayer;
    var pipes = touchPipes ? Apply(network.GetPipeIds().Cast<ObjectId>(), pipeStyleId) : (Styled: 0, Moved: 0);
    var structures = touchStructures ? Apply(network.GetStructureIds().Cast<ObjectId>(), structureStyleId) : (Styled: 0, Moved: 0);

    return new Dictionary<string, object?>
    {
      ["objectType"] = "pipe_network",
      ["name"] = networkName,
      ["namePrefix"] = prefix,
      ["partFamily"] = partFamily,
      ["names"] = partNames?.ToList(),
      ["pipeStyle"] = pipeStyle,
      ["pipesChanged"] = pipes.Styled,
      ["structureStyle"] = structureStyle,
      ["structuresChanged"] = structures.Styled,
      ["layer"] = layer,
      ["partsMovedToLayer"] = pipes.Moved + structures.Moved,
    };
  }

  private static void EnsureLayer(Database database, Transaction transaction, string name, bool create)
  {
    var table = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
    if (table.Has(name)) return;
    if (!create)
      throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Layer '{name}' does not exist; pass createLayer=true to create it. Nothing was changed.");
    table.UpgradeOpen();
    var record = new LayerTableRecord { Name = name };
    table.Add(record);
    transaction.AddNewlyCreatedDBObject(record, true);
  }

  /// <summary>Turns a corridor's feature lines on or off per code (FeatureLineCodeInfo.IsConnected; all
  /// codes when none are given) and rebuilds the corridor. A code set style only hides the codes it lists;
  /// the rest are drawn with the default style, which is why a "no display" code set style left the trench lines visible.</summary>
  public static Task<object?> SetCorridorFeatureLinesAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var connected = PluginRuntime.GetOptionalBool(parameters, "connected")
      ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "connected (true/false) is required.");
    var codes = (PluginRuntime.GetParameter(parameters, "codes") as JsonArray)?.Select(n => n?.GetValue<string>()).OfType<string>().ToList();
    var rebuild = PluginRuntime.GetOptionalBool(parameters, "rebuild") ?? true;

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var corridor = CivilObjectUtils.FindCorridorByName((CivilDocument)civilDoc, transaction, name, OpenMode.ForWrite);
      var infos = corridor.FeatureLineCodeInfos;
      var all = infos.CodeNames();
      var unknown = codes?.Where(c => !all.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList() ?? new List<string>();
      if (unknown.Count > 0)
        throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Codes not in corridor '{name}': {string.Join(", ", unknown)}. Codes: {string.Join(", ", all)}.");

      var changed = new List<Dictionary<string, object?>>();
      foreach (var code in all)
      {
        if (codes != null && !codes.Contains(code, StringComparer.OrdinalIgnoreCase)) continue;
        var info = infos[code];
        var before = info.IsConnected;
        info.IsConnected = connected;
        changed.Add(new Dictionary<string, object?> { ["code"] = code, ["was"] = before, ["now"] = connected });
      }

      if (rebuild) corridor.Rebuild();
      return new Dictionary<string, object?> { ["corridor"] = name, ["codes"] = changed, ["rebuilt"] = rebuild };
    });
  }

  /// <summary>Model space annotation scale (CANNOSCALE) and the Civil 3D drawing scale that styles and
  /// labels are sized by; set changes the annotation scale, which Civil 3D follows.</summary>
  public static Task<object?> GetScaleAsync(JsonObject? parameters) =>
    CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) => DescribeScale((CivilDocument)civilDoc, database));

  public static Task<object?> SetScaleAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "scale");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var scales = database.ObjectContextManager.GetContextCollection("ACDB_ANNOTATIONSCALES");
      Autodesk.AutoCAD.DatabaseServices.AnnotationScale? match = null;
      var available = new List<string>();
      foreach (var context in scales)
      {
        if (context is not Autodesk.AutoCAD.DatabaseServices.AnnotationScale scale) continue;
        available.Add(scale.Name);
        if (string.Equals(scale.Name, name, StringComparison.OrdinalIgnoreCase)) match = scale;
      }

      if (match == null)
        throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Annotation scale '{name}' is not in the drawing. Available: {string.Join(", ", available)}.");
      var before = DescribeScale((CivilDocument)civilDoc, database);
      database.Cannoscale = match;
      var after = DescribeScale((CivilDocument)civilDoc, database);
      after["before"] = before;
      return after;
    });
  }

  private static Dictionary<string, object?> DescribeScale(CivilDocument civilDoc, Database database)
  {
    double? drawingScale = null;
    try
    {
      drawingScale = civilDoc.Settings.DrawingSettings.UnitZoneSettings.DrawingScale;
    }
    catch (Exception)
    {
      // older or partial settings objects
    }

    return new Dictionary<string, object?>
    {
      ["annotationScale"] = database.Cannoscale?.Name,
      ["civilDrawingScale"] = drawingScale,
    };
  }

  public static Task<object?> ListLayersAsync(JsonObject? parameters)
  {
    var contains = PluginRuntime.GetOptionalString(parameters, "contains");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var table = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
      var layers = new List<Dictionary<string, object?>>();
      foreach (ObjectId id in table)
      {
        var layer = (LayerTableRecord)transaction.GetObject(id, OpenMode.ForRead);
        if (!string.IsNullOrWhiteSpace(contains) && layer.Name.IndexOf(contains, StringComparison.OrdinalIgnoreCase) < 0) continue;
        layers.Add(LayerState(layer, database, transaction));
      }

      return new Dictionary<string, object?> { ["layers"] = layers.OrderBy(l => (string?)l["name"], StringComparer.OrdinalIgnoreCase).ToList() };
    });
  }

  public static Task<object?> SetLayersAsync(JsonObject? parameters)
  {
    var names = (PluginRuntime.GetParameter(parameters, "layers") as JsonArray)?.Select(n => n?.GetValue<string>()).OfType<string>().ToList();
    if (names == null || names.Count == 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "layers must list at least one layer name.");
    var on = PluginRuntime.GetOptionalBool(parameters, "on");
    var frozen = PluginRuntime.GetOptionalBool(parameters, "frozen");
    var plot = PluginRuntime.GetOptionalBool(parameters, "plot");
    var linetype = PluginRuntime.GetOptionalString(parameters, "linetype");
    // A layer's colour is an ACI number 1-255 (ByLayer/ByBlock make no sense for a layer).
    short? color = null;
    if (PluginRuntime.GetParameter(parameters, "color") is JsonValue colorNode)
    {
      if (colorNode.GetValueKind() != System.Text.Json.JsonValueKind.Number || colorNode.GetValue<int>() is < 1 or > 255)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "A layer colour is an ACI number 1-255. Nothing was changed.");
      color = (short)colorNode.GetValue<int>();
    }

    LineWeight? lineweight = null;
    if (PluginRuntime.GetParameter(parameters, "lineweight") is JsonValue weightNode)
    {
      lineweight = weightNode.GetValueKind() == System.Text.Json.JsonValueKind.Number
        ? AcadCommands.StandardLineWeight(weightNode.GetValue<int>())
        : string.Equals(weightNode.GetValue<string>(), "Default", StringComparison.OrdinalIgnoreCase) ? LineWeight.ByLineWeightDefault
        : throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "A layer lineweight is hundredths of a mm (e.g. 35) or Default. Nothing was changed.");
    }

    // C99j: transparency in percent (0 = opaque, 90 = the most a layer takes), as the Layer Properties Manager; also on
    // xref-dependent layers, where it is a host-side override kept with VISRETAIN 1 (pilot project: the base
    // map xref's layers plotted black although the template's page setups now plot transparency, M41).
    double? transparency = null;
    if (PluginRuntime.GetParameter(parameters, "transparency") is JsonValue transNode)
    {
      if (transNode.GetValueKind() != System.Text.Json.JsonValueKind.Number || transNode.GetValue<double>() is < 0 or > 90)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "A layer transparency is a percent 0-90. Nothing was changed.");
      transparency = transNode.GetValue<double>();
    }

    if (on == null && frozen == null && plot == null && color == null && lineweight == null && transparency == null && string.IsNullOrWhiteSpace(linetype))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Give on, frozen, color, linetype, lineweight, transparency and/or plot.");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var table = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
      var missing = names.Where(n => !table.Has(n)).ToList();
      if (missing.Count > 0)
        throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Layers not found: {string.Join(", ", missing)}. Nothing was changed.");
      if (frozen == true && names.Any(n => table[n] == database.Clayer))
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "The current layer cannot be frozen. Nothing was changed.");
      var linetypeId = string.IsNullOrWhiteSpace(linetype) ? ObjectId.Null : AcadCommands.LoadedLinetypeId(database, transaction, linetype!);
      if (!linetypeId.IsNull && ((LinetypeTableRecord)transaction.GetObject(linetypeId, OpenMode.ForRead)).Name is var ltName
        && (string.Equals(ltName, "ByLayer", StringComparison.OrdinalIgnoreCase) || string.Equals(ltName, "ByBlock", StringComparison.OrdinalIgnoreCase)))
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "A layer's linetype must be a real linetype, not ByLayer/ByBlock. Nothing was changed.");

      var result = new List<Dictionary<string, object?>>();
      foreach (var name in names)
      {
        var layer = (LayerTableRecord)transaction.GetObject(table[name], OpenMode.ForWrite);
        if (on.HasValue) layer.IsOff = !on.Value;
        if (frozen.HasValue) layer.IsFrozen = frozen.Value;
        if (plot.HasValue) layer.IsPlottable = plot.Value;
        if (color.HasValue) layer.Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, color.Value);
        if (!linetypeId.IsNull) layer.LinetypeObjectId = linetypeId;
        if (lineweight.HasValue) layer.LineWeight = lineweight.Value;
        if (transparency.HasValue) layer.Transparency = new Autodesk.AutoCAD.Colors.Transparency(LayerStandardCommands.TransparencyAlpha(transparency.Value));
        result.Add(LayerState(layer, database, transaction));
      }

      return new Dictionary<string, object?>
      {
        ["layers"] = result,
        ["note"] = frozen.HasValue ? "Freezing or thawing may need REGEN before the view updates." : null,
      };
    });
  }

  private static Dictionary<string, object?> LayerState(LayerTableRecord layer, Database database, Transaction transaction) => new()
  {
    ["name"] = layer.Name,
    ["on"] = !layer.IsOff,
    ["frozen"] = layer.IsFrozen,
    ["locked"] = layer.IsLocked,
    ["current"] = layer.ObjectId == database.Clayer,
    ["color"] = layer.Color.ToString(),
    ["linetype"] = layer.LinetypeObjectId.IsNull ? null : (transaction.GetObject(layer.LinetypeObjectId, OpenMode.ForRead) as LinetypeTableRecord)?.Name,
    // Hundredths of a mm; -3 = Default.
    ["lineweight"] = layer.LineWeight == LineWeight.ByLineWeightDefault ? "Default" : (object)(int)layer.LineWeight,
    ["plot"] = layer.IsPlottable,
    // C99j: percent as the Layer Properties Manager shows it (0 = opaque)
    ["transparency"] = LayerStandardCommands.TransparencyText(layer.Transparency),
  };

  /// <summary>Linetypes loaded in the drawing (names usable as linetype in layer_set, entities_style_set and
  /// create_polyline).</summary>
  public static Task<object?> ListLinetypesAsync(JsonObject? parameters)
  {
    var contains = PluginRuntime.GetOptionalString(parameters, "contains");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var table = (LinetypeTable)transaction.GetObject(database.LinetypeTableId, OpenMode.ForRead);
      var linetypes = new List<Dictionary<string, object?>>();
      foreach (ObjectId id in table)
      {
        var record = (LinetypeTableRecord)transaction.GetObject(id, OpenMode.ForRead);
        if (!string.IsNullOrWhiteSpace(contains) && record.Name.IndexOf(contains, StringComparison.OrdinalIgnoreCase) < 0) continue;
        linetypes.Add(new Dictionary<string, object?>
        {
          ["name"] = record.Name,
          ["description"] = string.IsNullOrWhiteSpace(record.AsciiDescription) ? null : record.AsciiDescription,
          ["patternLength"] = Math.Round(record.PatternLength, 6),
        });
      }

      return new Dictionary<string, object?> { ["linetypes"] = linetypes.OrderBy(l => (string?)l["name"], StringComparer.OrdinalIgnoreCase).ToList() };
    });
  }

  private static string? StyleName(Transaction transaction, ObjectId styleId)
  {
    if (styleId.IsNull) return null;
    try
    {
      return CivilObjectUtils.GetName(transaction.GetObject(styleId, OpenMode.ForRead));
    }
    catch (Exception)
    {
      return null;
    }
  }
}
