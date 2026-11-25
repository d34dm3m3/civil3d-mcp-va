using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AcEntity = Autodesk.AutoCAD.DatabaseServices.Entity;

namespace Civil3DMcpPlugin;

/// <summary>
/// C223 (F4, 2026-11-24) drawEntities: draws a described set of plain AutoCAD objects under one script id prefix in
/// one transaction, as a rerunnable state instead of "list on the prefix, erase, draw one object at a time, set the id
/// afterwards" (use case: about 30 project scripts with ids, 18 with handle files, three own drawing classes).
///
/// Each object carries the script id (C3DMCP_ID XData, C105) and, as idData, what drawEntities wrote last time: the
/// item type, a hash of the item spec, the geometry read back from the object after writing (points, sampled above 400)
/// and its text. On a rerun, per item id:
/// - missing in the drawing: created;
/// - present and still as written last time (geometry within tolerance, same text): unchanged when the spec is the same,
///   else updated to the new spec (the object is rebuilt by the creating command and takes over the old handle with
///   SwapIdWith, so the handle stays);
/// - present but changed by hand since (geometry moved more than tolerance, vertices added or removed, or text edited):
///   with keepManual (default) left as it is and reported as manual; without keepManual updated like the others;
/// - present without a drawEntities state (an id written by another command): updated (as the earlier Python flag class:
///   without a stored position nothing counts as moved);
/// objects with the prefix that no item names are erased (replace, default true), hand-edited ones too, and further
/// objects with the same id are erased as duplicates. Objects on locked layers are not touched (skipped).
/// Scope: the given layout's space (default Model). The creating commands are reused (createPolyline, createLineSegment,
/// createCircle, createText, createMText, createHatch, createPoints, insertBlock, addMLeader; addLayoutText for texts in a
/// paper layout), so an item may carry any further parameter of its command (frame, arrowhead, pattern, width, ...).
/// </summary>
public static partial class DrawEntitiesCommands
{
  private const int MaxStoredPoints = 400;
  private const int StateVersion = 1;
  private static readonly HashSet<string> Types = new(StringComparer.Ordinal)
    { "polyline", "line", "circle", "text", "mtext", "hatch", "point", "block", "mleader" };
  // keys drawEntities maps itself; every other key of an item goes through to the creating command
  private static readonly HashSet<string> Mapped = new(StringComparer.Ordinal)
    { "id", "type", "data", "points", "x", "y", "textX", "textY", "text", "height", "rotation", "layer", "closed", "blockName", "attributes", "layout", "createLayer" };
  private static readonly string[] StyleKeys = { "color", "linetype", "lineweight", "linetypeScale", "transparency" };

  private sealed record Item(int Index, string Id, string Type, string Hash, string Method, JsonObject Params, JsonObject Style, JsonNode? Data);

  private sealed record State(string Type, string Hash, int Count, int Step, List<double[]> Geometry, string? Text);

  private sealed record Existing(string Handle, string Id, State? State, List<double[]> Geometry, string? Text, string Layer, bool Locked, string Kind);

  public static async Task<object?> DrawEntitiesAsync(JsonObject? parameters, CancellationToken cancellationToken)
  {
    var idPrefix = PluginRuntime.GetRequiredString(parameters, "idPrefix");
    var replace = PluginRuntime.GetOptionalBool(parameters, "replace") ?? true;
    var keepManual = PluginRuntime.GetOptionalBool(parameters, "keepManual") ?? true;
    var tolerance = PluginRuntime.GetOptionalDouble(parameters, "tolerance") ?? 0.01;
    var dryRun = PluginRuntime.GetOptionalBool(parameters, "dryRun") ?? false;
    var layoutName = PluginRuntime.GetOptionalString(parameters, "layout")?.Trim();
    var paper = !string.IsNullOrEmpty(layoutName) && !string.Equals(layoutName, "Model", StringComparison.OrdinalIgnoreCase);
    var layers = PluginRuntime.GetParameter(parameters, "layers") as JsonArray;
    if (!(tolerance >= 0) || double.IsInfinity(tolerance))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "tolerance must be >= 0 (drawing units). Nothing was changed.");
    if (PluginRuntime.GetParameter(parameters, "items") is not JsonArray itemList)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "items must be a list (an empty list with replace erases every object with the prefix). Nothing was changed.");

    var items = new List<Item>();
    for (var i = 0; i < itemList.Count; i++)
      items.Add(ParseItem(itemList[i], i, idPrefix, paper ? layoutName : null));
    foreach (var dup in items.GroupBy(it => it.Id, StringComparer.Ordinal).Where(g => g.Count() > 1))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"id '{dup.Key}' is given {dup.Count()} times. Nothing was changed.");

    async Task<(object? Result, bool Commit)> Run()
    {
      Dictionary<string, object?>? layerAnswer = null;
      if (layers is { Count: > 0 })
        layerAnswer = await Call("layersImport", new JsonObject { ["rows"] = layers.DeepClone(), ["dryRun"] = dryRun }, "layers", cancellationToken);

      var existing = await CivilExecution.ReadAsync((doc, civilDoc, database, transaction) => Scan(database, transaction, idPrefix, paper ? layoutName : null));
      var byId = existing.GroupBy(e => e.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
      var wanted = new HashSet<string>(items.Select(it => it.Id), StringComparer.Ordinal);

      var created = new List<string>();
      var updated = new List<string>();
      var unchanged = new List<string>();
      var manual = new List<string>();
      var erased = new List<string>();
      var skipped = new List<Dictionary<string, object?>>();
      var manualDetail = new List<Dictionary<string, object?>>();
      var updatedDetail = new List<Dictionary<string, object?>>();
      var erasedManual = new List<string>();
      var handles = new Dictionary<string, string>(StringComparer.Ordinal);
      var toErase = new List<Existing>();
      var toCreate = new List<Item>();
      var toUpdate = new List<(Item Item, Existing Old, string Reason)>();

      // objects no item names (replace), and duplicates of one id
      foreach (var (id, list) in byId)
      {
        var extra = wanted.Contains(id) ? list.Skip(1) : replace ? list : Enumerable.Empty<Existing>();
        foreach (var ex in extra)
        {
          if (ex.Locked) { skipped.Add(new() { ["id"] = id, ["handle"] = ex.Handle, ["reason"] = "locked layer", ["action"] = "erase" }); continue; }
          toErase.Add(ex);
          erased.Add(id);
          if (ex.State != null && Manual(ex, tolerance).Manual) erasedManual.Add(id);
        }
      }

      foreach (var item in items)
      {
        if (!byId.TryGetValue(item.Id, out var list)) { toCreate.Add(item); continue; }
        var ex = list[0];
        handles[item.Id] = ex.Handle;
        if (ex.Locked) { skipped.Add(new() { ["id"] = item.Id, ["handle"] = ex.Handle, ["reason"] = "locked layer", ["action"] = "update" }); continue; }
        var (isManual, moved, textChanged, countChanged) = ex.State == null ? (false, (double?)null, false, false) : Manual(ex, tolerance);
        if (isManual && keepManual)
        {
          manual.Add(item.Id);
          manualDetail.Add(new()
          {
            ["id"] = item.Id, ["handle"] = ex.Handle, ["moved"] = moved is double m ? Math.Round(m, 4) : null, ["textChanged"] = textChanged,
            ["vertexCountChanged"] = countChanged, ["specChanged"] = ex.State!.Hash != item.Hash || ex.State.Type != item.Type,
          });
          continue;
        }
        if (!isManual && ex.State != null && ex.State.Hash == item.Hash && ex.State.Type == item.Type) { unchanged.Add(item.Id); continue; }
        toUpdate.Add((item, ex, ex.State == null ? "no drawEntities state" : isManual ? "changed by hand, keepManual false" : "spec changed"));
      }

      if (!dryRun)
      {
        if (toErase.Count > 0)
          await Call("eraseEntities", new JsonObject { ["handles"] = new JsonArray(toErase.Select(e => (JsonNode?)JsonValue.Create(e.Handle)).ToArray()) }, "erase", cancellationToken);
        foreach (var item in toCreate)
        {
          var handle = await Create(item, cancellationToken);
          await CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
          {
            Finish(database, transaction, Open(database, transaction, handle), item);
            return null;
          });
          handles[item.Id] = handle;
        }
        foreach (var (item, old, reason) in toUpdate)
        {
          var fresh = await Create(item, cancellationToken);
          var kept = await CivilExecution.WriteAsync((doc, civilDoc, database, transaction) =>
          {
            var oldEntity = Open(database, transaction, old.Handle);
            var newEntity = Open(database, transaction, fresh);
            oldEntity.UpgradeOpen();
            newEntity.UpgradeOpen();
            var keptHandle = true;
            try
            {
              // the new geometry takes over the old object's id (handle); the old geometry, now under the new id, goes
              oldEntity.SwapIdWith(newEntity.ObjectId, true, true);
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
              keptHandle = false;
            }
            oldEntity.Erase();
            Finish(database, transaction, newEntity, item);
            return (Handle: newEntity.Handle.ToString(), Kept: keptHandle);
          });
          handles[item.Id] = kept.Handle;
          updatedDetail.Add(new() { ["id"] = item.Id, ["handle"] = kept.Handle, ["handleKept"] = kept.Kept, ["reason"] = reason });
        }
      }
      else
      {
        foreach (var (item, old, reason) in toUpdate) updatedDetail.Add(new() { ["id"] = item.Id, ["handle"] = old.Handle, ["reason"] = reason });
      }
      created.AddRange(toCreate.Select(it => it.Id));
      updated.AddRange(toUpdate.Select(u => u.Item.Id));

      var result = new Dictionary<string, object?>
      {
        ["idPrefix"] = idPrefix, ["layout"] = paper ? layoutName : "Model", ["dryRun"] = dryRun, ["replace"] = replace, ["keepManual"] = keepManual,
        ["tolerance"] = tolerance,
        ["created"] = created, ["updated"] = updated, ["unchanged"] = unchanged, ["erased"] = erased, ["manual"] = manual,
        ["counts"] = new Dictionary<string, int>
        {
          ["created"] = created.Count, ["updated"] = updated.Count, ["unchanged"] = unchanged.Count, ["erased"] = erased.Count,
          ["manual"] = manual.Count, ["skipped"] = skipped.Count,
        },
        ["manualDetail"] = manualDetail, ["updatedDetail"] = updatedDetail, ["erasedManual"] = erasedManual, ["skipped"] = skipped,
        ["handles"] = handles,
        ["layers"] = layerAnswer == null ? null : new Dictionary<string, object?>
        {
          ["created"] = layerAnswer.GetValueOrDefault("created"), ["changed"] = layerAnswer.GetValueOrDefault("changed"),
          ["unchanged"] = layerAnswer.GetValueOrDefault("unchanged"), ["errors"] = layerAnswer.GetValueOrDefault("errors"),
        },
      };
      return (result, true);
    }

    // one transaction: inside a caller's batch the calls run inline in it; otherwise an own batch with one transaction
    if (CivilExecution.InBatch) return (await Run()).Result;
    var drawing = PluginRuntime.GetRequestedDrawing().Name;
    return await CivilExecution.ExecuteBatchAsync<object?>(async () =>
    {
      var previous = PluginRuntime.SetRequestedDrawing(drawing);
      try { return await Run(); }
      finally { PluginRuntime.SetRequestedDrawing(previous); }
    }, singleTransaction: true);
  }

  // ---------- items ----------

  private static Item ParseItem(JsonNode? node, int index, string prefix, string? layout)
  {
    string Where(string? id = null) => $"items[{index}]" + (id != null ? $" ({id})" : "");
    if (node is not JsonObject o)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{Where()} must be an object {{id, type, ...}}. Nothing was changed.");
    var rawId = o["id"] is JsonValue iv && iv.TryGetValue<string>(out var s) ? s.Trim() : "";
    if (rawId.Length == 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{Where()} needs an id. Nothing was changed.");
    var id = rawId.StartsWith(prefix, StringComparison.Ordinal) ? rawId : prefix + rawId;
    if (id.Length > 255)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{Where(id)}: the id with its prefix is longer than 255 characters. Nothing was changed.");
    var type = (o["type"] is JsonValue tv && tv.TryGetValue<string>(out var t) ? t : "").Trim().ToLowerInvariant();
    if (!Types.Contains(type))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{Where(id)}: type must be one of {string.Join(", ", Types)}. Nothing was changed.");
    if (o.ContainsKey("layout"))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{Where(id)}: layout is given per call, not per item. Nothing was changed.");

    var p = new JsonObject();
    foreach (var (key, value) in o)
      if (!Mapped.Contains(key) && value != null) p[key] = value.DeepClone();
    var layer = o["layer"] is JsonValue lv && lv.TryGetValue<string>(out var ln) && !string.IsNullOrWhiteSpace(ln) ? ln.Trim() : null;
    if (layer != null) { p["layer"] = layer; p["createLayer"] = o["createLayer"]?.DeepClone() ?? true; }
    var points = Points(o["points"], Where(id));
    (double X, double Y)? At(int i)
    {
      if (i == 0 && o["x"] != null && o["y"] != null) return (Num(o["x"], Where(id) + ".x"), Num(o["y"], Where(id) + ".y"));
      return i < points.Count ? points[i] : null;
    }
    (double X, double Y) Need(int i, string what) => At(i) ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{Where(id)}: {what}. Nothing was changed.");
    string Text() => o["text"] is JsonValue xv && xv.TryGetValue<string>(out var x) ? x
      : throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{Where(id)}: a {type} needs text. Nothing was changed.");
    void Rotation() { if (o["rotation"] != null) p["rotationDegrees"] = Num(o["rotation"], Where(id) + ".rotation"); }
    void Layout() { if (layout != null) p["layout"] = layout; }
    void NoPaper() { if (layout != null) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{Where(id)}: a {type} cannot be drawn in a paper layout. Nothing was changed."); }
    JsonArray XY(IEnumerable<(double X, double Y)> list) => new(list.Select(q => (JsonNode?)new JsonObject { ["x"] = q.X, ["y"] = q.Y }).ToArray());

    string method;
    switch (type)
    {
      case "polyline":
        if (points.Count < 2) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{Where(id)}: a polyline needs at least 2 points. Nothing was changed.");
        method = "createPolyline";
        p["points"] = XY(points);
        p["closed"] = o["closed"] is JsonValue cv && cv.TryGetValue<bool>(out var c) && c ? 1 : 0;
        Layout();
        break;
      case "line":
        if (points.Count != 2) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{Where(id)}: a line needs points with exactly 2 points. Nothing was changed.");
        if (layout != null)
        {
          method = "createPolyline";   // createLineSegment draws in model space only
          p["points"] = XY(points);
          p["closed"] = 0;
          Layout();
        }
        else
        {
          method = "createLineSegment";
          (p["startX"], p["startY"], p["endX"], p["endY"]) = (points[0].X, points[0].Y, points[1].X, points[1].Y);
        }
        break;
      case "circle":
      {
        var q = Need(0, "a circle needs x/y (or points[0]) and radius");
        if (o["radius"] == null) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{Where(id)}: a circle needs radius. Nothing was changed.");
        method = "createCircle";
        (p["x"], p["y"]) = (q.X, q.Y);
        Layout();
        break;
      }
      case "text":
      case "mtext":
      {
        var q = Need(0, $"a {type} needs x/y (or points[0])");
        p["text"] = Text();
        (p["x"], p["y"]) = (q.X, q.Y);
        Rotation();
        if (layout != null)
        {
          method = "addLayoutText";   // createText/createMText draw in model space only
          p["layout"] = layout;
          if (o["height"] != null) p["height"] = Num(o["height"], Where(id) + ".height");
        }
        else if (type == "text")
        {
          method = "createText";
          if (o["height"] != null) p["height"] = Num(o["height"], Where(id) + ".height");
        }
        else
        {
          method = "createMText";
          if (o["height"] != null) p["textHeight"] = Num(o["height"], Where(id) + ".height");
        }
        break;
      }
      case "hatch":
        if (points.Count < 3) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{Where(id)}: a hatch needs at least 3 points (its boundary). Nothing was changed.");
        method = "createHatch";
        p["boundary"] = new JsonArray(points.Select(q => (JsonNode?)new JsonArray(q.X, q.Y)).ToArray());
        Layout();
        break;
      case "point":
      {
        NoPaper();
        var q = Need(0, "a point needs x/y (or points[0])");
        method = "createPoints";
        var pt = new JsonObject { ["x"] = q.X, ["y"] = q.Y };
        if (p["z"] != null) { pt["z"] = p["z"]!.DeepClone(); p.Remove("z"); }
        p["points"] = new JsonArray(pt);
        break;
      }
      case "block":
      {
        var q = Need(0, "a block needs x/y (or points[0])");
        method = "insertBlock";
        p["blockName"] = o["blockName"] is JsonValue bv && bv.TryGetValue<string>(out var b) && !string.IsNullOrWhiteSpace(b) ? b
          : throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{Where(id)}: a block needs blockName. Nothing was changed.");
        (p["x"], p["y"]) = (q.X, q.Y);
        Rotation();
        if (o["attributes"] is JsonObject attrs) p["attributes"] = attrs.DeepClone();
        Layout();
        break;
      }
      default:   // mleader
      {
        NoPaper();
        // arrow: x/y, else points[0]; text: textX/textY, else the next point (points[0] after x/y, else points[1])
        var arrow = Need(0, "a multileader needs its arrow point (x/y or points[0])");
        var hasXY = o["x"] != null && o["y"] != null;
        var next = hasXY ? 0 : 1;
        (double X, double Y) label = o["textX"] != null && o["textY"] != null
          ? (Num(o["textX"], Where(id) + ".textX"), Num(o["textY"], Where(id) + ".textY"))
          : points.Count > next ? points[next]
          : throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{Where(id)}: a multileader needs its text point (textX/textY or the point after the arrow in points). Nothing was changed.");
        if (o["height"] == null) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{Where(id)}: a multileader needs height (text height, model units). Nothing was changed.");
        method = "addMLeader";
        p["text"] = Text();
        (p["x"], p["y"], p["textX"], p["textY"]) = (arrow.X, arrow.Y, label.X, label.Y);
        p["textHeight"] = Num(o["height"], Where(id) + ".height");
        Rotation();
        break;
      }
    }

    var style = new JsonObject();
    foreach (var key in StyleKeys)
      if (o[key] != null) style[key] = o[key]!.DeepClone();
    var spec = (JsonObject)o.DeepClone();
    spec["id"] = id;
    spec["layout"] = layout ?? "Model";
    return new Item(index, id, type, Hash(spec), method, p, style, o["data"]?.DeepClone());
  }

  private static double Num(JsonNode? node, string what) =>
    node is JsonValue v && v.TryGetValue<double>(out var d) && double.IsFinite(d) ? d
      : throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{what} must be a number. Nothing was changed.");

  private static List<(double X, double Y)> Points(JsonNode? node, string where)
  {
    var list = new List<(double X, double Y)>();
    if (node == null) return list;
    if (node is not JsonArray a) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{where}.points must be a list of [x, y] or {{x, y}}. Nothing was changed.");
    for (var i = 0; i < a.Count; i++)
    {
      if (a[i] is JsonArray pair && pair.Count >= 2) list.Add((Num(pair[0], $"{where}.points[{i}][0]"), Num(pair[1], $"{where}.points[{i}][1]")));
      else if (a[i] is JsonObject q && q["x"] != null && q["y"] != null) list.Add((Num(q["x"], $"{where}.points[{i}].x"), Num(q["y"], $"{where}.points[{i}].y")));
      else throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{where}.points[{i}] must be [x, y] or {{x, y}}. Nothing was changed.");
    }
    return list;
  }

  /// <summary>A short hash of the item spec with sorted keys, so that the same spec gives the same hash.</summary>
  private static string Hash(JsonNode node)
  {
    var sb = new StringBuilder();
    Canonical(node, sb);
    var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
    return Convert.ToHexString(bytes, 0, 8).ToLowerInvariant();
  }

  private static void Canonical(JsonNode? node, StringBuilder sb)
  {
    switch (node)
    {
      case null: sb.Append("null"); break;
      case JsonObject o:
        sb.Append('{');
        var first = true;
        foreach (var (key, value) in o.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
          if (!first) sb.Append(',');
          first = false;
          sb.Append(System.Text.Json.JsonSerializer.Serialize(key)).Append(':');
          Canonical(value, sb);
        }
        sb.Append('}');
        break;
      case JsonArray a:
        sb.Append('[');
        for (var i = 0; i < a.Count; i++) { if (i > 0) sb.Append(','); Canonical(a[i], sb); }
        sb.Append(']');
        break;
      default: sb.Append(node.ToJsonString()); break;
    }
  }

  // ---------- drawing ----------

  /// <summary>Runs the item's creating command and returns the new object's handle; errors name the item.</summary>
  private static async Task<string> Create(Item item, CancellationToken cancellationToken)
  {
    var answer = await Call(item.Method, (JsonObject)item.Params.DeepClone(), $"items[{item.Index}] ({item.Id})", cancellationToken);
    var handle = answer.GetValueOrDefault("handle") as string ?? answer.GetValueOrDefault("lineId") as string
      ?? (answer.GetValueOrDefault("handles") as IEnumerable<string>)?.FirstOrDefault();
    return handle ?? throw new JsonRpcDispatchException("CIVIL3D.INTERNAL_ERROR", $"items[{item.Index}] ({item.Id}): {item.Method} reported no handle.");
  }

  private static async Task<Dictionary<string, object?>> Call(string method, JsonObject parameters, string what, CancellationToken cancellationToken)
  {
    try
    {
      return await CommandDispatcher.DispatchAsync(method, parameters, cancellationToken) as Dictionary<string, object?>
        ?? throw new JsonRpcDispatchException("CIVIL3D.INTERNAL_ERROR", $"{what}: {method} gave no answer.");
    }
    catch (JsonRpcDispatchException ex) when (!ex.Message.StartsWith(what, StringComparison.Ordinal))
    {
      throw new JsonRpcDispatchException(ex.Code, $"{what}: {ex.Message.Replace("Nothing was created.", "").Replace("Nothing was changed.", "").Trim()} Nothing was changed (drawEntities is all or nothing).");
    }
  }

  /// <summary>Style the creating command does not set itself (texts, points, blocks, multileaders), then the script id
  /// with the state read back from the object.</summary>
  private static void Finish(Database database, Transaction transaction, AcEntity entity, Item item)
  {
    if (!entity.IsWriteEnabled) entity.UpgradeOpen();
    if (item.Style.Count > 0) AcadCommands.ApplyEntityStyle(entity, item.Style, database, transaction);
    var (geometry, text) = Signature(entity, transaction);
    var (count, step, sample) = Sample(geometry);
    var state = new JsonObject
    {
      ["de"] = StateVersion, ["t"] = item.Type, ["h"] = item.Hash, ["n"] = count, ["k"] = step,
      ["g"] = new JsonArray(sample.Select(q => (JsonNode?)new JsonArray(Math.Round(q[0], 4), Math.Round(q[1], 4))).ToArray()),
    };
    if (text != null) state["tx"] = text;
    if (item.Data != null) state["d"] = item.Data.DeepClone();
    ScriptIdCommands.EnsureApp(database, transaction);
    ScriptIdCommands.Write(entity, item.Id, state.ToJsonString());
  }

  private static AcEntity Open(Database database, Transaction transaction, string handle)
  {
    if (!database.TryGetObjectId(new Handle(Convert.ToInt64(handle, 16)), out var id) || id.IsErased
        || transaction.GetObject(id, OpenMode.ForRead) is not AcEntity entity)
      throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Handle {handle} is not a drawing object.");
    return entity;
  }

  /// <summary>The objects with a script id starting with the prefix in the space (model or the layout).</summary>
  private static List<Existing> Scan(Database database, Transaction transaction, string prefix, string? layout)
  {
    var spaceId = layout == null ? SymbolUtilityServices.GetBlockModelSpaceId(database) : LayoutCommands.FindLayout(database, transaction, layout).BlockTableRecordId;
    var space = (BlockTableRecord)transaction.GetObject(spaceId, OpenMode.ForRead);
    var layers = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
    var list = new List<Existing>();
    foreach (ObjectId oid in space)
    {
      if (transaction.GetObject(oid, OpenMode.ForRead) is not AcEntity entity) continue;
      var (id, data) = ScriptIdCommands.Read(entity);
      if (id == null || !id.StartsWith(prefix, StringComparison.Ordinal)) continue;
      var (geometry, text) = Signature(entity, transaction);
      var locked = layers.Has(entity.Layer) && ((LayerTableRecord)transaction.GetObject(layers[entity.Layer], OpenMode.ForRead)).IsLocked;
      list.Add(new Existing(entity.Handle.ToString(), id, ParseState(data), geometry, text, entity.Layer, locked, entity.GetRXClass().DxfName));
    }
    // a stable order: the lowest handle first (the original when an id was copied)
    return list.OrderBy(e => Convert.ToInt64(e.Handle, 16)).ToList();
  }

  /// <summary>C241 (placeFlags): the objects under a prefix in model space or a layout with what drawEntities would
  /// decide: whether they carry a drawEntities state, whether they changed by hand since (geometry beyond tolerance or
  /// text) and the item data stored with the state ("d"), or the raw idData of an id written by another command.</summary>
  internal sealed record ExistingObject(string Handle, string Id, bool HasState, bool ChangedByHand, double? Moved, JsonNode? ItemData, string? RawData, bool Locked);

  internal static List<ExistingObject> ReadExisting(Database database, Transaction transaction, string prefix, string? layout, double tolerance)
  {
    var list = new List<ExistingObject>();
    foreach (var ex in Scan(database, transaction, prefix, layout))
    {
      var (manual, moved, _, _) = ex.State == null ? (false, (double?)null, false, false) : Manual(ex, tolerance);
      string? raw = null;
      JsonNode? itemData = null;
      if (database.TryGetObjectId(new Handle(Convert.ToInt64(ex.Handle, 16)), out var oid) && transaction.GetObject(oid, OpenMode.ForRead) is DBObject o)
      {
        raw = ScriptIdCommands.Read(o).Data;
        if (ex.State != null && raw != null)
          try { itemData = (JsonNode.Parse(raw) as JsonObject)?["d"]?.DeepClone(); } catch (System.Text.Json.JsonException) { }
      }
      list.Add(new ExistingObject(ex.Handle, ex.Id, ex.State != null, manual, moved, itemData, raw, ex.Locked));
    }
    return list;
  }

  private static State? ParseState(string? data)
  {
    if (string.IsNullOrEmpty(data) || data[0] != '{') return null;
    try
    {
      if (JsonNode.Parse(data) is not JsonObject o || o["de"] == null || o["h"] is not JsonValue h || o["g"] is not JsonArray g) return null;
      var geometry = g.OfType<JsonArray>().Where(a => a.Count >= 2).Select(a => new[] { a[0]!.GetValue<double>(), a[1]!.GetValue<double>() }).ToList();
      return new State(o["t"]?.GetValue<string>() ?? "", h.GetValue<string>(), o["n"]?.GetValue<int>() ?? geometry.Count, o["k"]?.GetValue<int>() ?? 1,
        geometry, o["tx"]?.GetValue<string>());
    }
    catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException)
    {
      return null;
    }
  }

  /// <summary>Whether an object changed by hand since drawEntities wrote it: geometry off by more than tolerance (the
  /// largest point distance), another vertex count, or other text.</summary>
  private static (bool Manual, double? Moved, bool TextChanged, bool CountChanged) Manual(Existing ex, double tolerance)
  {
    var st = ex.State!;
    var textChanged = !string.Equals(st.Text, ex.Text, StringComparison.Ordinal);
    if (ex.Geometry.Count != st.Count) return (true, null, textChanged, true);
    var (_, _, now) = Sample(ex.Geometry, st.Step);
    if (now.Count != st.Geometry.Count) return (true, null, textChanged, true);
    var moved = now.Zip(st.Geometry, (a, b) => Math.Sqrt((a[0] - b[0]) * (a[0] - b[0]) + (a[1] - b[1]) * (a[1] - b[1]))).DefaultIfEmpty(0).Max();
    return (moved > tolerance || textChanged, moved, textChanged, false);
  }

  /// <summary>The points kept in the state: all up to 400, else every step-th and the last.</summary>
  private static (int Count, int Step, List<double[]> Sample) Sample(List<double[]> geometry, int? step = null)
  {
    var k = step ?? Math.Max(1, (int)Math.Ceiling(geometry.Count / (double)MaxStoredPoints));
    var sample = geometry.Where((_, i) => i % k == 0 || i == geometry.Count - 1).ToList();
    return (geometry.Count, k, sample);
  }

  /// <summary>Points that describe where an object lies (a rotation or size shows as a second point moving) and its text.</summary>
  private static (List<double[]> Geometry, string? Text) Signature(AcEntity entity, Transaction transaction)
  {
    static double[] P(Point3d q) => new[] { q.X, q.Y };
    static double[] Along(Point3d q, double angle, double length) => new[] { q.X + Math.Cos(angle) * length, q.Y + Math.Sin(angle) * length };
    var g = new List<double[]>();
    string? text = null;
    switch (entity)
    {
      case Polyline pl:
        for (var i = 0; i < pl.NumberOfVertices; i++) { var v = pl.GetPoint2dAt(i); g.Add(new[] { v.X, v.Y }); }
        break;
      case Line ln: g.Add(P(ln.StartPoint)); g.Add(P(ln.EndPoint)); break;
      case Circle c: g.Add(P(c.Center)); g.Add(new[] { c.Center.X + c.Radius, c.Center.Y }); break;
      case DBText t: g.Add(P(t.Position)); g.Add(Along(t.Position, t.Rotation, t.Height)); text = t.TextString; break;
      case MText m: g.Add(P(m.Location)); g.Add(Along(m.Location, m.Rotation, m.TextHeight)); text = m.Contents; break;
      case DBPoint pt: g.Add(P(pt.Position)); break;
      case BlockReference br:
      {
        g.Add(P(br.Position));
        g.Add(Along(br.Position, br.Rotation, br.ScaleFactors.X));
        var attrs = new List<string>();
        foreach (ObjectId aid in br.AttributeCollection)
          if (transaction.GetObject(aid, OpenMode.ForRead) is AttributeReference ar && !ar.IsErased) attrs.Add(ar.Tag + "=" + MultilineAttributes.Value(ar));
        text = attrs.Count == 0 ? null : string.Join("\n", attrs.OrderBy(a => a, StringComparer.Ordinal));
        break;
      }
      case MLeader ml:
      {
        if (ml.ContentType == ContentType.MTextContent)
        {
          g.Add(P(ml.TextLocation));
          using var mt = ml.MText;
          text = mt?.Contents;
        }
        foreach (var leader in ml.GetLeaderIndexes())
          foreach (var line in ml.GetLeaderLineIndexes((int)leader))
            g.Add(P(ml.GetFirstVertex((int)line)));
        break;
      }
      default:
        try { var e = entity.GeometricExtents; g.Add(P(e.MinPoint)); g.Add(P(e.MaxPoint)); }
        catch (Autodesk.AutoCAD.Runtime.Exception) { }
        break;
    }
    return (g, text);
  }
}
