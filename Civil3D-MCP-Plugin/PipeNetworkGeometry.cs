using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil;
using Autodesk.Civil.DatabaseServices;
using Autodesk.Civil.DatabaseServices.Styles;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;

namespace Civil3DMcpPlugin;

/// <summary>Geometry and parameter helpers shared by the network commands (C190-C204, gathered in the architecture pass
/// A2): pipe ends to structure edges, surface elevations, plan crossings, pipes at a structure, cover along a pipe.</summary>
public static partial class PipeNetworkCommands
{
  private const double TrimMargin = 0.005;

  /// <summary>C206: families without a structure body (break points and fittings in the line). Pipes are not trimmed to
  /// them (a pipe trimmed away from a valve is disconnected by Civil, live 2026-11-24) and networkCrossings does not
  /// treat them as structures a pipe can pass through.</summary>
  internal static readonly HashSet<string> InlineFamilies = new(StringComparer.OrdinalIgnoreCase)
    { "Brytpunkt", "Null Structure", "Ventil", "Luftning", "Tömning", "Propp", "Spolpost", "Brandpost" };

  /// <summary>C190 core: pulls pipe ends connected to a structure back to the structure's edge in plan, elevation along
  /// the pipe's grade; computed from the structure centre so reruns give the same result.</summary>
  private static List<Dictionary<string, object?>> TrimPipeEnds(Dictionary<ObjectId, Structure> structures, IEnumerable<Pipe> pipes,
    Func<Structure, double> radius, bool dryRun)
  {
    var result = new List<Dictionary<string, object?>>();
    foreach (var pipe in pipes)
    {
      Point3d s0 = pipe.StartPoint, e0 = pipe.EndPoint;
      structures.TryGetValue(pipe.StartStructureId, out var ss);
      structures.TryGetValue(pipe.EndStructureId, out var es);
      // 5 mm inside the edge: an end exactly on the structure boundary is disconnected by Civil (live 2026-11-24:
      // Nedstigningsbrunn 1000 trimmed to 0.500 lost the pipe, 0.495 kept it)
      double rs = ss == null ? 0 : Math.Max(0, radius(ss) - TrimMargin), re = es == null ? 0 : Math.Max(0, radius(es) - TrimMargin);
      var a = ss != null ? new Point2d(ss.Location.X, ss.Location.Y) : new Point2d(s0.X, s0.Y);
      var b = es != null ? new Point2d(es.Location.X, es.Location.Y) : new Point2d(e0.X, e0.Y);
      var L = a.GetDistanceTo(b);
      if (L < 1e-6 || L - rs - re < 0.05)
      {
        result.Add(new Dictionary<string, object?> { ["name"] = pipe.Name, ["skipped"] = "too short for the structure radii" });
        continue;
      }
      var u = (b - a) / L;
      var sXY = rs > 0 ? a + u * rs : new Point2d(s0.X, s0.Y);
      var eXY = re > 0 ? b - u * re : new Point2d(e0.X, e0.Y);
      var d2 = new Vector2d(e0.X - s0.X, e0.Y - s0.Y);
      double Z(Point2d q) => d2.Length < 1e-9 ? s0.Z
        : s0.Z + (e0.Z - s0.Z) * (new Vector2d(q.X - s0.X, q.Y - s0.Y).DotProduct(d2) / d2.LengthSqrd);
      var s1 = new Point3d(sXY.X, sXY.Y, Z(sXY));
      var e1 = new Point3d(eXY.X, eXY.Y, Z(eXY));
      var moved = Math.Max(s1.DistanceTo(s0), e1.DistanceTo(e0));
      if (!dryRun && moved > 1e-6)
      {
        if (!pipe.IsWriteEnabled) pipe.UpgradeOpen();
        pipe.StartPoint = s1;
        pipe.EndPoint = e1;
      }
      result.Add(new Dictionary<string, object?>
      {
        ["name"] = pipe.Name,
        ["startStructure"] = ss?.Name, ["startRadius"] = Math.Round(rs, 3),
        ["endStructure"] = es?.Name, ["endRadius"] = Math.Round(re, 3),
        ["length2d"] = new[] { Math.Round(new Point2d(s0.X, s0.Y).GetDistanceTo(new Point2d(e0.X, e0.Y)), 3), Math.Round(sXY.GetDistanceTo(eXY), 3) },
        ["moved"] = Math.Round(moved, 4),
      });
    }
    return result;
  }

  private static Dictionary<string, double> RadiusMap(JsonObject? parameters, string key)
  {
    var d = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
    if (PluginRuntime.GetParameter(parameters, key) is JsonObject o)
      foreach (var kv in o)
      {
        var v = kv.Value?.GetValue<double>();
        if (v == null || v < 0)
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{key}.{kv.Key} must be a radius >= 0 in metres. Nothing was changed.");
        d[kv.Key] = v.Value;
      }
    return d;
  }

  private static Func<Structure, double> RadiusRule(Dictionary<string, double> byName, Dictionary<string, double> byFamily) => st =>
    byName.TryGetValue(st.Name ?? "", out var r) ? r
    : byFamily.TryGetValue(st.PartFamilyName ?? "", out r) ? r
    : st.InnerDiameterOrWidth / 2;
  private static double? SurfaceZ(CivilSurface? surface, double x, double y)
  {
    if (surface == null) return null;
    try { return surface.FindElevationAtXY(x, y); }
    catch (Autodesk.AutoCAD.Runtime.Exception) { return null; }
  }

  private static CivilSurface? NetworkSurface(Network network, Transaction transaction)
  {
    var id = ReferenceSurfaceOrNull(network);
    return id.IsNull ? null : transaction.GetObject(id, OpenMode.ForRead) as CivilSurface;
  }

  private static (double X, double Y) ReadXY(JsonNode? node, string what)
  {
    if (node is JsonObject o && o["x"] != null && o["y"] != null) return (o["x"]!.GetValue<double>(), o["y"]!.GetValue<double>());
    if (node is JsonArray a && a.Count >= 2) return (a[0]!.GetValue<double>(), a[1]!.GetValue<double>());
    throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{what} must be {{x, y}} or [x, y]. Nothing was changed.");
  }

  /// <summary>Plan crossings of segment a->b (inverts za, zb, outer diameter od) with the pipes of the given networks:
  /// vertical clearance between the outer walls.</summary>
  private static List<Dictionary<string, object?>> Crossings(Transaction transaction, IEnumerable<Network> networks, Point2d a, Point2d b,
    double invA, double invB, double inner, double wall, ICollection<ObjectId> skip)
  {
    var res = new List<Dictionary<string, object?>>();
    var seg = new LineSegment2d(a, b);
    foreach (var net in networks)
      foreach (ObjectId id in net.GetPipeIds())
      {
        if (skip.Contains(id)) continue;
        var p = CivilObjectUtils.GetRequiredObject<Pipe>(transaction, id, OpenMode.ForRead);
        var o = new LineSegment2d(new Point2d(p.StartPoint.X, p.StartPoint.Y), new Point2d(p.EndPoint.X, p.EndPoint.Y));
        var hits = seg.IntersectWith(o);
        if (hits == null || hits.Length == 0) continue;
        var x = hits[0];
        var t = a.GetDistanceTo(x) / seg.Length;
        var to = o.Length < 1e-9 ? 0 : o.StartPoint.GetDistanceTo(x) / o.Length;
        var zc = p.StartPoint.Z + (p.EndPoint.Z - p.StartPoint.Z) * to;
        var own = invA + (invB - invA) * t;
        var over = own + inner / 2 > zc;
        // between the outer walls: own outer bottom = invert - wall, own outer top = invert + inner + wall
        var clear = over ? (own - wall) - (zc + p.OuterDiameterOrWidth / 2) : (zc - p.OuterDiameterOrWidth / 2) - (own + inner + wall);
        res.Add(new Dictionary<string, object?>
        {
          ["network"] = net.Name, ["pipe"] = p.Name, ["over"] = over, ["clearance"] = Math.Round(clear, 3),
          ["x"] = Math.Round(x.X, 3), ["y"] = Math.Round(x.Y, 3),
        });
      }
    return res;
  }

  private static double GetOuter(Structure st)
  {
    try { return st.DiameterOrWidth; } catch { return st.InnerDiameterOrWidth; }
  }

  private static List<Pipe> PipesAt(Network network, Transaction transaction, ObjectId structureId, OpenMode mode) =>
    network.GetPipeIds().Cast<ObjectId>().Select(id => CivilObjectUtils.GetRequiredObject<Pipe>(transaction, id, mode))
      .Where(p => p.StartStructureId == structureId || p.EndStructureId == structureId).ToList();
  private static Point2d XY(Point3d p) => new(p.X, p.Y);

  /// <summary>Elevation of a pipe end placed on the line between two structure centres at a given invert at each centre:
  /// centreline z at the end's projection.</summary>
  private static double ZOnGrade(Point2d a, double invA, Point2d b, double invB, Point3d end, double inner)
  {
    var v = b - a;
    var t = v.LengthSqrd < 1e-12 ? 0 : (XY(end) - a).DotProduct(v) / v.LengthSqrd;
    return invA + (invB - invA) * t + inner / 2;
  }

  /// <summary>C226: a pipe's invert at the projection of a point (a structure centre) on its line, extrapolated along its
  /// grade: the invert a locked pipe gives the structure.</summary>
  private static double InvertAtCentre(Pipe pipe, Point2d c)
  {
    var a = XY(pipe.StartPoint);
    var v = XY(pipe.EndPoint) - a;
    var t = v.LengthSqrd < 1e-12 ? 0 : (c - a).DotProduct(v) / v.LengthSqrd;
    return pipe.StartPoint.Z + (pipe.EndPoint.Z - pipe.StartPoint.Z) * t - pipe.InnerDiameterOrWidth / 2;
  }

  internal static (double Min, double AtStation, double ShareBelow, int Samples) CoverAlong(Pipe pipe, CivilSurface surface, double step, double? minCover)
  {
    var a = pipe.StartPoint; var b = pipe.EndPoint;
    var L = XY(a).GetDistanceTo(XY(b));
    var n = Math.Max(1, (int)Math.Ceiling(L / Math.Max(step, 0.05)));
    double min = double.MaxValue, at = 0; int below = 0, samples = 0;
    for (var i = 0; i <= n; i++)
    {
      var t = (double)i / n;
      var x = a.X + (b.X - a.X) * t; var y = a.Y + (b.Y - a.Y) * t;
      var crown = a.Z + (b.Z - a.Z) * t + pipe.OuterDiameterOrWidth / 2;
      double g;
      try { g = surface.FindElevationAtXY(x, y); } catch (Autodesk.AutoCAD.Runtime.Exception) { continue; }
      var c = g - crown;
      samples++;
      if (c < min) { min = c; at = L * t; }
      if (minCover != null && c < minCover.Value) below++;
    }
    return (samples == 0 ? double.NaN : min, at, samples == 0 ? 0 : (double)below / samples, samples);
  }
}
