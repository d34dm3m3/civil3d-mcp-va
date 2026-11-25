using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.DatabaseServices;

namespace Civil3DMcpPlugin;

/// <summary>C229 addLayoutTable and C235 addProfileViewGeometry (F7, 2026-11-24): two drawers on drawEntities (C223),
/// so that a rerun updates, leaves hand edits and erases what is no longer asked for under one script id prefix.</summary>
public static partial class DrawEntitiesCommands
{
  private static string Str(JsonNode? n) => n switch
  {
    null => "",
    JsonValue v when v.TryGetValue<string>(out var s) => s,
    JsonValue v when v.TryGetValue<double>(out var d) => d.ToString(System.Globalization.CultureInfo.InvariantCulture),
    JsonValue v when v.TryGetValue<bool>(out var b) => b ? "true" : "false",
    _ => n.ToJsonString(),
  };

  /// <summary>C229: a table of texts in a layout (paper units) or Model: {layout (default Model), x, y (the top left
  /// corner), columns: [{title, width, align? left|center|right, key?}], rows: [[cell, ...] | {key or title: cell}],
  /// textHeight (2.5), rowHeight? (1.8 x textHeight), headerHeight? (rowHeight), padding? (0.4 x textHeight), title?
  /// (a line above the header), titleHeight? (1.2 x textHeight), header=true, lines: "none" | "header" (default: a line
  /// under the header) | "grid" (border, row and column lines), layer?, textStyle? (layouts), id (the script id prefix),
  /// replace=true, keepManual=true, dryRun?}. One MText per cell (middle-attached, so rows line up whatever the
  /// characters), empty cells left out. Drawn with drawEntities under "{id}/": a rerun with other rows updates the cells,
  /// erases the rows no longer given and leaves cells moved or edited by hand. Answer: drawEntities' answer and table
  /// {x, y, width, height, bottomY, rows, columns}. Use case: a project script wrote the right
  /// column table text by text and erased it by handles kept in a JSON file.</summary>
  public static async Task<object?> AddLayoutTableAsync(JsonObject? parameters, CancellationToken cancellationToken)
  {
    var layout = PluginRuntime.GetOptionalString(parameters, "layout")?.Trim();
    var id = PluginRuntime.GetRequiredString(parameters, "id").Trim();
    var x0 = PluginRuntime.GetRequiredDouble(parameters, "x");
    var y0 = PluginRuntime.GetRequiredDouble(parameters, "y");
    var h = PluginRuntime.GetOptionalDouble(parameters, "textHeight") ?? 2.5;
    if (!(h > 0)) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "textHeight must be > 0. Nothing was changed.");
    var rowH = PluginRuntime.GetOptionalDouble(parameters, "rowHeight") ?? 1.8 * h;
    var headH = PluginRuntime.GetOptionalDouble(parameters, "headerHeight") ?? rowH;
    var pad = PluginRuntime.GetOptionalDouble(parameters, "padding") ?? 0.4 * h;
    var title = PluginRuntime.GetOptionalString(parameters, "title");
    var titleH = PluginRuntime.GetOptionalDouble(parameters, "titleHeight") ?? 1.2 * h;
    var header = PluginRuntime.GetOptionalBool(parameters, "header") ?? true;
    var lines = (PluginRuntime.GetOptionalString(parameters, "lines") ?? "header").Trim().ToLowerInvariant();
    if (lines is not ("none" or "header" or "grid"))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "lines must be none, header or grid. Nothing was changed.");
    var layer = PluginRuntime.GetOptionalString(parameters, "layer");
    var textStyle = PluginRuntime.GetOptionalString(parameters, "textStyle");
    if (PluginRuntime.GetParameter(parameters, "columns") is not JsonArray colList || colList.Count == 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "columns must list at least one {title, width, align?}. Nothing was changed.");
    var cols = colList.Select((n, i) =>
    {
      if (n is not JsonObject o || o["width"]?.GetValue<double>() is not double w || !(w > 0))
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"columns[{i}] needs width > 0. Nothing was changed.");
      var align = (o["align"]?.GetValue<string>() ?? "left").Trim().ToLowerInvariant();
      if (align is not ("left" or "center" or "right"))
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"columns[{i}].align must be left, center or right. Nothing was changed.");
      var t = o["title"]?.GetValue<string>() ?? "";
      return (Title: t, Key: o["key"]?.GetValue<string>() ?? t, Width: w, Align: align);
    }).ToList();
    var rowList = PluginRuntime.GetParameter(parameters, "rows") as JsonArray ?? new JsonArray();
    var rows = rowList.Select((n, r) => n switch
    {
      JsonArray a => cols.Select((c, i) => i < a.Count ? Str(a[i]) : "").ToList(),
      JsonObject o => cols.Select(c => Str(o[c.Key])).ToList(),
      _ => throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"rows[{r}] must be a list of cells or an object by column key. Nothing was changed."),
    }).ToList();

    var paper = !string.IsNullOrEmpty(layout) && !string.Equals(layout, "Model", StringComparison.OrdinalIgnoreCase);
    var items = new JsonArray();
    var width = cols.Sum(c => c.Width);
    var top = y0;
    JsonObject Text(string itemId, string text, double x, double y, double height, string attachment)
    {
      var it = new JsonObject { ["id"] = itemId, ["type"] = "mtext", ["text"] = text, ["x"] = x, ["y"] = y, ["height"] = height, ["attachment"] = attachment };
      if (layer != null) it["layer"] = layer;
      if (paper && textStyle != null) it["textStyle"] = textStyle;
      return it;
    }
    JsonObject Line(string itemId, double xa, double ya, double xb, double yb)
    {
      var it = new JsonObject { ["id"] = itemId, ["type"] = "line", ["points"] = new JsonArray(new JsonArray(xa, ya), new JsonArray(xb, yb)) };
      if (layer != null) it["layer"] = layer;
      return it;
    }
    if (!string.IsNullOrEmpty(title))
    {
      items.Add(Text("title", title, x0, top, titleH, "BottomLeft"));
      top -= 0.5 * titleH;
    }
    var tableTop = top;
    double ColX(int i) => x0 + cols.Take(i).Sum(c => c.Width);
    void Row(string prefix, List<string> cells, double yTop, double height)
    {
      for (var i = 0; i < cols.Count; i++)
      {
        if (string.IsNullOrEmpty(cells[i])) continue;
        var (x, att) = cols[i].Align switch
        {
          "center" => (ColX(i) + cols[i].Width / 2, "MiddleCenter"),
          "right" => (ColX(i) + cols[i].Width - pad, "MiddleRight"),
          _ => (ColX(i) + pad, "MiddleLeft"),
        };
        items.Add(Text($"{prefix}c{i}", cells[i], x, yTop - height / 2, h, att));
      }
    }
    if (header)
    {
      Row("h", cols.Select(c => c.Title).ToList(), top, headH);
      top -= headH;
      if (lines != "none") items.Add(Line("line-h", x0, top, x0 + width, top));
    }
    for (var r = 0; r < rows.Count; r++)
    {
      Row($"r{r}", rows[r], top, rowH);
      top -= rowH;
      if (lines == "grid") items.Add(Line($"line-r{r}", x0, top, x0 + width, top));
    }
    if (lines == "grid")
    {
      items.Add(Line("line-top", x0, tableTop, x0 + width, tableTop));
      for (var i = 0; i <= cols.Count; i++) items.Add(Line($"line-c{i}", ColX(i), tableTop, ColX(i), top));
    }

    var draw = new JsonObject
    {
      ["idPrefix"] = id + "/", ["items"] = items, ["replace"] = PluginRuntime.GetOptionalBool(parameters, "replace") ?? true,
      ["keepManual"] = PluginRuntime.GetOptionalBool(parameters, "keepManual") ?? true, ["dryRun"] = PluginRuntime.GetOptionalBool(parameters, "dryRun") ?? false,
    };
    if (paper) draw["layout"] = layout;
    var answer = await DrawEntitiesAsync(draw, cancellationToken) as Dictionary<string, object?> ?? new();
    answer["table"] = new Dictionary<string, object?>
    {
      ["x"] = x0, ["y"] = y0, ["width"] = Math.Round(width, 3), ["height"] = Math.Round(y0 - top, 3), ["bottomY"] = Math.Round(top, 3),
      ["rows"] = rows.Count, ["columns"] = cols.Count,
    };
    return answer;
  }

  /// <summary>C235: geometry in profile views given by station and elevation, drawn once per view: {profileViewNames,
  /// idPrefix, items: [{id, type (as drawEntities), points: [{station, elevation}] | station, elevation, text?, layer?, any
  /// drawEntities item key}], replace=true, keepManual=true, skipOutside=true (an item whose stations are all outside a
  /// view's station range is not drawn in it), layers?, dryRun?}. Each item becomes "{view}/{id}" under idPrefix (one
  /// drawEntities call for all views, so replace erases per view what is no longer given). Stations are raw (Civil API).
  /// A point outside the view's elevation range is placed on the view's linear scale. A multileader: points[0] the arrow,
  /// points[1] the text. Answer: drawEntities' answer, views [{name, drawn, skipped}]. Use case: project scripts
  /// (crossings, drop profiles, protective sleeves) converted station/elevation with profileViewStationToXY and drew per
  /// view in Python.</summary>
  public static async Task<object?> AddProfileViewGeometryAsync(JsonObject? parameters, CancellationToken cancellationToken)
  {
    var views = (PluginRuntime.GetParameter(parameters, "profileViewNames") as JsonArray)?.Select(n => n?.GetValue<string>()).OfType<string>().ToList();
    if (views is not { Count: > 0 })
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "profileViewNames must list at least one profile view. Nothing was changed.");
    var idPrefix = PluginRuntime.GetRequiredString(parameters, "idPrefix");
    if (PluginRuntime.GetParameter(parameters, "items") is not JsonArray itemList)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "items must be a list. Nothing was changed.");
    var skipOutside = PluginRuntime.GetOptionalBool(parameters, "skipOutside") ?? true;

    // the station/elevation of each item point
    var specs = itemList.Select((n, i) =>
    {
      if (n is not JsonObject o) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"items[{i}] must be an object. Nothing was changed.");
      var pts = new List<(double S, double E)>();
      if (o["points"] is JsonArray pa)
        foreach (var (pn, k) in pa.Select((pn, k) => (pn, k)))
        {
          if (pn is JsonObject po && po["station"] != null && po["elevation"] != null) pts.Add((po["station"]!.GetValue<double>(), po["elevation"]!.GetValue<double>()));
          else if (pn is JsonArray pp && pp.Count >= 2) pts.Add((pp[0]!.GetValue<double>(), pp[1]!.GetValue<double>()));
          else throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"items[{i}].points[{k}] must be {{station, elevation}} or [station, elevation]. Nothing was changed.");
        }
      else if (o["station"] != null && o["elevation"] != null) pts.Add((o["station"]!.GetValue<double>(), o["elevation"]!.GetValue<double>()));
      else throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"items[{i}] needs points [{{station, elevation}}] or station and elevation. Nothing was changed.");
      if (o["id"] is not JsonValue) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"items[{i}] needs an id. Nothing was changed.");
      return (Item: o, Points: pts);
    }).ToList();

    var placed = await CivilExecution.ReadAsync((doc, civilDoc, database, transaction) =>
    {
      var outItems = new JsonArray();
      var report = new List<Dictionary<string, object?>>();
      foreach (var vn in views)
      {
        var view = ProfileEditCommands.FindProfileViewByName(civilDoc, transaction, vn);
        double s0 = view.StationStart, s1 = view.StationEnd, e0 = view.ElevationMin, e1 = view.ElevationMax;
        // the view's linear scale from two points inside its ranges (a corner itself may not be found)
        double sa = s0 + 0.25 * (s1 - s0), sb = s0 + 0.75 * (s1 - s0), ea = e0 + 0.25 * (e1 - e0), eb = e0 + 0.75 * (e1 - e0);
        double xa = 0, ya = 0, xb = 0, yb = 0;
        var linear = s1 > s0 && e1 > e0 && view.FindXYAtStationAndElevation(sa, ea, ref xa, ref ya) & view.FindXYAtStationAndElevation(sb, eb, ref xb, ref yb);
        (double X, double Y) ToXY(double s, double e)
        {
          double x = 0, y = 0;
          if (s >= s0 - 1e-6 && s <= s1 + 1e-6 && e >= e0 - 1e-6 && e <= e1 + 1e-6 && view.FindXYAtStationAndElevation(s, e, ref x, ref y)) return (x, y);
          if (!linear) throw new JsonRpcDispatchException("CIVIL3D.OPERATION_FAILED", $"Profile view '{view.Name}' cannot place station {s} elevation {e} (stations {s0:F2}-{s1:F2}, elevations {e0:F2}-{e1:F2}). Nothing was changed.");
          return (xa + (s - sa) * (xb - xa) / (sb - sa), ya + (e - ea) * (yb - ya) / (eb - ea));
        }
        int drawn = 0, skipped = 0;
        foreach (var (item, pts) in specs)
        {
          if (skipOutside && pts.All(p => p.S < s0 - 1e-6 || p.S > s1 + 1e-6)) { skipped++; continue; }
          var copy = (JsonObject)item.DeepClone();
          copy.Remove("station"); copy.Remove("elevation");
          copy["id"] = $"{view.Name}/{Str(item["id"])}";
          var xy = pts.Select(p => ToXY(p.S, p.E)).ToList();
          if (item["points"] != null) copy["points"] = new JsonArray(xy.Select(q => (JsonNode?)new JsonArray(q.X, q.Y)).ToArray());
          else { copy["x"] = xy[0].X; copy["y"] = xy[0].Y; }
          outItems.Add(copy);
          drawn++;
        }
        report.Add(new Dictionary<string, object?> { ["name"] = view.Name, ["drawn"] = drawn, ["skipped"] = skipped,
          ["stationStart"] = Math.Round(s0, 3), ["stationEnd"] = Math.Round(s1, 3) });
      }
      return (Items: outItems, Report: report);
    });

    var draw = new JsonObject
    {
      ["idPrefix"] = idPrefix, ["items"] = placed.Items, ["replace"] = PluginRuntime.GetOptionalBool(parameters, "replace") ?? true,
      ["keepManual"] = PluginRuntime.GetOptionalBool(parameters, "keepManual") ?? true, ["dryRun"] = PluginRuntime.GetOptionalBool(parameters, "dryRun") ?? false,
    };
    if (PluginRuntime.GetParameter(parameters, "layers") is JsonArray layers) draw["layers"] = layers.DeepClone();
    if (PluginRuntime.GetOptionalDouble(parameters, "tolerance") is double tol) draw["tolerance"] = tol;
    var answer = await DrawEntitiesAsync(draw, cancellationToken) as Dictionary<string, object?> ?? new();
    answer["views"] = placed.Report;
    return answer;
  }
}
