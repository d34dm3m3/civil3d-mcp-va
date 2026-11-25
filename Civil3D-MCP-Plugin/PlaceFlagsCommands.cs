using System.Globalization;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.DatabaseServices;
using AcEntity = Autodesk.AutoCAD.DatabaseServices.Entity;

namespace Civil3DMcpPlugin;

/// <summary>
/// C241 placeFlags (F9, 2026-11-25): flags (multileaders without arrow, framed and masked, text level on the sheet)
/// placed free of texts, network parts and each other, as one rerunnable call. Use case: the original Python text
/// layout and the projects' plan and profile flag scripts (9 scripts, about 1 600 lines) read the obstacles, scored candidate positions in Python and wrote the flags one at a time with
/// a Python-side guard for hand-moved flags).
///
/// The placement is FlagPlacer (FlagPlacementMath.cs, the Python algorithm ported and checked against it on the same
/// cases). The flags are drawn with drawEntities (C223) under idPrefix, so a flag the user moved by hand is left as it
/// is (drawEntities' rule), and its box and leader are obstacles for the others; flags of the prefix that no item
/// names are erased (eraseStale). Flags written by the earlier Python flag class (idData {beraknad: computed text point, pil: arrow, manuell: manual}) count
/// as hand-moved when their text lies more than tolerance from the position Python computed, as there.
/// </summary>
public static class PlaceFlagsCommands
{
  private sealed record FlagItem(int Index, string Id, double X, double Y, string Text, List<(double X, double Y)> Extra, List<double>? Directions,
    string? Layer, (double X, double Y)? Fixed);

  private sealed record OldFlag(string Id, string Handle, bool Manual, string Reason, double TextX, double TextY, List<(double X, double Y)> Arrows,
    string? Contents, JsonObject? Data, bool HasState, bool Locked);

  private sealed record Scene(List<(double X, double Y)> Area, double Rotation, double? Scale, string? ViewportHandle, List<OldFlag> Old,
    List<List<(double X, double Y)>> Soft, List<List<(double X, double Y)>> Pipes, List<(double X, double Y, double R)> Structures,
    List<(List<(double X, double Y)> Pts, double R, bool Closed, string What)> Hard, Dictionary<string, int> Counts, List<string> Notes);

  private static double Num(JsonNode? n, string what) =>
    n is JsonValue v && v.TryGetValue<double>(out var d) && double.IsFinite(d) ? d
    : throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{what} must be a number. Nothing was changed.");

  private static (double X, double Y) XYOf(JsonNode? n, string what) => n switch
  {
    JsonObject o when o["x"] != null && o["y"] != null => (Num(o["x"], what + ".x"), Num(o["y"], what + ".y")),
    JsonArray a when a.Count >= 2 => (Num(a[0], what + "[0]"), Num(a[1], what + "[1]")),
    _ => throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{what} must be {{x, y}} or [x, y]. Nothing was changed."),
  };

  private static List<string> Strings(JsonNode? n) => (n as JsonArray)?.Select(x => x is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : null)
    .OfType<string>().Where(s => s.Length > 0).ToList() ?? new List<string>();

  /// <summary>C241: {idPrefix, layout? (its plan viewport: viewport? handle, else the one showing most anchors) | area?
  /// ([[x, y], ...] model polygon or {minX, minY, maxX, maxY}) with twist? (the viewport twist, degrees; the text runs
  /// along -twist), items: [{id, anchor {x, y}, text, extraAnchors?: [{x, y}], directions?: [degrees on the sheet,
  /// counterclockwise from its x axis], layer?, fixed?: {x, y} (text position given, no search)}], textHeight (model)
  /// | textHeightMm (paper, with layout or scale), obstacles?: {networks (["*"]), layerContains ([]: hard objects on
  /// layers containing any), includeXrefs (false), softLines (true: lines and polylines on visible layers count as
  /// lines under a box), texts (true: texts, mtexts and multileaders are hard), clearance (0.15 x h), structureClearance
  /// (0.1 x h), textClearance (0.1 x h), reserve (0.35 x h)}, keepManual (true), eraseStale (true), manualText keep|update,
  /// tolerance (0.05), layer ("NOT-FLAGGA"), frame (true), mask (true), arrowhead ("none"), drawOrderTop (true), margin
  /// (1.0 x h, from the area's edge), inset (0: anchors inside the area shrunk by inset), dryRun?}.</summary>
  public static async Task<object?> PlaceFlagsAsync(JsonObject? parameters, CancellationToken cancellationToken)
  {
    var idPrefix = PluginRuntime.GetRequiredString(parameters, "idPrefix").Trim();
    if (idPrefix.Length == 0) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "idPrefix must not be empty. Nothing was changed.");
    var layoutName = PluginRuntime.GetOptionalString(parameters, "layout")?.Trim();
    var vpHandle = PluginRuntime.GetOptionalString(parameters, "viewport")?.Trim();
    var areaNode = PluginRuntime.GetParameter(parameters, "area");
    if (string.IsNullOrEmpty(layoutName) == (areaNode == null))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Give layout (its plan viewport is the area) or area (a model polygon), not both. Nothing was changed.");
    var twistGiven = PluginRuntime.GetOptionalDouble(parameters, "twist");
    var keepManual = PluginRuntime.GetOptionalBool(parameters, "keepManual") ?? true;
    var eraseStale = PluginRuntime.GetOptionalBool(parameters, "eraseStale") ?? true;
    var manualText = (PluginRuntime.GetOptionalString(parameters, "manualText") ?? "keep").Trim().ToLowerInvariant();
    if (manualText is not ("keep" or "update")) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "manualText must be keep or update. Nothing was changed.");
    var tolerance = PluginRuntime.GetOptionalDouble(parameters, "tolerance") ?? 0.05;
    var defLayer = PluginRuntime.GetOptionalString(parameters, "layer") ?? "NOT-FLAGGA";
    var frame = PluginRuntime.GetOptionalBool(parameters, "frame") ?? true;
    var mask = PluginRuntime.GetOptionalBool(parameters, "mask") ?? true;
    var arrowhead = PluginRuntime.GetOptionalString(parameters, "arrowhead") ?? "none";
    var drawOrderTop = PluginRuntime.GetOptionalBool(parameters, "drawOrderTop") ?? true;
    var dryRun = PluginRuntime.GetOptionalBool(parameters, "dryRun") ?? false;
    var hGiven = PluginRuntime.GetOptionalDouble(parameters, "textHeight");
    var hMm = PluginRuntime.GetOptionalDouble(parameters, "textHeightMm");
    var scaleGiven = PluginRuntime.GetOptionalDouble(parameters, "scale");
    if (hGiven == null && hMm == null) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Give textHeight (model units) or textHeightMm (paper mm, with layout or scale). Nothing was changed.");
    var ob = PluginRuntime.GetParameter(parameters, "obstacles") as JsonObject ?? new JsonObject();
    var networks = ob["networks"] is JsonArray ? Strings(ob["networks"]) : new List<string> { "*" };
    var hardLayers = Strings(ob["layerContains"]);
    var includeXrefs = ob["includeXrefs"]?.GetValue<bool>() ?? false;
    var softLines = ob["softLines"]?.GetValue<bool>() ?? true;
    var texts = ob["texts"]?.GetValue<bool>() ?? true;

    if (PluginRuntime.GetParameter(parameters, "items") is not JsonArray itemList)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "items must be a list of {id, anchor {x, y}, text}. Nothing was changed.");
    var items = itemList.Select((n, i) =>
    {
      var w = $"items[{i}]";
      if (n is not JsonObject o) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{w} must be an object. Nothing was changed.");
      var id = o["id"] is JsonValue iv && iv.TryGetValue<string>(out var s) ? s.Trim() : "";
      if (id.Length == 0) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{w} needs an id. Nothing was changed.");
      if (id.StartsWith(idPrefix, StringComparison.Ordinal)) id = id[idPrefix.Length..];
      var (x, y) = XYOf(o["anchor"], w + ".anchor");
      var text = o["text"] is JsonValue tv && tv.TryGetValue<string>(out var t) && t.Length > 0 ? t
        : throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{w} ({id}) needs text. Nothing was changed.");
      var extra = (o["extraAnchors"] as JsonArray)?.Select((q, k) => XYOf(q, $"{w}.extraAnchors[{k}]")).ToList() ?? new List<(double X, double Y)>();
      var dirs = (o["directions"] as JsonArray)?.Select((q, k) => Num(q, $"{w}.directions[{k}]")).ToList();
      (double X, double Y)? fixedAt = o["fixed"] != null ? XYOf(o["fixed"], w + ".fixed") : null;
      return new FlagItem(i, id, x, y, text, extra, dirs, o["layer"] is JsonValue lv && lv.TryGetValue<string>(out var l) && l.Trim().Length > 0 ? l.Trim() : null, fixedAt);
    }).ToList();
    foreach (var dup in items.GroupBy(it => it.Id, StringComparer.Ordinal).Where(g => g.Count() > 1))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"id '{dup.Key}' is given {dup.Count()} times. Nothing was changed.");

    // ---- read: the area, the existing flags of the prefix, the obstacles
    var scene = await CivilExecution.ReadAsync((doc, civilDoc, database, transaction) =>
    {
      var notes = new List<string>();
      List<(double X, double Y)> area;
      double rotation;
      double? scale = scaleGiven;
      string? usedVp = null;
      Viewport? viewport = null;
      if (!string.IsNullOrEmpty(layoutName))
      {
        var layout = LayoutCommands.FindLayout(database, transaction, layoutName!);
        var paperVp = ViewportCommands.PaperViewportId(layout, transaction);
        var cands = new List<(Viewport Vp, List<double[]> Poly)>();
        foreach (ObjectId id in (BlockTableRecord)transaction.GetObject(layout.BlockTableRecordId, OpenMode.ForRead))
          if (id != paperVp && transaction.GetObject(id, OpenMode.ForRead) is Viewport vp && ViewportCommands.ModelPolygon(vp) is { } poly)
            if (vpHandle == null || string.Equals(vp.Handle.ToString(), vpHandle, StringComparison.OrdinalIgnoreCase)) cands.Add((vp, poly));
        if (cands.Count == 0)
          throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", vpHandle == null ? $"Layout '{layoutName}' has no viewport with a view. Nothing was changed."
            : $"Layout '{layoutName}' has no viewport {vpHandle}. Nothing was changed.");
        var pick = cands.OrderByDescending(c => items.Count(it => ViewportCommands.InPolygon(c.Poly, it.X, it.Y))).ThenByDescending(c => c.Vp.Width * c.Vp.Height).First();
        viewport = pick.Vp;
        area = pick.Poly.Select(p => (p[0], p[1])).ToList();
        rotation = -pick.Vp.TwistAngle * 180 / Math.PI;
        scale ??= 1000.0 / pick.Vp.CustomScale;
        usedVp = pick.Vp.Handle.ToString();
      }
      else
      {
        area = areaNode switch
        {
          JsonArray a when a.Count >= 3 => a.Select((q, k) => XYOf(q, $"area[{k}]")).ToList(),
          JsonObject b => new List<(double X, double Y)>
          {
            (Num(b["minX"], "area.minX"), Num(b["minY"], "area.minY")), (Num(b["maxX"], "area.maxX"), Num(b["minY"], "area.minY")),
            (Num(b["maxX"], "area.maxX"), Num(b["maxY"], "area.maxY")), (Num(b["minX"], "area.minX"), Num(b["maxY"], "area.maxY")),
          },
          _ => throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "area must be a polygon [[x, y], ...] (3 or more) or {minX, minY, maxX, maxY}. Nothing was changed."),
        };
        rotation = -(twistGiven ?? 0);
      }
      var h = hGiven ?? (scale is double sc ? hMm!.Value * sc / 1000.0
        : throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "textHeightMm needs layout or scale. Nothing was changed."));
      double minX = area.Min(p => p.X) - 5 * h, maxX = area.Max(p => p.X) + 5 * h, minY = area.Min(p => p.Y) - 5 * h, maxY = area.Max(p => p.Y) + 5 * h;
      bool Near(Extents3d e) => !(e.MaxPoint.X < minX || e.MinPoint.X > maxX || e.MaxPoint.Y < minY || e.MinPoint.Y > maxY);

      // layers that do not show: off or frozen in the drawing, frozen in the viewport
      var hidden = new HashSet<ObjectId>();
      var lt = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
      foreach (ObjectId lid in lt)
        if (transaction.GetObject(lid, OpenMode.ForRead) is LayerTableRecord l && (l.IsOff || l.IsFrozen)) hidden.Add(lid);
      if (viewport != null) foreach (ObjectId lid in viewport.GetFrozenLayers()) hidden.Add(lid);

      // ---- the prefix's flags and whether they were moved by hand
      var old = new List<OldFlag>();
      foreach (var ex in DrawEntitiesCommands.ReadExisting(database, transaction, idPrefix, null, tolerance))
      {
        if (!database.TryGetObjectId(new Handle(Convert.ToInt64(ex.Handle, 16)), out var oid) || transaction.GetObject(oid, OpenMode.ForRead) is not MLeader ml
            || ml.ContentType != ContentType.MTextContent) continue;
        var arrows = new List<(double X, double Y)>();
        foreach (var li in ml.GetLeaderIndexes())
          foreach (var ln in ml.GetLeaderLineIndexes((int)li)) { var v = ml.GetFirstVertex((int)ln); arrows.Add((v.X, v.Y)); }
        string? contents;
        using (var mt = ml.MText) contents = mt?.Contents;
        var data = ex.ItemData as JsonObject;
        var manual = false; var reason = "";
        if (ex.HasState)
        {
          if (ex.ChangedByHand) { manual = true; reason = $"changed by hand since placeFlags wrote it (moved {ex.Moved?.ToString("0.###", CultureInfo.InvariantCulture) ?? "?"} m or text edited)"; }
          else if (data?["manual"]?.GetValue<bool>() == true) { manual = true; reason = "kept as hand-moved (earlier run)"; }
        }
        else if (ex.RawData is { Length: > 0 } raw && raw[0] == '{')
        {
          // a flag of the earlier Python flag class: {beraknad: [x, y] (computed), pil (arrow), manuell (manual)}
          try
          {
            if (JsonNode.Parse(raw) is JsonObject py)
            {
              if (py["manuell"]?.GetValue<bool>() == true) { manual = true; reason = "marked manual by the earlier Python flag tool"; }
              else if (py["beraknad"] is JsonArray b && b.Count >= 2)
              {
                var d = Math.Sqrt(Math.Pow(ml.TextLocation.X - b[0]!.GetValue<double>(), 2) + Math.Pow(ml.TextLocation.Y - b[1]!.GetValue<double>(), 2));
                if (d > tolerance) { manual = true; reason = $"moved {d:0.###} m from the position the earlier Python flag tool computed"; }
              }
            }
          }
          catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException or FormatException) { }
        }
        old.Add(new OldFlag(ex.Id[idPrefix.Length..], ex.Handle, manual && keepManual, reason, ml.TextLocation.X, ml.TextLocation.Y, arrows, contents, data, ex.HasState, ex.Locked));
      }
      var oldHandles = old.Select(o => o.Handle).ToHashSet(StringComparer.OrdinalIgnoreCase);

      // ---- networks: pipes (soft x 10, hard, reserved) and structure bodies (hard)
      var pipes = new List<List<(double X, double Y)>>();
      var structures = new List<(double X, double Y, double R)>();
      var names = PipeNetworkCommands.ListPipeNetworkNames(civilDoc, transaction).Where(n => networks.Any(p => PipeNetworkCommands.NetworkNameMatch(p, n))).ToList();
      foreach (var nn in names)
      {
        var net = PipeNetworkCommands.FindPipeNetworkByName(civilDoc, transaction, nn, OpenMode.ForRead);
        foreach (ObjectId pid in net.GetPipeIds())
          if (transaction.GetObject(pid, OpenMode.ForRead) is Pipe p && !hidden.Contains(p.LayerId))
          {
            var a = (p.StartPoint.X, p.StartPoint.Y); var b = (p.EndPoint.X, p.EndPoint.Y);
            if (Math.Sqrt(Math.Pow(a.Item1 - b.Item1, 2) + Math.Pow(a.Item2 - b.Item2, 2)) > 0.05
                && !(Math.Max(a.Item1, b.Item1) < minX || Math.Min(a.Item1, b.Item1) > maxX || Math.Max(a.Item2, b.Item2) < minY || Math.Min(a.Item2, b.Item2) > maxY))
              pipes.Add(new List<(double X, double Y)> { a, b });
          }
        foreach (ObjectId sid in net.GetStructureIds())
          if (transaction.GetObject(sid, OpenMode.ForRead) is Structure s && !PipeNetworkCommands.InlineFamilies.Contains(s.PartFamilyName ?? "") && !hidden.Contains(s.LayerId)
              && s.Location.X >= minX && s.Location.X <= maxX && s.Location.Y >= minY && s.Location.Y <= maxY)
          {
            double r;
            try { r = Math.Max(s.InnerDiameterOrWidth / 2, 0.1); } catch (Exception) { r = 0.2; }
            structures.Add((s.Location.X, s.Location.Y, r));
          }
      }

      // ---- other objects: soft lines, hard objects on the given layers, texts
      var soft = new List<List<(double X, double Y)>>();
      var hard = new List<(List<(double X, double Y)> Pts, double R, bool Closed, string What)>();
      var counts = new Dictionary<string, int> { ["networks"] = names.Count, ["pipes"] = pipes.Count, ["structures"] = structures.Count };
      bool HardLayer(string layer) => hardLayers.Any(s => layer.Contains(s, StringComparison.OrdinalIgnoreCase));
      var step = Math.Max(0.25 * h, 0.05);
      List<(double X, double Y)> Sample(Curve c, Matrix3d xf)
      {
        var list = new List<(double X, double Y)>();
        void Add(Point3d q) { var t = q.TransformBy(xf); list.Add((t.X, t.Y)); }
        if (c is Polyline pl)
        {
          var n = pl.Closed ? pl.NumberOfVertices : pl.NumberOfVertices - 1;
          if (pl.NumberOfVertices > 0) Add(pl.GetPoint3dAt(0));
          for (var i = 0; i < n; i++)
          {
            if (Math.Abs(pl.GetBulgeAt(i)) > 1e-9)
            {
              var seg = pl.GetArcSegmentAt(i);
              var k = Math.Clamp((int)Math.Ceiling(seg.Radius * Math.Abs(seg.EndAngle - seg.StartAngle) / step), 2, 200);
              for (var m = 1; m < k; m++) Add(pl.GetPointAtParameter(i + (double)m / k));
            }
            Add(pl.GetPoint3dAt((i + 1) % pl.NumberOfVertices));
          }
        }
        else if (c is Line ln) { Add(ln.StartPoint); Add(ln.EndPoint); }
        else if (c is Polyline2d or Polyline3d) { for (var t = c.StartParam; t <= c.EndParam + 1e-9; t += 1) Add(c.GetPointAtParameter(Math.Min(t, c.EndParam))); }
        else
        {
          var length = c.GetDistanceAtParameter(c.EndParam) - c.GetDistanceAtParameter(c.StartParam);
          var k = Math.Clamp((int)Math.Ceiling(length / step), 8, 2000);
          for (var m = 0; m <= k; m++) Add(c.GetPointAtDist(length * m / k));
        }
        return list;
      }
      List<(double X, double Y)> Box(Extents3d e) => new() { (e.MinPoint.X, e.MinPoint.Y), (e.MaxPoint.X, e.MinPoint.Y), (e.MaxPoint.X, e.MaxPoint.Y), (e.MinPoint.X, e.MaxPoint.Y) };
      var textR = (ob["textClearance"]?.GetValue<double>() ?? 0.1) * (ob["textClearance"] == null ? h : 1);
      var clr = (ob["clearance"]?.GetValue<double>() ?? 0.15) * (ob["clearance"] == null ? h : 1);
      void Collect(BlockTableRecord space, Matrix3d xf, int depth, string source)
      {
        foreach (ObjectId oid in space)
        {
          if (transaction.GetObject(oid, OpenMode.ForRead) is not AcEntity e || e is Viewport) continue;
          if (depth == 0 && oldHandles.Contains(e.Handle.ToString())) continue;
          if (hidden.Contains(e.LayerId)) continue;
          if (e is Pipe or Structure) continue;   // networks are read above
          Extents3d ext;
          try { ext = e.GeometricExtents; if (depth > 0) ext.TransformBy(xf); } catch (Exception) { continue; }
          if (!Near(ext)) continue;
          var isHard = HardLayer(e.Layer);
          try
          {
            switch (e)
            {
              case BlockReference br:
              {
                var btr = (BlockTableRecord)transaction.GetObject(br.BlockTableRecord, OpenMode.ForRead);
                if (btr.IsFromExternalReference)
                {
                  if (includeXrefs && depth < 4 && btr.IsResolved) Collect(btr, xf * br.BlockTransform, depth + 1, "xref:" + btr.Name);
                  continue;
                }
                if (isHard) { hard.Add((Box(ext), clr, true, "block")); counts["hardBlocks"] = counts.GetValueOrDefault("hardBlocks") + 1; }
                break;
              }
              case MText mt when texts || isHard:
              {
                var pts = mt.GetBoundingPoints();
                var list = new List<(double X, double Y)>();
                foreach (Point3d q in pts) { var t = q.TransformBy(xf); list.Add((t.X, t.Y)); }
                // GetBoundingPoints: top left, top right, bottom left, bottom right
                if (list.Count == 4) list = new() { list[2], list[3], list[1], list[0] };
                hard.Add((list, textR, true, "mtext"));
                counts["texts"] = counts.GetValueOrDefault("texts") + 1;
                break;
              }
              case DBText when texts || isHard:
                hard.Add((Box(ext), textR, true, "text"));
                counts["texts"] = counts.GetValueOrDefault("texts") + 1;
                break;
              case MLeader ml when texts || isHard:
              {
                if (ml.ContentType == ContentType.MTextContent)
                  using (var mt = ml.MText)
                  {
                    try
                    {
                      var bp = mt.GetBoundingPoints();
                      var list = new List<(double X, double Y)>();
                      foreach (Point3d q in bp) { var t = q.TransformBy(xf); list.Add((t.X, t.Y)); }
                      if (list.Count == 4) hard.Add((new() { list[2], list[3], list[1], list[0] }, textR, true, "mleader"));
                    }
                    catch (Exception) { hard.Add((Box(ext), textR, true, "mleader")); }
                  }
                foreach (var li in ml.GetLeaderIndexes())
                  foreach (var ln in ml.GetLeaderLineIndexes((int)li))
                  {
                    var a = ml.GetFirstVertex((int)ln).TransformBy(xf);
                    var b = ml.TextLocation.TransformBy(xf);
                    hard.Add((new() { (a.X, a.Y), (b.X, b.Y) }, textR, false, "leader"));
                  }
                counts["texts"] = counts.GetValueOrDefault("texts") + 1;
                break;
              }
              case Circle c when isHard:
              {
                var q = c.Center.TransformBy(xf);
                hard.Add((new() { (q.X, q.Y) }, c.Radius + clr, false, "circle"));
                counts["hardObjects"] = counts.GetValueOrDefault("hardObjects") + 1;
                break;
              }
              case DBPoint p when isHard:
              {
                var q = p.Position.TransformBy(xf);
                hard.Add((new() { (q.X, q.Y) }, clr, false, "point"));
                counts["hardObjects"] = counts.GetValueOrDefault("hardObjects") + 1;
                break;
              }
              case Curve c when (isHard && c is Line or Polyline or Polyline2d or Polyline3d or Arc or Spline or Ellipse) || (softLines && c is Line or Polyline or Polyline2d or Polyline3d or Arc):
              {
                var pts = Sample(c, xf);
                if (pts.Count < 2) break;
                if (isHard) { hard.Add((pts, clr, false, "line")); counts["hardObjects"] = counts.GetValueOrDefault("hardObjects") + 1; }
                else { soft.Add(pts); counts["softLines"] = counts.GetValueOrDefault("softLines") + 1; }
                break;
              }
            }
          }
          catch (Autodesk.AutoCAD.Runtime.Exception ex) { if (notes.Count < 20) notes.Add($"{source} {e.Handle}: {ex.Message}"); }
        }
      }
      Collect((BlockTableRecord)transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(database), OpenMode.ForRead), Matrix3d.Identity, 0, "model");
      return new Scene(area, rotation, scale, usedVp, old, soft, pipes, structures, hard, counts, notes);
    });

    var hModel = hGiven ?? hMm!.Value * scene.Scale!.Value / 1000.0;
    double Scaled(string key, double perH) => ob[key] is JsonNode n ? Num(n, "obstacles." + key) : perH * hModel;
    var margin = PluginRuntime.GetOptionalDouble(parameters, "margin") ?? 1.0 * hModel;
    var inset = PluginRuntime.GetOptionalDouble(parameters, "inset") ?? 0.0;

    // ---- place
    var placer = new FlagPlacer(scene.Rotation, scene.Area, margin);
    foreach (var pts in scene.Pipes)
    {
      placer.AddSoftLine(pts, FlagPlacer.NetworkWeight);
      placer.ReserveBuffered(pts, Scaled("reserve", 0.35));
      placer.AddHard(pts, Scaled("clearance", 0.15));
    }
    foreach (var (x, y, r) in scene.Structures) placer.AddHard(new[] { (x, y) }, r + Scaled("structureClearance", 0.1));
    foreach (var pts in scene.Soft) placer.AddSoftLine(pts);
    foreach (var (pts, r, closed, _) in scene.Hard) placer.AddHard(pts, r, closed);

    var byId = scene.Old.GroupBy(o => o.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
    // hand-moved flags first: they stay where the user put them and the others keep clear of them
    var manualOut = new List<Dictionary<string, object?>>();
    foreach (var it in items)
      if (byId.TryGetValue(it.Id, out var o) && o.Manual)
      {
        var a = o.Arrows.Count > 0 ? o.Arrows[0] : (it.X, it.Y);
        placer.AddPlacedFlag(a.Item1, a.Item2, o.TextX, o.TextY, o.Contents ?? it.Text, hModel);
        manualOut.Add(new Dictionary<string, object?>
        {
          ["id"] = it.Id, ["handle"] = o.Handle, ["text"] = new[] { Math.Round(o.TextX, 3), Math.Round(o.TextY, 3) }, ["reason"] = o.Reason,
          ["textDiffers"] = !string.Equals(o.Contents, it.Text.Replace("\r\n", "\\P").Replace("\n", "\\P"), StringComparison.Ordinal),
        });
      }
    foreach (var it in items.Where(i => i.Fixed != null && !(byId.TryGetValue(i.Id, out var o) && o.Manual)))
      placer.AddPlacedFlag(it.X, it.Y, it.Fixed!.Value.X, it.Fixed.Value.Y, it.Text, hModel);

    var placed = new List<Dictionary<string, object?>>();
    var noFree = new List<string>();
    var outside = new List<string>();
    var drawItems = new JsonArray();
    JsonObject Item(FlagItem it, double ax, double ay, double tx, double ty, string text, List<(double X, double Y)> extra, JsonObject? data)
    {
      var o = new JsonObject
      {
        ["id"] = it.Id, ["type"] = "mleader", ["x"] = ax, ["y"] = ay, ["textX"] = tx, ["textY"] = ty, ["text"] = text, ["height"] = hModel,
        ["rotation"] = Math.Round(scene.Rotation, 9), ["layer"] = it.Layer ?? defLayer, ["frame"] = frame, ["arrowhead"] = arrowhead,
      };
      if (mask) o["backgroundColor"] = 0;
      if (extra.Count > 0) o["extraPoints"] = new JsonArray(extra.Select(q => (JsonNode?)new JsonObject { ["x"] = q.X, ["y"] = q.Y }).ToArray());
      if (data != null) o["data"] = data;
      return o;
    }
    foreach (var it in items)
    {
      if (byId.TryGetValue(it.Id, out var o) && o.Manual)
      {
        // as written last time (the stored spec), else where it lies now
        var at = o.Data?["at"] as JsonArray;
        double ax, ay, tx, ty;
        if (at is { Count: 4 }) (ax, ay, tx, ty) = (at[0]!.GetValue<double>(), at[1]!.GetValue<double>(), at[2]!.GetValue<double>(), at[3]!.GetValue<double>());
        else
        {
          var a = o.Arrows.Count > 0 ? o.Arrows[0] : (it.X, it.Y);
          (ax, ay, tx, ty) = (Math.Round(a.Item1, 4), Math.Round(a.Item2, 4), Math.Round(o.TextX, 4), Math.Round(o.TextY, 4));
        }
        var text = manualText == "update" ? it.Text : o.Data?["text"]?.GetValue<string>() ?? o.Contents ?? it.Text;
        var extra = o.Data?["at"] is JsonArray ? it.Extra : o.Arrows.Skip(1).ToList();
        drawItems.Add(Item(it, ax, ay, tx, ty, text, extra, new JsonObject { ["manual"] = true, ["at"] = new JsonArray(ax, ay, tx, ty), ["text"] = text }));
        continue;
      }
      if (it.Fixed is { } fx)
      {
        drawItems.Add(Item(it, it.X, it.Y, fx.X, fx.Y, it.Text, it.Extra, null));
        placed.Add(new Dictionary<string, object?> { ["id"] = it.Id, ["anchor"] = new[] { it.X, it.Y }, ["text"] = new[] { fx.X, fx.Y }, ["fixed"] = true, ["free"] = null });
        continue;
      }
      if (!placer.InsideArea(it.X, it.Y, inset)) { outside.Add(it.Id); continue; }
      var p = placer.Place(it.X, it.Y, it.Text, hModel, it.Directions);
      var (txr, tyr) = (Math.Round(p.TextX, 4), Math.Round(p.TextY, 4));
      drawItems.Add(Item(it, it.X, it.Y, txr, tyr, it.Text, it.Extra, null));
      if (!p.Free) noFree.Add(it.Id);
      placed.Add(new Dictionary<string, object?>
      {
        ["id"] = it.Id, ["anchor"] = new[] { it.X, it.Y }, ["text"] = new[] { txr, tyr }, ["free"] = p.Free, ["score"] = Math.Round(p.Score, 3),
        ["attach"] = p.Attach, ["distance"] = Math.Round(p.Distance, 3), ["angleDeg"] = p.AngleDeg, ["candidates"] = p.Candidates,
      });
    }

    var warnings = new List<string>(scene.Notes);
    var replace = eraseStale;
    if (eraseStale && drawItems.Count == 0 && scene.Old.Count > 0)
    {
      replace = false;
      warnings.Add($"no flag was written but {scene.Old.Count} exist under '{idPrefix}': nothing erased (as the Python flag scripts did)");
    }
    var answer = new Dictionary<string, object?>
    {
      ["idPrefix"] = idPrefix, ["dryRun"] = dryRun, ["layout"] = layoutName, ["viewport"] = scene.ViewportHandle,
      ["area"] = scene.Area.Select(q => new[] { Math.Round(q.X, 3), Math.Round(q.Y, 3) }).ToList(), ["twist"] = Math.Round(-scene.Rotation, 6), ["textRotation"] = Math.Round(scene.Rotation, 6),
      ["scale"] = scene.Scale, ["textHeight"] = Math.Round(hModel, 4), ["placed"] = placed, ["manual"] = manualOut, ["noFreeSpot"] = noFree, ["skippedOutside"] = outside,
      ["obstacles"] = scene.Counts, ["warnings"] = warnings,
      ["counts"] = new Dictionary<string, int> { ["placed"] = placed.Count, ["manual"] = manualOut.Count, ["noFreeSpot"] = noFree.Count, ["skippedOutside"] = outside.Count },
    };
    if (dryRun)
    {
      var stale = scene.Old.Where(o => items.All(i => i.Id != o.Id)).Select(o => o.Id).ToList();
      answer["wouldErase"] = replace ? stale : new List<string>();
      return answer;
    }

    var drawn = await DrawEntitiesCommands.DrawEntitiesAsync(new JsonObject
    {
      ["idPrefix"] = idPrefix, ["items"] = drawItems, ["replace"] = replace, ["keepManual"] = keepManual, ["tolerance"] = tolerance,
    }, cancellationToken) as Dictionary<string, object?>;
    var handles = drawn?.GetValueOrDefault("handles") as Dictionary<string, string> ?? new Dictionary<string, string>();

    // hand-moved flags whose text drawEntities left as it was: the new text in place, geometry untouched
    var textUpdated = new List<string>();
    if (manualText == "update")
    {
      var keptByDraw = (drawn?.GetValueOrDefault("manual") as IEnumerable<string>)?.ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>();
      var todo = items.Where(it => keptByDraw.Contains(idPrefix + it.Id) && handles.ContainsKey(idPrefix + it.Id)).ToList();
      if (todo.Count > 0)
        await CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
        {
          foreach (var it in todo)
            if (database.TryGetObjectId(new Handle(Convert.ToInt64(handles[idPrefix + it.Id], 16)), out var oid) && transaction.GetObject(oid, OpenMode.ForWrite) is MLeader ml
                && ml.ContentType == ContentType.MTextContent)
            {
              var mt = ml.MText;
              var want = it.Text.Replace("\r\n", "\\P").Replace("\n", "\\P");
              if (mt == null || mt.Contents == want) { mt?.Dispose(); continue; }
              var loc = ml.TextLocation;
              mt.Contents = want;
              ml.MText = mt;
              ml.TextLocation = loc;
              mt.Dispose();
              textUpdated.Add(it.Id);
            }
          return null;
        });
    }
    if (drawOrderTop && handles.Count > 0)
      try
      {
        await CommandDispatcher.DispatchAsync("setDrawOrder", new JsonObject { ["handles"] = new JsonArray(handles.Values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()), ["position"] = "top" }, cancellationToken);
      }
      catch (JsonRpcDispatchException ex) { warnings.Add("draw order: " + ex.Message); }
    answer["manualTextUpdated"] = textUpdated;
    answer["drawn"] = drawn == null ? null : new Dictionary<string, object?>
    {
      ["counts"] = drawn.GetValueOrDefault("counts"), ["created"] = drawn.GetValueOrDefault("created"), ["updated"] = drawn.GetValueOrDefault("updated"),
      ["unchanged"] = drawn.GetValueOrDefault("unchanged"), ["erased"] = drawn.GetValueOrDefault("erased"), ["manual"] = drawn.GetValueOrDefault("manual"),
      ["erasedManual"] = drawn.GetValueOrDefault("erasedManual"), ["skipped"] = drawn.GetValueOrDefault("skipped"), ["handles"] = handles,
    };
    return answer;
  }
}
