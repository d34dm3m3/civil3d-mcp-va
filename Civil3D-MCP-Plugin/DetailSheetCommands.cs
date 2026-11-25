using System.Globalization;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace Civil3DMcpPlugin;

/// <summary>
/// C239 buildDetailSheet (F8, 2026-11-25): a detail sheet as one rerunnable call: the layout from the template layout
/// (or a copy of another sheet), one viewport per view fitted to its source at its scale, a title and a scale line per
/// view, the right column's legend rows (legend_rows.json, stacked as the original Python legend script did), the right column's
/// and the title block's attributes and the revision rows above the title block's heading row. Use case: per-project
/// detail sheet scripts (about 700 lines in nine scripts) copied "A1 Plan+Profil", erased its large viewports and
/// legend rows, sized each viewport from a bbox file and wrote titles, attributes and revision rows one call at a
/// time.
///
/// Idempotent under the layout's name: viewports carry the script id "sheetvp/{name}/{view}" with their spec, and are
/// left as they are when the spec is the same, else erased and created anew (a changed viewport does not plot right,
/// lesson from the pilot); views no longer given are erased. Texts, legend rows and revision rows are drawn with
/// drawEntities (C223) under "sheet/{name}/", so a rerun updates them, keeps hand edits and erases what is no longer
/// given.
/// </summary>
public static class DetailSheetCommands
{
  private const string DefaultTemplate = "A1 Plan+Profil";
  private static readonly string[] DefaultClearBlocks = { "TF-", "Skalstock", "Norrpil", "Kompassros" };
  private static readonly double[] RevisionColumns = { -100, -92, -84, -52, -42, -30, -14, 0 };
  private static readonly string[] RevisionFields = { "rev", "ant", "text", "godk", "date", "trvDate", "trvDnr" };

  private sealed record View(string Key, string Title, double? Scale, JsonObject Source, double AnchorX, double AnchorY, double Margin, double TitleHeight,
    string ScaleText, double ScaleTextHeight, double Gap, double? Width, double? Height, double Twist, string? Layer, JsonArray? Frozen, bool CreateLayer);

  private sealed record Planned(View View, double Scale, double CenterX, double CenterY, double Width, double Height, double PaperX, double PaperY,
    double[] Area, string Signature);

  private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

  private static double Num(JsonNode? n, string what) =>
    n is JsonValue v && v.TryGetValue<double>(out var d) && double.IsFinite(d) ? d
    : throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{what} must be a number. Nothing was changed.");

  private static (double X, double Y) XYOf(JsonNode? n, string what) => n switch
  {
    JsonObject o when o["x"] != null && o["y"] != null => (Num(o["x"], what + ".x"), Num(o["y"], what + ".y")),
    JsonArray a when a.Count >= 2 => (Num(a[0], what + "[0]"), Num(a[1], what + "[1]")),
    _ => throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{what} must be {{x, y}} or [x, y] (paper mm). Nothing was changed."),
  };

  internal static string BlockName(BlockReference r, Transaction t) => r.IsDynamicBlock
    ? ((BlockTableRecord)t.GetObject(r.DynamicBlockTableRecord, OpenMode.ForRead)).Name : r.Name;

  /// <summary>C239: {name, template? (a layout of the drawing copied fresh, default "A1 Plan+Profil"; its viewports wider
  /// than clear.viewportsWiderThan 600 mm and blocks named clear.blockPrefixes [TF-, Skalstock, Norrpil, Kompassros] are
  /// removed), copyFrom? (a layout copied as it is, nothing removed), replace (false: an existing layout is updated in
  /// place; true: a fresh copy, tab position kept), views: [{id?, title, scale?, source: {idPrefix | bbox {minX, minY,
  /// maxX, maxY} | profileView}, anchor {x, y} (top left of the title, paper mm), margin (3 mm), titleHeight (3.5),
  /// scaleText ("SCALE 1:{scale}", "" none), scaleTextHeight (2.5), gap (title to viewport top, default title + scale
  /// line), size? {width, height} (fixed viewport: scale fitted when left out), twistDegrees (0), layer?, frozenLayers?}],
  /// rightColumn?: {rows: [legend block names], values?: {tag: value}, catalogPath?}, stamp?: {tag: value} (RITNINGSNR
  /// defaults to name), stampBlock ("Stamp"), revisions?: [{rev, ant?, text, godk?, date?, trvDate?, trvDnr?}] oldest
  /// first, textLayer?, revisionLayer ("RIT-TF"), planArea [45, 22, 716, 580] (warnings), keepManual (true), dryRun?
  /// (plans the views, writes nothing), profile?}.</summary>
  public static async Task<object?> BuildDetailSheetAsync(JsonObject? parameters, CancellationToken cancellationToken)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name").Trim();
    var template = PluginRuntime.GetOptionalString(parameters, "template");
    var copyFrom = PluginRuntime.GetOptionalString(parameters, "copyFrom");
    if (template != null && copyFrom != null)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Give template or copyFrom, not both. Nothing was changed.");
    var source = copyFrom ?? template ?? DefaultTemplate;
    var replace = PluginRuntime.GetOptionalBool(parameters, "replace") ?? false;
    var keepManual = PluginRuntime.GetOptionalBool(parameters, "keepManual") ?? true;
    var dryRun = PluginRuntime.GetOptionalBool(parameters, "dryRun") ?? false;
    var textLayer = PluginRuntime.GetOptionalString(parameters, "textLayer");
    var stampBlock = PluginRuntime.GetOptionalString(parameters, "stampBlock") ?? "Stamp";
    var revLayer = PluginRuntime.GetOptionalString(parameters, "revisionLayer") ?? "RIT-TF";
    var clear = PluginRuntime.GetParameter(parameters, "clear") as JsonObject;
    var clearWider = clear?["viewportsWiderThan"] is JsonNode cw ? Num(cw, "clear.viewportsWiderThan") : 600.0;
    var clearBlocks = clear?["blockPrefixes"] is JsonArray cb ? cb.Select(x => x?.GetValue<string>() ?? "").Where(x => x.Length > 0).ToArray() : DefaultClearBlocks;
    double[] planArea = PluginRuntime.GetParameter(parameters, "planArea") is JsonArray pa && pa.Count == 4
      ? pa.Select((x, i) => Num(x, $"planArea[{i}]")).ToArray() : new[] { 45.0, 22.0, 716.0, 580.0 };
    // top-level defaults for the views (the profile fills them: blad.detalj.*)
    var dMargin = PluginRuntime.GetOptionalDouble(parameters, "margin") ?? 3.0;
    var dTitleH = PluginRuntime.GetOptionalDouble(parameters, "titleHeight") ?? 3.5;
    var dScaleH = PluginRuntime.GetOptionalDouble(parameters, "scaleTextHeight") ?? 2.5;
    var dLayer = PluginRuntime.GetOptionalString(parameters, "viewportLayer");

    if (PluginRuntime.GetParameter(parameters, "views") is not JsonArray viewList)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "views must be a list of {title, scale, source, anchor} (an empty list erases the sheet's own viewports). Nothing was changed.");
    var views = viewList.Select((n, i) =>
    {
      var w = $"views[{i}]";
      if (n is not JsonObject o) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{w} must be an object. Nothing was changed.");
      var key = o["id"] is JsonValue iv && iv.TryGetValue<string>(out var ids) && ids.Trim().Length > 0 ? ids.Trim() : $"v{i + 1}";
      var title = o["title"] is JsonValue tv && tv.TryGetValue<string>(out var ts) ? ts : "";
      double? scale = o["scale"] is JsonNode sn ? Num(sn, w + ".scale") : null;
      if (scale is <= 0) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{w}.scale must be > 0 (20 for 1:20). Nothing was changed.");
      if (o["source"] is not JsonObject src || (src["idPrefix"] == null ? 0 : 1) + (src["bbox"] == null ? 0 : 1) + (src["profileView"] == null ? 0 : 1) != 1)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{w}.source must be one of {{idPrefix}}, {{bbox: {{minX, minY, maxX, maxY}}}} or {{profileView}}. Nothing was changed.");
      var (ax, ay) = XYOf(o["anchor"], w + ".anchor");
      double? width = null, height = null;
      if (o["size"] is JsonObject sz)
      {
        width = Num(sz["width"], w + ".size.width"); height = Num(sz["height"], w + ".size.height");
        if (!(width > 0) || !(height > 0)) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{w}.size must be > 0. Nothing was changed.");
      }
      if (scale == null && width == null)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{w} needs scale (or size, and the scale is fitted). Nothing was changed.");
      var titleH = o["titleHeight"] is JsonNode th ? Num(th, w + ".titleHeight") : dTitleH;
      var scaleH = o["scaleTextHeight"] is JsonNode sh ? Num(sh, w + ".scaleTextHeight") : dScaleH;
      var scaleText = o["scaleText"] is JsonValue st && st.TryGetValue<string>(out var stt) ? stt : "SCALE 1:{scale}";
      var gap = o["gap"] is JsonNode g ? Num(g, w + ".gap")
        : (title.Length > 0 ? titleH + 1.5 : 0) + (scaleText.Length > 0 ? scaleH + 1.5 : 0);
      return new View(key, title, scale, src, ax, ay, o["margin"] is JsonNode m ? Num(m, w + ".margin") : dMargin, titleH, scaleText, scaleH, gap,
        width, height, o["twistDegrees"] is JsonNode tw ? Num(tw, w + ".twistDegrees") : 0,
        o["layer"] is JsonValue lv && lv.TryGetValue<string>(out var ls) ? ls : dLayer, o["frozenLayers"] as JsonArray, o["createLayer"]?.GetValue<bool>() ?? true);
    }).ToList();
    foreach (var dup in views.GroupBy(v => v.Key, StringComparer.Ordinal).Where(gr => gr.Count() > 1))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"view id '{dup.Key}' is given {dup.Count()} times. Nothing was changed.");

    // the right column catalogue and the stack, checked before anything is written
    var rightColumn = PluginRuntime.GetParameter(parameters, "rightColumn") as JsonObject;
    JsonObject? catalog = null;
    var stack = new List<(string Block, double Dy)>();
    string? catalogFile = null;
    if (rightColumn != null)
    {
      catalogFile = CatalogPath(rightColumn["catalogPath"]?.GetValue<string>(), PluginRuntime.GetOptionalString(parameters, "rulesPath"));
      catalog = JsonNode.Parse(File.ReadAllText(catalogFile)) as JsonObject
        ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{catalogFile} is not a JSON object. Nothing was changed.");
      var rows = (rightColumn["rows"] as JsonArray)?.Select(x => x?.GetValue<string>() ?? "").Where(x => x.Length > 0).ToList();
      if (rows != null) stack = Stack(catalog, rows);
    }
    var stamp = PluginRuntime.GetParameter(parameters, "stamp") as JsonObject;
    var revisions = PluginRuntime.GetParameter(parameters, "revisions") as JsonArray;
    var warnings = new List<string>();

    // ---- the views planned against their sources
    var planned = await CivilExecution.ReadAsync((doc, civilDoc, database, transaction) =>
      views.Select(v => Plan(v, civilDoc, database, transaction)).ToList());
    foreach (var p in planned)
    {
      if (p.PaperX + p.Width > planArea[2] + 1e-6) warnings.Add($"{p.View.Key}: reaches into the right column ({p.PaperX + p.Width:F0} mm > {planArea[2]:F0})");
      if (p.PaperY - p.Height < planArea[1] - 1e-6) warnings.Add($"{p.View.Key}: reaches below the plan area ({p.PaperY - p.Height:F0} mm < {planArea[1]:F0})");
      if (p.PaperX < planArea[0] - 1e-6) warnings.Add($"{p.View.Key}: starts left of the plan area ({p.PaperX:F0} mm < {planArea[0]:F0})");
      if (p.View.AnchorY > planArea[3] + 1e-6) warnings.Add($"{p.View.Key}: its title is above the plan area ({p.View.AnchorY:F0} mm > {planArea[3]:F0})");
    }
    for (var i = 0; i < planned.Count; i++)
      for (var j = i + 1; j < planned.Count; j++)
      {
        var (a, b) = (planned[i], planned[j]);
        bool Over(double a0, double a1, double b0, double b1) => a0 < b1 - 1e-6 && b0 < a1 - 1e-6;
        if (Over(a.PaperX, a.PaperX + a.Width, b.PaperX, b.PaperX + b.Width) && Over(a.PaperY - a.Height, a.View.AnchorY, b.PaperY - b.Height, b.View.AnchorY))
          warnings.Add($"{a.View.Key} and {b.View.Key} overlap on the sheet");
      }
    List<Dictionary<string, object?>> Report(Dictionary<string, (string Status, string? Handle)>? done) => planned.Select(p => new Dictionary<string, object?>
    {
      ["id"] = p.View.Key, ["title"] = p.View.Title, ["scale"] = p.Scale, ["source"] = p.View.Source.DeepClone(),
      ["area"] = p.Area.Select(x => Math.Round(x, 3)).ToArray(), ["viewCenter"] = new[] { Math.Round(p.CenterX, 3), Math.Round(p.CenterY, 3) },
      ["paper"] = new Dictionary<string, object?> { ["x"] = Math.Round(p.PaperX, 2), ["top"] = Math.Round(p.PaperY, 2), ["width"] = Math.Round(p.Width, 2), ["height"] = Math.Round(p.Height, 2) },
      ["status"] = done?.GetValueOrDefault(p.View.Key).Status ?? "planned", ["handle"] = done?.GetValueOrDefault(p.View.Key).Handle,
    }).ToList();
    if (dryRun)
      return new Dictionary<string, object?> { ["layout"] = name, ["dryRun"] = true, ["source"] = source, ["views"] = Report(null), ["warnings"] = warnings,
        ["rightColumnRows"] = stack.Select(s => s.Block).ToList() };

    // ---- the layout
    var exists = await CivilExecution.ReadAsync((doc, civilDoc, database, transaction) =>
    {
      var all = LayoutCommands.Layouts(database, transaction).Where(l => !l.ModelType).Select(l => l.LayoutName).ToList();
      if (!all.Contains(source, StringComparer.OrdinalIgnoreCase) && !(all.Contains(name, StringComparer.OrdinalIgnoreCase) && !replace))
        throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Layout '{source}' is not in the drawing (layouts: {string.Join(", ", all)}). Nothing was changed.");
      return all.Contains(name, StringComparer.OrdinalIgnoreCase);
    });
    string made;
    if (!exists || replace)
    {
      await LayoutCommands.CreateLayoutAsync(new JsonObject { ["name"] = name, ["copyFrom"] = source, ["replace"] = exists });
      made = exists ? "replaced" : "created";
      if (copyFrom == null)
      {
        var removed = await CivilExecution.WriteAsync((doc, civilDoc, database, transaction) =>
        {
          var layout = LayoutCommands.FindLayout(database, transaction, name);
          var space = (BlockTableRecord)transaction.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
          var first = true; int vps = 0, blocks = 0;
          foreach (ObjectId id in space)
          {
            var e = transaction.GetObject(id, OpenMode.ForRead);
            if (e is Viewport vp)
            {
              if (first) { first = false; continue; }
              if (vp.Width > clearWider) { vp.UpgradeOpen(); vp.Erase(); vps++; }
            }
            else if (e is BlockReference br && clearBlocks.Any(pref => BlockName(br, transaction).StartsWith(pref, StringComparison.OrdinalIgnoreCase)))
            { br.UpgradeOpen(); br.Erase(); blocks++; }
          }
          return (Viewports: vps, Blocks: blocks);
        });
        warnings.Add($"from template '{source}': {removed.Viewports} large viewport(s) and {removed.Blocks} block(s) removed");
      }
    }
    else made = "inPlace";

    // ---- viewports: unchanged, recreated, created, erased
    var vpPrefix = $"sheetvp/{name}/";
    var existing = await CivilExecution.ReadAsync((doc, civilDoc, database, transaction) =>
    {
      var layout = LayoutCommands.FindLayout(database, transaction, name);
      var list = new List<(string Key, string Handle, string? Sig)>();
      foreach (ObjectId id in (BlockTableRecord)transaction.GetObject(layout.BlockTableRecordId, OpenMode.ForRead))
        if (transaction.GetObject(id, OpenMode.ForRead) is Viewport vp)
        {
          var (sid, data) = ScriptIdCommands.Read(vp);
          if (sid != null && sid.StartsWith(vpPrefix, StringComparison.Ordinal)) list.Add((sid[vpPrefix.Length..], vp.Handle.ToString(), data));
        }
      return list;
    });
    var done = new Dictionary<string, (string Status, string? Handle)>(StringComparer.Ordinal);
    var erase = new List<string>();
    foreach (var ex in existing)
    {
      var p = planned.FirstOrDefault(x => x.View.Key == ex.Key);
      if (p != null && ex.Sig == p.Signature && !done.ContainsKey(p.View.Key)) done[p.View.Key] = ("unchanged", ex.Handle);
      else erase.Add(ex.Handle);
    }
    var erasedViews = existing.Where(e => erase.Contains(e.Handle) && planned.All(p => p.View.Key != e.Key)).Select(e => e.Key).ToList();
    if (erase.Count > 0)
      await CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
      {
        foreach (var h in erase)
          if (database.TryGetObjectId(new Handle(Convert.ToInt64(h, 16)), out var id) && !id.IsErased)
            transaction.GetObject(id, OpenMode.ForWrite).Erase();
        return null;
      });
    foreach (var p in planned)
    {
      if (done.ContainsKey(p.View.Key)) continue;
      var vpParams = new JsonObject
      {
        ["layout"] = name, ["centerX"] = p.PaperX + p.Width / 2, ["centerY"] = p.PaperY - p.Height / 2, ["width"] = p.Width, ["height"] = p.Height,
        ["viewCenterX"] = p.CenterX, ["viewCenterY"] = p.CenterY, ["twistDegrees"] = p.View.Twist, ["scaleDenominator"] = p.Scale, ["locked"] = true,
      };
      if (p.View.Layer != null) { vpParams["layer"] = p.View.Layer; vpParams["createLayer"] = p.View.CreateLayer; }
      if (p.View.Frozen != null) vpParams["frozenLayers"] = p.View.Frozen.DeepClone();
      var r = await ViewportCommands.CreateViewportAsync(vpParams) as Dictionary<string, object?>;
      var handle = r?["handle"] as string;
      if (r?.GetValueOrDefault("warning") is string w) warnings.Add($"{p.View.Key}: {w}");
      if (handle != null)
        await CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
        {
          ScriptIdCommands.EnsureApp(database, transaction);
          var obj = transaction.GetObject(database.GetObjectId(false, new Handle(Convert.ToInt64(handle, 16)), 0), OpenMode.ForWrite);
          ScriptIdCommands.Write(obj, vpPrefix + p.View.Key, p.Signature);
          return null;
        });
      done[p.View.Key] = (existing.Any(e => e.Key == p.View.Key) ? "recreated" : "created", handle);
    }

    // ---- the drawn items: titles, scale lines, legend rows, revision rows
    var items = new JsonArray();
    JsonObject Text(string id, string text, double x, double y, double h, string? layer, string attachment = "TopLeft")
    {
      var it = new JsonObject { ["id"] = id, ["type"] = "mtext", ["text"] = text, ["x"] = x, ["y"] = y, ["height"] = h, ["attachment"] = attachment };
      if (layer != null) it["layer"] = layer;
      return it;
    }
    foreach (var p in planned)
    {
      var v = p.View;
      if (v.Title.Length > 0) items.Add(Text($"view/{v.Key}/title", v.Title, v.AnchorX, v.AnchorY, v.TitleHeight, textLayer));
      if (v.ScaleText.Length > 0)
        items.Add(Text($"view/{v.Key}/scale", v.ScaleText.Replace("{scale}", F(p.Scale)), v.AnchorX,
          v.AnchorY - (v.Title.Length > 0 ? v.TitleHeight + 1.5 : 0), v.ScaleTextHeight, textLayer));
    }
    // the right column: its insertion point, the template's untagged legend rows erased
    Dictionary<string, object?>? columnReport = null;
    var drawnPrefix = $"sheet/{name}/";
    var blockAt = await CivilExecution.ReadAsync((doc, civilDoc, database, transaction) =>
    {
      var layout = LayoutCommands.FindLayout(database, transaction, name);
      var found = new Dictionary<string, Point3d>(StringComparer.OrdinalIgnoreCase);
      foreach (ObjectId id in (BlockTableRecord)transaction.GetObject(layout.BlockTableRecordId, OpenMode.ForRead))
        if (transaction.GetObject(id, OpenMode.ForRead) is BlockReference br) found.TryAdd(BlockName(br, transaction), br.Position);
      return found;
    });
    if (catalog != null)
    {
      var spalt = catalog["spalt"] as JsonObject ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{catalogFile} has no 'spalt'.");
      var colBlock = spalt["block"]?.GetValue<string>() ?? "Högerspalt VA";
      var colLayer = spalt["lager"]?.GetValue<string>();
      if (!blockAt.TryGetValue(colBlock, out var col))
        throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Layout '{name}' has no '{colBlock}' block for the right column. The layout and its viewports are made; nothing else was changed.");
      var rowBlocks = (catalog["rader"] as JsonArray ?? new JsonArray()).Select(r => r?["block"]?.GetValue<string>()).OfType<string>()
        .Append(catalog["fot"]?["block"]?.GetValue<string>() ?? "").Where(x => x.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
      var untagged = 0;
      if (rightColumn!["rows"] is JsonArray)
        untagged = await CivilExecution.WriteAsync((doc, civilDoc, database, transaction) =>
        {
          var layout = LayoutCommands.FindLayout(database, transaction, name);
          var n = 0;
          foreach (ObjectId id in (BlockTableRecord)transaction.GetObject(layout.BlockTableRecordId, OpenMode.ForRead))
            if (transaction.GetObject(id, OpenMode.ForRead) is BlockReference br && rowBlocks.Contains(BlockName(br, transaction))
                && !(ScriptIdCommands.Read(br).Id ?? "").StartsWith(drawnPrefix, StringComparison.Ordinal))
            { br.UpgradeOpen(); br.Erase(); n++; }
          return n;
        });
      foreach (var (block, dy) in stack)
      {
        var it = new JsonObject { ["id"] = $"tf/{block}", ["type"] = "block", ["blockName"] = block, ["x"] = col.X, ["y"] = col.Y + dy };
        if (colLayer != null) it["layer"] = colLayer;
        items.Add(it);
      }
      columnReport = new Dictionary<string, object?> { ["block"] = colBlock, ["at"] = new[] { Math.Round(col.X, 2), Math.Round(col.Y, 2) }, ["rows"] = stack.Select(s => s.Block).ToList(),
        ["templateRowsErased"] = untagged, ["catalog"] = catalogFile };
      if (rightColumn["values"] is JsonObject vals && vals.Count > 0)
        columnReport["attributes"] = await LayoutCommands.SetBlockAttributesAsync(new JsonObject { ["layout"] = name, ["blockName"] = colBlock, ["values"] = vals.DeepClone() });
    }
    // revision rows above the title block's heading row (the block's rows at y 99-106, columns as its heading)
    Dictionary<string, object?>? revReport = null;
    if (revisions != null)
    {
      if (!blockAt.TryGetValue(stampBlock, out var st))
        throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Layout '{name}' has no '{stampBlock}' block for the revision rows. The layout and its viewports are made; nothing else was changed.");
      const double headingTop = 106.0, row = 5.0, textH = 1.8;
      for (var i = 0; i < revisions.Count; i++)
      {
        if (revisions[i] is not JsonObject r) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"revisions[{i}] must be {{rev, text, ...}}.");
        var yb = st.Y + headingTop + i * row;
        var yt = yb + row;
        var frame = new JsonObject { ["id"] = $"rev/{i}/frame", ["type"] = "polyline", ["layer"] = revLayer,
          ["points"] = new JsonArray(new JsonArray(st.X + RevisionColumns[0], yb), new JsonArray(st.X + RevisionColumns[0], yt), new JsonArray(st.X, yt), new JsonArray(st.X, yb)) };
        items.Add(frame);
        for (var k = 1; k < RevisionColumns.Length - 1; k++)
          items.Add(new JsonObject { ["id"] = $"rev/{i}/col{k}", ["type"] = "line", ["layer"] = revLayer,
            ["points"] = new JsonArray(new JsonArray(st.X + RevisionColumns[k], yb), new JsonArray(st.X + RevisionColumns[k], yt)) });
        for (var k = 0; k < RevisionFields.Length; k++)
          if (r[RevisionFields[k]] is JsonValue fv && fv.TryGetValue<string>(out var txt) && txt.Length > 0)
            items.Add(Text($"rev/{i}/{RevisionFields[k]}", txt, st.X + RevisionColumns[k] + 1.5, yb + 1.5, textH, textLayer, "BottomLeft"));
      }
      revReport = new Dictionary<string, object?> { ["rows"] = revisions.Count, ["stampAt"] = new[] { Math.Round(st.X, 2), Math.Round(st.Y, 2) } };
    }
    var drawn = await DrawEntitiesCommands.DrawEntitiesAsync(new JsonObject
    {
      ["idPrefix"] = drawnPrefix, ["layout"] = name, ["items"] = items, ["replace"] = true, ["keepManual"] = keepManual,
    }, cancellationToken) as Dictionary<string, object?>;

    object? stampReport = null;
    if (stamp != null)
    {
      var values = (JsonObject)stamp.DeepClone();
      if (!values.Any(kv => string.Equals(kv.Key, "RITNINGSNR", StringComparison.OrdinalIgnoreCase))) values["RITNINGSNR"] = name;
      stampReport = await LayoutCommands.SetBlockAttributesAsync(new JsonObject { ["layout"] = name, ["blockName"] = stampBlock, ["values"] = values });
    }

    return new Dictionary<string, object?>
    {
      ["layout"] = name, ["made"] = made, ["source"] = source, ["views"] = Report(done), ["erasedViews"] = erasedViews,
      ["drawn"] = drawn == null ? null : new Dictionary<string, object?>
      {
        ["idPrefix"] = drawnPrefix, ["counts"] = drawn.GetValueOrDefault("counts"), ["manual"] = drawn.GetValueOrDefault("manual"), ["skipped"] = drawn.GetValueOrDefault("skipped"),
      },
      ["rightColumn"] = columnReport, ["stamp"] = stampReport, ["revisions"] = revReport, ["warnings"] = warnings,
    };
  }

  /// <summary>A view's model area (its source), scale and paper rectangle.</summary>
  private static Planned Plan(View v, Autodesk.Civil.ApplicationServices.CivilDocument civilDoc, Database database, Transaction transaction)
  {
    double minX, minY, maxX, maxY;
    string what;
    if (v.Source["bbox"] is JsonObject b)
    {
      (minX, minY, maxX, maxY) = (Num(b["minX"], "bbox.minX"), Num(b["minY"], "bbox.minY"), Num(b["maxX"], "bbox.maxX"), Num(b["maxY"], "bbox.maxY"));
      what = "bbox";
    }
    else if (v.Source["idPrefix"] is JsonValue pv && pv.TryGetValue<string>(out var prefix))
    {
      var e = ViewportCommands.ModelExtentsByScriptId(database, transaction, prefix)
        ?? throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"view {v.Key}: no model space object has a script id starting with '{prefix}'. Nothing was changed.");
      (minX, minY, maxX, maxY) = (e.MinPoint.X, e.MinPoint.Y, e.MaxPoint.X, e.MaxPoint.Y);
      what = "idPrefix";
    }
    else
    {
      var pvName = v.Source["profileView"]!.GetValue<string>();
      var view = ProfileEditCommands.FindProfileViewByName(civilDoc, transaction, pvName);
      what = "profileView";
      if (v.Source["bands"] is JsonValue bv && bv.TryGetValue<bool>(out var withBands) && !withBands)
      {
        // the graph only: its station and elevation range (bands, axes and title left out)
        double x0 = 0, y0 = 0, x1 = 0, y1 = 0;
        if (!view.FindXYAtStationAndElevation(view.StationStart, view.ElevationMin, ref x0, ref y0) | !view.FindXYAtStationAndElevation(view.StationEnd, view.ElevationMax, ref x1, ref y1))
          throw new JsonRpcDispatchException("CIVIL3D.OPERATION_FAILED", $"view {v.Key}: profile view '{pvName}' cannot place its own corners. Nothing was changed.");
        (minX, minY, maxX, maxY) = (Math.Min(x0, x1), Math.Min(y0, y1), Math.Max(x0, x1), Math.Max(y0, y1));
      }
      else
      {
        // the whole object: graph, axes, title and bands (bands are drawn at the drawing's annotation scale)
        var e = view.GeometricExtents;
        (minX, minY, maxX, maxY) = (e.MinPoint.X, e.MinPoint.Y, e.MaxPoint.X, e.MaxPoint.Y);
      }
    }
    if (!(maxX > minX) && !(maxY > minY))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"view {v.Key}: the {what} area has no extent. Nothing was changed.");
    // the area in the view's frame (paper x shows the model direction R(-twist) (1, 0)), as createViewport fitTo
    var t = -v.Twist * Math.PI / 180.0;
    double ux = Math.Cos(t), uy = Math.Sin(t), vx = -uy, vy = ux;
    var corners = new[] { (minX, minY), (maxX, minY), (maxX, maxY), (minX, maxY) };
    var us = corners.Select(q => q.Item1 * ux + q.Item2 * uy).ToList();
    var vs = corners.Select(q => q.Item1 * vx + q.Item2 * vy).ToList();
    double mu = (us.Min() + us.Max()) / 2, mv = (vs.Min() + vs.Max()) / 2, wu = us.Max() - us.Min(), hv = vs.Max() - vs.Min();
    var cx = mu * ux + mv * vx; var cy = mu * uy + mv * vy;
    double scale, w, h;
    if (v.Width is double fw && v.Height is double fh)
    {
      w = fw; h = fh;
      scale = v.Scale ?? Math.Max(1, Math.Ceiling(Math.Max(wu * 1000 / Math.Max(1e-6, fw - 2 * v.Margin), hv * 1000 / Math.Max(1e-6, fh - 2 * v.Margin)) - 1e-9));
    }
    else
    {
      scale = v.Scale!.Value;
      w = wu * 1000 / scale + 2 * v.Margin;
      h = hv * 1000 / scale + 2 * v.Margin;
    }
    var top = v.AnchorY - v.Gap;
    var sig = string.Join("|", F(v.AnchorX), F(top), F(w), F(h), F(cx), F(cy), F(scale), F(v.Twist), v.Layer ?? "",
      v.Frozen == null ? "" : string.Join(",", v.Frozen.Select(x => x?.ToString() ?? "")));
    return new Planned(v, scale, cx, cy, w, h, v.AnchorX, top, new[] { minX, minY, maxX, maxY }, sig);
  }

  /// <summary>The legend rows' offsets below the right column's insertion point (as the original Python legend script stacked them):
  /// catalogue order, a gap between groups, the P109 footnote after the symbol group; an error when the stack runs below
  /// the column's lowest line.</summary>
  internal static List<(string Block, double Dy)> Stack(JsonObject catalog, List<string> chosen)
  {
    var s = catalog["spalt"] as JsonObject ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "The catalogue has no 'spalt'. Nothing was changed.");
    double V(string k) => s[k]?.GetValue<double>() ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"The catalogue's spalt has no {k}. Nothing was changed.");
    var rows = (catalog["rader"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
    var known = rows.Select(r => r["block"]?.GetValue<string>() ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase);
    var unknown = chosen.Where(c => !known.Contains(c)).ToList();
    if (unknown.Count > 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"rightColumn.rows: not in the catalogue: {string.Join(", ", unknown)}. Nothing was changed.");
    var want = chosen.ToHashSet(StringComparer.OrdinalIgnoreCase);
    var order = rows.Where(r => want.Contains(r["block"]?.GetValue<string>() ?? "")).ToList();
    var outList = new List<(string, double)>();
    var y = V("forsta_rad_y");
    string? group = null;
    foreach (var r in order)
    {
      var gr = r["grupp"]?.GetValue<string>();
      if (group != null && gr != group) y -= V("gruppluft");
      outList.Add((r["block"]!.GetValue<string>(), y));
      group = gr;
      y -= V("radavstand");
    }
    if (catalog["fot"] is JsonObject fot && fot["block"]?.GetValue<string>() is { } fb && order.Any(r => r["grupp"]?.GetValue<string>() == fot["efter_grupp"]?.GetValue<string>()))
      outList.Add((fb, y + V("radavstand") - V("fot_avstand")));
    if (outList.Count > 0 && outList.Min(x => x.Item2) < V("nedersta_y"))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{order.Count} legend rows do not fit in the right column (lowest baseline {outList.Min(x => x.Item2):F1} < {V("nedersta_y")}). Nothing was changed.");
    return outList;
  }

  /// <summary>The legend catalogue: catalogPath, else legend_rows.json in the rule profile's parent folder or beside
  /// the profile (e.g. &lt;templates&gt;\legend_rows.json next to &lt;templates&gt;\Rules\rules_profile.json). Its keys
  /// (spalt, rader, grupp, fot, ...) are the catalogue's data format.</summary>
  internal static string CatalogPath(string? given, string? rulesPath)
  {
    if (!string.IsNullOrWhiteSpace(given)) return FileBoundary.ResolveLocalReadPath(given!.Trim(), ".json");
    var (profile, _) = RulesProfile.FindProfile(rulesPath);
    var dir = Path.GetDirectoryName(profile)!;
    foreach (var cand in new[] { Path.Combine(Path.GetDirectoryName(dir) ?? dir, "legend_rows.json"), Path.Combine(dir, "legend_rows.json") })
      if (File.Exists(cand)) return cand;
    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"No legend_rows.json beside {profile}; give rightColumn.catalogPath. Nothing was changed.");
  }
}
