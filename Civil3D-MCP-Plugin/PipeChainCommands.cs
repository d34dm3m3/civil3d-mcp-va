using System.Globalization;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil;
using Autodesk.Civil.DatabaseServices;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;

namespace Civil3DMcpPlugin;

/// <summary>C237 tracePipeChain and C238 applyChainProfile (F8, 2026-11-25): a pressure line's elevation line as data.
/// Use case: project scripts did this in Python (about 900 lines in 4 scripts). Each script traced the chain of pipes by end coordinates, sampled the ground and the
/// crossings along it, solved its own optimisation (a linear programme with scipy) and then erased, re-added and
/// re-graded the pipes between the new break points. The optimisation stays in Python; the plugin
/// reads the chain (trace) and writes the chosen break points (apply): split at the points, set the heights, merge
/// what is straight again.</summary>
public static partial class PipeNetworkCommands
{
  /// <summary>Break point families a straight merge may remove (valves, air valves and other fittings stay).</summary>
  private static readonly HashSet<string> MergeFamilies = new(StringComparer.OrdinalIgnoreCase) { "Brytpunkt", "Null Structure" };

  private const double FreeEndTolerance = 0.02;

  /// <summary>One network as a graph: nodes are structures ("s:handle") or coincident free pipe ends ("f:n").</summary>
  private sealed class NetGraph
  {
    public Network Network = null!;
    public readonly List<Pipe> Pipes = new();
    public readonly Dictionary<ObjectId, Structure> Structures = new();
    public readonly Dictionary<ObjectId, (string A, string B)> Ends = new();
    public readonly Dictionary<string, List<Pipe>> At = new(StringComparer.Ordinal);
    public readonly Dictionary<string, Point2d> Point = new(StringComparer.Ordinal);
    public readonly Dictionary<string, Structure> NodeStructure = new(StringComparer.Ordinal);
  }

  /// <summary>One pipe of a chain, in chain direction.</summary>
  private sealed class ChainLink
  {
    public Pipe Pipe = null!;
    public bool Reversed;
    public string NodeA = "", NodeB = "";
    public double S0, S1;
    public Point3d A => Reversed ? Pipe.EndPoint : Pipe.StartPoint;
    public Point3d B => Reversed ? Pipe.StartPoint : Pipe.EndPoint;
    public bool AIsStart => !Reversed;
    public double Length2d => XY(A).GetDistanceTo(XY(B));
    public double Grade => Length2d < 1e-9 ? 0 : (B.Z - A.Z) / Length2d;
    public double CentreAt(double s) => S1 - S0 < 1e-9 ? A.Z : A.Z + (B.Z - A.Z) * Math.Clamp((s - S0) / (S1 - S0), 0, 1);
  }

  private sealed class ChainNode
  {
    public string Key = "";
    public double Station;
    public Point2d At;
    public Structure? Structure;
    public ChainLink? In, Out;
  }

  private sealed class PipeChain
  {
    public NetGraph Graph = null!;
    public readonly List<ChainLink> Links = new();
    public readonly List<ChainNode> Nodes = new();
    public readonly List<Point2d> Path = new();
    public readonly List<double> PathStations = new();
    public double Length;
    public readonly List<string> Notes = new();

    public Point2d PointAt(double s)
    {
      if (Path.Count == 1) return Path[0];
      for (var i = 1; i < Path.Count; i++)
        if (s <= PathStations[i] + 1e-9 || i == Path.Count - 1)
        {
          var d = PathStations[i] - PathStations[i - 1];
          var t = d < 1e-12 ? 0 : Math.Clamp((s - PathStations[i - 1]) / d, 0, 1);
          return Path[i - 1] + (Path[i] - Path[i - 1]) * t;
        }
      return Path[^1];
    }

    /// <summary>Station and plan offset (left positive) of a point projected on the chain path.</summary>
    public (double Station, double Offset) Project(Point2d q)
    {
      double best = double.MaxValue, st = 0, off = 0;
      for (var i = 1; i < Path.Count; i++)
      {
        var seg = new LineSegment2d(Path[i - 1], Path[i]);
        var c = seg.GetClosestPointTo(q).Point;
        var d = c.GetDistanceTo(q);
        if (d >= best) continue;
        best = d;
        st = PathStations[i - 1] + Path[i - 1].GetDistanceTo(c);
        var v = Path[i] - Path[i - 1];
        var w = q - Path[i - 1];
        off = Math.Sign(v.X * w.Y - v.Y * w.X) * d;
      }
      return (st, off);
    }

    /// <summary>The link whose pipe covers station s (null in a gap at a structure or outside).</summary>
    public ChainLink? LinkAt(double s) => Links.FirstOrDefault(l => s >= l.S0 - 1e-6 && s <= l.S1 + 1e-6);
  }

  private static NetGraph BuildGraph(Network network, Transaction transaction)
  {
    var g = new NetGraph { Network = network };
    foreach (ObjectId id in network.GetStructureIds())
    {
      var s = CivilObjectUtils.GetRequiredObject<Structure>(transaction, id, OpenMode.ForRead);
      g.Structures[id] = s;
    }
    var free = new List<(Point2d P, string Key)>();
    string Node(ObjectId sid, Point3d end)
    {
      if (!sid.IsNull && g.Structures.TryGetValue(sid, out var s))
      {
        var k = "s:" + s.Handle;
        g.Point[k] = XY(s.Location);
        g.NodeStructure[k] = s;
        return k;
      }
      var p = XY(end);
      foreach (var (q, key) in free)
        if (q.GetDistanceTo(p) <= FreeEndTolerance) return key;
      var nk = "f:" + free.Count.ToString(CultureInfo.InvariantCulture);
      free.Add((p, nk));
      g.Point[nk] = p;
      return nk;
    }
    foreach (ObjectId id in network.GetPipeIds())
    {
      var p = CivilObjectUtils.GetRequiredObject<Pipe>(transaction, id, OpenMode.ForRead);
      g.Pipes.Add(p);
      var a = Node(p.StartStructureId, p.StartPoint);
      var b = Node(p.EndStructureId, p.EndPoint);
      g.Ends[p.ObjectId] = (a, b);
      foreach (var k in new[] { a, b }.Distinct())
      {
        if (!g.At.TryGetValue(k, out var list)) g.At[k] = list = new List<Pipe>();
        list.Add(p);
      }
    }
    return g;
  }

  /// <summary>The chain asked for: {pipes} (any order, one unbranched run), {from, to} (structure or pipe names: the
  /// shortest run between them in plan length, a named pipe included as the first or last pipe), or {from} alone (a
  /// pipe or a structure with one pipe; followed through the straightest onward pipe at each node while its plan
  /// deflection is at most followMaxDeg).</summary>
  private static PipeChain ResolveChain(NetGraph g, string? from, string? to, List<string>? pipes, double followMaxDeg)
  {
    Pipe PipeNamed(string n) => g.Pipes.FirstOrDefault(p => string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase))
      ?? throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Network '{g.Network.Name}' has no pipe '{n}'. Nothing was changed.");
    string? StructureNode(string n)
    {
      var s = g.Structures.Values.FirstOrDefault(x => string.Equals(x.Name, n, StringComparison.OrdinalIgnoreCase));
      return s == null ? null : "s:" + s.Handle;
    }
    string Other(Pipe p, string node) => g.Ends[p.ObjectId].A == node ? g.Ends[p.ObjectId].B : g.Ends[p.ObjectId].A;
    var seq = new List<(Pipe Pipe, bool Rev)>();
    string start;

    if (pipes is { Count: > 0 })
    {
      var set = pipes.Select(PipeNamed).Distinct().ToList();
      var ids = set.Select(p => p.ObjectId).ToHashSet();
      var deg = new Dictionary<string, int>(StringComparer.Ordinal);
      foreach (var p in set)
        foreach (var k in new[] { g.Ends[p.ObjectId].A, g.Ends[p.ObjectId].B }) deg[k] = deg.GetValueOrDefault(k) + 1;
      var branch = deg.FirstOrDefault(kv => kv.Value > 2);
      if (branch.Key != null)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"The pipes branch at {NodeLabel(g, branch.Key)} ({branch.Value} of them meet there); give one unbranched run. Nothing was changed.");
      var ends = deg.Where(kv => kv.Value == 1).Select(kv => kv.Key).ToList();
      if (ends.Count == 0)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "The pipes form a loop; give a run with two ends. Nothing was changed.");
      if (ends.Count > 2)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"The pipes are not one connected run ({ends.Count / 2} pieces). Nothing was changed.");
      var fromNode = from == null ? null : StructureNode(from);
      var firstListed = set[0];
      start = fromNode != null && ends.Contains(fromNode) ? fromNode
        : ends.FirstOrDefault(e => g.Ends[firstListed.ObjectId].A == e || g.Ends[firstListed.ObjectId].B == e) ?? ends[0];
      var cur = start;
      var left = new HashSet<ObjectId>(ids);
      while (left.Count > 0)
      {
        var p = set.FirstOrDefault(x => left.Contains(x.ObjectId) && (g.Ends[x.ObjectId].A == cur || g.Ends[x.ObjectId].B == cur));
        if (p == null)
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "The pipes are not one connected run. Nothing was changed.");
        seq.Add((p, g.Ends[p.ObjectId].B == cur && g.Ends[p.ObjectId].A != cur));
        left.Remove(p.ObjectId);
        cur = Other(p, cur);
      }
      return BuildChain(g, seq, start);
    }

    if (string.IsNullOrWhiteSpace(from))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Give pipes, or from (and to): structure or pipe names. Nothing was changed.");
    var fromPipe = StructureNode(from!) == null ? PipeNamed(from!) : null;
    var fromNodes = fromPipe != null ? new[] { g.Ends[fromPipe.ObjectId].A, g.Ends[fromPipe.ObjectId].B } : new[] { StructureNode(from!)! };

    if (string.IsNullOrWhiteSpace(to))
    {
      // follow: from a pipe (from its free end, else its start) or a structure with one pipe
      Pipe first;
      if (fromPipe != null)
      {
        first = fromPipe;
        var (a, b) = g.Ends[first.ObjectId];
        start = g.At[a].Count == 1 ? a : g.At[b].Count == 1 ? b : a;
      }
      else
      {
        start = fromNodes[0];
        var at = g.At.GetValueOrDefault(start) ?? new List<Pipe>();
        if (at.Count != 1)
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Structure '{from}' has {at.Count} pipes; give to, or a pipe as from. Nothing was changed.");
        first = at[0];
      }
      var used = new HashSet<ObjectId>();
      var node = start;
      var pipe = first;
      while (pipe != null && used.Add(pipe.ObjectId))
      {
        var rev = g.Ends[pipe.ObjectId].A != node;
        seq.Add((pipe, rev));
        var next = Other(pipe, node);
        var dirIn = g.Point[next] - g.Point[node];
        Pipe? best = null; var bestDev = double.MaxValue;
        foreach (var q in g.At.GetValueOrDefault(next) ?? new List<Pipe>())
        {
          if (used.Contains(q.ObjectId)) continue;
          var dirOut = g.Point[Other(q, next)] - g.Point[next];
          var dev = dirIn.Length < 1e-9 || dirOut.Length < 1e-9 ? 0 : dirIn.GetAngleTo(dirOut) * 180 / Math.PI;
          if (dev < bestDev) { bestDev = dev; best = q; }
        }
        node = next;
        pipe = best != null && bestDev <= followMaxDeg ? best : null;
      }
      var chainF = BuildChain(g, seq, start);
      chainF.Notes.Add($"followed from {from} through the straightest onward pipe (at most {followMaxDeg} degrees)");
      return chainF;
    }

    var toPipe = StructureNode(to!) == null ? PipeNamed(to!) : null;
    var toNodes = toPipe != null ? new[] { g.Ends[toPipe.ObjectId].A, g.Ends[toPipe.ObjectId].B } : new[] { StructureNode(to!)! };
    List<(Pipe Pipe, bool Rev)>? bestPath = null; string? bestStart = null; var bestLen = double.MaxValue;
    foreach (var s in fromNodes)
    {
      var (dist, prev) = Dijkstra(g, s);
      foreach (var t in toNodes)
      {
        if (!dist.ContainsKey(t)) continue;
        var path = new List<(Pipe Pipe, bool Rev)>();
        for (var n = t; n != s;)
        {
          var (p, back) = prev[n];
          path.Insert(0, (p, g.Ends[p.ObjectId].A != back));
          n = back;
        }
        var startNode = s;
        if (fromPipe != null && (path.Count == 0 || path[0].Pipe.ObjectId != fromPipe.ObjectId))
        {
          if (path.Any(x => x.Pipe.ObjectId == fromPipe.ObjectId)) continue;
          startNode = Other(fromPipe, s);
          path.Insert(0, (fromPipe, g.Ends[fromPipe.ObjectId].A != startNode));
        }
        if (toPipe != null && (path.Count == 0 || path[^1].Pipe.ObjectId != toPipe.ObjectId))
        {
          if (path.Any(x => x.Pipe.ObjectId == toPipe.ObjectId)) continue;
          path.Add((toPipe, g.Ends[toPipe.ObjectId].A != t));
        }
        if (path.Count == 0) continue;
        var len = path.Sum(x => XY(x.Pipe.StartPoint).GetDistanceTo(XY(x.Pipe.EndPoint)));
        if (len < bestLen) { bestLen = len; bestPath = path; bestStart = startNode; }
      }
    }
    if (bestPath == null)
      throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"No run of pipes in '{g.Network.Name}' connects {from} and {to}. Nothing was changed.");
    return BuildChain(g, bestPath, bestStart!);
  }

  private static (Dictionary<string, double> Dist, Dictionary<string, (Pipe Pipe, string From)> Prev) Dijkstra(NetGraph g, string source)
  {
    var dist = new Dictionary<string, double>(StringComparer.Ordinal) { [source] = 0 };
    var prev = new Dictionary<string, (Pipe, string)>(StringComparer.Ordinal);
    var done = new HashSet<string>(StringComparer.Ordinal);
    while (true)
    {
      string? u = null; var du = double.MaxValue;
      foreach (var (k, d) in dist) if (!done.Contains(k) && d < du) { du = d; u = k; }
      if (u == null) break;
      done.Add(u);
      foreach (var p in g.At.GetValueOrDefault(u) ?? new List<Pipe>())
      {
        var (a, b) = g.Ends[p.ObjectId];
        var v = a == u ? b : a;
        var nd = du + XY(p.StartPoint).GetDistanceTo(XY(p.EndPoint));
        if (!dist.TryGetValue(v, out var old) || nd < old - 1e-9) { dist[v] = nd; prev[v] = (p, u); }
      }
    }
    return (dist, prev);
  }

  private static string NodeLabel(NetGraph g, string key) =>
    g.NodeStructure.TryGetValue(key, out var s) ? $"structure {s.Name}" : $"the free ends at {g.Point[key].X:F2}, {g.Point[key].Y:F2}";

  /// <summary>Stations along the chain: the path runs through structure centres where a pipe stops short of one
  /// (trimmed to a manhole's edge, C190), so the station is centre to centre as in Civil's network lengths.</summary>
  private static PipeChain BuildChain(NetGraph g, List<(Pipe Pipe, bool Rev)> seq, string start)
  {
    var c = new PipeChain { Graph = g };
    var s = 0.0;
    void Add(Point2d p)
    {
      if (c.Path.Count > 0)
      {
        var d = c.Path[^1].GetDistanceTo(p);
        if (d < 1e-4) return;
        s += d;
      }
      c.Path.Add(p);
      c.PathStations.Add(s);
    }
    ChainNode NodeAt(string key)
    {
      var n = new ChainNode { Key = key, At = g.Point[key], Structure = g.NodeStructure.GetValueOrDefault(key) };
      Add(g.Point[key]);
      n.Station = s;
      return n;
    }
    var first = true;
    foreach (var (pipe, rev) in seq)
    {
      var l = new ChainLink { Pipe = pipe, Reversed = rev, NodeA = rev ? g.Ends[pipe.ObjectId].B : g.Ends[pipe.ObjectId].A, NodeB = rev ? g.Ends[pipe.ObjectId].A : g.Ends[pipe.ObjectId].B };
      if (first)
      {
        c.Nodes.Add(NodeAt(l.NodeA));
        first = false;
      }
      Add(XY(l.A));
      l.S0 = s;
      Add(XY(l.B));
      l.S1 = s;
      var nb = NodeAt(l.NodeB);
      // a pipe end beyond the node centre (it runs past it): keep the stations monotonic
      if (nb.Station < l.S1) nb.Station = l.S1;
      nb.In = l;
      c.Nodes[^1].Out = l;
      c.Nodes.Add(nb);
      c.Links.Add(l);
    }
    c.Length = s;
    return c;
  }

  /// <summary>Angles at a chain node: plan deflection, vertical bend (change of grade angle) and the 3D deflection.</summary>
  private static (double Plan, double Vertical, double Bend3d) NodeAngles(ChainNode n)
  {
    if (n.In == null || n.Out == null) return (0, 0, 0);
    var i2 = XY(n.In.B) - XY(n.In.A); var o2 = XY(n.Out.B) - XY(n.Out.A);
    var plan = i2.Length < 1e-9 || o2.Length < 1e-9 ? 0 : i2.GetAngleTo(o2) * 180 / Math.PI;
    var vert = Math.Abs(Math.Atan(n.Out.Grade) - Math.Atan(n.In.Grade)) * 180 / Math.PI;
    var i3 = n.In.B - n.In.A; var o3 = n.Out.B - n.Out.A;
    var b3 = i3.Length < 1e-9 || o3.Length < 1e-9 ? 0 : i3.GetAngleTo(o3) * 180 / Math.PI;
    return (plan, vert, b3);
  }

  private static double R3(double v) => Math.Round(v, 3);

  /// <summary>The chain as data (shared by the trace and the after-report of apply).</summary>
  private static Dictionary<string, object?> DescribeChain(PipeChain c, Transaction transaction, CivilSurface? surface, double step, double? minCover,
    double maxBendDeg, List<Network> against, List<CrossingRule> rules, double minClear, bool samples)
  {
    var net = c.Graph.Network;
    var pipes = c.Links.Select(l => new Dictionary<string, object?>
    {
      ["name"] = l.Pipe.Name, ["reversed"] = l.Reversed, ["startStation"] = R3(l.S0), ["endStation"] = R3(l.S1), ["length2d"] = R3(l.Length2d),
      ["from"] = NodeName(c.Graph, l.NodeA), ["to"] = NodeName(c.Graph, l.NodeB),
      ["centreStart"] = R3(l.A.Z), ["centreEnd"] = R3(l.B.Z), ["invertStart"] = R3(l.A.Z - l.Pipe.InnerDiameterOrWidth / 2), ["invertEnd"] = R3(l.B.Z - l.Pipe.InnerDiameterOrWidth / 2),
      ["gradePermille"] = Math.Round(l.Grade * 1000, 2), ["innerDiameterMm"] = Math.Round(l.Pipe.InnerDiameterOrWidth * 1000, 1),
      ["outerDiameterMm"] = Math.Round(l.Pipe.OuterDiameterOrWidth * 1000, 1), ["partFamily"] = l.Pipe.PartFamilyName, ["partSize"] = l.Pipe.PartSizeName,
      ["locked"] = IsLocked(l.Pipe),
    }).ToList();
    var inChain = c.Links.Select(l => l.Pipe.ObjectId).ToHashSet();
    var nodes = new List<Dictionary<string, object?>>();
    var over = new List<Dictionary<string, object?>>();
    foreach (var n in c.Nodes)
    {
      var (plan, vert, b3) = NodeAngles(n);
      var branches = (c.Graph.At.GetValueOrDefault(n.Key) ?? new List<Pipe>()).Where(p => !inChain.Contains(p.ObjectId)).Select(p => p.Name).ToList();
      double? g = SurfaceZ(surface, n.At.X, n.At.Y);
      var d = new Dictionary<string, object?>
      {
        ["station"] = R3(n.Station), ["name"] = n.Structure?.Name, ["family"] = n.Structure?.PartFamilyName,
        ["kind"] = n.Structure == null ? "free" : InlineFamilies.Contains(n.Structure.PartFamilyName ?? "") ? "inline" : "structure",
        ["x"] = R3(n.At.X), ["y"] = R3(n.At.Y),
        ["centreIn"] = n.In == null ? null : R3(n.In.B.Z), ["centreOut"] = n.Out == null ? null : R3(n.Out.A.Z),
        ["rim"] = n.Structure == null ? null : R3(n.Structure.RimElevation), ["sump"] = n.Structure == null ? null : R3(n.Structure.SumpElevation),
        ["ground"] = g == null ? null : R3(g.Value),
        ["planDeflectionDeg"] = Math.Round(plan, 2), ["verticalBendDeg"] = Math.Round(vert, 2), ["bendDeg"] = Math.Round(b3, 2),
        ["branches"] = branches, ["locked"] = n.Structure != null && IsLocked(n.Structure),
      };
      nodes.Add(d);
      if (b3 > maxBendDeg + 1e-6)
        over.Add(new Dictionary<string, object?> { ["station"] = R3(n.Station), ["name"] = n.Structure?.Name, ["bendDeg"] = Math.Round(b3, 2),
          ["planDeflectionDeg"] = Math.Round(plan, 2), ["verticalBendDeg"] = Math.Round(vert, 2),
          ["tee"] = branches.Count > 0 });   // the chain turns at a branch: a tee, not a bend, when the fitting is one
    }

    // samples: ground, centre, cover along the chain
    var sampleList = new List<Dictionary<string, object?>>();
    double minC = double.MaxValue, minCAt = 0;
    var below = new List<(double From, double To, double Least)>();
    if (c.Length > 0)
    {
      var st = Math.Max(step, Math.Max(0.1, c.Length / 5000));
      var stations = new List<double>();
      for (var x = 0.0; x < c.Length - 1e-6; x += st) stations.Add(x);
      stations.Add(c.Length);
      stations.AddRange(c.Links.SelectMany(l => new[] { l.S0, l.S1 }));
      foreach (var s in stations.Distinct().OrderBy(x => x))
      {
        var q = c.PointAt(s);
        var l = c.LinkAt(s);
        double? g = SurfaceZ(surface, q.X, q.Y);
        double? centre = l?.CentreAt(s);
        double? crown = centre == null ? null : centre + l!.Pipe.OuterDiameterOrWidth / 2;
        double? cover = g == null || crown == null ? null : g - crown;
        if (cover is double cv)
        {
          if (cv < minC) { minC = cv; minCAt = s; }
          if (minCover is double mc && cv < mc - 1e-6)
          {
            if (below.Count > 0 && s - below[^1].To <= st + 1e-6) below[^1] = (below[^1].From, s, Math.Min(below[^1].Least, cv));
            else below.Add((s, s, cv));
          }
        }
        if (samples)
          sampleList.Add(new Dictionary<string, object?>
          {
            ["station"] = R3(s), ["x"] = R3(q.X), ["y"] = R3(q.Y), ["pipe"] = l?.Pipe.Name, ["centre"] = centre == null ? null : R3(centre.Value),
            ["invert"] = centre == null ? null : R3(centre.Value - l!.Pipe.InnerDiameterOrWidth / 2), ["crown"] = crown == null ? null : R3(crown.Value),
            ["ground"] = g == null ? null : R3(g.Value), ["cover"] = cover == null ? null : R3(cover.Value),
          });
      }
    }

    // crossings with the against networks along the chain
    var crossings = new List<Dictionary<string, object?>>();
    if (against.Count > 0)
    {
      var chainHandles = c.Links.Select(l => l.Pipe.Handle.ToString()).Concat(c.Nodes.Where(n => n.Structure != null).Select(n => n.Structure!.Handle.ToString()))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
      foreach (var on in against)
      {
        var oStructs = on.GetStructureIds().Cast<ObjectId>().Select(id => CivilObjectUtils.GetRequiredObject<Structure>(transaction, id, OpenMode.ForRead)).ToDictionary(x => x.ObjectId);
        foreach (ObjectId id in on.GetPipeIds())
        {
          var q = CivilObjectUtils.GetRequiredObject<Pipe>(transaction, id, OpenMode.ForRead);
          // a part linked to the chain (a service's connection point, C199) is a connection, not a crossing
          var linked = new[] { q.StartStructureId, q.EndStructureId }.Any(sid => !sid.IsNull && oStructs.TryGetValue(sid, out var os)
            && ReadLink(os) is { } lk && chainHandles.Contains(lk.HostHandle));
          if (linked) continue;
          var qs = new LineSegment2d(XY(q.StartPoint), XY(q.EndPoint));
          foreach (var l in c.Links)
          {
            var ls = new LineSegment2d(XY(l.A), XY(l.B));
            var hits = ls.IntersectWith(qs);
            if (hits == null || hits.Length == 0) continue;
            var x = hits[0];
            var s = l.S0 + XY(l.A).GetDistanceTo(x);
            var own = l.CentreAt(s);
            var tq = qs.Length < 1e-9 ? 0 : qs.StartPoint.GetDistanceTo(x) / qs.Length;
            var oz = q.StartPoint.Z + (q.EndPoint.Z - q.StartPoint.Z) * tq;
            var ownOver = own > oz;
            var clear = ownOver ? (own - l.Pipe.OuterDiameterOrWidth / 2) - (oz + q.OuterDiameterOrWidth / 2) : (oz - q.OuterDiameterOrWidth / 2) - (own + l.Pipe.OuterDiameterOrWidth / 2);
            var (up, low) = ownOver ? (net.Name, on.Name) : (on.Name, net.Name);
            var need = minClear;
            int? rule = null;
            for (var i = 0; i < rules.Count; i++)
            {
              var r = rules[i];
              if ((NetworkNameMatch(r.Upper, up) && NetworkNameMatch(r.Lower, low)) || (r.Either && NetworkNameMatch(r.Upper, low) && NetworkNameMatch(r.Lower, up))) { need = r.Clearance; rule = i; break; }
            }
            crossings.Add(new Dictionary<string, object?>
            {
              ["station"] = R3(s), ["x"] = R3(x.X), ["y"] = R3(x.Y), ["pipe"] = l.Pipe.Name, ["other"] = $"{on.Name}/{q.Name}", ["otherNetwork"] = on.Name,
              ["side"] = ownOver ? "over" : "under", ["centre"] = R3(own), ["otherCentre"] = R3(oz), ["otherOuterDiameterMm"] = Math.Round(q.OuterDiameterOrWidth * 1000, 1),
              ["clearance"] = R3(clear), ["required"] = need, ["rule"] = rule, ["ok"] = clear >= need - 0.0005,
            });
          }
        }
      }
      crossings = crossings.OrderBy(x => (double)x["station"]!).ToList();
    }

    var answer = new Dictionary<string, object?>
    {
      ["network"] = net.Name, ["surface"] = surface?.Name, ["length"] = R3(c.Length), ["pipeCount"] = c.Links.Count, ["nodeCount"] = c.Nodes.Count,
      ["start"] = NodeName(c.Graph, c.Nodes.First().Key), ["end"] = NodeName(c.Graph, c.Nodes.Last().Key),
      ["pipes"] = pipes, ["nodes"] = nodes,
      ["minCover"] = minC == double.MaxValue ? null : new Dictionary<string, object?> { ["cover"] = R3(minC), ["station"] = R3(minCAt) },
      ["coverBelow"] = minCover == null ? null : below.Select(b => new Dictionary<string, object?> { ["from"] = R3(b.From), ["to"] = R3(b.To), ["least"] = R3(b.Least) }).ToList(),
      ["maxBendDeg"] = maxBendDeg, ["bendsOverMax"] = over,
      ["crossings"] = against.Count == 0 ? null : crossings,
      ["crossingsUnderRequired"] = against.Count == 0 ? null : crossings.Count(x => !(bool)x["ok"]!),
      ["notes"] = c.Notes,
    };
    if (samples) answer["samples"] = sampleList;
    return answer;
  }

  private static string? NodeName(NetGraph g, string key) =>
    g.NodeStructure.TryGetValue(key, out var s) ? s.Name : null;

  /// <summary>The chain parameters: chain {from, to, pipes} (a tracePipeChain answer works: its pipes [{name}]), or the
  /// same keys at the top level.</summary>
  private static (string? From, string? To, List<string>? Pipes) ChainSpec(JsonObject? parameters)
  {
    var node = PluginRuntime.GetParameter(parameters, "chain") as JsonObject ?? parameters;
    string? Str(string k) => node?[k] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;
    List<string>? pipes = null;
    if (node?["pipes"] is JsonArray a)
      pipes = a.Select(x => x switch
      {
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonObject o when o["name"] is JsonValue nv && nv.TryGetValue<string>(out var s) => s,
        _ => throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "chain pipes must be names or {name} objects. Nothing was changed."),
      }).ToList();
    var from = Str("from"); var to = Str("to");
    if (node != parameters && from == null && to == null && pipes == null)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "chain needs pipes or from (and to). Nothing was changed.");
    return (from, to, pipes);
  }

  private static List<Network> AgainstNetworks(object civilDoc, Transaction transaction, List<string>? patterns, string own) =>
    patterns == null ? new List<Network>() :
    ListPipeNetworkNames(civilDoc, transaction).Where(n => !string.IsNullOrEmpty(n) && !string.Equals(n, own, StringComparison.OrdinalIgnoreCase)
      && patterns.Any(p => NetworkNameMatch(p, n))).Select(n => FindPipeNetworkByName(civilDoc, transaction, n, OpenMode.ForRead)).ToList();

  // ---------- C237 tracePipeChain ----------

  /// <summary>C237 (reads): {network, from?, to?, pipes?, chain?: {from, to, pipes}, followMaxDeg (60), surface? (default
  /// the network's reference surface), against?: [network patterns, * wildcard], rules?, minClearance (0.10), step (1.0),
  /// minCover?, maxBendDeg (45; profile: vatten.maxBojGrader), samples (true)}. Answer: the ordered chain with stations
  /// (centre to centre through structures), per pipe its stations, centreline and invert at both ends, grade and size;
  /// per node the station, structure, centre in and out, ground, plan deflection, vertical bend and 3D bend, branches;
  /// samples every step (ground, centre, invert, crown, cover); crossings with the against networks along the chain
  /// (side, clearance between the outer walls, required by the rules); the least cover, stretches under minCover and
  /// the bends over maxBendDeg.</summary>
  public static Task<object?> TracePipeChainAsync(JsonObject? parameters)
  {
    var networkName = PluginRuntime.GetOptionalString(parameters, "network") ?? PluginRuntime.GetRequiredString(parameters, "networkName");
    var (from, to, pipes) = ChainSpec(parameters);
    var followMax = PluginRuntime.GetOptionalDouble(parameters, "followMaxDeg") ?? 60;
    var surfaceName = PluginRuntime.GetOptionalString(parameters, "surface");
    var against = OptionalStringList(parameters, "against");
    var rules = ReadCrossingRules(PluginRuntime.GetParameter(parameters, "rules") as JsonArray, "rules");
    var minClear = PluginRuntime.GetOptionalDouble(parameters, "minClearance") ?? 0.10;
    var step = PluginRuntime.GetOptionalDouble(parameters, "step") ?? 1.0;
    var minCover = PluginRuntime.GetOptionalDouble(parameters, "minCover");
    var maxBend = PluginRuntime.GetOptionalDouble(parameters, "maxBendDeg") ?? 45;
    var samples = PluginRuntime.GetOptionalBool(parameters, "samples") ?? true;
    if (!(step > 0)) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "step must be > 0 (m).");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var network = FindPipeNetworkByName(civilDoc, transaction, networkName, OpenMode.ForRead);
      var chain = ResolveChain(BuildGraph(network, transaction), from, to, pipes, followMax);
      var surface = string.IsNullOrWhiteSpace(surfaceName) ? NetworkSurface(network, transaction)
        : CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, surfaceName!, OpenMode.ForRead);
      return DescribeChain(chain, transaction, surface, step, minCover, maxBend, AgainstNetworks(civilDoc, transaction, against, network.Name), rules, minClear, samples);
    });
  }

  // ---------- C238 applyChainProfile ----------

  private sealed record ProfilePoint(double Station, double Z, string From);

  /// <summary>C238: sets a chain's elevation line from given break points: {network, chain {from, to | pipes} (or the
  /// keys at the top level; a tracePipeChain answer works), points: [{station, z} | [station, z]] or vertices: [{x, y,
  /// z}] (projected on the chain; maxOffset 0.5), mode: "center" (z = centreline, default) | "invert", splitAt:
  /// "breakpoints" (default: a break point structure is inserted at every point that is not within nodeTolerance of
  /// a node) | "none", mergeStraight (true: break points with two pipes of the same part and size are removed where the
  /// line runs straight on, plan deflection at most mergePlanDeg 0.3 and grade change at most mergeGradePermille 0.5),
  /// keep?: [structure names never merged], partName ("Brytpunkt"), nameFormat ("BP {station}"), branches: "follow"
  /// (default: pipes of the network at an inline node and parts linked to the chain (C199) get the chain's centreline;
  /// mode center only) | "keep", nodeTolerance (0.05), maxBendDeg (45; profile vatten.maxBojGrader), surface?, minCover?,
  /// step (1.0), against?, rules?, minClearance?, dryRun? (made and aborted)}. Stations outside the first and last point
  /// keep their elevations. Locked pipes and structures (C226) are not split, set or merged and are listed. Answer:
  /// splits, set ends, merged nodes, locked, notSplit, and the chain after (tracePipeChain's answer without samples)
  /// with bendsOverMax: the bend rule is reported, not enforced.</summary>
  public static async Task<object?> ApplyChainProfileAsync(JsonObject? parameters, CancellationToken cancellationToken)
  {
    var networkName = PluginRuntime.GetOptionalString(parameters, "network") ?? PluginRuntime.GetRequiredString(parameters, "networkName");
    var (from, to, pipes) = ChainSpec(parameters);
    var followMax = PluginRuntime.GetOptionalDouble(parameters, "followMaxDeg") ?? 60;
    var mode = (PluginRuntime.GetOptionalString(parameters, "mode") ?? "center").Trim().ToLowerInvariant();
    if (mode == "centre") mode = "center";
    if (mode is not ("center" or "invert"))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "mode must be center or invert. Nothing was changed.");
    var splitAt = (PluginRuntime.GetOptionalString(parameters, "splitAt") ?? "breakpoints").Trim().ToLowerInvariant();
    if (splitAt is not ("breakpoints" or "none"))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "splitAt must be breakpoints or none. Nothing was changed.");
    var merge = PluginRuntime.GetOptionalBool(parameters, "mergeStraight") ?? true;
    var mergePlan = PluginRuntime.GetOptionalDouble(parameters, "mergePlanDeg") ?? 0.3;
    var mergeGrade = (PluginRuntime.GetOptionalDouble(parameters, "mergeGradePermille") ?? 0.5) / 1000;
    var keep = OptionalStringList(parameters, "keep")?.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var partName = PluginRuntime.GetOptionalString(parameters, "partName") ?? "Brytpunkt";
    var nameFormat = PluginRuntime.GetOptionalString(parameters, "nameFormat") ?? "BP {station}";
    var branches = (PluginRuntime.GetOptionalString(parameters, "branches") ?? "follow").Trim().ToLowerInvariant();
    if (branches is not ("follow" or "keep"))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "branches must be follow or keep. Nothing was changed.");
    var nodeTol = PluginRuntime.GetOptionalDouble(parameters, "nodeTolerance") ?? 0.05;
    var maxOffset = PluginRuntime.GetOptionalDouble(parameters, "maxOffset") ?? 0.5;
    var maxBend = PluginRuntime.GetOptionalDouble(parameters, "maxBendDeg") ?? 45;
    var surfaceName = PluginRuntime.GetOptionalString(parameters, "surface");
    var minCover = PluginRuntime.GetOptionalDouble(parameters, "minCover");
    var step = PluginRuntime.GetOptionalDouble(parameters, "step") ?? 1.0;
    var against = OptionalStringList(parameters, "against");
    var rules = ReadCrossingRules(PluginRuntime.GetParameter(parameters, "rules") as JsonArray, "rules");
    var minClear = PluginRuntime.GetOptionalDouble(parameters, "minClearance") ?? 0.10;
    var dryRun = PluginRuntime.GetOptionalBool(parameters, "dryRun") ?? false;
    if (!(nodeTol >= 0.001) || !(step > 0))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "nodeTolerance must be >= 0.001 m and step > 0. Nothing was changed.");

    var rawPoints = new List<(double? S, double? X, double? Y, double Z, int I)>();
    if (PluginRuntime.GetParameter(parameters, "points") is JsonArray pa)
      foreach (var (n, i) in pa.Select((n, i) => (n, i)))
      {
        if (n is JsonObject o && o["station"] != null && o["z"] != null) rawPoints.Add((o["station"]!.GetValue<double>(), null, null, o["z"]!.GetValue<double>(), i));
        else if (n is JsonArray a && a.Count >= 2) rawPoints.Add((a[0]!.GetValue<double>(), null, null, a[1]!.GetValue<double>(), i));
        else throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"points[{i}] must be {{station, z}} or [station, z]. Nothing was changed.");
      }
    if (PluginRuntime.GetParameter(parameters, "vertices") is JsonArray va)
    {
      if (rawPoints.Count > 0) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Give points or vertices, not both. Nothing was changed.");
      foreach (var (n, i) in va.Select((n, i) => (n, i)))
      {
        if (n is JsonObject o && o["x"] != null && o["y"] != null && o["z"] != null) rawPoints.Add((null, o["x"]!.GetValue<double>(), o["y"]!.GetValue<double>(), o["z"]!.GetValue<double>(), i));
        else if (n is JsonArray a && a.Count >= 3) rawPoints.Add((null, a[0]!.GetValue<double>(), a[1]!.GetValue<double>(), a[2]!.GetValue<double>(), i));
        else throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"vertices[{i}] must be {{x, y, z}} or [x, y, z]. Nothing was changed.");
      }
    }
    if (rawPoints.Count < 2)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "points (or vertices) must give at least two break points. Nothing was changed.");
    if (rawPoints.Any(p => !double.IsFinite(p.Z) || (p.S is double s && !double.IsFinite(s))))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Every break point needs a finite station and z. Nothing was changed.");

    PipeChain Chain(Autodesk.Civil.ApplicationServices.CivilDocument civilDoc, Transaction transaction, List<string>? names = null)
    {
      var network = FindPipeNetworkByName(civilDoc, transaction, networkName, OpenMode.ForRead);
      return ResolveChain(BuildGraph(network, transaction), names == null ? from : null, names == null ? to : null, names ?? pipes, followMax);
    }

    // C238 fix (F9): a dry run that lets branches follow also writes parts of other networks (linked services) in the
    // aborted transaction; their state is compared before and after, so a dry run that left anything behind is an error
    // instead of a silent change (live 2026-11-25 nothing stayed: the abort covers every network)
    var guard = dryRun && branches == "follow" && !CivilExecution.InBatch
      ? await CivilExecution.ReadAsync((doc, civilDoc, database, transaction) => OtherNetworksState(civilDoc, transaction, networkName)) : null;
    var result = await RunWriteOrDry(async () =>
    {
      // ---- plan: the chain, the profile, the splits
      var plan = await CivilExecution.ReadAsync((doc, civilDoc, database, transaction) =>
      {
        var c = Chain(civilDoc, transaction);
        var pts = new List<ProfilePoint>();
        foreach (var r in rawPoints)
        {
          if (r.S is double s)
          {
            if (s < -nodeTol || s > c.Length + nodeTol)
              throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"points[{r.I}] station {s:F3} is outside the chain (0-{c.Length:F3}). Nothing was changed.");
            pts.Add(new ProfilePoint(Math.Clamp(s, 0, c.Length), r.Z, $"points[{r.I}]"));
          }
          else
          {
            var (st, off) = c.Project(new Point2d(r.X!.Value, r.Y!.Value));
            if (Math.Abs(off) > maxOffset)
              throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"vertices[{r.I}] lies {Math.Abs(off):F3} m from the chain (maxOffset {maxOffset}). Nothing was changed.");
            pts.Add(new ProfilePoint(st, r.Z, $"vertices[{r.I}]"));
          }
        }
        pts = pts.OrderBy(p => p.Station).ToList();
        for (var i = 1; i < pts.Count; i++)
          if (pts[i].Station - pts[i - 1].Station < 1e-4)
            throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{pts[i - 1].From} and {pts[i].From} have the same station {pts[i].Station:F3}. Nothing was changed.");
        var names = c.Graph.Pipes.Select(p => p.Name).Concat(c.Graph.Structures.Values.Select(s => s.Name ?? "")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var splits = new List<(string Pipe, double Station, double X, double Y, string Name, string NewPipe, double DistFromStart)>();
        var notSplit = new List<Dictionary<string, object?>>();
        var lockedSkips = new List<Dictionary<string, object?>>();
        string Free(string stem) { var nm = stem; for (var i = 2; names.Contains(nm); i++) nm = $"{stem}-{i}"; names.Add(nm); return nm; }
        if (splitAt == "breakpoints")
          foreach (var p in pts)
          {
            var near = c.Nodes.OrderBy(n => Math.Abs(n.Station - p.Station)).First();
            if (Math.Abs(near.Station - p.Station) <= nodeTol) continue;   // at a node: the node takes the height
            var l = c.Links.FirstOrDefault(x => p.Station > x.S0 + nodeTol && p.Station < x.S1 - nodeTol);
            if (l == null) { notSplit.Add(new() { ["point"] = p.From, ["station"] = R3(p.Station), ["reason"] = "inside a structure (between pipe end and centre) or at a pipe end" }); continue; }
            if (IsLocked(l.Pipe)) { notSplit.Add(new() { ["point"] = p.From, ["station"] = R3(p.Station), ["reason"] = $"pipe {l.Pipe.Name} is locked" }); lockedSkips.Add(LockedEntry(l.Pipe, "split")); continue; }
            var t = (p.Station - l.S0) / (l.S1 - l.S0);
            if (t < 0.01 || t > 0.99) { notSplit.Add(new() { ["point"] = p.From, ["station"] = R3(p.Station), ["reason"] = $"too near an end of {l.Pipe.Name} (t {t:F3})" }); continue; }
            var q = c.PointAt(p.Station);
            var stTxt = p.Station.ToString("F2", CultureInfo.InvariantCulture);
            var nm = Free(nameFormat.Replace("{station}", stTxt).Replace("{pipe}", l.Pipe.Name).Replace("{network}", networkName));
            var dStart = l.AIsStart ? p.Station - l.S0 : l.S1 - p.Station;
            splits.Add((l.Pipe.Name, p.Station, q.X, q.Y, nm, "", dStart));
          }
        // per pipe from its own end backwards, so the original pipe (start to the split) takes the next split
        var ordered = splits.GroupBy(x => x.Pipe, StringComparer.OrdinalIgnoreCase).SelectMany(gr => gr.OrderByDescending(x => x.DistFromStart)).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
          var o = ordered[i];
          ordered[i] = (o.Pipe, o.Station, o.X, o.Y, o.Name, Free(o.Pipe + "b"), o.DistFromStart);
        }
        return (Points: pts, Splits: ordered, NotSplit: notSplit, Locked: lockedSkips, ChainPipes: c.Links.Select(l => l.Pipe.Name).ToList());
      });
      double? Profile(double s)
      {
        var p = plan.Points;
        if (s < p[0].Station - 1e-6 || s > p[^1].Station + 1e-6) return null;
        for (var i = 1; i < p.Count; i++)
          if (s <= p[i].Station + 1e-9)
            return p[i - 1].Z + (p[i].Z - p[i - 1].Z) * (s - p[i - 1].Station) / (p[i].Station - p[i - 1].Station);
        return p[^1].Z;
      }

      // ---- splits
      var splitDone = new List<Dictionary<string, object?>>();
      var chainNames = new List<string>(plan.ChainPipes);
      foreach (var sp in plan.Splits)
      {
        await SplitPipeAsync(new JsonObject
        {
          ["networkName"] = networkName, ["pipe"] = sp.Pipe, ["at"] = new JsonObject { ["x"] = sp.X, ["y"] = sp.Y },
          ["partName"] = partName, ["name"] = sp.Name, ["newPipeName"] = sp.NewPipe,
        });
        chainNames.Add(sp.NewPipe);
        splitDone.Add(new Dictionary<string, object?> { ["pipe"] = sp.Pipe, ["station"] = R3(sp.Station), ["structure"] = sp.Name, ["newPipe"] = sp.NewPipe });
      }

      // ---- heights
      var locked = new List<Dictionary<string, object?>>(plan.Locked);
      var set = await CivilExecution.WriteAsync((doc, civilDoc, database, transaction) =>
      {
        var c = Chain(civilDoc, transaction, chainNames);
        var items = new Dictionary<string, List<ZItem>>(StringComparer.OrdinalIgnoreCase);
        void Item(string net, ZItem it) { if (!items.TryGetValue(net, out var l)) items[net] = l = new List<ZItem>(); l.Add(it); }
        var ends = 0; var follow = new List<string>();
        var lockedNodes = c.Nodes.Where(n => n.Structure != null && IsLocked(n.Structure)).Select(n => n.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var l in c.Links)
        {
          if (IsLocked(l.Pipe)) { locked.Add(LockedEntry(l.Pipe, "heights")); continue; }
          foreach (var (s, isA) in new[] { (l.S0, true), (l.S1, false) })
          {
            var v = Profile(s);
            if (v == null) continue;
            if (lockedNodes.Contains(isA ? l.NodeA : l.NodeB)) continue;
            var z = mode == "invert" ? v.Value + l.Pipe.InnerDiameterOrWidth / 2 : v.Value;
            Item(networkName, new ZItem(null, l.Pipe.Name, isA ? l.AIsStart : !l.AIsStart, z, false));
            ends++;
          }
        }
        foreach (var k in lockedNodes) locked.Add(LockedEntry(c.Graph.NodeStructure[k], "heights at the node"));
        if (branches == "follow" && mode == "center")
        {
          var inChain = c.Links.Select(l => l.Pipe.ObjectId).ToHashSet();
          foreach (var n in c.Nodes)
          {
            if (n.Structure == null || !InlineFamilies.Contains(n.Structure.PartFamilyName ?? "") || lockedNodes.Contains(n.Key)) continue;
            var v = Profile(n.Station);
            if (v == null) continue;
            foreach (var p in (c.Graph.At.GetValueOrDefault(n.Key) ?? new List<Pipe>()).Where(p => !inChain.Contains(p.ObjectId)))
            {
              if (IsLocked(p)) { locked.Add(LockedEntry(p, "branch height")); continue; }
              Item(networkName, new ZItem(null, p.Name, p.StartStructureId == n.Structure.ObjectId, v.Value, false));
              follow.Add($"{networkName}/{p.Name}");
            }
          }
          // parts of other networks linked to the chain (C199): their pipes at the linked structure follow
          var hosts = c.Links.Select(l => l.Pipe.Handle.ToString()).Concat(c.Nodes.Where(n => n.Structure != null).Select(n => n.Structure!.Handle.ToString()))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
          foreach (var on in ListPipeNetworkNames(civilDoc, transaction).Where(x => !string.Equals(x, networkName, StringComparison.OrdinalIgnoreCase)))
          {
            var onet = FindPipeNetworkByName(civilDoc, transaction, on, OpenMode.ForRead);
            foreach (ObjectId sid in onet.GetStructureIds())
            {
              var s = CivilObjectUtils.GetRequiredObject<Structure>(transaction, sid, OpenMode.ForRead);
              if (ReadLink(s) is not { } lk || !hosts.Contains(lk.HostHandle)) continue;
              if (IsLocked(s)) { locked.Add(LockedEntry(s, "linked part height")); continue; }
              var (st, off) = c.Project(XY(s.Location));
              if (Math.Abs(off) > 0.05) continue;
              var v = Profile(st);
              if (v == null) continue;
              foreach (var p in PipesAt(onet, transaction, sid, OpenMode.ForRead))
              {
                if (IsLocked(p)) { locked.Add(LockedEntry(p, "linked part height")); continue; }
                Item(on, new ZItem(null, p.Name, p.StartStructureId == sid, v.Value, false));
                follow.Add($"{on}/{p.Name}");
              }
            }
          }
        }
        foreach (var (net, list) in items)
          ApplyZ(FindPipeNetworkByName(civilDoc, transaction, net, OpenMode.ForRead), transaction, list);
        return (Ends: ends, Follow: follow);
      });

      // ---- merge straight runs
      var merged = new List<Dictionary<string, object?>>();
      var kept = new List<Dictionary<string, object?>>();
      if (merge)
        await CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
        {
          for (var guard = 0; guard < 2000; guard++)
          {
            var c = Chain(civilDoc, transaction, chainNames);
            ChainNode? hit = null;
            foreach (var n in c.Nodes)
            {
              if (n.In == null || n.Out == null || n.Structure == null) continue;
              if (!MergeFamilies.Contains(n.Structure.PartFamilyName ?? "") || keep.Contains(n.Structure.Name ?? "")) continue;
              var at = c.Graph.At.GetValueOrDefault(n.Key) ?? new List<Pipe>();
              if (at.Count != 2) continue;
              var (planDev, _, _) = NodeAngles(n);
              if (planDev > mergePlan || Math.Abs(n.Out.Grade - n.In.Grade) > mergeGrade) continue;
              string? why = null;
              if (IsLocked(n.Structure) || IsLocked(n.In.Pipe) || IsLocked(n.Out.Pipe)) why = "locked";
              else if (!string.Equals(n.In.Pipe.PartFamilyName, n.Out.Pipe.PartFamilyName, StringComparison.OrdinalIgnoreCase)
                || Math.Abs(n.In.Pipe.InnerDiameterOrWidth - n.Out.Pipe.InnerDiameterOrWidth) > 0.0005) why = "part or size changes";
              if (why != null)
              {
                if (!kept.Any(k => (string?)k["name"] == n.Structure.Name))
                  kept.Add(new Dictionary<string, object?> { ["name"] = n.Structure.Name, ["station"] = R3(n.Station), ["reason"] = why });
                continue;
              }
              hit = n;
              break;
            }
            if (hit == null) break;
            merged.Add(new Dictionary<string, object?> { ["structure"] = hit.Structure!.Name, ["station"] = R3(hit.Station), ["kept"] = hit.In!.Pipe.Name, ["removed"] = hit.Out!.Pipe.Name });
            chainNames.RemoveAll(x => string.Equals(x, hit.Out.Pipe.Name, StringComparison.OrdinalIgnoreCase));
            MergeAt(database, transaction, networkName, hit, civilDoc);
          }
          return null;
        });

      // ---- after
      var after = await CivilExecution.ReadAsync((doc, civilDoc, database, transaction) =>
      {
        var c = Chain(civilDoc, transaction, chainNames);
        var network = c.Graph.Network;
        var surface = string.IsNullOrWhiteSpace(surfaceName) ? NetworkSurface(network, transaction)
          : CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, surfaceName!, OpenMode.ForRead);
        var d = DescribeChain(c, transaction, surface, step, minCover, maxBend, AgainstNetworks(civilDoc, transaction, against, network.Name), rules, minClear, false);
        // the profile against the result: largest difference at the given points
        var worst = 0.0; string? worstAt = null;
        foreach (var p in plan.Points)
        {
          var l = c.LinkAt(p.Station);
          if (l == null) continue;
          var z = l.CentreAt(p.Station) - (mode == "invert" ? l.Pipe.InnerDiameterOrWidth / 2 : 0);
          if (Math.Abs(z - p.Z) > Math.Abs(worst)) { worst = z - p.Z; worstAt = p.From; }
        }
        d["largestDifferenceAtPoints"] = new Dictionary<string, object?> { ["dz"] = R3(worst), ["point"] = worstAt };
        return d;
      });
      var bends = (List<Dictionary<string, object?>>)after["bendsOverMax"]!;
      var answer = new Dictionary<string, object?>
      {
        ["network"] = networkName, ["dryRun"] = dryRun, ["mode"] = mode, ["points"] = plan.Points.Count,
        ["splits"] = splitDone, ["notSplit"] = plan.NotSplit, ["endsSet"] = set.Ends, ["followed"] = set.Follow,
        ["merged"] = merged, ["notMerged"] = kept, ["locked"] = locked,
        ["counts"] = new Dictionary<string, int> { ["split"] = splitDone.Count, ["endsSet"] = set.Ends, ["followed"] = set.Follow.Count, ["merged"] = merged.Count,
          ["locked"] = locked.Count, ["bendsOverMax"] = bends.Count },
        ["after"] = after,
      };
      if (bends.Count > 0)
        answer["warning"] = $"{bends.Count} node(s) bend more than {maxBend} degrees (vatten.maxBojGrader): split the bend (splitBends) or change the points";
      if (!dryRun) await RefreshBands(new[] { networkName }, answer);
      return answer;
    }, dryRun, "applyChainProfile");
    if (guard != null && result is Dictionary<string, object?> res)
    {
      var now = await CivilExecution.ReadAsync((doc, civilDoc, database, transaction) => OtherNetworksState(civilDoc, transaction, networkName));
      var changed = guard.Where(kv => !now.TryGetValue(kv.Key, out var v) || v != kv.Value).Select(kv => kv.Key)
        .Concat(now.Keys.Where(k => !guard.ContainsKey(k))).Distinct().ToList();
      if (changed.Count > 0)
        throw new JsonRpcDispatchException("CIVIL3D.INTERNAL_ERROR",
          $"applyChainProfile dry run with branches follow left changes in other networks: {string.Join(", ", changed.Take(20))}{(changed.Count > 20 ? ", ..." : "")}. Undo them in Civil 3D (U) and run with branches keep.");
      res["otherNetworksChecked"] = guard.Count;
    }
    return result;
  }

  /// <summary>C238 fix: the geometry and links of every part of the other networks, for the dry run's check.</summary>
  private static Dictionary<string, string> OtherNetworksState(Autodesk.Civil.ApplicationServices.CivilDocument civilDoc, Transaction transaction, string networkName)
  {
    static string P(Autodesk.AutoCAD.Geometry.Point3d q) => string.Create(CultureInfo.InvariantCulture, $"{q.X:F6},{q.Y:F6},{q.Z:F6}");
    var state = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var name in ListPipeNetworkNames(civilDoc, transaction).Where(n => !string.Equals(n, networkName, StringComparison.OrdinalIgnoreCase)))
    {
      var network = FindPipeNetworkByName(civilDoc, transaction, name, OpenMode.ForRead);
      foreach (ObjectId pid in network.GetPipeIds())
        if (transaction.GetObject(pid, OpenMode.ForRead) is Pipe p)
          state[$"{name}/{p.Name}"] = $"{P(p.StartPoint)}|{P(p.EndPoint)}|{ReadLink(p)?.HostHandle}";
      foreach (ObjectId sid in network.GetStructureIds())
        if (transaction.GetObject(sid, OpenMode.ForRead) is Structure st)
        {
          string sump;
          try { sump = st.SumpElevation.ToString("F6", CultureInfo.InvariantCulture); } catch (Exception) { sump = "?"; }
          state[$"{name}/{st.Name}"] = $"{P(st.Location)}|{sump}|{ReadLink(st)?.HostHandle}";
        }
    }
    return state;
  }

  /// <summary>Removes a break point with two pipes: the incoming pipe runs on to the far end of the outgoing one (its
  /// structure connection there kept), the outgoing pipe and the break point are erased; links (C199) to them move to
  /// the kept pipe.</summary>
  private static void MergeAt(Database database, Transaction transaction, string networkName, ChainNode n, object civilDoc)
  {
    var k = (Pipe)transaction.GetObject(n.In!.Pipe.ObjectId, OpenMode.ForWrite);
    var q = (Pipe)transaction.GetObject(n.Out!.Pipe.ObjectId, OpenMode.ForWrite);
    var node = (Structure)transaction.GetObject(n.Structure!.ObjectId, OpenMode.ForWrite);
    var qAtStart = q.StartStructureId == node.ObjectId;
    var far = qAtStart ? q.EndPoint : q.StartPoint;
    var farS = qAtStart ? q.EndStructureId : q.StartStructureId;
    var gone = new[] { q.Handle.ToString(), node.Handle.ToString() }.ToHashSet(StringComparer.OrdinalIgnoreCase);
    if (!q.StartStructureId.IsNull) q.Disconnect(ConnectorPositionType.Start);
    if (!q.EndStructureId.IsNull) q.Disconnect(ConnectorPositionType.End);
    q.Erase();
    var kAtStart = k.StartStructureId == node.ObjectId;
    var conn = kAtStart ? ConnectorPositionType.Start : ConnectorPositionType.End;
    k.Disconnect(conn);
    if (kAtStart) k.StartPoint = far; else k.EndPoint = far;
    if (!farS.IsNull)
    {
      k.ConnectToStructure(conn, farS, true);
      if (kAtStart) k.StartPoint = far; else k.EndPoint = far;
    }
    node.Erase();
    // links to the erased parts point at the kept pipe
    foreach (var on in ListPipeNetworkNames(civilDoc, transaction))
    {
      var onet = FindPipeNetworkByName(civilDoc, transaction, on, OpenMode.ForRead);
      foreach (var id in onet.GetStructureIds().Cast<ObjectId>().Concat(onet.GetPipeIds().Cast<ObjectId>()))
      {
        if (id.IsErased) continue;
        var part = transaction.GetObject(id, OpenMode.ForRead);
        if (ReadLink(part) is { } lk && gone.Contains(lk.HostHandle))
          WriteLink(database, transaction, part, networkName, k.Name, k.Handle.ToString());
      }
    }
  }
}
