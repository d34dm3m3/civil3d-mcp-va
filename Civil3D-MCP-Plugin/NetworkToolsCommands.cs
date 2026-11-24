using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil;
using Autodesk.Civil.DatabaseServices;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;

namespace Civil3DMcpPlugin;

/// <summary>C193-C198, gap analysis 2026-11-24 points 7-11 and 14 (requested: bring the
/// remaining improvement items into the MCP): grading from the outlet, moving structures with their pipes,
/// splitting a pipe, cover along pipes, splitting bends over 45 degrees, regenerating layouts.</summary>
public static partial class PipeNetworkCommands
{
  // ---------- C193 gradeGravityNetwork ----------

  /// <summary>C193: inverts of a gravity network from its outlet upstream: {networkName, outlet:{structure, invert},
  /// slopes?:{pipeName: permille}, defaultPermille? (else each pipe keeps its present slope), dropStraight=0.01,
  /// dropBend=0.03, bendThresholdDeg=10, crownToCrown=true, setSumps=true, dryRun?}. Walking upstream, the invert of a
  /// pipe at its downstream structure = the structure's outgoing invert + the drop through the structure (straight or
  /// bend by the horizontal deviation; crown to crown when the incoming pipe is smaller: at least D out - D in); its
  /// upstream invert adds slope x the centre-to-centre length. Pipe ends (trimmed or not) get the centreline on that
  /// grade; sumps = the lowest outgoing invert. Answer per structure (invert out, rim, depth) and pipe (slope, inverts).
  /// Use case: every project computed the inverts in Python from a fixed point (no drop through structures in one
  /// project, a separate structure-drop script in another).</summary>
  public static Task<object?> GradeGravityNetworkAsync(JsonObject? parameters)
  {
    var networkName = PluginRuntime.GetRequiredString(parameters, "networkName");
    if (PluginRuntime.GetParameter(parameters, "outlet") is not JsonObject outO || outO["structure"]?.GetValue<string>() is not string outName
        || outO["invert"] == null)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "outlet {structure, invert} is required. Nothing was changed.");
    var outInvert = outO["invert"]!.GetValue<double>();
    var slopes = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
    if (PluginRuntime.GetParameter(parameters, "slopes") is JsonObject so)
      foreach (var kv in so) slopes[kv.Key] = kv.Value!.GetValue<double>() / 1000;
    var defaultSlope = PluginRuntime.GetOptionalDouble(parameters, "defaultPermille") / 1000;
    var dropStraight = PluginRuntime.GetOptionalDouble(parameters, "dropStraight") ?? 0.01;
    var dropBend = PluginRuntime.GetOptionalDouble(parameters, "dropBend") ?? 0.03;
    var bendDeg = PluginRuntime.GetOptionalDouble(parameters, "bendThresholdDeg") ?? 10;
    var crown = PluginRuntime.GetOptionalBool(parameters, "crownToCrown") ?? true;
    var setSumps = PluginRuntime.GetOptionalBool(parameters, "setSumps") ?? true;
    var dryRun = PluginRuntime.GetOptionalBool(parameters, "dryRun") ?? false;

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var network = FindPipeNetworkByName(civilDoc, transaction, networkName, OpenMode.ForRead);
      var mode = dryRun ? OpenMode.ForRead : OpenMode.ForWrite;
      var structs = network.GetStructureIds().Cast<ObjectId>().Select(id => CivilObjectUtils.GetRequiredObject<Structure>(transaction, id, mode))
        .ToDictionary(s => s.ObjectId);
      var pipes = network.GetPipeIds().Cast<ObjectId>().Select(id => CivilObjectUtils.GetRequiredObject<Pipe>(transaction, id, mode)).ToList();
      var outlet = structs.Values.FirstOrDefault(s => string.Equals(s.Name, outName, StringComparison.OrdinalIgnoreCase))
        ?? throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Structure '{outName}' is not in '{networkName}'. Nothing was changed.");
      Point2d C(ObjectId id) => XY(structs[id].Location);
      var outInv = new Dictionary<ObjectId, double> { [outlet.ObjectId] = outInvert };   // structure -> outgoing invert
      var outPipe = new Dictionary<ObjectId, Pipe?> { [outlet.ObjectId] = null };
      var pipeInv = new Dictionary<Pipe, (double Down, double Up, double Slope, ObjectId DownS, ObjectId UpS)>();
      var queue = new Queue<ObjectId>();
      queue.Enqueue(outlet.ObjectId);
      while (queue.Count > 0)
      {
        var s = queue.Dequeue();
        foreach (var p in pipes.Where(p => !pipeInv.ContainsKey(p) && (p.StartStructureId == s || p.EndStructureId == s)))
        {
          if (outPipe[s] == p) continue;
          var up = p.StartStructureId == s ? p.EndStructureId : p.StartStructureId;
          if (up.IsNull || !structs.ContainsKey(up)) continue;
          if (outInv.ContainsKey(up)) continue;                                   // loop: already graded
          var drop = 0.0;
          var op = outPipe[s];
          if (op != null)
          {
            var next = op.StartStructureId == s ? op.EndStructureId : op.StartStructureId;
            var din = C(s) - C(up);
            var dout = next.IsNull || !structs.ContainsKey(next) ? din : C(next) - C(s);
            var dev = din.Length < 1e-9 || dout.Length < 1e-9 ? 0 : din.GetAngleTo(dout) * 180 / Math.PI;
            drop = dev > bendDeg ? dropBend : dropStraight;
            if (crown && op.InnerDiameterOrWidth > p.InnerDiameterOrWidth) drop = Math.Max(drop, op.InnerDiameterOrWidth - p.InnerDiameterOrWidth);
          }
          var down = outInv[s] + drop;
          var L = C(s).GetDistanceTo(C(up));
          var slope = slopes.TryGetValue(p.Name ?? "", out var sl) ? sl : defaultSlope ?? Math.Abs(p.Slope);
          var upInv = down + slope * L;
          pipeInv[p] = (down, upInv, slope, s, up);
          outInv[up] = upInv;
          outPipe[up] = p;
          queue.Enqueue(up);
        }
      }
      var resultPipes = new List<Dictionary<string, object?>>();
      foreach (var (p, g) in pipeInv)
      {
        var a = C(g.DownS); var b = C(g.UpS);
        var zs = ZOnGrade(a, g.Down, b, g.Up, p.StartPoint, p.InnerDiameterOrWidth);
        var ze = ZOnGrade(a, g.Down, b, g.Up, p.EndPoint, p.InnerDiameterOrWidth);
        if (!dryRun)
        {
          p.StartPoint = new Point3d(p.StartPoint.X, p.StartPoint.Y, zs);
          p.EndPoint = new Point3d(p.EndPoint.X, p.EndPoint.Y, ze);
        }
        resultPipes.Add(new Dictionary<string, object?> { ["name"] = p.Name, ["slopePermille"] = Math.Round(g.Slope * 1000, 2),
          ["invertDown"] = Math.Round(g.Down, 3), ["invertUp"] = Math.Round(g.Up, 3), ["down"] = structs[g.DownS].Name, ["up"] = structs[g.UpS].Name });
      }
      var resultStructs = new List<Dictionary<string, object?>>();
      foreach (var (id, inv) in outInv)
      {
        var st = structs[id];
        // sump: the lowest invert at this structure (its outgoing pipe, or the incoming ones at the outlet)
        var low = pipeInv.Where(kv => kv.Value.DownS == id).Select(kv => kv.Value.Down).Append(inv).Min();
        if (setSumps && !dryRun && Math.Abs(st.SumpElevation - low) > 0.0005)
        {
          st.ControlSumpBy = StructureControlSumpType.ByElevation;
          st.SumpElevation = low;
        }
        resultStructs.Add(new Dictionary<string, object?> { ["name"] = st.Name, ["invertOut"] = Math.Round(inv, 3), ["sump"] = Math.Round(low, 3),
          ["rim"] = Math.Round(st.RimElevation, 3), ["depth"] = Math.Round(st.RimElevation - low, 3) });
      }
      var ungraded = pipes.Where(p => !pipeInv.ContainsKey(p)).Select(p => p.Name).ToList();
      return new Dictionary<string, object?> { ["networkName"] = network.Name, ["dryRun"] = dryRun, ["structures"] = resultStructs,
        ["pipes"] = resultPipes, ["ungradedPipes"] = ungraded };
    });
  }

  // ---------- C194 moveStructures ----------

  /// <summary>C194: moves structures with their pipes: {networkName, moves:[{name, x, y} | {name, dx, dy}],
  /// rimFromSurface=true, dryRun?}. Each connected pipe end follows: a pipe that stopped at the structure's edge (C190)
  /// stops at the same distance from the new centre, toward the pipe's other end; the end keeps its elevation (regrade
  /// with gradeGravityNetwork if needed). Rims follow the reference surface. Use case: moveEntities moves only the structure (log 2026-10), and
  /// a project moved pipe and service ends in Python (the project scripts).</summary>
  public static Task<object?> MoveStructuresAsync(JsonObject? parameters)
  {
    var networkName = PluginRuntime.GetRequiredString(parameters, "networkName");
    var rimFromSurface = PluginRuntime.GetOptionalBool(parameters, "rimFromSurface") ?? true;
    var dryRun = PluginRuntime.GetOptionalBool(parameters, "dryRun") ?? false;
    var moves = ((PluginRuntime.GetParameter(parameters, "moves") as JsonArray) ?? new JsonArray()).Select((n, i) =>
    {
      if (n is not JsonObject o || o["name"]?.GetValue<string>() is not string nm)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"moves[{i}] needs name. Nothing was changed.");
      double? x = o["x"]?.GetValue<double>(), y = o["y"]?.GetValue<double>(), dx = o["dx"]?.GetValue<double>(), dy = o["dy"]?.GetValue<double>();
      if ((x == null) != (y == null) || (dx == null) != (dy == null) || (x == null && dx == null))
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"moves[{i}] ({nm}): give x and y, or dx and dy. Nothing was changed.");
      return (Name: nm, X: x, Y: y, DX: dx, DY: dy);
    }).ToList();
    if (moves.Count == 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "moves must list at least one structure. Nothing was changed.");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var network = FindPipeNetworkByName(civilDoc, transaction, networkName, OpenMode.ForRead);
      var surface = NetworkSurface(network, transaction);
      var mode = dryRun ? OpenMode.ForRead : OpenMode.ForWrite;
      var found = moves.Select(m => (M: m, S: FindStructureByName(network, transaction, m.Name, mode))).ToList();
      var result = new List<Dictionary<string, object?>>();
      // Civil moves the connected pipe ends with the structure (to its new centre, connection kept; live 2026-11-24);
      // ends that stopped at the edge (C190) are trimmed again to the distance they had
      var structs = network.GetStructureIds().Cast<ObjectId>().Select(id => CivilObjectUtils.GetRequiredObject<Structure>(transaction, id, OpenMode.ForRead))
        .ToDictionary(x => x.ObjectId);
      var edge = new Dictionary<ObjectId, double>();
      var affected = new Dictionary<ObjectId, Pipe>();
      foreach (var (m, st) in found)
        foreach (var p in PipesAt(network, transaction, st.ObjectId, mode))
        {
          affected[p.ObjectId] = p;
          foreach (var (sid, end) in new[] { (p.StartStructureId, p.StartPoint), (p.EndStructureId, p.EndPoint) })
            if (!sid.IsNull && structs.TryGetValue(sid, out var s0))
              edge[sid] = Math.Max(edge.TryGetValue(sid, out var e) ? e : 0, XY(end).GetDistanceTo(XY(s0.Location)));
        }
      foreach (var (m, st) in found)
      {
        var c0 = XY(st.Location);
        var c1 = m.X != null ? new Point2d(m.X.Value, m.Y!.Value) : c0 + new Vector2d(m.DX!.Value, m.DY!.Value);
        double? rim = rimFromSurface ? SurfaceZ(surface, c1.X, c1.Y) : null;
        if (!dryRun)
        {
          st.Location = new Point3d(c1.X, c1.Y, st.Location.Z);
          if (rim != null) { st.AutomaticRimSurfaceAdjustment = false; st.RimElevation = rim.Value; }
        }
        result.Add(new Dictionary<string, object?> { ["name"] = st.Name, ["from"] = new[] { Math.Round(c0.X, 3), Math.Round(c0.Y, 3) },
          ["to"] = new[] { Math.Round(c1.X, 3), Math.Round(c1.Y, 3) }, ["rim"] = rim == null ? null : Math.Round(rim.Value, 3),
          ["pipes"] = PipesAt(network, transaction, st.ObjectId, OpenMode.ForRead).Select(x => x.Name).ToList() });
      }
      if (!dryRun && affected.Count > 0)
      {
        var fresh = network.GetStructureIds().Cast<ObjectId>().Select(id => CivilObjectUtils.GetRequiredObject<Structure>(transaction, id, OpenMode.ForRead))
          .ToDictionary(x => x.ObjectId);
        TrimPipeEnds(fresh, affected.Values, x => edge.TryGetValue(x.ObjectId, out var e) && e > 0.001 ? e + TrimMargin : 0, false);
      }
      return new Dictionary<string, object?> { ["networkName"] = network.Name, ["dryRun"] = dryRun, ["moved"] = result };
    });
  }

  // ---------- C195 splitPipe ----------

  /// <summary>C195: inserts a structure in a pipe: {networkName, pipe, at:{x,y} | station (m from the start, 2D),
  /// partName="Brytpunkt", structureDiameter?, name?, newPipeName? (default "{pipe}b"), dryRun?}. The pipe ends at the new
  /// structure; a new pipe of the same part, size and style runs on to the old end structure; elevations follow the old
  /// grade; the rim follows the reference surface. Use case: valves and bend points were added by rewriting point lists
  /// in Python before a rebuild (the project scripts).</summary>
  public static Task<object?> SplitPipeAsync(JsonObject? parameters)
  {
    var networkName = PluginRuntime.GetRequiredString(parameters, "networkName");
    var pipeName = PluginRuntime.GetRequiredString(parameters, "pipe");
    var station = PluginRuntime.GetOptionalDouble(parameters, "station");
    (double X, double Y)? at = PluginRuntime.GetParameter(parameters, "at") is JsonNode an ? ReadXY(an, "at") : null;
    if ((station == null) == (at == null))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Give at {x, y} or station. Nothing was changed.");
    var partName = PluginRuntime.GetOptionalString(parameters, "partName") ?? "Brytpunkt";
    var structureDiameter = PluginRuntime.GetOptionalDouble(parameters, "structureDiameter");
    var newName = PluginRuntime.GetOptionalString(parameters, "name");
    var newPipeName = PluginRuntime.GetOptionalString(parameters, "newPipeName") ?? pipeName + "b";
    var dryRun = PluginRuntime.GetOptionalBool(parameters, "dryRun") ?? false;

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var network = FindPipeNetworkByName(civilDoc, transaction, networkName, OpenMode.ForWrite);
      var pipe = FindPipeByName(network, transaction, pipeName, OpenMode.ForWrite);
      var a = pipe.StartPoint; var b = pipe.EndPoint;
      var seg = new LineSegment2d(XY(a), XY(b));
      var p2 = at != null ? seg.GetClosestPointTo(new Point2d(at.Value.X, at.Value.Y)).Point : XY(a) + (XY(b) - XY(a)).GetNormal() * station!.Value;
      var t = seg.Length < 1e-9 ? 0 : XY(a).GetDistanceTo(p2) / seg.Length;
      if (t < 0.01 || t > 0.99)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"The split point is at the end of '{pipeName}' (t = {t:F3}). Nothing was changed.");
      var zc = a.Z + (b.Z - a.Z) * t;
      var inv = zc - pipe.InnerDiameterOrWidth / 2;
      var rim = SurfaceZ(NetworkSurface(network, transaction), p2.X, p2.Y) ?? zc + 1.5;
      var answer = new Dictionary<string, object?> { ["networkName"] = network.Name, ["pipe"] = pipe.Name, ["dryRun"] = dryRun,
        ["at"] = new[] { Math.Round(p2.X, 3), Math.Round(p2.Y, 3) }, ["t"] = Math.Round(t, 4), ["invert"] = Math.Round(inv, 3), ["rim"] = Math.Round(rim, 3) };
      if (dryRun) return answer;
      if (!string.IsNullOrWhiteSpace(newName)) RefuseTakenName(network, transaction, newName!);
      RefuseTakenName(network, transaction, newPipeName);
      var sid = AddStructureToNetwork(network, transaction, new Point3d(p2.X, p2.Y, rim), partName, rim, 0.0, structureDiameter);
      var st = CivilObjectUtils.GetRequiredObject<Structure>(transaction, sid, OpenMode.ForWrite);
      if (!string.IsNullOrWhiteSpace(newName)) st.Name = newName!;
      st.AutomaticRimSurfaceAdjustment = false;
      st.RimElevation = rim;
      var oldEndStructure = pipe.EndStructureId;
      var family = pipe.PartFamilyName;
      var innerMm = pipe.InnerDiameterOrWidth * 1000;
      var style = pipe.StyleId;
      if (!oldEndStructure.IsNull) pipe.Disconnect(ConnectorPositionType.End);
      var mid = new Point3d(p2.X, p2.Y, zc);
      pipe.EndPoint = mid;
      pipe.ConnectToStructure(ConnectorPositionType.End, sid, true);
      pipe.EndPoint = mid;
      var nid = AddPipeToNetwork(network, transaction, family, innerMm, mid, b, sid, oldEndStructure);
      var np = CivilObjectUtils.GetRequiredObject<Pipe>(transaction, nid, OpenMode.ForWrite);
      np.Name = newPipeName;
      np.StyleId = style;
      np.StartPoint = mid;
      np.EndPoint = b;
      st.ControlSumpBy = StructureControlSumpType.ByElevation;
      st.SumpElevation = inv;
      answer["structure"] = st.Name;
      answer["newPipe"] = np.Name;
      return answer;
    });
  }

  /// <summary>C196: cover over the crown along each pipe against a surface: {networkNames? (default all), surface? (default
  /// each network's reference surface), step=1.0, minCover?} -> per pipe the least cover, its station from the start, the
  /// share of samples under minCover. Use case: cover was only checked at pipe ends (V set to ground - 1.5 m at its bend
  /// points only, in one project), and QC minCover read a property that does not exist (fixed with the same sampling, C196).</summary>
  public static Task<object?> PipeCoverAlongAsync(JsonObject? parameters)
  {
    var networks = OptionalStringList(parameters, "networkNames");
    var surfaceName = PluginRuntime.GetOptionalString(parameters, "surface");
    var step = PluginRuntime.GetOptionalDouble(parameters, "step") ?? 1.0;
    var minCover = PluginRuntime.GetOptionalDouble(parameters, "minCover");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var names = networks is { Count: > 0 } ? networks : ListPipeNetworkNames(civilDoc, transaction);
      var given = string.IsNullOrWhiteSpace(surfaceName) ? null : CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, surfaceName!, OpenMode.ForRead);
      var result = new List<Dictionary<string, object?>>();
      foreach (var nn in names)
      {
        var network = FindPipeNetworkByName(civilDoc, transaction, nn, OpenMode.ForRead);
        var surface = given ?? NetworkSurface(network, transaction);
        if (surface == null) { result.Add(new() { ["network"] = network.Name, ["error"] = "no surface" }); continue; }
        var pipes = new List<Dictionary<string, object?>>();
        foreach (ObjectId id in network.GetPipeIds())
        {
          var p = CivilObjectUtils.GetRequiredObject<Pipe>(transaction, id, OpenMode.ForRead);
          var c = CoverAlong(p, surface, step, minCover);
          pipes.Add(new Dictionary<string, object?> { ["name"] = p.Name, ["minCover"] = double.IsNaN(c.Min) ? null : Math.Round(c.Min, 3),
            ["atStation"] = Math.Round(c.AtStation, 2), ["shareBelow"] = minCover == null ? null : Math.Round(c.ShareBelow, 3), ["samples"] = c.Samples });
        }
        var under = minCover == null ? 0 : pipes.Count(x => x["minCover"] is double v && v < minCover.Value);
        result.Add(new Dictionary<string, object?> { ["network"] = network.Name, ["surface"] = surface.Name,
          ["pipesUnderMinCover"] = minCover == null ? null : under, ["pipes"] = pipes.OrderBy(x => x["minCover"] as double? ?? 99).ToList() });
      }
      return new Dictionary<string, object?> { ["step"] = step, ["minCover"] = minCover, ["networks"] = result };
    });
  }

  // ---------- C197 splitBends ----------

  /// <summary>C197: splits bends over maxDeg at structures with exactly two pipes: {networkName, maxDeg=45,
  /// straightBetween=0.8, families? (default ["Brytpunkt"]), structures? [names], dryRun?}. A bend of d degrees becomes
  /// n = ceil(d / maxDeg) bends of d / n with straightBetween between them, placed symmetrically on the two pipe lines;

  /// the structure moves to the first bend, n - 1 new structures of the same part are added and the pipes between
  /// them get the same part, size and style; elevations follow a straight grade between the two outer bend points.
  /// Use case: the user's design rule of at most 45 degrees of bend per point (pressure sewer and water); a project
  /// split bends in Python.</summary>
  public static Task<object?> SplitBendsAsync(JsonObject? parameters)
  {
    var networkName = PluginRuntime.GetRequiredString(parameters, "networkName");
    var maxDeg = PluginRuntime.GetOptionalDouble(parameters, "maxDeg") ?? 45.0;
    var between = PluginRuntime.GetOptionalDouble(parameters, "straightBetween") ?? 0.8;
    var families = OptionalStringList(parameters, "families") ?? new List<string> { "Brytpunkt" };
    var only = OptionalStringList(parameters, "structures");
    var dryRun = PluginRuntime.GetOptionalBool(parameters, "dryRun") ?? false;
    if (!(maxDeg > 1 && maxDeg < 180) || !(between > 0))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "maxDeg must be 1-180 and straightBetween > 0. Nothing was changed.");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var network = FindPipeNetworkByName(civilDoc, transaction, networkName, OpenMode.ForWrite);
      var surface = NetworkSurface(network, transaction);
      var result = new List<Dictionary<string, object?>>();
      var structures = network.GetStructureIds().Cast<ObjectId>().Select(id => CivilObjectUtils.GetRequiredObject<Structure>(transaction, id, OpenMode.ForRead)).ToList();
      foreach (var st in structures)
      {
        if (!families.Contains(st.PartFamilyName ?? "", StringComparer.OrdinalIgnoreCase)) continue;
        if (only != null && !only.Contains(st.Name ?? "", StringComparer.OrdinalIgnoreCase)) continue;
        var ps = PipesAt(network, transaction, st.ObjectId, OpenMode.ForRead);
        if (ps.Count != 2) continue;
        var V = XY(st.Location);
        Point3d Far(Pipe p) => p.StartStructureId == st.ObjectId ? p.EndPoint : p.StartPoint;
        Point3d Near(Pipe p) => p.StartStructureId == st.ObjectId ? p.StartPoint : p.EndPoint;
        var p1 = ps[0]; var p2 = ps[1];
        var dIn = (V - XY(Far(p1))).GetNormal();
        var dOut = (XY(Far(p2)) - V).GetNormal();
        var dev = dIn.GetAngleTo(dOut) * 180 / Math.PI;
        if (dev <= maxDeg + 1e-6) continue;
        var n = (int)Math.Ceiling(dev / maxDeg);
        var step = dIn.GetAngleTo(dOut) / n * Math.Sign(dIn.X * dOut.Y - dIn.Y * dOut.X);
        // chord from the first to the last bend: n - 1 straight pieces, the k-th turned k x step from dIn
        var chord = new Vector2d(0, 0);
        for (var k = 1; k < n; k++) chord += dIn.RotateBy(step * k) * between;
        // first bend on the incoming line (V - dIn*t1), last on the outgoing line (V + dOut*t2): dIn*t1 + dOut*t2 = chord
        var det = dIn.X * dOut.Y - dIn.Y * dOut.X;
        var t1 = (chord.X * dOut.Y - chord.Y * dOut.X) / det;
        var t2 = (dIn.X * chord.Y - dIn.Y * chord.X) / det;
        var L1 = V.GetDistanceTo(XY(Far(p1))); var L2 = V.GetDistanceTo(XY(Far(p2)));
        var entry = new Dictionary<string, object?> { ["structure"] = st.Name, ["deviationDeg"] = Math.Round(dev, 1), ["bends"] = n,
          ["eachDeg"] = Math.Round(dev / n, 1) };
        if (t1 < 0.05 || t2 < 0.05 || t1 > L1 - 0.1 || t2 > L2 - 0.1)
        { entry["skipped"] = "the split bends do not fit on the pipes"; result.Add(entry); continue; }
        var pts = new List<Point2d> { V - dIn * t1 };
        for (var k = 1; k < n; k++) pts.Add(pts[^1] + dIn.RotateBy(step * k) * between);
        var z1 = Near(p1).Z + (Far(p1).Z - Near(p1).Z) * (t1 / L1);
        var z2 = Near(p2).Z + (Far(p2).Z - Near(p2).Z) * (t2 / L2);
        entry["points"] = pts.Select(q => new[] { Math.Round(q.X, 3), Math.Round(q.Y, 3) }).ToList();
        if (dryRun) { result.Add(entry); continue; }
        // structure to the first bend; p1 ends there
        var sw = CivilObjectUtils.GetRequiredObject<Structure>(transaction, st.ObjectId, OpenMode.ForWrite);
        var w1 = CivilObjectUtils.GetRequiredObject<Pipe>(transaction, p1.ObjectId, OpenMode.ForWrite);
        var w2 = CivilObjectUtils.GetRequiredObject<Pipe>(transaction, p2.ObjectId, OpenMode.ForWrite);
        var family = w2.PartFamilyName; var innerMm = w2.InnerDiameterOrWidth * 1000; var style = w2.StyleId;
        var p2AtStart = w2.StartStructureId == st.ObjectId;
        w2.Disconnect(p2AtStart ? ConnectorPositionType.Start : ConnectorPositionType.End);
        sw.Location = new Point3d(pts[0].X, pts[0].Y, sw.Location.Z);
        var z1c = z1;
        if (w1.StartStructureId == st.ObjectId) w1.StartPoint = new Point3d(pts[0].X, pts[0].Y, z1c); else w1.EndPoint = new Point3d(pts[0].X, pts[0].Y, z1c);
        var total = between * (n - 1);
        var prevId = st.ObjectId; var prevZ = z1c;
        var created = new List<string>();
        for (var k = 1; k < n; k++)
        {
          var zk = z1 + (z2 - z1) * (between * k / Math.Max(total, 1e-9));
          var q = pts[k];
          var rim = SurfaceZ(surface, q.X, q.Y) ?? sw.RimElevation;
          var nid = AddStructureToNetwork(network, transaction, new Point3d(q.X, q.Y, rim), sw.PartFamilyName, rim, 0.0, null);
          var ns = CivilObjectUtils.GetRequiredObject<Structure>(transaction, nid, OpenMode.ForWrite);
          ns.Name = $"{st.Name}-b{k}";
          ns.StyleId = sw.StyleId;
          var pid = AddPipeToNetwork(network, transaction, family, innerMm, new Point3d(pts[k - 1].X, pts[k - 1].Y, prevZ), new Point3d(q.X, q.Y, zk), prevId, nid);
          var np = CivilObjectUtils.GetRequiredObject<Pipe>(transaction, pid, OpenMode.ForWrite);
          np.Name = $"{st.Name}-bp{k}";
          np.StyleId = style;
          np.StartPoint = new Point3d(pts[k - 1].X, pts[k - 1].Y, prevZ);
          np.EndPoint = new Point3d(q.X, q.Y, zk);
          created.Add(ns.Name); created.Add(np.Name);
          prevId = nid; prevZ = zk;
        }
        var lastPt = new Point3d(pts[^1].X, pts[^1].Y, z2);
        if (p2AtStart) { w2.StartPoint = lastPt; w2.ConnectToStructure(ConnectorPositionType.Start, prevId, true); w2.StartPoint = lastPt; }
        else { w2.EndPoint = lastPt; w2.ConnectToStructure(ConnectorPositionType.End, prevId, true); w2.EndPoint = lastPt; }
        entry["created"] = created;
        result.Add(entry);
      }
      return new Dictionary<string, object?> { ["networkName"] = network.Name, ["dryRun"] = dryRun, ["maxDeg"] = maxDeg, ["split"] = result };
    });
  }

  // ---------- C198 regenLayouts ----------

  /// <summary>C198: REGENALL on several layouts in one call: {layouts? [names] (default every paper layout),
  /// restoreCurrent=true}. Each layout is made current and regenerated through the document's COM object (all
  /// viewports); the starting tab is restored. Use case: scripts switched tab and ran REGENALL per sheet (the project
  /// scripts; C186 was added only for that).</summary>
  public static Task<object?> RegenLayoutsAsync(JsonObject? parameters)
  {
    var wanted = OptionalStringList(parameters, "layouts");
    var restore = PluginRuntime.GetOptionalBool(parameters, "restoreCurrent") ?? true;
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var all = new List<string>();
      var dict = (DBDictionary)transaction.GetObject(database.LayoutDictionaryId, OpenMode.ForRead);
      foreach (DBDictionaryEntry e in dict)
        if (transaction.GetObject(e.Value, OpenMode.ForRead) is Layout l && !l.ModelType) all.Add(l.LayoutName);
      var targets = wanted ?? all;
      var unknown = targets.Where(t => !all.Contains(t, StringComparer.OrdinalIgnoreCase)).ToList();
      if (unknown.Count > 0)
        throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Layouts not found: {string.Join(", ", unknown)}. Nothing was regenerated.");
      var start = LayoutManager.Current.CurrentLayout;
      var done = new List<Dictionary<string, object?>>();
      var acad = doc.GetAcadDocument();
      foreach (var t in targets)
      {
        LayoutManager.Current.CurrentLayout = all.First(a => string.Equals(a, t, StringComparison.OrdinalIgnoreCase));
        string how;
        try { Civil3DCompatibility.InvokeComMethod(acad, "Regen", 1); how = "all"; }
        catch (Exception ex) { doc.Editor.Regen(); how = "current (" + PluginRuntime.DescribeException(ex) + ")"; }
        done.Add(new Dictionary<string, object?> { ["layout"] = t, ["regen"] = how });
      }
      if (restore) LayoutManager.Current.CurrentLayout = start;
      return new Dictionary<string, object?> { ["regenerated"] = done, ["current"] = LayoutManager.Current.CurrentLayout };
    });
  }
}
