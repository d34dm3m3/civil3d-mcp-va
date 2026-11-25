using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil;
using Autodesk.Civil.DatabaseServices;

namespace Civil3DMcpPlugin;

/// <summary>C233 resolveCrossings (F7, 2026-11-24): crossings without the required clearance fixed in the profile of
/// one network's pipes, with a knee or by moving one pipe end. Use case: project scripts solved crossings in Python
/// (about 700 lines in 9 scripts); compare Naviate Raise/Lower Parallel Pipes. Drafting rule: the water main is drawn straight
/// and crossings between the water main and services are solved in the field, so by default neither is changed.</summary>
public static partial class PipeNetworkCommands
{
  private sealed record Against(string Pattern, double? Clearance, string? Side);

  /// <summary>A pipe end or every pipe end at a structure set to an elevation (invert, or centreline for pressure pipes).</summary>
  private sealed record ZItem(string? Structure, string? Pipe, bool Start, double Z, bool IsInvert);

  private sealed record Split(string Pipe, Point2d At, string Name, string NewPipe);

  private sealed class Fix
  {
    public string Pipe = "";
    public string Mode = "";
    public readonly List<Split> Splits = new();
    public readonly List<ZItem> Z = new();
    public Dictionary<string, object?> Change = new();
  }

  private sealed class CrossingCase
  {
    public string Pipe = "", Other = "", OtherNetwork = "";
    public Point2d At;
    public double Clearance, Required;
    public string Side = "";
    public string Status = "unresolved";
    public string? Reason;
    public string? Mode;
    public int? Pass;
    public Dictionary<string, object?>? Change;
    public List<string> NewParts = new();
    public double? ClearanceAfter;
    public bool? OkAfter;
  }

  private static readonly List<(string Medium, string Type)> FallbackMediumTypes = new()
    { ("spill", "S"), ("dag", "D"), ("dran", "Dr"), ("tryckspill", "TS"), ("tryckspill", "LTA"), ("vatten", "V") };

  /// <summary>C233: {network (the one changed), against: [{network (name, * as a wildcard), clearance? (else the rules or
  /// minClearance), side? over|under (where this network's pipe goes; default where it is now)}], pipes? (only these),
  /// mode: "knee" (default: break points; a pressure pipe gets two at the crossing +- kneeAfter at the needed
  /// centreline, a gravity pipe one kneeAfter past the crossing downstream, else upstream, keeping both ends) |
  /// "lowerEnd" (one end of the pipe moved, the one that changes least; an end at a break point moves the joint with its
  /// other pipes), kneeAfter (0.35), minCover? (a raised point keeps this cover over the crown), minSlopePermille? (gravity
  /// pipes keep their direction and at least this slope; default 0), partName ("Brytpunkt"), allowWaterMain (false: a water
  /// main is never changed), solveWaterServiceCrossings (false: water main against a service network is left to the
  /// field), servicePatterns (["*serv*"]), minClearance (0.10), rules?, maxPasses (3), profile?, dryRun? (made and aborted)}.
  /// One crossing per pipe and pass (the worst); the rest in the next pass. Locked pipes and structures (C226) are not
  /// changed. Answer per crossing: pipe, other, clearance before, required, side, status resolved|unresolved|skipped,
  /// reason, mode, change, newParts, clearanceAfter; counts; crossings after.</summary>
  public static Task<object?> ResolveCrossingsAsync(JsonObject? parameters, CancellationToken cancellationToken)
  {
    var networkName = PluginRuntime.GetRequiredString(parameters, "network");
    if (PluginRuntime.GetParameter(parameters, "against") is not JsonArray againstList || againstList.Count == 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "against must list at least one {network, clearance?, side?}. Nothing was changed.");
    var against = againstList.Select((n, i) =>
    {
      if (n is not JsonObject o || o["network"]?.GetValue<string>() is not { Length: > 0 } pat)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"against[{i}] must be {{network, clearance?, side?}}. Nothing was changed.");
      var sd = o["side"]?.GetValue<string>()?.Trim().ToLowerInvariant();
      if (sd is not (null or "over" or "under" or "keep"))
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"against[{i}].side must be over, under or keep. Nothing was changed.");
      return new Against(pat, o["clearance"]?.GetValue<double>(), sd == "keep" ? null : sd);
    }).ToList();
    var onlyPipes = OptionalStringList(parameters, "pipes")?.ToHashSet(StringComparer.OrdinalIgnoreCase);
    var mode = (PluginRuntime.GetOptionalString(parameters, "mode") ?? "knee").Trim();
    if (mode is not ("knee" or "lowerEnd"))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "mode must be knee or lowerEnd. Nothing was changed.");
    var kneeAfter = PluginRuntime.GetOptionalDouble(parameters, "kneeAfter") ?? 0.35;
    var minCover = PluginRuntime.GetOptionalDouble(parameters, "minCover");
    var minSlope = (PluginRuntime.GetOptionalDouble(parameters, "minSlopePermille") ?? 0) / 1000.0;
    var partName = PluginRuntime.GetOptionalString(parameters, "partName") ?? "Brytpunkt";
    var allowWaterMain = PluginRuntime.GetOptionalBool(parameters, "allowWaterMain") ?? false;
    var solveWaterService = PluginRuntime.GetOptionalBool(parameters, "solveWaterServiceCrossings") ?? false;
    var servicePatterns = OptionalStringList(parameters, "servicePatterns") ?? new List<string> { "*serv*" };
    var minClear = PluginRuntime.GetOptionalDouble(parameters, "minClearance") ?? 0.10;
    var rules = ReadCrossingRules(PluginRuntime.GetParameter(parameters, "rules") as JsonArray, "rules");
    var maxPasses = Math.Clamp(PluginRuntime.GetOptionalInt(parameters, "maxPasses") ?? 3, 1, 10);
    var dryRun = PluginRuntime.GetOptionalBool(parameters, "dryRun") ?? false;
    if (!(kneeAfter > 0.05))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "kneeAfter must be over 0.05 m. Nothing was changed.");

    // media from the source rules' civilTyper when the profile is found, else the default prefixes
    var types = FallbackMediumTypes;
    try
    {
      var loaded = RulesProfile.Load(PluginRuntime.GetOptionalString(parameters, "rulesPath"), PluginRuntime.GetOptionalString(parameters, "kommun"),
        PluginRuntime.GetOptionalString(parameters, "drawingPath"));
      if (RulesProfile.ReadSource(loaded)?["medier"] is JsonObject medier)
      {
        var t = new List<(string, string)>();
        foreach (var (mk, mv) in medier)
          if (mv?["civilTyper"] is JsonArray ct)
            foreach (var x in ct) if (x is JsonValue v && v.TryGetValue<string>(out var ts)) t.Add((mk, ts));
        if (t.Count > 0) types = t;
      }
      // with the profile: its own word on the water main (vatten.stamRak) and water main against services
      // (korsning.vMotServisLosesIFalt); explicit parameters win
      if (RulesProfile.Requested(parameters))
      {
        if (PluginRuntime.GetOptionalBool(parameters, "allowWaterMain") == null && RulesProfile.At(loaded.Merged, "vatten.stamRak") is JsonValue sr
            && sr.TryGetValue<bool>(out var straight)) allowWaterMain = !straight;
        if (PluginRuntime.GetOptionalBool(parameters, "solveWaterServiceCrossings") == null && RulesProfile.At(loaded.Merged, "korsning.vMotServisLosesIFalt") is JsonValue vf
            && vf.TryGetValue<bool>(out var field)) solveWaterService = !field;
      }
    }
    catch (JsonRpcDispatchException) { }
    bool IsService(string n) => servicePatterns.Any(p => NetworkNameMatch(p, n));
    bool IsGravity(string n) => MediumOf(n, types) is "spill" or "dag" or "dran";
    bool IsWaterMain(string n) => MediumOf(n, types) == "vatten" && !IsService(n);

    var cases = new List<CrossingCase>();
    CrossingCase CaseFor(string pipe, string other, Point2d at)
    {
      var stem = pipe;
      var hit = cases.FirstOrDefault(c => c.Other == other && c.At.GetDistanceTo(at) < 0.1);
      if (hit != null) return hit;
      var c = new CrossingCase { Pipe = stem, Other = other, At = at };
      cases.Add(c);
      return c;
    }

    return RunWriteOrDry(async () =>
    {
      var passes = 0;
      for (var pass = 1; pass <= maxPasses; pass++)
      {
        var fixes = await CivilExecution.ReadAsync((doc, civilDoc, database, transaction) =>
          PlanPass(civilDoc, transaction, pass));
        if (fixes.Count == 0) break;
        passes = pass;
        foreach (var (fix, cs) in fixes)
        {
          foreach (var sp in fix.Splits)
          {
            await CommandDispatcher.DispatchAsync("splitPipe", new JsonObject
            {
              ["networkName"] = networkName, ["pipe"] = sp.Pipe, ["at"] = new JsonObject { ["x"] = sp.At.X, ["y"] = sp.At.Y },
              ["partName"] = partName, ["name"] = sp.Name, ["newPipeName"] = sp.NewPipe,
            }, cancellationToken);
            cs.NewParts.Add(sp.Name);
            cs.NewParts.Add(sp.NewPipe);
          }
          await CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
          {
            ApplyZ(FindPipeNetworkByName(civilDoc, transaction, networkName, OpenMode.ForRead), transaction, fix.Z);
            return null;
          });
          cs.Mode = fix.Mode;
          cs.Change = fix.Change;
          cs.Pass = pass;
        }
      }
      // after: the crossings again, matched to the cases by the other pipe and the place
      var after = await CivilExecution.ReadAsync((doc, civilDoc, database, transaction) =>
      {
        var (nets, _) = Scope(civilDoc, transaction);
        return ComputeCrossings(transaction, nets, rules, minClear, true, null).Crossings;
      });
      var remaining = new List<Dictionary<string, object?>>();
      foreach (var c in after)
      {
        var (mine, other, _) = Sides(c);
        if (mine == null) continue;
        var at = new Point2d((double)c["x"]!, (double)c["y"]!);
        var cs = cases.FirstOrDefault(k => k.Other == other && k.At.GetDistanceTo(at) < 0.5);
        var need = Need(c);
        var ok = (double)c["clearance"]! >= need - 0.0005;
        if (cs != null) { cs.ClearanceAfter = (double)c["clearance"]!; cs.OkAfter = ok; }
        if (!ok) remaining.Add(c);
      }
      foreach (var cs in cases)
      {
        if (cs.Status == "skipped") continue;
        if (cs.Mode != null) cs.Status = cs.OkAfter == true ? "resolved" : "unresolved";
        if (cs.Mode != null && cs.OkAfter != true) cs.Reason ??= "still under the required clearance after the change";
        if (cs.Mode == null && cs.OkAfter == true) { cs.Status = "resolved"; cs.Reason = "resolved by another change"; }
      }
      var list = cases.Select(c => new Dictionary<string, object?>
      {
        ["pipe"] = $"{networkName}/{c.Pipe}", ["other"] = c.Other, ["x"] = Math.Round(c.At.X, 3), ["y"] = Math.Round(c.At.Y, 3),
        ["clearance"] = Math.Round(c.Clearance, 3), ["required"] = c.Required, ["side"] = c.Side, ["status"] = c.Status, ["reason"] = c.Reason,
        ["mode"] = c.Mode, ["pass"] = c.Pass, ["change"] = c.Change, ["newParts"] = c.NewParts,
        ["clearanceAfter"] = c.ClearanceAfter, ["okAfter"] = c.OkAfter,
      }).ToList();
      var answer = new Dictionary<string, object?>
      {
        ["network"] = networkName, ["mode"] = mode, ["dryRun"] = dryRun, ["passes"] = passes, ["crossings"] = list,
        ["counts"] = new Dictionary<string, int>
        {
          ["found"] = cases.Count, ["resolved"] = cases.Count(c => c.Status == "resolved"), ["unresolved"] = cases.Count(c => c.Status == "unresolved"),
          ["skipped"] = cases.Count(c => c.Status == "skipped"),
        },
        ["remainingUnderRequired"] = remaining,
      };
      if (!dryRun && passes > 0) await RefreshBands(new[] { networkName }, answer);
      return answer;
    }, dryRun, "resolveCrossings");

    // ---- the networks in scope: this one and those against matches
    (List<Network> Nets, Network Net) Scope(Autodesk.Civil.ApplicationServices.CivilDocument civilDoc, Transaction transaction)
    {
      var net = FindPipeNetworkByName(civilDoc, transaction, networkName, OpenMode.ForRead);
      var names = ListPipeNetworkNames(civilDoc, transaction).Where(n => !string.IsNullOrEmpty(n) && !string.Equals(n, net.Name, StringComparison.OrdinalIgnoreCase)
        && against.Any(a => NetworkNameMatch(a.Pattern, n))).ToList();
      var nets = new List<Network> { net };
      nets.AddRange(names.Select(n => FindPipeNetworkByName(civilDoc, transaction, n, OpenMode.ForRead)));
      return (nets, net);
    }

    // which side of a crossing is this network's pipe: (mine "pipe", other "net/pipe", mineIsUpper)
    (string? Mine, string Other, bool MineUpper) Sides(Dictionary<string, object?> c)
    {
      var up = (string)c["upperNetwork"]!; var low = (string)c["lowerNetwork"]!;
      bool upMine = string.Equals(up, networkName, StringComparison.OrdinalIgnoreCase), lowMine = string.Equals(low, networkName, StringComparison.OrdinalIgnoreCase);
      if (upMine == lowMine) return (null, "", false);
      var mine = upMine ? (string)c["upper"]! : (string)c["lower"]!;
      return (mine[(mine.IndexOf('/') + 1)..], upMine ? (string)c["lower"]! : (string)c["upper"]!, upMine);
    }

    Against? Rule(string otherNetwork) => against.FirstOrDefault(a => NetworkNameMatch(a.Pattern, otherNetwork));
    double Need(Dictionary<string, object?> c)
    {
      var (_, other, _) = Sides(c);
      var a = Rule(other[..other.IndexOf('/')]);
      return a?.Clearance ?? (double)c["required"]!;
    }

    // ---- one pass: the worst crossing per pipe of this network and its fix
    List<(Fix Fix, CrossingCase Case)> PlanPass(Autodesk.Civil.ApplicationServices.CivilDocument civilDoc, Transaction transaction, int pass)
    {
      var (nets, net) = Scope(civilDoc, transaction);
      var (crossings, _) = ComputeCrossings(transaction, nets, rules, minClear, false, null);
      var pipes = net.GetPipeIds().Cast<ObjectId>().Select(id => CivilObjectUtils.GetRequiredObject<Pipe>(transaction, id, OpenMode.ForRead))
        .ToDictionary(p => p.Name, p => p, StringComparer.OrdinalIgnoreCase);
      var structs = net.GetStructureIds().Cast<ObjectId>().Select(id => CivilObjectUtils.GetRequiredObject<Structure>(transaction, id, OpenMode.ForRead))
        .ToDictionary(s => s.ObjectId);
      var names = pipes.Keys.Concat(structs.Values.Select(s => s.Name ?? "")).ToHashSet(StringComparer.OrdinalIgnoreCase);
      var otherPipes = new Dictionary<string, Pipe>(StringComparer.OrdinalIgnoreCase);
      foreach (var n in nets.Skip(1))
        foreach (ObjectId id in n.GetPipeIds()) { var p = CivilObjectUtils.GetRequiredObject<Pipe>(transaction, id, OpenMode.ForRead); otherPipes[$"{n.Name}/{p.Name}"] = p; }
      var surface = NetworkSurface(net, transaction);
      var gravity = IsGravity(net.Name);
      var fixes = new List<(Fix, CrossingCase)>();
      var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      foreach (var c in crossings.OrderBy(c => (double)c["clearance"]! - Need(c)))
      {
        var (mine, other, mineUpper) = Sides(c);
        if (mine == null) continue;
        var otherNet = other[..other.IndexOf('/')];
        var rule = Rule(otherNet);
        if (rule == null) continue;
        if (onlyPipes != null && !onlyPipes.Contains(mine) && !onlyPipes.Any(op => mine.StartsWith(op, StringComparison.OrdinalIgnoreCase))) continue;
        var need = Need(c);
        if ((double)c["clearance"]! >= need - 0.0005) continue;
        var at = new Point2d((double)c["x"]!, (double)c["y"]!);
        var cs = CaseFor(mine, other, at);
        if (cs.Status == "skipped" || cs.Mode != null) continue;   // decided, or changed in an earlier pass
        if (pass == 1 || cs.Clearance == 0) { cs.Clearance = (double)c["clearance"]!; cs.Required = need; }
        cs.OtherNetwork = otherNet;
        var wantOver = rule.Side == null ? mineUpper : rule.Side == "over";
        cs.Side = wantOver ? "over" : "under";
        if (IsWaterMain(net.Name) && !allowWaterMain)
        { cs.Status = "skipped"; cs.Reason = "the water main is drawn straight (vatten.stamRak); allowWaterMain: true lets it change"; continue; }
        if (!solveWaterService && ((IsWaterMain(otherNet) && IsService(net.Name)) || (IsWaterMain(net.Name) && IsService(otherNet))))
        { cs.Status = "skipped"; cs.Reason = "water main against a service: solved in the field (korsning.vMotServisLosesIFalt); solveWaterServiceCrossings: true changes the service"; continue; }
        if (!pipes.TryGetValue(mine, out var p)) continue;
        if (IsLocked(p)) { cs.Status = "skipped"; cs.Reason = $"pipe {p.Name} is locked: {ReadLock(p)?.Note}"; continue; }
        if (done.Contains(mine)) { cs.Reason = "waits for the next pass (another crossing on the same pipe went first)"; continue; }
        var q = otherPipes[other];
        var fix = PlanFix(p, q, at, wantOver, need, gravity, structs, pipes.Values.ToList(), surface, names, out var why);
        if (fix == null) { cs.Reason = why; continue; }
        cs.Reason = null;
        done.Add(mine);
        fixes.Add((fix, cs));
      }
      return fixes;
    }

    // ---- the fix of one crossing
    Fix? PlanFix(Pipe p, Pipe q, Point2d at, bool over, double clearance, bool gravity, Dictionary<ObjectId, Structure> structs, List<Pipe> netPipes,
      Autodesk.Civil.DatabaseServices.Surface? surface, HashSet<string> names, out string? why)
    {
      why = null;
      Point2d a = XY(p.StartPoint), b = XY(p.EndPoint);
      var L = a.GetDistanceTo(b);
      var dX = a.GetDistanceTo(at);
      double Zp(double d) => p.StartPoint.Z + (p.EndPoint.Z - p.StartPoint.Z) * (L < 1e-9 ? 0 : d / L);
      var qa = XY(q.StartPoint); var qb = XY(q.EndPoint);
      var tq = qa.GetDistanceTo(qb) < 1e-9 ? 0 : qa.GetDistanceTo(at) / qa.GetDistanceTo(qb);
      var zq = q.StartPoint.Z + (q.EndPoint.Z - q.StartPoint.Z) * tq;
      double rp = p.OuterDiameterOrWidth / 2, rq = q.OuterDiameterOrWidth / 2, inner = p.InnerDiameterOrWidth;
      var margin = 0.001;
      var need = over ? zq + rq + clearance + rp + margin : zq - rq - clearance - rp - margin;
      Point2d At(double d) => Lerp(a, b, L < 1e-9 ? 0 : d / L);
      bool CoverOk(double d, double z, out string? reason)
      {
        reason = null;
        if (minCover == null || z <= Zp(d) + 1e-9) return true;
        var g = SurfaceZ(surface, At(d).X, At(d).Y);
        if (g == null) return true;
        if (g.Value - (z + rp) >= minCover.Value - 1e-6) return true;
        reason = $"the raised pipe would have {g.Value - (z + rp):F3} m cover at {d:F2} m along {p.Name} (minCover {minCover})";
        return false;
      }
      // gravity: the original downstream end stays the lower one and every part keeps at least minSlope
      var downAtEnd = p.EndPoint.Z <= p.StartPoint.Z;
      bool Falls(double zUpstream, double zDownstream, double length, string part, out string? reason)
      {
        reason = null;
        if (!gravity || length < 1e-6) return true;
        var s = (zUpstream - zDownstream) / length;
        if (s >= minSlope - 1e-9 && s >= -1e-9) return true;
        reason = $"{part} would fall {s * 1000:F1} permille (minSlopePermille {minSlope * 1000:F1})";
        return false;
      }
      string Free(string stem) { var nm = stem; for (var i = 2; names.Contains(nm); i++) nm = $"{stem}{i}"; names.Add(nm); return nm; }

      if (mode == "knee")
      {
        if (!gravity)
        {
          double d1 = dX - kneeAfter, d2 = dX + kneeAfter;
          if (d1 < 0.1 || d2 > L - 0.1) { why = $"the crossing is {Math.Min(dX, L - dX):F2} m from an end of {p.Name}, too near for two knees kneeAfter {kneeAfter} apart (try lowerEnd or a smaller kneeAfter)"; return null; }
          if (!CoverOk(d1, need, out why) || !CoverOk(d2, need, out why)) return null;
          var f = new Fix { Pipe = p.Name, Mode = "knee" };
          var k1 = Free($"{p.Name} knä1"); var r1 = Free($"{p.Name}b");
          var k2 = Free($"{p.Name} knä2"); var r2 = Free($"{p.Name}c");
          f.Splits.Add(new Split(p.Name, At(d1), k1, r1));
          f.Splits.Add(new Split(r1, At(d2), k2, r2));
          f.Z.Add(new ZItem(k1, null, false, need, false));
          f.Z.Add(new ZItem(k2, null, false, need, false));
          f.Change = new() { ["knees"] = new[] { k1, k2 }, ["centre"] = Math.Round(need, 3), ["was"] = Math.Round(Zp(dX), 3), ["at"] = new[] { Math.Round(d1, 2), Math.Round(d2, 2) } };
          return f;
        }
        // gravity: one knee, downstream of the crossing first
        double zU = downAtEnd ? p.StartPoint.Z : p.EndPoint.Z, zD = downAtEnd ? p.EndPoint.Z : p.StartPoint.Z;
        var dXU = downAtEnd ? dX : L - dX;                                   // the crossing from the upstream end
        double DfromStart(double du) => downAtEnd ? du : L - du;
        var tried = new List<string>();
        foreach (var downstream in new[] { true, false })
        {
          var dK = downstream ? dXU + kneeAfter : dXU - kneeAfter;
          if (dK < 0.1 || dK > L - 0.1 || dXU < 0.05 || L - dXU < 0.05) { tried.Add($"{(downstream ? "downstream" : "upstream")}: too near the end"); continue; }
          var zK = downstream ? zU + (need - zU) * dK / dXU : zD + (need - zD) * (L - dK) / (L - dXU);
          if (!Falls(zU, zK, dK, "the part above the knee", out var r1) || !Falls(zK, zD, L - dK, "the part below the knee", out r1)) { tried.Add(r1!); continue; }
          if (!CoverOk(DfromStart(dK), zK, out var r2)) { tried.Add(r2!); continue; }
          var f = new Fix { Pipe = p.Name, Mode = "knee" };
          var k = Free($"{p.Name} knä"); var rest = Free($"{p.Name}b");
          f.Splits.Add(new Split(p.Name, At(DfromStart(dK)), k, rest));
          f.Z.Add(new ZItem(k, null, false, zK - inner / 2, true));
          f.Change = new() { ["knee"] = k, ["side"] = downstream ? "downstream" : "upstream", ["invert"] = Math.Round(zK - inner / 2, 3),
            ["was"] = Math.Round(Zp(DfromStart(dK)) - inner / 2, 3), ["at"] = Math.Round(DfromStart(dK), 2) };
          return f;
        }
        why = "no knee keeps the fall: " + string.Join("; ", tried);
        return null;
      }

      // lowerEnd: move one end so that the straight pipe passes the needed height at the crossing
      var cands = new List<(bool Start, double Z, double Change)>();
      if (dX >= 0.05) { var zNew = p.StartPoint.Z + (need - p.StartPoint.Z) * L / dX; cands.Add((false, zNew, zNew - p.EndPoint.Z)); }
      if (L - dX >= 0.05) { var zNew = p.EndPoint.Z + (need - p.EndPoint.Z) * L / (L - dX); cands.Add((true, zNew, zNew - p.StartPoint.Z)); }
      var reasons = new List<string>();
      foreach (var (start, zNew, change) in cands.OrderBy(x => Math.Abs(x.Change)))
      {
        string label = start ? "start" : "end";
        double zS = start ? zNew : p.StartPoint.Z, zE = start ? p.EndPoint.Z : zNew;
        if (!Falls(downAtEnd ? zS : zE, downAtEnd ? zE : zS, L, p.Name, out var r)) { reasons.Add($"{label}: {r}"); continue; }
        if (!CoverOk(start ? 0 : L, zNew, out r)) { reasons.Add($"{label}: {r}"); continue; }
        var sid = start ? p.StartStructureId : p.EndStructureId;
        var f = new Fix { Pipe = p.Name, Mode = "lowerEnd" };
        if (!sid.IsNull && structs.TryGetValue(sid, out var s))
        {
          if (IsLocked(s)) { reasons.Add($"{label}: structure {s.Name} is locked"); continue; }
          var others = netPipes.Where(o => o.ObjectId != p.ObjectId && (o.StartStructureId == sid || o.EndStructureId == sid)).ToList();
          var newInv = zNew - inner / 2;
          if (InlineFamilies.Contains(s.PartFamilyName ?? "") && others.Count > 0)
          {
            // a joint: its other pipes move with it
            string? bad = null;
            foreach (var o in others)
            {
              if (IsLocked(o)) { bad = $"pipe {o.Name} at {s.Name} is locked"; break; }
              var oStart = o.StartStructureId == sid;
              var oz = gravity ? newInv + o.InnerDiameterOrWidth / 2 : zNew;
              double ozS = oStart ? oz : o.StartPoint.Z, ozE = oStart ? o.EndPoint.Z : oz;
              var oDownAtEnd = o.EndPoint.Z <= o.StartPoint.Z;
              if (!Falls(oDownAtEnd ? ozS : ozE, oDownAtEnd ? ozE : ozS, XY(o.StartPoint).GetDistanceTo(XY(o.EndPoint)), o.Name, out var r3)) { bad = r3; break; }
            }
            if (bad != null) { reasons.Add($"{label}: {bad}"); continue; }
            f.Z.Add(new ZItem(s.Name, null, false, gravity ? newInv : zNew, gravity));
          }
          else
          {
            if (gravity)
            {
              // a manhole: no backfall against the pipes on the other side
              var pDown = downAtEnd ? !start : start;      // this end is p's downstream end
              double InvAt(Pipe o) => (o.StartStructureId == sid ? o.StartPoint.Z : o.EndPoint.Z) - o.InnerDiameterOrWidth / 2;
              bool Leaves(Pipe o) { var here = InvAt(o); var there = (o.StartStructureId == sid ? o.EndPoint.Z : o.StartPoint.Z) - o.InnerDiameterOrWidth / 2; return here >= there; }
              var outs = others.Where(Leaves).Select(InvAt).ToList();
              var ins = others.Where(o => !Leaves(o)).Select(InvAt).ToList();
              if (pDown && outs.Count > 0 && newInv < outs.Min() - 1e-4) { reasons.Add($"{label}: the invert {newInv:F3} would be under the outlet of {s.Name} ({outs.Min():F3})"); continue; }
              if (!pDown && ins.Count > 0 && newInv > ins.Min() + 1e-4) { reasons.Add($"{label}: the invert {newInv:F3} would be over an inlet of {s.Name} ({ins.Min():F3})"); continue; }
            }
            f.Z.Add(new ZItem(null, p.Name, start, zNew, false));
          }
        }
        else f.Z.Add(new ZItem(null, p.Name, start, zNew, false));
        f.Change = new() { ["end"] = label, ["structure"] = sid.IsNull || !structs.ContainsKey(sid) ? null : structs[sid].Name,
          ["invert"] = Math.Round(zNew - inner / 2, 3), ["was"] = Math.Round((start ? p.StartPoint.Z : p.EndPoint.Z) - inner / 2, 3), ["dz"] = Math.Round(change, 3) };
        return f;
      }
      why = "no end can move: " + string.Join("; ", reasons);
      return null;
    }
  }

  /// <summary>Sets pipe end elevations: a pipe end, or every pipe end at a structure (invert + inner / 2 for gravity,
  /// else the centreline); then the sumps (break points the lowest invert, others only lowered).</summary>
  private static void ApplyZ(Network network, Transaction transaction, List<ZItem> items)
  {
    var pipes = network.GetPipeIds().Cast<ObjectId>().Select(id => CivilObjectUtils.GetRequiredObject<Pipe>(transaction, id, OpenMode.ForWrite)).ToList();
    var touched = new HashSet<ObjectId>();
    void SetEnd(Pipe p, bool start, double z)
    {
      if (start) { var s = p.StartPoint; p.StartPoint = new Point3d(s.X, s.Y, z); }
      else { var e = p.EndPoint; p.EndPoint = new Point3d(e.X, e.Y, z); }
      var sid = start ? p.StartStructureId : p.EndStructureId;
      if (!sid.IsNull) touched.Add(sid);
    }
    foreach (var it in items)
    {
      if (it.Structure != null)
      {
        var s = FindStructureByName(network, transaction, it.Structure, OpenMode.ForRead);
        foreach (var p in pipes.Where(p => p.StartStructureId == s.ObjectId || p.EndStructureId == s.ObjectId))
          SetEnd(p, p.StartStructureId == s.ObjectId, it.IsInvert ? it.Z + p.InnerDiameterOrWidth / 2 : it.Z);
      }
      else
      {
        var p = pipes.First(x => string.Equals(x.Name, it.Pipe, StringComparison.OrdinalIgnoreCase));
        SetEnd(p, it.Start, it.IsInvert ? it.Z + p.InnerDiameterOrWidth / 2 : it.Z);
      }
    }
    foreach (var sid in touched)
    {
      var s = CivilObjectUtils.GetRequiredObject<Structure>(transaction, sid, OpenMode.ForWrite);
      var inverts = pipes.Where(p => p.StartStructureId == sid || p.EndStructureId == sid)
        .Select(p => (p.StartStructureId == sid ? p.StartPoint.Z : p.EndPoint.Z) - p.InnerDiameterOrWidth / 2).ToList();
      if (inverts.Count == 0) continue;
      var low = inverts.Min();
      var inline = InlineFamilies.Contains(s.PartFamilyName ?? "");
      if (!inline && s.SumpElevation <= low + 1e-4) continue;
      s.ControlSumpBy = StructureControlSumpType.ByElevation;
      s.SumpElevation = low;
    }
  }
}
