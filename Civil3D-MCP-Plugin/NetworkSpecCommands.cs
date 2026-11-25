using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil;
using Autodesk.Civil.DatabaseServices;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;

namespace Civil3DMcpPlugin;

/// <summary>C224 (F5, 2026-11-24): the declarative level of syncPipeNetwork. Polylines split into pipes and break
/// points with stable names, parts inserted on a line or a pipe (valves), branches where a line's end lies on another
/// line, and elevation rules: gravity pipes graded upstream from outlets, pressure pipes at a depth below the surface in
/// every node. Use case: project scripts computed all of this in Python (gravity grading, pressure depths, valves on
/// lines, branches, services) before calling syncPipeNetwork and wrote the node elevations to a side file; three
/// projects did the same.</summary>
public static partial class PipeNetworkCommands
{
  // ---------- spec model (mutable while the spec is expanded) ----------

  private sealed class SpecStructure
  {
    public string Name = "";
    public string PartName = "";
    public double? Dia;
    public double X, Y;
    public double? Rim, Sump, Depth;
    public string Origin = "structures";
  }

  private sealed class SpecPipe
  {
    public string Name = "";
    public string PartName = "";
    public double? Dia;
    public string? StartStructure, EndStructure;
    public (double X, double Y)? StartXY, EndXY;
    public double? StartInvert, EndInvert, StartZ, EndZ, Slope, CenterDepth;
    public string? Junction;
    public string Origin = "pipes";
    public bool Explicit => StartInvert != null || StartZ != null || EndInvert != null || EndZ != null;
  }

  private sealed class SpecLinePoint
  {
    public double X, Y;
    public bool Orig;
    public string? Name, Part;
    public double? Dia;
    public string Kind = "vertex";
    public SpecStructure? Node;
  }

  private sealed class SpecLine
  {
    public string Name = "";
    public List<SpecLinePoint> Pts = new();
    public string PartName = "";
    public double? Dia;
    public string NodePrefix = "";
    public string NodePart = "Brytpunkt";
    public string PipeFormat = "{name}-{seg:02d}";
    public double? Slope, CenterDepth;
    public string? Junction;
  }

  private sealed record SpecInsert(int Index, string? Pipe, string? Line, double X, double Y, string PartName, string? Name, double? Dia,
    double? Snap, string? NewPipeName);

  private sealed record SpecOutlet(string Structure, double? Invert);

  private sealed class SpecModel
  {
    public List<SpecStructure> Structures = new();
    public List<SpecPipe> Pipes = new();
    public List<SpecLine> Lines = new();
    public List<SpecInsert> Inserts = new();
    public List<SpecOutlet> Outlets = new();
    public double Snap = 0.30;
    public double NodeTolerance = 0.01;
    public double DropStraight, DropBend, BendDeg = 10;
    public string Junction = "invert";
    public List<Dictionary<string, object?>> Inserted = new();
    public List<Dictionary<string, object?>> Branches = new();
    public List<Dictionary<string, object?>> LineReport = new();
    public List<string> Warnings = new();
  }

  private static double? Num(JsonObject o, string key) => o[key] is JsonValue v ? v.GetValue<double>() : null;
  private static string? Str(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;

  private static readonly HashSet<string> Junctions = new(StringComparer.OrdinalIgnoreCase) { "invert", "centered", "crown" };

  private static string? ReadJunction(JsonObject o, string what)
  {
    var j = Str(o, "junction");
    if (j != null && !Junctions.Contains(j))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{what}.junction must be invert, centered or crown. Nothing was changed.");
    return j?.ToLowerInvariant();
  }

  /// <summary>Reads structures, pipes, polylines, insert and outlets with the rule parameters.</summary>
  private static SpecModel ParseSpec(JsonObject? parameters)
  {
    var m = new SpecModel
    {
      Snap = PluginRuntime.GetOptionalDouble(parameters, "snapTolerance") ?? 0.30,
      NodeTolerance = PluginRuntime.GetOptionalDouble(parameters, "nodeTolerance") ?? 0.01,
      DropStraight = PluginRuntime.GetOptionalDouble(parameters, "dropStraight") ?? 0.0,
      DropBend = PluginRuntime.GetOptionalDouble(parameters, "dropBend") ?? 0.0,
      BendDeg = PluginRuntime.GetOptionalDouble(parameters, "bendThresholdDeg") ?? 10.0,
    };
    var junction = PluginRuntime.GetOptionalString(parameters, "junction");
    if (junction != null && !Junctions.Contains(junction))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "junction must be invert, centered or crown. Nothing was changed.");
    m.Junction = junction?.ToLowerInvariant() ?? ((PluginRuntime.GetOptionalBool(parameters, "crownToCrown") ?? false) ? "crown" : "invert");
    if (!(m.NodeTolerance > 0) || m.Snap < 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "nodeTolerance must be > 0 and snapTolerance >= 0. Nothing was changed.");

    var sArr = (PluginRuntime.GetParameter(parameters, "structures") as JsonArray) ?? new JsonArray();
    for (var i = 0; i < sArr.Count; i++)
    {
      if (sArr[i] is not JsonObject o || Str(o, "name") is not string nm || Str(o, "partName") is not string pn || o["x"] == null || o["y"] == null)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"structures[{i}] needs name, partName, x and y. Nothing was changed.");
      m.Structures.Add(new SpecStructure
      {
        Name = nm, PartName = pn, Dia = Num(o, "structureDiameter"), X = Num(o, "x")!.Value, Y = Num(o, "y")!.Value,
        Rim = Num(o, "rim"), Sump = Num(o, "sump"), Depth = Num(o, "depthBelowSurface"),
      });
    }
    var pArr = (PluginRuntime.GetParameter(parameters, "pipes") as JsonArray) ?? new JsonArray();
    for (var i = 0; i < pArr.Count; i++)
    {
      if (pArr[i] is not JsonObject o || Str(o, "name") is not string nm || Str(o, "partName") is not string pn)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"pipes[{i}] needs name and partName. Nothing was changed.");
      var p = new SpecPipe
      {
        Name = nm, PartName = pn, Dia = Num(o, "diameter"), StartStructure = Str(o, "startStructure"), EndStructure = Str(o, "endStructure"),
        StartXY = o["start"] == null ? null : ReadXY(o["start"], $"pipes[{i}].start"), EndXY = o["end"] == null ? null : ReadXY(o["end"], $"pipes[{i}].end"),
        StartInvert = Num(o, "startInvert"), EndInvert = Num(o, "endInvert"), StartZ = Num(o, "startZ"), EndZ = Num(o, "endZ"),
        Slope = Num(o, "slopePermille"), CenterDepth = Num(o, "centerDepth"), Junction = ReadJunction(o, $"pipes[{i}]"),
      };
      if ((p.StartStructure == null && p.StartXY == null) || (p.EndStructure == null && p.EndXY == null))
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"pipes[{i}] ({nm}): each end needs a structure or a point. Nothing was changed.");
      if (p.Explicit && ((p.StartInvert == null && p.StartZ == null) || (p.EndInvert == null && p.EndZ == null)))
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"pipes[{i}] ({nm}): give startInvert or startZ, and endInvert or endZ. Nothing was changed.");
      if (p.Explicit && p.Slope != null)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"pipes[{i}] ({nm}): give either slopePermille or the end heights, not both. Nothing was changed.");
      if (p.Slope != null && (p.StartStructure == null || p.EndStructure == null))
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"pipes[{i}] ({nm}): a graded pipe (slopePermille) needs a structure at both ends. Nothing was changed.");
      m.Pipes.Add(p);
    }
    var lArr = (PluginRuntime.GetParameter(parameters, "polylines") as JsonArray) ?? new JsonArray();
    for (var i = 0; i < lArr.Count; i++)
    {
      if (lArr[i] is not JsonObject o || Str(o, "name") is not string nm || Str(o, "partName") is not string pn || o["points"] is not JsonArray pts || pts.Count < 2)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"polylines[{i}] needs name, partName and at least two points. Nothing was changed.");
      var line = new SpecLine
      {
        Name = nm, PartName = pn, Dia = Num(o, "diameter"), NodePrefix = Str(o, "nodePrefix") ?? nm + "-BP", NodePart = Str(o, "nodePartName") ?? "Brytpunkt",
        PipeFormat = Str(o, "pipeNameFormat") ?? "{name}-{seg:02d}", Slope = Num(o, "slopePermille"), CenterDepth = Num(o, "centerDepth"),
        Junction = ReadJunction(o, $"polylines[{i}]"),
      };
      for (var k = 0; k < pts.Count; k++)
      {
        var (x, y) = ReadXY(pts[k], $"polylines[{i}].points[{k}]");
        line.Pts.Add(new SpecLinePoint { X = x, Y = y, Orig = true });
      }
      m.Lines.Add(line);
    }
    var iArr = (PluginRuntime.GetParameter(parameters, "insert") as JsonArray) ?? new JsonArray();
    for (var i = 0; i < iArr.Count; i++)
    {
      if (iArr[i] is not JsonObject o || o["at"] == null || Str(o, "partName") is not string pn)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"insert[{i}] needs at {{x, y}} and partName. Nothing was changed.");
      var (x, y) = ReadXY(o["at"], $"insert[{i}].at");
      var pipe = Str(o, "pipe");
      var line = Str(o, "line") ?? Str(o, "polyline");
      if (pipe != null && line != null)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"insert[{i}]: give pipe or line, not both. Nothing was changed.");
      m.Inserts.Add(new SpecInsert(i, pipe, line, x, y, pn, Str(o, "name"), Num(o, "structureDiameter"), Num(o, "snap"), Str(o, "newPipeName")));
    }
    var oArr = (PluginRuntime.GetParameter(parameters, "outlets") as JsonArray) ?? new JsonArray();
    for (var i = 0; i < oArr.Count; i++)
    {
      if (oArr[i] is not JsonObject o || Str(o, "structure") is not string st)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"outlets[{i}] needs structure (and invert, except for a downstream end). Nothing was changed.");
      m.Outlets.Add(new SpecOutlet(st, Num(o, "invert")));
    }
    if (m.Outlets.Count > 0 && m.Outlets.All(o => o.Invert == null))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "At least one outlet needs an invert. Nothing was changed.");
    return m;
  }

  // ---------- plane geometry (plain doubles: the expansion runs before anything is opened) ----------

  private static double Dist(double ax, double ay, double bx, double by) => Math.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by));

  private static (double D, double T, double X, double Y) SegmentProjection(double ax, double ay, double bx, double by, double px, double py)
  {
    double dx = bx - ax, dy = by - ay, l2 = dx * dx + dy * dy;
    var t = l2 < 1e-18 ? 0 : Math.Clamp(((px - ax) * dx + (py - ay) * dy) / l2, 0, 1);
    double qx = ax + t * dx, qy = ay + t * dy;
    return (Dist(px, py, qx, qy), t, qx, qy);
  }

  /// <summary>Nearest point of a polyline: distance, segment index (the point lies between Pts[seg] and Pts[seg + 1]),
  /// the projected point. The first segment wins a tie.</summary>
  private static (double D, int Seg, double X, double Y) LineProjection(List<SpecLinePoint> pts, double px, double py)
  {
    (double D, int Seg, double X, double Y) best = (double.MaxValue, 0, 0, 0);
    for (var i = 0; i < pts.Count - 1; i++)
    {
      var p = SegmentProjection(pts[i].X, pts[i].Y, pts[i + 1].X, pts[i + 1].Y, px, py);
      if (p.D < best.D - 1e-12) best = (p.D, i, p.X, p.Y);
    }
    return best;
  }

  private static string PipeName(string format, string line, int seg, int sub)
  {
    var s = format.Replace("{name}", line);
    s = Regex.Replace(s, @"\{seg(?::0*(\d+)d?)?\}", mm => mm.Groups[1].Success ? seg.ToString().PadLeft(int.Parse(mm.Groups[1].Value), '0') : seg.ToString());
    const string letters = "bcdefghijklmnopqrstuvwxyz";
    return sub == 0 ? s : s + (sub <= letters.Length ? letters[sub - 1].ToString() : "_" + sub);
  }

  // ---------- expansion: insert, branches, polylines to structures and pipes ----------

  /// <summary>Expands polylines, inserts and branches into structures and pipes (as the project scripts' valves-on-lines and water passes 1-2):
  /// 1. inserts on lines, in the given order: within snap of a point of the line (the nearest line unless line is
  ///    given) the point becomes the part, else the projection is inserted; 2. branches: a line end within nodeTolerance
  ///    of another line (not at one of its points) is inserted there; 3. nodes, lines in order: points within
  ///    nodeTolerance are one node (also a given structure at that place); a new node from an original corner takes the
  ///    next number of its nodePrefix (shared by lines with the same prefix), so inserted parts never renumber corners;
  ///    an inserted point's name is its insert name, else "{line}-I{k}" (insert) or "{line}-T{k}" (branch); 4. pipes per
  ///    segment named by pipeNameFormat with the corner number, a segment split by inserted points gets b, c, ...;
  ///    segments under 0.05 m are skipped; 5. inserts on pipes split the pipe (newPipeName, default "{pipe}b").</summary>
  private static void ExpandSpec(SpecModel m, Func<string, (double X, double Y)?> existingXY)
  {
    var lineByName = new Dictionary<string, SpecLine>(StringComparer.OrdinalIgnoreCase);
    foreach (var l in m.Lines)
      if (!lineByName.TryAdd(l.Name, l))
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Polyline '{l.Name}' is given twice. Nothing was changed.");

    // 1. inserts on lines
    foreach (var ins in m.Inserts.Where(x => x.Pipe == null))
    {
      if (m.Lines.Count == 0)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"insert[{ins.Index}] names no pipe and there are no polylines. Nothing was changed.");
      SpecLine line;
      if (ins.Line != null)
      {
        if (!lineByName.TryGetValue(ins.Line, out line!))
          throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"insert[{ins.Index}]: polyline '{ins.Line}' is not given. Nothing was changed.");
      }
      else line = m.Lines.OrderBy(l => LineProjection(l.Pts, ins.X, ins.Y).D).First();
      var snap = ins.Snap ?? m.Snap;
      var near = Enumerable.Range(0, line.Pts.Count).OrderBy(k => Dist(line.Pts[k].X, line.Pts[k].Y, ins.X, ins.Y)).First();
      var dNear = Dist(line.Pts[near].X, line.Pts[near].Y, ins.X, ins.Y);
      if (dNear <= snap)
      {
        var q = line.Pts[near];
        if (ins.Name != null) q.Name = ins.Name;
        q.Part = ins.PartName;
        q.Dia = ins.Dia;
        m.Inserted.Add(new Dictionary<string, object?> { ["insert"] = ins.Index, ["line"] = line.Name, ["partName"] = ins.PartName, ["snapped"] = true,
          ["distance"] = Math.Round(dNear, 3), ["at"] = new[] { Math.Round(q.X, 4), Math.Round(q.Y, 4) } });
        continue;
      }
      var pr = LineProjection(line.Pts, ins.X, ins.Y);
      line.Pts.Insert(pr.Seg + 1, new SpecLinePoint { X = pr.X, Y = pr.Y, Orig = false, Name = ins.Name, Part = ins.PartName, Dia = ins.Dia, Kind = "insert" });
      m.Inserted.Add(new Dictionary<string, object?> { ["insert"] = ins.Index, ["line"] = line.Name, ["partName"] = ins.PartName, ["snapped"] = false,
        ["distance"] = Math.Round(pr.D, 3), ["at"] = new[] { Math.Round(pr.X, 4), Math.Round(pr.Y, 4) } });
    }

    // 2. branches: a line end on another line
    foreach (var b in m.Lines)
      foreach (var e in new[] { b.Pts[0], b.Pts[^1] })
        foreach (var h in m.Lines)
        {
          if (ReferenceEquals(h, b)) continue;
          if (h.Pts.Any(q => Dist(q.X, q.Y, e.X, e.Y) < m.NodeTolerance)) continue;
          var pr = LineProjection(h.Pts, e.X, e.Y);
          if (pr.D > m.NodeTolerance) continue;
          h.Pts.Insert(pr.Seg + 1, new SpecLinePoint { X = e.X, Y = e.Y, Orig = false, Kind = "branch" });
          m.Branches.Add(new Dictionary<string, object?> { ["line"] = b.Name, ["on"] = h.Name, ["at"] = new[] { Math.Round(e.X, 4), Math.Round(e.Y, 4) } });
        }

    // 3. nodes
    var nodes = new List<SpecStructure>();
    var counters = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    SpecStructure? Find(double x, double y) =>
      nodes.FirstOrDefault(n => Dist(n.X, n.Y, x, y) < m.NodeTolerance) ?? m.Structures.FirstOrDefault(s => Dist(s.X, s.Y, x, y) < m.NodeTolerance);
    foreach (var line in m.Lines)
    {
      int ins = 0, br = 0;
      foreach (var q in line.Pts)
      {
        var node = Find(q.X, q.Y);
        if (node == null)
        {
          string name;
          if (q.Orig)
          {
            counters[line.NodePrefix] = counters.TryGetValue(line.NodePrefix, out var c) ? c + 1 : 1;
            name = q.Name ?? line.NodePrefix + counters[line.NodePrefix];
          }
          else name = q.Name ?? (q.Kind == "branch" ? $"{line.Name}-T{++br}" : $"{line.Name}-I{++ins}");
          node = new SpecStructure { Name = name, PartName = q.Part ?? line.NodePart, Dia = q.Dia, X = q.X, Y = q.Y, Depth = line.CenterDepth, Origin = "polyline:" + line.Name };
          nodes.Add(node);
        }
        else if (node.Origin.StartsWith("polyline:"))
        {
          if (q.Name != null) node.Name = q.Name;   // a corner made by another line becomes the named part
          if (q.Part != null) { node.PartName = q.Part; node.Dia = q.Dia; }
          node.Depth ??= line.CenterDepth;
        }
        else if (q.Part != null && q.Name == null)
        {
          node.PartName = q.Part;
          node.Dia = q.Dia ?? node.Dia;
        }
        q.Node = node;
      }
    }
    m.Structures.AddRange(nodes);

    // 4. pipes of the lines
    foreach (var line in m.Lines)
    {
      int seg = 0, sub = 0;
      var made = new List<string>();
      for (var i = 0; i < line.Pts.Count - 1; i++)
      {
        var a = line.Pts[i];
        var b = line.Pts[i + 1];
        if (a.Orig) { seg++; sub = 0; } else sub++;
        if (ReferenceEquals(a.Node, b.Node) || Dist(a.X, a.Y, b.X, b.Y) < 0.05) continue;
        var p = new SpecPipe
        {
          Name = PipeName(line.PipeFormat, line.Name, seg, sub), PartName = line.PartName, Dia = line.Dia,
          StartStructure = a.Node!.Name, EndStructure = b.Node!.Name, StartXY = (a.Node.X, a.Node.Y), EndXY = (b.Node.X, b.Node.Y),
          Slope = line.Slope, CenterDepth = line.Slope == null ? line.CenterDepth : null, Junction = line.Junction, Origin = "polyline:" + line.Name,
        };
        m.Pipes.Add(p);
        made.Add(p.Name);
      }
      m.LineReport.Add(new Dictionary<string, object?>
      {
        ["name"] = line.Name, ["nodes"] = line.Pts.Select(q => q.Node!.Name).Distinct().ToList(), ["pipes"] = made,
        ["length"] = Math.Round(Enumerable.Range(0, line.Pts.Count - 1).Sum(k => Dist(line.Pts[k].X, line.Pts[k].Y, line.Pts[k + 1].X, line.Pts[k + 1].Y)), 3),
      });
    }

    // 5. inserts on pipes
    (double X, double Y)? XYOf(string? structure) =>
      structure == null ? null : m.Structures.FirstOrDefault(s => string.Equals(s.Name, structure, StringComparison.OrdinalIgnoreCase)) is { } s ? (s.X, s.Y) : existingXY(structure);
    foreach (var ins in m.Inserts.Where(x => x.Pipe != null))
    {
      var p = m.Pipes.FirstOrDefault(x => string.Equals(x.Name, ins.Pipe, StringComparison.OrdinalIgnoreCase))
        ?? throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"insert[{ins.Index}]: pipe '{ins.Pipe}' is not in the spec. Nothing was changed.");
      var a = p.StartXY ?? XYOf(p.StartStructure) ?? throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"insert[{ins.Index}]: the start of '{p.Name}' has no position. Nothing was changed.");
      var b = p.EndXY ?? XYOf(p.EndStructure) ?? throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"insert[{ins.Index}]: the end of '{p.Name}' has no position. Nothing was changed.");
      var pr = SegmentProjection(a.X, a.Y, b.X, b.Y, ins.X, ins.Y);
      var L = Dist(a.X, a.Y, b.X, b.Y);
      var snap = ins.Snap ?? m.Snap;
      var dA = Dist(a.X, a.Y, ins.X, ins.Y);
      var dB = Dist(b.X, b.Y, ins.X, ins.Y);
      if (Math.Min(dA, dB) <= snap)
      {
        var endName = dA <= dB ? p.StartStructure : p.EndStructure;
        var st = (endName == null ? null : m.Structures.FirstOrDefault(s => string.Equals(s.Name, endName, StringComparison.OrdinalIgnoreCase)))
          ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"insert[{ins.Index}] snaps to the {(dA <= dB ? "start" : "end")} of '{p.Name}', which is not a structure of the spec. Nothing was changed.");
        st.PartName = ins.PartName;
        st.Dia = ins.Dia ?? st.Dia;
        if (ins.Name != null && !string.Equals(ins.Name, st.Name, StringComparison.OrdinalIgnoreCase))
        {
          foreach (var x in m.Pipes)
          {
            if (string.Equals(x.StartStructure, st.Name, StringComparison.OrdinalIgnoreCase)) x.StartStructure = ins.Name;
            if (string.Equals(x.EndStructure, st.Name, StringComparison.OrdinalIgnoreCase)) x.EndStructure = ins.Name;
          }
          foreach (var o in m.Outlets.ToList())
            if (string.Equals(o.Structure, st.Name, StringComparison.OrdinalIgnoreCase)) m.Outlets[m.Outlets.IndexOf(o)] = o with { Structure = ins.Name };
          st.Name = ins.Name;
        }
        m.Inserted.Add(new Dictionary<string, object?> { ["insert"] = ins.Index, ["pipe"] = p.Name, ["partName"] = ins.PartName, ["snapped"] = true,
          ["structure"] = st.Name, ["distance"] = Math.Round(Math.Min(dA, dB), 3) });
        continue;
      }
      if (pr.T <= 0 || pr.T >= 1 || L < 0.1)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"insert[{ins.Index}] does not lie along '{p.Name}'. Nothing was changed.");
      var newName = ins.Name ?? $"{p.Name}-I";
      var newPipe = ins.NewPipeName ?? p.Name + "b";
      var node = new SpecStructure { Name = newName, PartName = ins.PartName, Dia = ins.Dia, X = pr.X, Y = pr.Y, Origin = "insert" };
      var q = new SpecPipe
      {
        Name = newPipe, PartName = p.PartName, Dia = p.Dia, StartStructure = newName, EndStructure = p.EndStructure, StartXY = (pr.X, pr.Y), EndXY = p.EndXY,
        Slope = p.Slope, CenterDepth = p.CenterDepth, Junction = p.Junction, Origin = p.Origin,
      };
      double? Lerp(double? u, double? v) => u == null || v == null ? null : u + (v - u) * pr.T;
      if (p.Explicit)
      {
        // heights given: the new point lies on the pipe's grade
        q.StartInvert = Lerp(p.StartInvert, p.EndInvert); q.StartZ = Lerp(p.StartZ, p.EndZ);
        q.EndInvert = p.EndInvert; q.EndZ = p.EndZ;
        if (q.StartInvert == null && q.StartZ == null)
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"insert[{ins.Index}]: '{p.Name}' mixes inverts and centre elevations; give one kind to insert in it. Nothing was changed.");
        p.EndInvert = q.StartInvert; p.EndZ = q.StartZ;
        node.Sump = q.StartInvert ?? q.StartZ;
      }
      else if (p.Slope == null)
      {
        var sDepth = m.Structures.FirstOrDefault(s => string.Equals(s.Name, p.StartStructure, StringComparison.OrdinalIgnoreCase))?.Depth;
        var eDepth = m.Structures.FirstOrDefault(s => string.Equals(s.Name, p.EndStructure, StringComparison.OrdinalIgnoreCase))?.Depth;
        node.Depth = p.CenterDepth ?? sDepth ?? eDepth;
      }
      p.EndStructure = newName;
      p.EndXY = (pr.X, pr.Y);
      m.Structures.Add(node);
      m.Pipes.Insert(m.Pipes.IndexOf(p) + 1, q);
      m.Inserted.Add(new Dictionary<string, object?> { ["insert"] = ins.Index, ["pipe"] = p.Name, ["partName"] = ins.PartName, ["snapped"] = false,
        ["structure"] = newName, ["newPipe"] = newPipe, ["distance"] = Math.Round(pr.D, 3), ["at"] = new[] { Math.Round(pr.X, 4), Math.Round(pr.Y, 4) } });
    }
  }

  // ---------- elevation rules ----------

  private sealed record HeightResult(List<Dictionary<string, object?>> Nodes, List<Dictionary<string, object?>> Pipes);

  /// <summary>Fills the heights of pipes without given heights. Graded pipes (slopePermille): from the outlets with an
  /// invert upstream, through every graded pipe whose other end is not yet known: invert at the known structure = its
  /// outgoing invert + the drop through it (dropStraight or dropBend by the horizontal deviation over bendThresholdDeg,
  /// none in break points and fittings; at least the junction offset: centered (D out - d in) / 2, crown D out - d in), then
  /// + slope x the centre-to-centre length. An outlet without invert is a downstream end: the run of graded pipes from
  /// it back to the first structure with three or more graded pipes (or an outlet) is graded downhill from there.
  /// Pressure pipes: the centre at a structure with depthBelowSurface is its rim (given or the surface) - depth; else
  /// the pipe's centerDepth below the rim or surface at that end. Structures without a given sump get the lowest invert
  /// at them (graded) or the centre (depth; Civil lowers the sump to the lowest pipe invert).</summary>
  private static HeightResult ApplyHeightRules(SpecModel m, CivilSurface? surface, Func<string, (double X, double Y, double Rim, string Family)?> existing,
    Func<SpecPipe, double> inner)
  {
    var sByName = m.Structures.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
    foreach (var dup in m.Structures.GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Structure name '{dup.Key}' is used twice (given and from a polyline or insert). Nothing was changed.");
    (double X, double Y) XYn(string n) => sByName.TryGetValue(n, out var s) ? (s.X, s.Y)
      : existing(n) is { } e ? (e.X, e.Y) : throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Structure '{n}' is neither given nor in the network. Nothing was changed.");
    string Family(string n) => sByName.TryGetValue(n, out var s) ? s.PartName : existing(n)?.Family ?? "";
    double? Ground(double x, double y) => SurfaceZ(surface, x, y);
    double RimOf(string n)
    {
      if (sByName.TryGetValue(n, out var s))
      {
        s.Rim ??= Ground(s.X, s.Y) ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Structure '{s.Name}': no rim given and no surface elevation at ({s.X:F2}, {s.Y:F2}). Nothing was changed.");
        return s.Rim.Value;
      }
      return existing(n)?.Rim ?? throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Structure '{n}' is neither given nor in the network. Nothing was changed.");
    }
    var nodeOut = new List<Dictionary<string, object?>>();
    var pipeOut = new List<Dictionary<string, object?>>();

    // graded network
    var graded = m.Pipes.Where(p => p.Slope != null).ToList();
    var outInv = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
    var pipeInv = new Dictionary<SpecPipe, (string Down, double DownInv, string Up, double UpInv)>();
    if (graded.Count > 0)
    {
      if (m.Outlets.Count == 0)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{graded.Count} pipes have slopePermille but no outlets are given. Nothing was changed.");
      var adj = new Dictionary<string, List<SpecPipe>>(StringComparer.OrdinalIgnoreCase);
      foreach (var p in graded)
        foreach (var n in new[] { p.StartStructure!, p.EndStructure! })
          (adj.TryGetValue(n, out var l) ? l : adj[n] = new List<SpecPipe>()).Add(p);
      string Other(SpecPipe p, string n) => string.Equals(p.StartStructure, n, StringComparison.OrdinalIgnoreCase) ? p.EndStructure! : p.StartStructure!;
      double Len(SpecPipe p) { var a = XYn(p.StartStructure!); var b = XYn(p.EndStructure!); return Dist(a.X, a.Y, b.X, b.Y); }
      var outPipe = new Dictionary<string, SpecPipe?>(StringComparer.OrdinalIgnoreCase);
      double Drop(string node, SpecPipe pin, SpecPipe? pout)
      {
        if (pout == null) return 0;
        var fall = 0.0;
        if (!InlineFamilies.Contains(Family(node)))
        {
          var c = XYn(node); var u = XYn(Other(pin, node)); var d = XYn(Other(pout, node));
          double ix = c.X - u.X, iy = c.Y - u.Y, ox = d.X - c.X, oy = d.Y - c.Y;
          var li = Math.Sqrt(ix * ix + iy * iy); var lo = Math.Sqrt(ox * ox + oy * oy);
          var dev = li < 1e-9 || lo < 1e-9 ? 0 : Math.Acos(Math.Clamp((ix * ox + iy * oy) / (li * lo), -1, 1)) * 180 / Math.PI;
          fall = dev > m.BendDeg ? m.DropBend : m.DropStraight;
        }
        var din = inner(pin); var dout = inner(pout);
        var off = (pin.Junction ?? m.Junction) switch { "crown" => dout - din, "centered" => (dout - din) / 2, _ => 0.0 };
        return Math.Max(fall, Math.Max(0, off));
      }
      foreach (var o in m.Outlets)
        if (!adj.ContainsKey(o.Structure))
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Outlet '{o.Structure}' has no graded pipe. Nothing was changed.");
      var invertOutlets = m.Outlets.Where(o => o.Invert != null).ToList();
      var isOutlet = new HashSet<string>(m.Outlets.Select(o => o.Structure), StringComparer.OrdinalIgnoreCase);
      // downstream ends: the run back to the first junction
      var sinkRuns = new List<(string Junction, List<SpecPipe> Run)>();
      var sinkPipes = new HashSet<SpecPipe>();
      foreach (var o in m.Outlets.Where(o => o.Invert == null))
      {
        var run = new List<SpecPipe>();
        var cur = o.Structure;
        SpecPipe? prev = null;
        while (true)
        {
          var next = adj[cur].Where(p => p != prev).ToList();
          if (cur != o.Structure && (adj[cur].Count >= 3 || isOutlet.Contains(cur))) break;
          if (next.Count != 1)
            throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Outlet '{o.Structure}' without invert must end a single run of graded pipes ('{cur}' has {next.Count} further). Nothing was changed.");
          run.Add(next[0]);
          prev = next[0];
          cur = Other(next[0], cur);
          if (run.Count > graded.Count)
            throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Outlet '{o.Structure}' without invert lies on a loop. Nothing was changed.");
        }
        run.Reverse();   // from the junction down to the outlet
        sinkRuns.Add((cur, run));
        foreach (var p in run) sinkPipes.Add(p);
      }
      var queue = new Queue<string>();
      foreach (var o in invertOutlets) { outInv[o.Structure] = o.Invert!.Value; outPipe[o.Structure] = null; queue.Enqueue(o.Structure); }
      var loops = new List<SpecPipe>();
      while (queue.Count > 0)
      {
        var s = queue.Dequeue();
        foreach (var p in adj[s])
        {
          if (pipeInv.ContainsKey(p) || sinkPipes.Contains(p) || outPipe[s] == p) continue;
          var u = Other(p, s);
          if (outInv.ContainsKey(u)) { if (!loops.Contains(p)) loops.Add(p); continue; }
          var down = outInv[s] + Drop(s, p, outPipe[s]);
          var up = down + p.Slope!.Value / 1000 * Len(p);
          pipeInv[p] = (s, down, u, up);
          outInv[u] = up;
          outPipe[u] = p;
          queue.Enqueue(u);
        }
      }
      foreach (var (j, run) in sinkRuns)
      {
        if (!outInv.TryGetValue(j, out var inv))
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"The downstream run from '{j}' is not reached from an outlet with invert. Nothing was changed.");
        var cur = j;
        SpecPipe? pin = null;
        foreach (var p in run)
        {
          var start = pin == null ? inv : inv - Drop(cur, pin, p);
          var n = Other(p, cur);
          var end = start - p.Slope!.Value / 1000 * Len(p);
          pipeInv[p] = (n, end, cur, start);
          outInv[n] = end;
          outPipe[n] = null;
          cur = n; inv = end; pin = p;
        }
      }
      foreach (var p in loops.Where(p => !pipeInv.ContainsKey(p)))
      {
        var a = p.StartStructure!; var b = p.EndStructure!;
        var (down, up) = outInv[a] <= outInv[b] ? (a, b) : (b, a);
        pipeInv[p] = (down, outInv[down], up, outInv[up]);
        m.Warnings.Add($"Pipe '{p.Name}' closes a loop: it runs between the inverts its structures got from other pipes ({outInv[down]:F3} to {outInv[up]:F3}), not at its slopePermille.");
      }
      var unreached = graded.Where(p => !pipeInv.ContainsKey(p)).Select(p => p.Name).ToList();
      if (unreached.Count > 0)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Graded pipes not connected to an outlet: {string.Join(", ", unreached)}. Nothing was changed.");
      foreach (var p in graded)
      {
        var g = pipeInv[p];
        var startIsDown = string.Equals(p.StartStructure, g.Down, StringComparison.OrdinalIgnoreCase);
        p.StartInvert = startIsDown ? g.DownInv : g.UpInv;
        p.EndInvert = startIsDown ? g.UpInv : g.DownInv;
        var L = Len(p);
        pipeOut.Add(new Dictionary<string, object?> { ["name"] = p.Name, ["rule"] = "graded", ["down"] = g.Down, ["up"] = g.Up,
          ["invertDown"] = Math.Round(g.DownInv, 4), ["invertUp"] = Math.Round(g.UpInv, 4), ["length"] = Math.Round(L, 3),
          ["slopePermille"] = Math.Round(L < 1e-9 ? 0 : (g.UpInv - g.DownInv) / L * 1000, 2) });
      }
      foreach (var (n, inv) in outInv)
      {
        var low = pipeInv.Values.Where(g => string.Equals(g.Down, n, StringComparison.OrdinalIgnoreCase)).Select(g => g.DownInv)
          .Concat(pipeInv.Values.Where(g => string.Equals(g.Up, n, StringComparison.OrdinalIgnoreCase)).Select(g => g.UpInv)).Append(inv).Min();
        if (sByName.TryGetValue(n, out var s)) s.Sump ??= low;
        var rim = RimOf(n);
        nodeOut.Add(new Dictionary<string, object?> { ["name"] = n, ["rule"] = "graded", ["rim"] = Math.Round(rim, 4), ["invert"] = Math.Round(low, 4),
          ["depth"] = Math.Round(rim - low, 3), ["outlet"] = isOutlet.Contains(n) ? true : null, ["inExisting"] = sByName.ContainsKey(n) ? null : true });
      }
    }

    // depth below the surface (pressure pipes)
    var centre = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
    foreach (var s in m.Structures.Where(s => s.Depth != null && !outInv.ContainsKey(s.Name)))
    {
      var z = RimOf(s.Name) - s.Depth!.Value;
      centre[s.Name] = z;
      s.Sump ??= z;
      nodeOut.Add(new Dictionary<string, object?> { ["name"] = s.Name, ["rule"] = "depth", ["rim"] = Math.Round(s.Rim!.Value, 4), ["centre"] = Math.Round(z, 4),
        ["depth"] = s.Depth });
    }
    foreach (var p in m.Pipes.Where(p => p.Slope == null && !p.Explicit))
    {
      double EndZ(string? st, (double X, double Y)? xy, string which)
      {
        if (st != null && centre.TryGetValue(st, out var z)) return z;
        if (p.CenterDepth != null)
        {
          if (st != null) return RimOf(st) - p.CenterDepth.Value;
          var g = Ground(xy!.Value.X, xy.Value.Y) ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Pipe '{p.Name}': no surface elevation at its {which}. Nothing was changed.");
          return g - p.CenterDepth.Value;
        }
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
          $"Pipe '{p.Name}' has no height at its {which}: give startInvert/startZ and endInvert/endZ, slopePermille with outlets, centerDepth, or depthBelowSurface on the structure. Nothing was changed.");
      }
      p.StartZ = EndZ(p.StartStructure, p.StartXY, "start");
      p.EndZ = EndZ(p.EndStructure, p.EndXY, "end");
      pipeOut.Add(new Dictionary<string, object?> { ["name"] = p.Name, ["rule"] = "depth", ["startZ"] = Math.Round(p.StartZ.Value, 4), ["endZ"] = Math.Round(p.EndZ.Value, 4) });
    }
    return new HeightResult(nodeOut, pipeOut);
  }
}
