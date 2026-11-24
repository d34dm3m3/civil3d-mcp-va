using System.Text.Json;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace Civil3DMcpPlugin;

/// <summary>
/// Block definition geometry (C87): add, change and erase the drawn entities of a block definition (lines, polylines,
/// rectangles, circles, texts, multiline texts, inserts): layer, colour, linetype, lineweight, text height, text and
/// position, or a move; and create a definition, empty or as a copy of another. C87b points existing references at
/// another definition and keeps their attribute values. Use case: the review of the company template's title block
/// (2026-10-09, G4–G9): frame 0,7 mm, labels 1,8 mm, status in the title column, TRV columns in the revision table, a
/// compact title block for A2–A4 and a legend block; C68 only edits attribute definitions, which stay with
/// block_definition_set. Everything named in a call is looked up and checked before the first change; a selector
/// that matches nothing (or not the expected count) stops the call.
/// </summary>
public static class BlockGeometryCommands
{
  private static readonly HashSet<string> SelectorTypes = new(StringComparer.Ordinal) { "any", "line", "polyline", "circle", "arc", "text", "mtext", "insert", "hatch" };
  private static readonly HashSet<string> AddTypes = new(StringComparer.Ordinal) { "line", "polyline", "rectangle", "circle", "text", "mtext", "insert" };

  private sealed record Selector(HashSet<string>? Handles, string Type, string? Layer, string? Text, string? TextContains,
    Extents3d? Within, double? MinLength, int? Expect, string Label);

  private sealed record Props(string? Layer, Color? Color, string? Linetype, double? LinetypeScale, LineWeight? Lineweight, Transparency? Transparency = null);

  private sealed record SetOp(Selector Select, Props Props, Vector3d? Move, Point3d? Start, Point3d? End, Point3d? Position,
    double? Height, string? Text, double? WidthFactor, string? TextStyle);

  private sealed record AddOp(int Index, string Type, Props Props, List<Point3d> Points, bool Closed, double Radius, string? Text,
    double Height, double Width, string? TextStyle, AttachmentPoint? Justify, double Rotation, double? WidthFactor, string? Block, Scale3d Scale,
    bool Plinegen = false);

  public static Task<object?> EditBlockEntitiesAsync(JsonObject? parameters)
  {
    var blockName = PluginRuntime.GetRequiredString(parameters, "blockName");
    var create = PluginRuntime.GetOptionalBool(parameters, "create") ?? false;
    var copyFrom = PluginRuntime.GetOptionalString(parameters, "copyFrom");
    var origin = OptionalPoint(PluginRuntime.GetParameter(parameters, "origin") as JsonNode, "origin");
    var erase = (PluginRuntime.GetParameter(parameters, "erase") as JsonArray ?? new JsonArray()).Select((n, i) => ParseSelector(n, $"erase[{i}]")).ToList();
    var sets = (PluginRuntime.GetParameter(parameters, "set") as JsonArray ?? new JsonArray()).Select((n, i) => ParseSet(n, $"set[{i}]")).ToList();
    var adds = (PluginRuntime.GetParameter(parameters, "add") as JsonArray ?? new JsonArray()).Select((n, i) => ParseAdd(n, i)).ToList();
    if (!create && copyFrom == null && erase.Count + sets.Count + adds.Count == 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Give create or copyFrom, erase, set and/or add. Nothing was changed.");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var table = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
      var layers = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
      var styles = (TextStyleTable)transaction.GetObject(database.TextStyleTableId, OpenMode.ForRead);
      BlockTableRecord? source = null;
      var making = create || copyFrom != null;
      if (making)
      {
        try
        {
          SymbolUtilityServices.ValidateSymbolName(blockName, false);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"'{blockName}' is not a valid block name. Nothing was changed.");
        }
        if (table.Has(blockName))
          throw new JsonRpcDispatchException("CIVIL3D.CONFLICT", $"Block '{blockName}' already exists; create and copyFrom only make new ones. Nothing was changed.");
        if (copyFrom != null) source = BlockDefinitionCommands.FindBlock(database, transaction, copyFrom, OpenMode.ForRead);
      }

      void CheckProps(Props p, string label)
      {
        if (p.Layer != null && !layers.Has(p.Layer))
          throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"{label}: layer '{p.Layer}' does not exist. Nothing was changed.");
        if (p.Linetype != null) AcadCommands.LoadedLinetypeId(database, transaction, p.Linetype);
      }
      void CheckStyle(string? style, string label)
      {
        if (style != null && !styles.Has(style))
          throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"{label}: text style '{style}' was not found. Nothing was changed.");
      }
      foreach (var s in sets) { CheckProps(s.Props, s.Select.Label); CheckStyle(s.TextStyle, s.Select.Label); }
      foreach (var a in adds)
      {
        var label = $"add[{a.Index}]";
        CheckProps(a.Props, label);
        CheckStyle(a.TextStyle, label);
        if (a.Block == null) continue;
        if (!table.Has(a.Block)) throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"{label}: block '{a.Block}' was not found. Nothing was changed.");
        if (string.Equals(a.Block, blockName, StringComparison.OrdinalIgnoreCase))
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{label}: a block cannot contain itself. Nothing was changed.");
      }

      BlockTableRecord record;
      if (making)
      {
        // A failure further down rolls the transaction back, the new definition included.
        table.UpgradeOpen();
        record = new BlockTableRecord { Name = blockName, Origin = origin ?? source?.Origin ?? Point3d.Origin };
        if (source != null) record.Units = source.Units;
        var newId = table.Add(record);
        transaction.AddNewlyCreatedDBObject(record, true);
        if (source != null)
        {
          var ids = new ObjectIdCollection();
          foreach (ObjectId id in source) ids.Add(id);
          if (ids.Count > 0) database.DeepCloneObjects(ids, newId, new IdMapping(), false);
        }
      }
      else
      {
        record = BlockDefinitionCommands.FindBlock(database, transaction, blockName, OpenMode.ForWrite);
      }

      var entities = new List<Entity>();
      foreach (ObjectId id in record)
        if (transaction.GetObject(id, OpenMode.ForRead) is Entity e && e is not AttributeDefinition) entities.Add(e);
      List<Entity> Match(Selector s)
      {
        var found = entities.Where(e => Matches(e, s)).ToList();
        if (s.Expect != null ? found.Count != s.Expect : found.Count == 0)
          throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND",
            $"{s.Label} matched {found.Count} entities in '{blockName}'{(s.Expect != null ? $" (expected {s.Expect})" : "")}. Nothing was changed.");
        return found;
      }

      var toErase = erase.SelectMany(Match).Distinct().ToList();
      var toSet = sets.Select(s => (Op: s, Found: Match(s.Select))).ToList();
      foreach (var (op, found) in toSet)
      {
        var label = op.Select.Label;
        if (found.Any(toErase.Contains))
          throw new JsonRpcDispatchException("CIVIL3D.CONFLICT", $"{label} matches an entity that erase removes. Nothing was changed.");
        if ((op.Height != null || op.Text != null || op.TextStyle != null) && found.Any(e => e is not DBText and not MText))
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{label}: height, text and textStyle only apply to texts. Nothing was changed.");
        if (op.WidthFactor != null && found.Any(e => e is not DBText))
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{label}: widthFactor only applies to single-line texts. Nothing was changed.");
        if ((op.Start != null || op.End != null) && (found.Count != 1 || found[0] is not Line))
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{label}: start and end need exactly one line. Nothing was changed.");
        if (op.Position != null && (found.Count != 1 || found[0] is not (DBText or MText or BlockReference)))
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{label}: position needs exactly one text, multiline text or insert. Nothing was changed.");
      }

      foreach (var e in toErase)
      {
        e.UpgradeOpen();
        e.Erase();
      }

      var changed = 0;
      foreach (var (op, found) in toSet)
      {
        foreach (var e in found)
        {
          e.UpgradeOpen();
          Apply(e, op.Props, database, transaction);
          switch (e)
          {
            case DBText t:
              if (op.TextStyle != null) t.TextStyleId = styles[op.TextStyle];
              if (op.Height != null) t.Height = op.Height.Value;
              if (op.WidthFactor != null) t.WidthFactor = op.WidthFactor.Value;
              if (op.Text != null) t.TextString = op.Text;
              if (op.Position != null)
              {
                if (t.Justify == AttachmentPoint.BaseLeft) t.Position = op.Position.Value;
                else t.AlignmentPoint = op.Position.Value;
              }
              break;
            case MText m:
              if (op.TextStyle != null) m.TextStyleId = styles[op.TextStyle];
              if (op.Height != null) m.TextHeight = op.Height.Value;
              if (op.Text != null) m.Contents = op.Text;
              if (op.Position != null) m.Location = op.Position.Value;
              break;
            case Line l:
              if (op.Start != null) l.StartPoint = op.Start.Value;
              if (op.End != null) l.EndPoint = op.End.Value;
              break;
            case BlockReference r when op.Position != null:
              r.Position = op.Position.Value;
              break;
          }

          if (op.Move != null) e.TransformBy(Matrix3d.Displacement(op.Move.Value));
          if (e is DBText moved) BlockDefinitionCommands.Realign(moved, database);
          changed++;
        }
      }

      var added = new List<Dictionary<string, object?>>();
      foreach (var a in adds)
      {
        var entity = Build(a, database, table, styles);
        record.AppendEntity(entity);
        transaction.AddNewlyCreatedDBObject(entity, true);
        if (entity is DBText text) BlockDefinitionCommands.Realign(text, database);
        added.Add(new Dictionary<string, object?> { ["type"] = a.Type, ["handle"] = entity.Handle.ToString() });
      }

      var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
      foreach (ObjectId id in record)
      {
        if (transaction.GetObject(id, OpenMode.ForRead) is not Entity e) continue;
        var type = e is AttributeDefinition ? "attdef" : TypeName(e);
        counts[type] = counts.GetValueOrDefault(type) + 1;
      }

      return new Dictionary<string, object?>
      {
        ["blockName"] = record.Name,
        ["created"] = making,
        ["copiedFrom"] = source?.Name,
        ["erased"] = toErase.Count,
        ["changed"] = changed,
        ["added"] = added,
        ["entityCounts"] = counts,
        ["note"] = "References show the change after a regen.",
      };
    });
  }

  /// <summary>
  /// C87b: point existing references of one block definition at another (in all layouts or the given ones), optionally
  /// with a new uniform scale, and bring their attributes in line with the new definition: values of tags both have
  /// are kept, tags the new one lacks are erased, new tags get their default. Need: the compact title block replaces
  /// the scaled-down A1 title block on the A2–A4 sheets of the company template and keeps what was filled in.
  /// </summary>
  public static Task<object?> ReplaceBlockReferencesAsync(JsonObject? parameters)
  {
    var from = PluginRuntime.GetRequiredString(parameters, "from");
    var to = PluginRuntime.GetRequiredString(parameters, "to");
    var layouts = (PluginRuntime.GetParameter(parameters, "layoutNames") as JsonArray)?.Select(n => n?.GetValue<string>() ?? "").Where(s => s.Length > 0)
      .ToHashSet(StringComparer.OrdinalIgnoreCase);
    var scale = PluginRuntime.GetOptionalDouble(parameters, "scale");
    if (scale is <= 0) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "scale must be > 0. Nothing was changed.");
    if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "from and to are the same block. Nothing was changed.");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var fromRecord = BlockDefinitionCommands.FindBlock(database, transaction, from, OpenMode.ForRead);
      var toRecord = BlockDefinitionCommands.FindBlock(database, transaction, to, OpenMode.ForRead);
      if (layouts != null)
      {
        var dictionary = (DBDictionary)transaction.GetObject(database.LayoutDictionaryId, OpenMode.ForRead);
        var missing = layouts.Where(l => !dictionary.Contains(l)).ToList();
        if (missing.Count > 0)
          throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Layouts not found: {string.Join(", ", missing)}. Nothing was changed.");
      }

      var definitions = new List<AttributeDefinition>();
      foreach (ObjectId id in toRecord)
        if (transaction.GetObject(id, OpenMode.ForRead) is AttributeDefinition d && !d.Constant) definitions.Add(d);

      var replaced = new List<Dictionary<string, object?>>();
      foreach (ObjectId referenceId in fromRecord.GetBlockReferenceIds(true, false))
      {
        var reference = (BlockReference)transaction.GetObject(referenceId, OpenMode.ForRead);
        var owner = (BlockTableRecord)transaction.GetObject(reference.OwnerId, OpenMode.ForRead);
        var space = owner.IsLayout ? ((Layout)transaction.GetObject(owner.LayoutId, OpenMode.ForRead)).LayoutName : owner.Name;
        if (layouts != null && !layouts.Contains(space)) continue;
        reference.UpgradeOpen();
        reference.BlockTableRecord = toRecord.ObjectId;
        if (scale != null) reference.ScaleFactors = new Scale3d(scale.Value);
        var (erased, addedTags) = BlockDefinitionCommands.SyncReferenceAttributes(reference, definitions, transaction, database);
        replaced.Add(new Dictionary<string, object?>
        {
          ["layout"] = space, ["handle"] = reference.Handle.ToString(), ["erasedTags"] = erased, ["addedTags"] = addedTags,
        });
      }

      if (replaced.Count == 0)
        throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND",
          $"No references of '{from}'{(layouts != null ? " in the given layouts" : "")}. Nothing was changed.");
      return new Dictionary<string, object?> { ["from"] = fromRecord.Name, ["to"] = toRecord.Name, ["replaced"] = replaced };
    });
  }

  private static Entity Build(AddOp a, Database database, BlockTable table, TextStyleTable styles)
  {
    Entity entity;
    switch (a.Type)
    {
      case "line":
        entity = new Line(a.Points[0], a.Points[1]);
        break;
      case "polyline":
      case "rectangle":
        var polyline = new Polyline();
        for (var i = 0; i < a.Points.Count; i++) polyline.AddVertexAt(i, new Point2d(a.Points[i].X, a.Points[i].Y), 0, 0, 0);
        polyline.Closed = a.Closed;
        // C126: linetype generation over the vertices (PLINEGEN). Use case: existing VA from
        // GIS has many short segments (median 1,8 m) and the linetype pattern restarted at every vertex, so it looked uneven.
        polyline.Plinegen = a.Plinegen;
        entity = polyline;
        break;
      case "circle":
        entity = new Circle(a.Points[0], Vector3d.ZAxis, a.Radius);
        break;
      case "text":
        var text = new DBText();
        text.SetDatabaseDefaults(database);
        text.TextString = a.Text ?? "";
        text.Height = a.Height;
        if (a.TextStyle != null) text.TextStyleId = styles[a.TextStyle];
        text.Rotation = a.Rotation;
        if (a.WidthFactor != null) text.WidthFactor = a.WidthFactor.Value;
        text.Justify = a.Justify ?? AttachmentPoint.BaseLeft;
        text.Position = a.Points[0];
        if (text.Justify != AttachmentPoint.BaseLeft) text.AlignmentPoint = a.Points[0];
        entity = text;
        break;
      case "mtext":
        var mtext = new MText();
        mtext.SetDatabaseDefaults(database);
        mtext.Location = a.Points[0];
        mtext.TextHeight = a.Height;
        mtext.Width = a.Width;
        if (a.TextStyle != null) mtext.TextStyleId = styles[a.TextStyle];
        mtext.Attachment = a.Justify ?? AttachmentPoint.TopLeft;
        mtext.Rotation = a.Rotation;
        mtext.Contents = a.Text ?? "";
        entity = mtext;
        break;
      default:   // insert
        entity = new BlockReference(a.Points[0], table[a.Block!]) { Rotation = a.Rotation, ScaleFactors = a.Scale };
        break;
    }

    if (entity is not DBText and not MText) entity.SetDatabaseDefaults(database);
    // Block content defaults to layer 0, ByLayer, whatever the current layer and settings are.
    entity.Layer = "0";
    entity.Color = Color.FromColorIndex(ColorMethod.ByLayer, 256);
    entity.Linetype = "ByLayer";
    entity.LineWeight = LineWeight.ByLayer;
    return ApplyTo(entity, a.Props, database);
  }

  private static Entity ApplyTo(Entity entity, Props p, Database database)
  {
    if (p.Layer != null) entity.Layer = p.Layer;
    if (p.Color != null) entity.Color = p.Color;
    if (p.Linetype != null) entity.Linetype = p.Linetype;
    if (p.LinetypeScale != null) entity.LinetypeScale = p.LinetypeScale.Value;
    if (p.Lineweight != null) entity.LineWeight = p.Lineweight.Value;
    if (p.Transparency != null) entity.Transparency = p.Transparency.Value;
    return entity;
  }

  private static void Apply(Entity entity, Props p, Database database, Transaction transaction)
  {
    if (p.Layer != null) entity.Layer = p.Layer;
    if (p.Color != null) entity.Color = p.Color;
    if (p.Linetype != null) entity.LinetypeId = AcadCommands.LoadedLinetypeId(database, transaction, p.Linetype);
    if (p.LinetypeScale != null) entity.LinetypeScale = p.LinetypeScale.Value;
    if (p.Lineweight != null) entity.LineWeight = p.Lineweight.Value;
    if (p.Transparency != null) entity.Transparency = p.Transparency.Value;
  }

  private static bool Matches(Entity e, Selector s)
  {
    if (s.Handles != null && !s.Handles.Contains(e.Handle.ToString())) return false;
    if (s.Type != "any" && TypeName(e) != s.Type) return false;
    if (s.Layer != null && !string.Equals(e.Layer, s.Layer, StringComparison.OrdinalIgnoreCase)) return false;
    var text = e switch { DBText t => t.TextString, MText m => m.Text, _ => null };
    if (s.Text != null && !string.Equals(text, s.Text, StringComparison.Ordinal)) return false;
    if (s.TextContains != null && (text == null || !text.Contains(s.TextContains, StringComparison.Ordinal))) return false;
    if (s.MinLength != null)
    {
      var length = e switch { Line l => l.Length, Polyline p => p.Length, Arc a => a.Length, _ => (double?)null };
      if (length == null || length < s.MinLength) return false;
    }
    if (s.Within != null)
    {
      try
      {
        var x = e.GeometricExtents;
        const double tolerance = 1e-6;
        var w = s.Within.Value;
        if (x.MinPoint.X < w.MinPoint.X - tolerance || x.MinPoint.Y < w.MinPoint.Y - tolerance
            || x.MaxPoint.X > w.MaxPoint.X + tolerance || x.MaxPoint.Y > w.MaxPoint.Y + tolerance) return false;
      }
      catch (Exception)
      {
        return false;   // empty text and some proxies have no extents
      }
    }
    return true;
  }

  private static string TypeName(Entity e) => e switch
  {
    Line => "line", Polyline => "polyline", Circle => "circle", Arc => "arc", MText => "mtext", DBText => "text",
    BlockReference => "insert", Hatch => "hatch", _ => e.GetType().Name.ToLowerInvariant(),
  };

  private static Selector ParseSelector(JsonNode? node, string label)
  {
    if (node is not JsonObject o)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{label} must be {{handles?, type?, layer?, text?, textContains?, within?, minLength?, expect?}}. Nothing was changed.");
    var handles = o["handles"] is JsonArray h ? h.Select(x => x?.GetValue<string>()?.Trim() ?? "").Where(x => x.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase) : null;
    var type = (o["type"]?.GetValue<string>() ?? "any").ToLowerInvariant();
    if (!SelectorTypes.Contains(type))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{label}: type '{type}' is not one of {string.Join(", ", SelectorTypes)}. Nothing was changed.");
    Extents3d? within = null;
    if (o["within"] is JsonArray w)
    {
      if (w.Count != 4) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{label}: within is [minX, minY, maxX, maxY]. Nothing was changed.");
      var v = w.Select(x => x!.GetValue<double>()).ToArray();
      within = new Extents3d(new Point3d(Math.Min(v[0], v[2]), Math.Min(v[1], v[3]), 0), new Point3d(Math.Max(v[0], v[2]), Math.Max(v[1], v[3]), 0));
    }
    var layer = o["layer"]?.GetValue<string>();
    var text = o["text"]?.GetValue<string>();
    var contains = o["textContains"]?.GetValue<string>();
    var minLength = o["minLength"]?.GetValue<double>();
    var expect = o["expect"]?.GetValue<int>();
    if (expect is < 0) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{label}: expect must be >= 0. Nothing was changed.");
    if (handles == null && type == "any" && layer == null && text == null && contains == null && within == null && minLength == null)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{label}: give at least one condition; an empty selector would match everything. Nothing was changed.");
    return new Selector(handles, type, layer, text, contains, within, minLength, expect, label);
  }

  private static Props ParseProps(JsonObject o, string label)
  {
    LineWeight? lineweight = null;
    if (o["lineweight"] is JsonValue lw)
    {
      if (lw.GetValueKind() == JsonValueKind.Number)
      {
        if (!lw.TryGetValue<int>(out var hundredths))
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{label}: lineweight is hundredths of a mm (70 = 0,7 mm). Nothing was changed.");
        lineweight = AcadCommands.StandardLineWeight(hundredths);
      }
      else
      {
        lineweight = lw.GetValue<string>().ToLowerInvariant() switch
        {
          "bylayer" => LineWeight.ByLayer,
          "byblock" => LineWeight.ByBlock,
          "default" => LineWeight.ByLineWeightDefault,
          var other => throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{label}: lineweight '{other}' must be a number, ByLayer, ByBlock or Default. Nothing was changed."),
        };
      }
    }
    var linetypeScale = o["linetypeScale"]?.GetValue<double>();
    if (linetypeScale is <= 0) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{label}: linetypeScale must be > 0. Nothing was changed.");
    // C99j: transparency ByLayer, ByBlock or percent 0-90 (the company logo's hatches carried their own, which showed once the
    // page setups plot transparency, M41)
    Transparency? transparency = null;
    if (o["transparency"] is JsonValue tv)
    {
      transparency = tv.GetValueKind() == JsonValueKind.Number
        ? tv.GetValue<double>() is var pct and >= 0 and <= 90 ? new Transparency(LayerStandardCommands.TransparencyAlpha(pct))
          : throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{label}: transparency is a percent 0-90, ByLayer or ByBlock. Nothing was changed.")
        : tv.GetValue<string>().ToLowerInvariant() switch
        {
          "bylayer" => new Transparency(TransparencyMethod.ByLayer),
          "byblock" => new Transparency(TransparencyMethod.ByBlock),
          var other => throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{label}: transparency '{other}' must be a percent, ByLayer or ByBlock. Nothing was changed."),
        };
    }
    return new Props(o["layer"]?.GetValue<string>(), BlockDefinitionCommands.ParseColor(o["color"], label), o["linetype"]?.GetValue<string>(), linetypeScale, lineweight, transparency);
  }

  private static SetOp ParseSet(JsonNode? node, string label)
  {
    if (node is not JsonObject o) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{label} must be {{select, …changes}}. Nothing was changed.");
    var select = ParseSelector(o["select"], $"{label}.select");
    var props = ParseProps(o, label);
    var move = OptionalPoint(o["move"], $"{label}.move");
    var height = o["height"]?.GetValue<double>();
    var width = o["widthFactor"]?.GetValue<double>();
    if (height is <= 0 || width is <= 0) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{label}: height and widthFactor must be > 0. Nothing was changed.");
    var op = new SetOp(select, props, move == null ? null : new Vector3d(move.Value.X, move.Value.Y, 0), OptionalPoint(o["start"], $"{label}.start"),
      OptionalPoint(o["end"], $"{label}.end"), OptionalPoint(o["position"], $"{label}.position"), height, o["text"]?.GetValue<string>(), width,
      o["textStyle"]?.GetValue<string>());
    if (props == new Props(null, null, null, null, null, null) && op.Move == null && op.Start == null && op.End == null && op.Position == null
        && op.Height == null && op.Text == null && op.WidthFactor == null && op.TextStyle == null)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{label}: nothing to change. Nothing was changed.");
    return op;
  }

  private static AddOp ParseAdd(JsonNode? node, int index)
  {
    var label = $"add[{index}]";
    if (node is not JsonObject o) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{label} must be an entity {{type, …}}. Nothing was changed.");
    var type = o["type"]?.GetValue<string>()?.ToLowerInvariant() ?? "";
    if (!AddTypes.Contains(type))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{label}: type '{type}' is not one of {string.Join(", ", AddTypes)}. Nothing was changed.");
    var props = ParseProps(o, label);
    Point3d Need(string key) => OptionalPoint(o[key], $"{label}.{key}")
      ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{label}: {type} needs {key} [x, y]. Nothing was changed.");
    double Positive(string key)
    {
      var v = o[key]?.GetValue<double>() ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{label}: {type} needs {key}. Nothing was changed.");
      return v > 0 ? v : throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{label}: {key} must be > 0. Nothing was changed.");
    }
    var rotation = (o["rotationDegrees"]?.GetValue<double>() ?? 0.0) * Math.PI / 180.0;
    var points = new List<Point3d>();
    var closed = false;
    var plinegen = false;
    double radius = 0, height = 0, width = 0;
    string? text = null, style = o["textStyle"]?.GetValue<string>(), block = null;
    AttachmentPoint? justify = null;
    double? widthFactor = null;
    var scale = new Scale3d(1.0);
    switch (type)
    {
      case "line":
        points.Add(Need("from")); points.Add(Need("to"));
        break;
      case "polyline":
        if (o["points"] is not JsonArray list || list.Count < 2)
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{label}: polyline needs points [[x, y], …] (two or more). Nothing was changed.");
        points.AddRange(list.Select((p, i) => OptionalPoint(p, $"{label}.points[{i}]")!.Value));
        closed = o["closed"]?.GetValue<bool>() ?? false;
        plinegen = o["plinegen"]?.GetValue<bool>() ?? false;
        break;
      case "rectangle":
        var a = Need("min");
        var b = Need("max");
        points.AddRange(new[] { new Point3d(a.X, a.Y, 0), new Point3d(b.X, a.Y, 0), new Point3d(b.X, b.Y, 0), new Point3d(a.X, b.Y, 0) });
        closed = true;
        break;
      case "circle":
        points.Add(Need("center")); radius = Positive("radius");
        break;
      case "text":
      case "mtext":
        points.Add(Need("at")); height = Positive("height");
        text = o["text"]?.GetValue<string>() ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{label}: {type} needs text. Nothing was changed.");
        var key = type == "text" ? "justify" : "attachment";
        var justifyText = o[key]?.GetValue<string>();
        if (justifyText != null)
          justify = Enum.TryParse<AttachmentPoint>(justifyText, false, out var parsed) ? parsed
            : throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{label}: {key} '{justifyText}' is not an AutoCAD attachment point. Nothing was changed.");
        if (type == "mtext")
        {
          width = o["width"]?.GetValue<double>() ?? 0.0;
          if (width < 0) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{label}: width must be >= 0. Nothing was changed.");
        }
        else
        {
          widthFactor = o["widthFactor"]?.GetValue<double>();
          if (widthFactor is <= 0) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{label}: widthFactor must be > 0. Nothing was changed.");
        }
        break;
      default:   // insert
        points.Add(Need("at"));
        block = o["block"]?.GetValue<string>() ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{label}: insert needs block. Nothing was changed.");
        if (o["scale"] is JsonArray pair && pair.Count == 2) scale = new Scale3d(pair[0]!.GetValue<double>(), pair[1]!.GetValue<double>(), 1.0);
        else if (o["scale"] is JsonValue one) scale = new Scale3d(one.GetValue<double>());
        if (scale.X <= 0 || scale.Y <= 0) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{label}: scale must be > 0. Nothing was changed.");
        break;
    }
    return new AddOp(index, type, props, points, closed, radius, text, height, width, style, justify, rotation, widthFactor, block, scale, plinegen);
  }

  private static Point3d? OptionalPoint(JsonNode? node, string label)
  {
    if (node == null) return null;
    if (node is JsonArray a && a.Count == 2) return new Point3d(a[0]!.GetValue<double>(), a[1]!.GetValue<double>(), 0.0);
    throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{label} must be [x, y]. Nothing was changed.");
  }
}
