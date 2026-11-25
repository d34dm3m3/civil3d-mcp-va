using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.DatabaseServices;
using AcEntity = Autodesk.AutoCAD.DatabaseServices.Entity;

namespace Civil3DMcpPlugin;

/// <summary>C234 checkNetworkAgainstEntities (F7, 2026-11-24): the free plan distance from network parts to other
/// owners' lines and objects (cables, gas, trees, poles, buildings) on given layers or blocks, also inside loaded xrefs
/// and in another drawing read as a side database (as listEntities sourcePath, C168). Use case: a Python
/// script measured these distances from dumped base map geometry (575 lines, used in every project). The rule values stay with the caller (required per group); the plugin measures.</summary>
public static partial class PipeNetworkCommands
{
  private sealed record ClearanceGroup(string Name, List<string> Layers, List<string> Blocks, double Radius, double? Required);

  /// <summary>A piece of another owner's geometry: a segment (or a point when A == B) with where it came from.</summary>
  private sealed record Piece(int Group, Point2d A, Point2d B, string Handle, string Layer, string Type, string? Block, string Source);

  private static bool PatternMatch(string pattern, string name)
  {
    if (NetworkNameMatch(pattern, name)) return true;
    var bar = name.LastIndexOf('|');
    return bar >= 0 && NetworkNameMatch(pattern, name[(bar + 1)..]);   // xref layers and blocks: "xref|name"
  }

  /// <summary>Closest points of two plan segments (either may be a point): distance, the points, whether they cross.</summary>
  private static (double D, Point2d P, Point2d Q, bool Crosses) SegSeg(Point2d a, Point2d b, Point2d c, Point2d d)
  {
    var ab = b - a; var cd = d - c;
    if (ab.Length > 1e-9 && cd.Length > 1e-9)
    {
      var hits = new LineSegment2d(a, b).IntersectWith(new LineSegment2d(c, d));
      if (hits is { Length: > 0 }) return (0, hits[0], hits[0], true);
    }
    var best = (D: double.MaxValue, P: a, Q: c);
    void Try(Point2d p, Point2d s0, Point2d s1, bool flip)
    {
      var q = Lerp(s0, s1, Project(p, s0, s1));
      var dd = p.GetDistanceTo(q);
      if (dd < best.D) best = flip ? (dd, q, p) : (dd, p, q);
    }
    Try(a, c, d, false); Try(b, c, d, false); Try(c, a, b, true); Try(d, a, b, true);
    return (best.D, best.P, best.Q, false);
  }

  /// <summary>C234: {networks? (default all), groups: [{name, layers?: [names, * wildcard; an xref layer "x|name" also
  /// matches "name"], blockNames?: [names], radius? (m added to the object, e.g. a trunk or pole; default 0), required?
  /// (m: a smaller free distance fails)}], includeXrefs=true (the contents of loaded xrefs in model space), sourcePath?:
  /// string | [string] (other drawings read as side databases, same coordinates), maxDistance=5.0, alignment?, limit=500,
  /// arcStep=0.25 (m between sample points on arcs and splines)}. Lines, polylines (arcs sampled), arcs, circles,
  /// splines, points and block inserts (a point at the insertion) on the group's layers, and inserts of its blocks.
  /// Per network part and group the nearest object within maxDistance: free plan distance (centre distance minus the
  /// pipe's outer radius, the structure's outer radius and the group radius; 0 or less = touches), crosses (a line
  /// crosses the pipe), the nearest points, the object (handle, layer, type, block, source), station/offset, required
  /// and ok. Reads only.</summary>
  public static Task<object?> CheckNetworkAgainstEntitiesAsync(JsonObject? parameters)
  {
    var networks = OptionalStringList(parameters, "networks");
    if (PluginRuntime.GetParameter(parameters, "groups") is not JsonArray groupList || groupList.Count == 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "groups must list at least one {name, layers?, blockNames?, radius?, required?}.");
    List<string> Strings(JsonNode? n) => (n as JsonArray)?.Select(x => x is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).OfType<string>()
      .Where(s => !string.IsNullOrWhiteSpace(s)).ToList() ?? new List<string>();
    var groups = groupList.Select((n, i) =>
    {
      if (n is not JsonObject o)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"groups[{i}] must be {{name, layers?, blockNames?, radius?, required?}}.");
      var g = new ClearanceGroup(o["name"]?.GetValue<string>() ?? $"group{i + 1}", Strings(o["layers"]), Strings(o["blockNames"]),
        o["radius"]?.GetValue<double>() ?? 0, o["required"]?.GetValue<double>());
      if (g.Layers.Count == 0 && g.Blocks.Count == 0)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"groups[{i}] ({g.Name}) needs layers or blockNames.");
      return g;
    }).ToList();
    var includeXrefs = PluginRuntime.GetOptionalBool(parameters, "includeXrefs") ?? true;
    var sources = PluginRuntime.GetParameter(parameters, "sourcePath") switch
    {
      JsonArray a => Strings(a),
      JsonValue v when v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) => new List<string> { s },
      _ => new List<string>(),
    };
    var sourcePaths = sources.Select(s => FileBoundary.ResolveLocalReadPath(s, ".dwg")).ToList();
    var maxDistance = PluginRuntime.GetOptionalDouble(parameters, "maxDistance") ?? 5.0;
    var alignmentName = PluginRuntime.GetOptionalString(parameters, "alignment");
    var limit = Math.Clamp(PluginRuntime.GetOptionalInt(parameters, "limit") ?? 500, 1, 20000);
    var arcStep = Math.Max(PluginRuntime.GetOptionalDouble(parameters, "arcStep") ?? 0.25, 0.01);

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var names = networks is { Count: > 0 } ? networks : ListPipeNetworkNames(civilDoc, transaction).Where(n => !string.IsNullOrEmpty(n)).ToList();
      var index = new PartIndex(names.Select(n => FindPipeNetworkByName(civilDoc, transaction, n, OpenMode.ForRead)), transaction);
      Alignment? al = string.IsNullOrWhiteSpace(alignmentName) ? null : CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName!);
      // the parts: pipes as segments, structures (with a body) as points with their outer radius
      var parts = new List<(string Network, string Name, string Kind, Point2d A, Point2d B, double R)>();
      foreach (var (n, p) in index.Pipes) parts.Add((n, p.Name, "pipe", XY(p.StartPoint), XY(p.EndPoint), p.OuterDiameterOrWidth / 2));
      foreach (var (n, s) in index.Structures)
        if (!InlineFamilies.Contains(s.PartFamilyName ?? "")) parts.Add((n, s.Name, "structure", XY(s.Location), XY(s.Location), GetOuter(s) / 2));
      if (parts.Count == 0) throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", "The networks have no parts.");
      var maxR = parts.Max(x => x.R) + groups.Max(g => g.Radius);
      double minX = parts.Min(x => Math.Min(x.A.X, x.B.X)) - maxDistance - maxR, maxX = parts.Max(x => Math.Max(x.A.X, x.B.X)) + maxDistance + maxR;
      double minY = parts.Min(x => Math.Min(x.A.Y, x.B.Y)) - maxDistance - maxR, maxY = parts.Max(x => Math.Max(x.A.Y, x.B.Y)) + maxDistance + maxR;
      bool Near(Point2d a, Point2d b) => !(Math.Max(a.X, b.X) < minX || Math.Min(a.X, b.X) > maxX || Math.Max(a.Y, b.Y) < minY || Math.Min(a.Y, b.Y) > maxY);

      var pieces = new List<Piece>();
      var scanned = new Dictionary<string, int>();
      void Collect(Database db, Transaction tr, BlockTableRecord space, Matrix3d xf, string source, int depth)
      {
        foreach (ObjectId oid in space)
        {
          if (tr.GetObject(oid, OpenMode.ForRead) is not AcEntity e) continue;
          scanned[source] = scanned.GetValueOrDefault(source) + 1;
          if (e is BlockReference br)
          {
            var btr = (BlockTableRecord)tr.GetObject(br.BlockTableRecord, OpenMode.ForRead);
            if (btr.IsFromExternalReference)
            {
              if (includeXrefs && depth < 4 && btr.IsResolved) Collect(db, tr, btr, xf * br.BlockTransform, "xref:" + btr.Name, depth + 1);
              continue;
            }
            var blockName = btr.Name;
            try { if (br.IsDynamicBlock) blockName = ((BlockTableRecord)tr.GetObject(br.DynamicBlockTableRecord, OpenMode.ForRead)).Name; } catch (Autodesk.AutoCAD.Runtime.Exception) { }
            var at = XY(br.Position.TransformBy(xf));
            for (var g = 0; g < groups.Count; g++)
              if (groups[g].Blocks.Any(b => PatternMatch(b, blockName)) || groups[g].Layers.Any(l => PatternMatch(l, e.Layer)))
                if (Near(at, at)) pieces.Add(new Piece(g, at, at, e.Handle.ToString(), e.Layer, "block", blockName, source));
            continue;
          }
          var gs = Enumerable.Range(0, groups.Count).Where(g => groups[g].Layers.Any(l => PatternMatch(l, e.Layer))).ToList();
          if (gs.Count == 0) continue;
          var pts = new List<List<Point2d>>();
          try
          {
            switch (e)
            {
              case DBPoint pt: pts.Add(new() { XY(pt.Position.TransformBy(xf)) }); break;
              case Line ln: pts.Add(new() { XY(ln.StartPoint.TransformBy(xf)), XY(ln.EndPoint.TransformBy(xf)) }); break;
              case Polyline pl:
              {
                var list = new List<Point2d>();
                var n = pl.Closed ? pl.NumberOfVertices : pl.NumberOfVertices - 1;
                for (var i = 0; i < n; i++)
                {
                  if (i == 0) list.Add(XY(pl.GetPoint3dAt(0).TransformBy(xf)));
                  var bulge = pl.GetBulgeAt(i);
                  var j = (i + 1) % pl.NumberOfVertices;
                  if (Math.Abs(bulge) > 1e-9)
                  {
                    var seg = pl.GetArcSegmentAt(i);
                    var k = Math.Clamp((int)Math.Ceiling(seg.Radius * Math.Abs(seg.EndAngle - seg.StartAngle) / arcStep), 2, 400);
                    for (var m = 1; m < k; m++) list.Add(XY(pl.GetPointAtParameter(i + (double)m / k).TransformBy(xf)));
                  }
                  list.Add(XY(pl.GetPoint3dAt(j).TransformBy(xf)));
                }
                if (pl.NumberOfVertices == 1) list.Add(XY(pl.GetPoint3dAt(0).TransformBy(xf)));
                pts.Add(list);
                break;
              }
              case Polyline2d or Polyline3d:
              {
                var c = (Curve)e;
                var list = new List<Point2d>();
                for (var t = c.StartParam; t <= c.EndParam + 1e-9; t += 1) list.Add(XY(c.GetPointAtParameter(Math.Min(t, c.EndParam)).TransformBy(xf)));
                pts.Add(list);
                break;
              }
              case Curve c:   // arcs, circles, ellipses, splines
              {
                var length = c.GetDistanceAtParameter(c.EndParam) - c.GetDistanceAtParameter(c.StartParam);
                var k = Math.Clamp((int)Math.Ceiling(length / arcStep), 8, 4000);
                var list = new List<Point2d>();
                for (var m = 0; m <= k; m++) list.Add(XY(c.GetPointAtDist(length * m / k).TransformBy(xf)));
                pts.Add(list);
                break;
              }
            }
          }
          catch (Autodesk.AutoCAD.Runtime.Exception) { continue; }
          var type = e.GetRXClass().DxfName;
          foreach (var list in pts)
            for (var i = 0; i < Math.Max(1, list.Count - 1); i++)
            {
              var a = list[i]; var b = list.Count > 1 ? list[i + 1] : list[i];
              if (!Near(a, b)) continue;
              foreach (var g in gs) pieces.Add(new Piece(g, a, b, e.Handle.ToString(), e.Layer, type, null, source));
            }
        }
      }
      Collect(database, transaction, (BlockTableRecord)transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(database), OpenMode.ForRead), Matrix3d.Identity, "drawing", 0);
      foreach (var path in sourcePaths)
      {
        using var side = new Database(false, true);
        side.ReadDwgFile(path, FileOpenMode.OpenForReadAndAllShare, true, "");
        side.CloseInput(true);
        using var t = side.TransactionManager.StartTransaction();
        Collect(side, t, (BlockTableRecord)t.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(side), OpenMode.ForRead), Matrix3d.Identity, "file:" + System.IO.Path.GetFileName(path), 0);
        t.Commit();
      }

      (double? St, double? Off) Station(Point2d q)
      {
        if (al == null) return (null, null);
        try { double st = 0, off = 0; al.StationOffset(q.X, q.Y, ref st, ref off); return (Math.Round(st, 2), Math.Round(off, 2)); }
        catch (Autodesk.AutoCAD.Runtime.Exception) { return (null, null); }
      }
      var items = new List<Dictionary<string, object?>>();
      var byGroup = pieces.GroupBy(pc => pc.Group).ToDictionary(g => g.Key, g => g.ToList());
      foreach (var part in parts)
        for (var g = 0; g < groups.Count; g++)
        {
          if (!byGroup.TryGetValue(g, out var list)) continue;
          var best = (Free: double.MaxValue, Hit: (D: 0.0, P: part.A, Q: part.A, Crosses: false), Piece: (Piece?)null);
          foreach (var pc in list)
          {
            var hit = SegSeg(part.A, part.B, pc.A, pc.B);
            var free = hit.D - part.R - groups[g].Radius;
            if (hit.Crosses) free = Math.Min(free, -part.R);
            if (free < best.Free) best = (free, hit, pc);
          }
          if (best.Piece == null || best.Free > maxDistance) continue;
          var (st, off) = Station(best.Hit.P);
          var req = groups[g].Required;
          items.Add(new Dictionary<string, object?>
          {
            ["group"] = groups[g].Name, ["network"] = part.Network, ["part"] = part.Name, ["kind"] = part.Kind,
            ["distance"] = Math.Round(best.Free, 3), ["centreDistance"] = Math.Round(best.Hit.D, 3), ["crosses"] = best.Hit.Crosses,
            ["required"] = req, ["ok"] = req == null ? null : best.Free >= req.Value - 0.0005,
            ["partPoint"] = R3(best.Hit.P), ["entityPoint"] = R3(best.Hit.Q), ["station"] = st, ["offset"] = off,
            ["entity"] = new Dictionary<string, object?> { ["handle"] = best.Piece.Handle, ["layer"] = best.Piece.Layer, ["type"] = best.Piece.Type,
              ["block"] = best.Piece.Block, ["source"] = best.Piece.Source },
          });
        }
      var sorted = items.OrderBy(i => (double)i["distance"]! - ((double?)i["required"] ?? 0)).ToList();
      return new Dictionary<string, object?>
      {
        ["networks"] = names, ["maxDistance"] = maxDistance, ["count"] = sorted.Count,
        ["failed"] = sorted.Count(i => i["ok"] is false),
        ["groups"] = groups.Select((g, i) => new Dictionary<string, object?> { ["name"] = g.Name, ["objects"] = byGroup.TryGetValue(i, out var l) ? l.Select(x => x.Handle + x.Source).Distinct().Count() : 0,
          ["pieces"] = byGroup.TryGetValue(i, out var l2) ? l2.Count : 0, ["within"] = sorted.Count(x => (string)x["group"]! == g.Name) }).ToList(),
        ["scanned"] = scanned, ["sources"] = sourcePaths,
        ["items"] = sorted.Take(limit).ToList(),
      };
    });
  }
}
