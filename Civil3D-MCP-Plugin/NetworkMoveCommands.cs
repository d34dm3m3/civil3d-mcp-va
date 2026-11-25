using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;

namespace Civil3DMcpPlugin;

/// <summary>C231 moveNetworkNode and C232 offsetPipeRun (F7, 2026-11-24): moving a node of a network in plan with
/// everything attached to it, and laying a run parallel to a reference run. Use case: project scripts moved
/// structures, pipe ends and the service ends on the moved pipes in Python (about 1 500 lines in 34 scripts), and
/// Naviate Offset Pipe Network does the offset part (D and V at a fixed centre-to-centre distance from S, which a draft
/// did by hand).</summary>
public static partial class PipeNetworkCommands
{
  /// <summary>Where a structure centre or a free pipe end goes, and why (the node itself, or linked to it).</summary>
  private sealed record NodeTarget(string Network, string Name, Point2d From, Point2d To, string How);

  /// <summary>A pipe whose ends change: old and new end points (centreline).</summary>
  private sealed record PipeMove(Pipe Pipe, string Network, Point3d OldStart, Point3d OldEnd, Point3d NewStart, Point3d NewEnd);

  /// <summary>What a node move changes: structure centres, free pipe ends (no structure at that end), centreline z of
  /// single pipe ends (z "interpolate", keepGrade), and the parts left because they are locked (C226).</summary>
  private sealed class NodePlan
  {
    public readonly Dictionary<ObjectId, NodeTarget> Structures = new();
    public readonly Dictionary<(ObjectId Pipe, bool Start), NodeTarget> FreeEnds = new();
    public readonly Dictionary<(ObjectId Pipe, bool Start), double> EndZ = new();
    public readonly List<Dictionary<string, object?>> Locked = new();
    public readonly List<string> Warnings = new();
    public readonly HashSet<ObjectId> Main = new();               // the nodes asked for (not followers)
  }

  /// <summary>Every pipe and structure of the networks by id, with its network name (Part.NetworkName is not read:
  /// it was empty on some ids, C35b).</summary>
  private sealed class PartIndex
  {
    public readonly Dictionary<ObjectId, (string Network, Part Part)> Parts = new();
    public readonly List<(string Network, Pipe Pipe)> Pipes = new();
    public readonly List<(string Network, Structure Structure)> Structures = new();
    public readonly Dictionary<string, Network> Networks = new(StringComparer.OrdinalIgnoreCase);

    public PartIndex(IEnumerable<Network> networks, Transaction transaction)
    {
      foreach (var net in networks)
      {
        Networks[net.Name] = net;
        foreach (ObjectId id in net.GetPipeIds())
        {
          var p = CivilObjectUtils.GetRequiredObject<Pipe>(transaction, id, OpenMode.ForRead);
          Parts[id] = (net.Name, p);
          Pipes.Add((net.Name, p));
        }
        foreach (ObjectId id in net.GetStructureIds())
        {
          var s = CivilObjectUtils.GetRequiredObject<Structure>(transaction, id, OpenMode.ForRead);
          Parts[id] = (net.Name, s);
          Structures.Add((net.Name, s));
        }
      }
    }

    public IEnumerable<(string Network, Pipe Pipe)> PipesAt(ObjectId structureId) =>
      Pipes.Where(x => x.Pipe.StartStructureId == structureId || x.Pipe.EndStructureId == structureId);
  }

  private static double Project(Point2d q, Point2d a, Point2d b)
  {
    var v = b - a;
    return v.LengthSqrd < 1e-12 ? 0 : Math.Clamp((q - a).DotProduct(v) / v.LengthSqrd, 0, 1);
  }

  private static Point2d Lerp(Point2d a, Point2d b, double t) => a + (b - a) * t;

  private static double[] R3(Point2d p) => new[] { Math.Round(p.X, 3), Math.Round(p.Y, 3) };

  /// <summary>The new end points of every pipe a plan touches: an end at a structure keeps its distance from the
  /// structure centre (a pipe trimmed to the edge, C190, stays trimmed) toward the pipe's other end; a free end goes
  /// where the plan says; z from EndZ, else the end's old z.</summary>
  private static Dictionary<ObjectId, PipeMove> PredictPipes(NodePlan plan, PartIndex index)
  {
    var affected = new HashSet<ObjectId>();
    foreach (var sid in plan.Structures.Keys)
      foreach (var (_, p) in index.PipesAt(sid)) affected.Add(p.ObjectId);
    foreach (var k in plan.FreeEnds.Keys) affected.Add(k.Pipe);
    foreach (var k in plan.EndZ.Keys) affected.Add(k.Pipe);

    Point2d OldCentre(ObjectId sid) => XY(((Structure)index.Parts[sid].Part).Location);
    Point2d Centre(ObjectId sid) => plan.Structures.TryGetValue(sid, out var t) ? t.To : OldCentre(sid);
    bool Known(ObjectId sid) => !sid.IsNull && index.Parts.ContainsKey(sid);

    var result = new Dictionary<ObjectId, PipeMove>();
    foreach (var id in affected)
    {
      if (!index.Parts.TryGetValue(id, out var entry) || entry.Part is not Pipe pipe) continue;
      Point3d s0 = pipe.StartPoint, e0 = pipe.EndPoint;
      Point2d Ref(bool start)
      {
        var sid = start ? pipe.StartStructureId : pipe.EndStructureId;
        if (Known(sid)) return Centre(sid);
        return plan.FreeEnds.TryGetValue((id, start), out var f) ? f.To : XY(start ? s0 : e0);
      }
      Point3d End(bool start)
      {
        var sid = start ? pipe.StartStructureId : pipe.EndStructureId;
        var old = start ? s0 : e0;
        Point2d q;
        if (Known(sid))
        {
          var c0 = OldCentre(sid);
          var d = XY(old).GetDistanceTo(c0);
          var c = Centre(sid);
          var v = Ref(!start) - c;
          q = d < 1e-4 || v.Length < 1e-9 ? c + (XY(old) - c0) : c + v.GetNormal() * d;
        }
        else q = plan.FreeEnds.TryGetValue((id, start), out var f) ? f.To : XY(old);
        var z = plan.EndZ.TryGetValue((id, start), out var zz) ? zz : old.Z;
        return new Point3d(q.X, q.Y, z);
      }
      result[id] = new PipeMove(pipe, entry.Network, s0, e0, End(true), End(false));
    }
    return result;
  }

  /// <summary>Parts of any network that follow the plan's moves: a structure linked (C199) to a moved structure moves
  /// with it; a structure linked to a moved pipe keeps its place along the pipe (the same fraction, as the earlier Python move script);
  /// a free pipe end on a moved pipe (within tolerance) or at a moved node does the same. One level: followers of
  /// followers are not followed. Locked followers stay and are listed.</summary>
  private static void AddFollowers(NodePlan plan, PartIndex index, Dictionary<ObjectId, PipeMove> moves, double tolerance)
  {
    var byHandle = new Dictionary<string, NodeTarget>(StringComparer.OrdinalIgnoreCase);
    foreach (var (sid, t) in plan.Structures) byHandle[index.Parts[sid].Part.Handle.ToString()] = t;
    var pipeByHandle = moves.Values.ToDictionary(m => m.Pipe.Handle.ToString(), m => m, StringComparer.OrdinalIgnoreCase);
    var nodes = plan.Structures.Values.Concat(plan.FreeEnds.Values).Where(t => (t.To - t.From).Length > 1e-9).ToList();

    foreach (var (net, s) in index.Structures)
    {
      if (plan.Structures.ContainsKey(s.ObjectId)) continue;
      var link = ReadLink(s);
      if (link == null) continue;
      var from = XY(s.Location);
      Point2d to;
      string how;
      if (byHandle.TryGetValue(link.HostHandle, out var host)) { to = from + (host.To - host.From); how = $"linked to {host.Network}/{host.Name}"; }
      else if (pipeByHandle.TryGetValue(link.HostHandle, out var pm))
      {
        var t = Project(from, XY(pm.OldStart), XY(pm.OldEnd));
        to = Lerp(XY(pm.NewStart), XY(pm.NewEnd), t);
        how = $"linked to {pm.Network}/{pm.Pipe.Name}";
      }
      else continue;
      if ((to - from).Length < 1e-6) continue;
      if (IsLocked(s)) { plan.Locked.Add(LockedEntry(s, "follow " + how)); continue; }
      var lp = index.PipesAt(s.ObjectId).Select(x => x.Pipe).FirstOrDefault(IsLocked);
      if (lp != null) { var e = LockedEntry(s, "follow " + how); e["note"] = $"locked pipe {lp.Name}: {ReadLock(lp)?.Note}"; plan.Locked.Add(e); continue; }
      plan.Structures[s.ObjectId] = new NodeTarget(net, s.Name, from, to, how);
    }

    foreach (var (net, p) in index.Pipes)
    {
      if (moves.ContainsKey(p.ObjectId)) continue;
      foreach (var start in new[] { true, false })
      {
        var sid = start ? p.StartStructureId : p.EndStructureId;
        if (!sid.IsNull && index.Parts.ContainsKey(sid)) continue;
        if (plan.FreeEnds.ContainsKey((p.ObjectId, start))) continue;
        var q = XY(start ? p.StartPoint : p.EndPoint);
        Point2d? to = null;
        var how = "";
        var node = nodes.FirstOrDefault(t => t.From.GetDistanceTo(q) <= tolerance);
        if (node != null) { to = q + (node.To - node.From); how = $"at {node.Network}/{node.Name}"; }
        else
          foreach (var pm in moves.Values)
          {
            var a = XY(pm.OldStart); var b = XY(pm.OldEnd);
            var t = Project(q, a, b);
            if (Lerp(a, b, t).GetDistanceTo(q) > tolerance) continue;
            to = Lerp(XY(pm.NewStart), XY(pm.NewEnd), t) + (q - Lerp(a, b, t));
            how = $"on {pm.Network}/{pm.Pipe.Name}";
            break;
          }
        if (to == null || (to.Value - q).Length < 1e-6) continue;
        if (IsLocked(p)) { plan.Locked.Add(LockedEntry(p, "follow " + how)); continue; }
        plan.FreeEnds[(p.ObjectId, start)] = new NodeTarget(net, $"{p.Name} {(start ? "start" : "end")}", q, to.Value, how);
      }
    }
  }

  /// <summary>z "interpolate" for the plan's main nodes: a node with two pipes gets the invert on the straight line
  /// between the two far ends (by the new plan lengths); a node with one pipe keeps that pipe's slope; other nodes keep
  /// their z (warning).</summary>
  private static void InterpolateMainNodes(NodePlan plan, PartIndex index, Dictionary<ObjectId, PipeMove> predicted)
  {
    var nodeEnds = new List<(string Node, List<(Pipe Pipe, bool Start)> Ends)>();
    foreach (var sid in plan.Main.Where(plan.Structures.ContainsKey))
      nodeEnds.Add((plan.Structures[sid].Name, index.PipesAt(sid).Select(x => (x.Pipe, x.Pipe.StartStructureId == sid)).ToList()));
    foreach (var g in plan.FreeEnds.Where(kv => plan.Main.Contains(kv.Key.Pipe)).GroupBy(kv => kv.Value.From.X.ToString("F3") + kv.Value.From.Y.ToString("F3")))
      nodeEnds.Add((g.First().Value.Name, g.Select(kv => ((Pipe)index.Parts[kv.Key.Pipe].Part, kv.Key.Start)).ToList()));

    foreach (var (node, ends) in nodeEnds)
    {
      double Inner(Pipe p) => p.InnerDiameterOrWidth;
      (Point3d Near, Point3d Far) NewEnds(Pipe p, bool start)
      {
        var pm = predicted[p.ObjectId];
        return start ? (pm.NewStart, pm.NewEnd) : (pm.NewEnd, pm.NewStart);
      }
      if (ends.Count == 2)
      {
        var (n1, f1) = NewEnds(ends[0].Pipe, ends[0].Start);
        var (n2, f2) = NewEnds(ends[1].Pipe, ends[1].Start);
        double inv1 = f1.Z - Inner(ends[0].Pipe) / 2, inv2 = f2.Z - Inner(ends[1].Pipe) / 2;
        double l1 = XY(n1).GetDistanceTo(XY(f1)), l2 = XY(n2).GetDistanceTo(XY(f2));
        if (l1 + l2 < 1e-6) continue;
        var inv = inv1 + (inv2 - inv1) * l1 / (l1 + l2);
        plan.EndZ[(ends[0].Pipe.ObjectId, ends[0].Start)] = inv + Inner(ends[0].Pipe) / 2;
        plan.EndZ[(ends[1].Pipe.ObjectId, ends[1].Start)] = inv + Inner(ends[1].Pipe) / 2;
      }
      else if (ends.Count == 1)
      {
        var (p, start) = ends[0];
        var nearOld = start ? p.StartPoint : p.EndPoint;
        var farOld = start ? p.EndPoint : p.StartPoint;
        var lOld = XY(nearOld).GetDistanceTo(XY(farOld));
        var (n, f) = NewEnds(p, start);
        var lNew = XY(n).GetDistanceTo(XY(f));
        if (lOld < 1e-6) continue;
        plan.EndZ[(p.ObjectId, start)] = farOld.Z + (nearOld.Z - farOld.Z) * lNew / lOld;
      }
      else plan.Warnings.Add($"z interpolate: {node} has {ends.Count} pipes; its z is kept.");
    }
  }

  /// <summary>Writes a plan: structure centres (rim from the network's reference surface with rimFromSurface), then the
  /// pipe ends as predicted, then sumps: break points and fittings (C206) get the lowest invert of their pipes, other
  /// structures are only lowered when a pipe invert would go under the sump.</summary>
  private static (List<Dictionary<string, object?>> Structures, List<Dictionary<string, object?>> Pipes) ApplyPlan(NodePlan plan, PartIndex index,
    Dictionary<ObjectId, PipeMove> predicted, Transaction transaction, bool rimFromSurface)
  {
    var structures = new List<Dictionary<string, object?>>();
    foreach (var (sid, t) in plan.Structures)
    {
      var (net, part) = index.Parts[sid];
      var s = (Structure)part;
      if (!s.IsWriteEnabled) s.UpgradeOpen();
      s.Location = new Point3d(t.To.X, t.To.Y, s.Location.Z);
      double? rim = null;
      if (rimFromSurface && SurfaceZ(NetworkSurface(index.Networks[net], transaction), t.To.X, t.To.Y) is double z)
      {
        rim = z;
        s.AutomaticRimSurfaceAdjustment = false;
        s.RimElevation = z;
      }
      structures.Add(new Dictionary<string, object?>
      {
        ["network"] = net, ["name"] = s.Name, ["from"] = R3(t.From), ["to"] = R3(t.To), ["shift"] = Math.Round((t.To - t.From).Length, 3),
        ["rim"] = rim == null ? null : Math.Round(rim.Value, 3), ["how"] = t.How,
      });
    }
    var pipes = new List<Dictionary<string, object?>>();
    foreach (var pm in predicted.Values)
    {
      var p = pm.Pipe;
      if (pm.NewStart.DistanceTo(p.StartPoint) < 1e-6 && pm.NewEnd.DistanceTo(p.EndPoint) < 1e-6) continue;
      if (!p.IsWriteEnabled) p.UpgradeOpen();
      p.StartPoint = pm.NewStart;
      p.EndPoint = pm.NewEnd;
      pipes.Add(new Dictionary<string, object?>
      {
        ["network"] = pm.Network, ["name"] = p.Name,
        ["start"] = new[] { Math.Round(pm.NewStart.X, 3), Math.Round(pm.NewStart.Y, 3), Math.Round(pm.NewStart.Z, 3) },
        ["end"] = new[] { Math.Round(pm.NewEnd.X, 3), Math.Round(pm.NewEnd.Y, 3), Math.Round(pm.NewEnd.Z, 3) },
        ["length2d"] = Math.Round(XY(pm.NewStart).GetDistanceTo(XY(pm.NewEnd)), 3),
        ["dz"] = Math.Round(Math.Max(Math.Abs(pm.NewStart.Z - pm.OldStart.Z), Math.Abs(pm.NewEnd.Z - pm.OldEnd.Z)), 4),
      });
    }
    // sumps of the structures whose pipes changed z
    var touched = plan.Structures.Keys.Concat(predicted.Values.SelectMany(pm => new[] { pm.Pipe.StartStructureId, pm.Pipe.EndStructureId }))
      .Where(id => !id.IsNull && index.Parts.ContainsKey(id)).Distinct();
    foreach (var sid in touched)
    {
      var s = (Structure)index.Parts[sid].Part;
      if (IsLocked(s)) continue;
      var inverts = index.PipesAt(sid).Select(x => (x.Pipe.StartStructureId == sid ? x.Pipe.StartPoint.Z : x.Pipe.EndPoint.Z) - x.Pipe.InnerDiameterOrWidth / 2).ToList();
      if (inverts.Count == 0) continue;
      var low = inverts.Min();
      var inline = InlineFamilies.Contains(s.PartFamilyName ?? "");
      if (!inline && s.SumpElevation <= low + 1e-4) continue;
      if (inline && Math.Abs(s.SumpElevation - low) < 1e-4) continue;
      if (!s.IsWriteEnabled) s.UpgradeOpen();
      s.ControlSumpBy = StructureControlSumpType.ByElevation;
      s.SumpElevation = low;
    }
    return (structures, pipes);
  }

  /// <summary>The crossings that involve the given pipes ("network/pipe"), each marked new when the pair did not cross
  /// before, with the clearance before when it did.</summary>
  private static List<Dictionary<string, object?>> CrossingsInvolving(List<Dictionary<string, object?>> after, List<Dictionary<string, object?>> before,
    HashSet<string> pipes)
  {
    static string Key(Dictionary<string, object?> c) => string.Join("|", new[] { (string)c["upper"]!, (string)c["lower"]! }.OrderBy(x => x, StringComparer.Ordinal));
    var was = new Dictionary<string, double>();
    foreach (var c in before) was[Key(c)] = (double)c["clearance"]!;
    var list = new List<Dictionary<string, object?>>();
    foreach (var c in after)
    {
      if (!pipes.Contains((string)c["upper"]!) && !pipes.Contains((string)c["lower"]!)) continue;
      var d = new Dictionary<string, object?>(c);
      var k = Key(c);
      d["new"] = !was.ContainsKey(k);
      d["clearanceBefore"] = was.TryGetValue(k, out var v) ? v : null;
      list.Add(d);
    }
    return list;
  }

  /// <summary>Runs a write command's body in one transaction (an own single-transaction batch, as drawEntities); a dry
  /// run makes the change for real in that transaction and aborts it, so the answer (heights, crossings after) is the
  /// one the run would give and nothing stays. Inside a caller's batch the body runs inline; a dry run is refused there
  /// (its abort would take the caller's other calls with it). The aborted dry run still leaves the drawing flagged as
  /// changed (unsavedChanges true, live 2026-11-24), though nothing in it changed.</summary>
  private static async Task<object?> RunWriteOrDry(Func<Task<Dictionary<string, object?>>> body, bool dryRun, string what)
  {
    if (CivilExecution.InBatch)
    {
      if (dryRun)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{what}: a dry run makes the change and aborts its transaction, which is not possible inside a batch. Call it outside the batch. Nothing was changed.");
      return await body();
    }
    var drawing = PluginRuntime.GetRequestedDrawing().Name;
    return await CivilExecution.ExecuteBatchAsync<object?>(async () =>
    {
      var previous = PluginRuntime.SetRequestedDrawing(drawing);
      try
      {
        var r = await body();
        if (dryRun) r["dryRunNote"] = "made in a transaction that was then aborted: nothing was changed";
        return (r, !dryRun);
      }
      finally { PluginRuntime.SetRequestedDrawing(previous); }
    }, singleTransaction: true);
  }

  private static async Task RefreshBands(IEnumerable<string> networks, Dictionary<string, object?> answer)
  {
    var refreshed = new List<object?>();
    foreach (var n in networks.Distinct(StringComparer.OrdinalIgnoreCase))
      if (await ProfileViewBandLabelCommands.RefreshProfileViewBandsAsync(new JsonObject { ["networkName"] = n }) is Dictionary<string, object?> r)
        refreshed.Add(r.GetValueOrDefault("refreshed"));
    answer["bandsRefreshed"] = refreshed;
  }

  /// <summary>A structure by "network/name" or by name in the given networks (an error when none or several match).</summary>
  private static (string Network, Structure Structure) ResolveStructure(PartIndex index, string spec)
  {
    var slash = spec.IndexOf('/');
    if (slash > 0 && index.Networks.ContainsKey(spec[..slash]))
    {
      var net = spec[..slash]; var nm = spec[(slash + 1)..];
      var hit = index.Structures.FirstOrDefault(x => string.Equals(x.Network, net, StringComparison.OrdinalIgnoreCase) && string.Equals(x.Structure.Name, nm, StringComparison.OrdinalIgnoreCase));
      return hit.Structure != null ? hit : throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Network '{net}' has no structure '{nm}'. Nothing was changed.");
    }
    var hits = index.Structures.Where(x => string.Equals(x.Structure.Name, spec, StringComparison.OrdinalIgnoreCase)).ToList();
    return hits.Count switch
    {
      1 => hits[0],
      0 => throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"No structure '{spec}' in {string.Join(", ", index.Networks.Keys)}. Nothing was changed."),
      _ => throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Structure '{spec}' is in {string.Join(", ", hits.Select(h => h.Network))}: give network/name or networks. Nothing was changed."),
    };
  }

  /// <summary>Plan, followers, write and the crossings before and after, shared by moveNetworkNode and offsetPipeRun.
  /// The plan holds the main nodes (and EndZ for keepGrade); zMode "interpolate" fills EndZ for them.</summary>
  private static Dictionary<string, object?> MoveCore(CivilDocument civilDoc, Transaction transaction, PartIndex index, NodePlan plan, string zMode,
    bool includeLinked, bool rimFromSurface, double tolerance, List<CrossingRule> rules, double minClear, Alignment? al)
  {
    var allNets = index.Networks.Values.ToList();
    var (before, _) = ComputeCrossings(transaction, allNets, rules, minClear, false, al);
    var predicted = PredictPipes(plan, index);
    if (zMode == "interpolate")
    {
      InterpolateMainNodes(plan, index, predicted);
      predicted = PredictPipes(plan, index);
    }
    var mainPipes = predicted.Keys.ToHashSet();
    if (includeLinked)
    {
      AddFollowers(plan, index, predicted, tolerance);
      predicted = PredictPipes(plan, index);
    }
    // locked pipes the plan would change (C226): the whole move is refused, nothing is written
    var lockedPipes = predicted.Values.Where(pm => IsLocked(pm.Pipe)
      && (pm.NewStart.DistanceTo(pm.OldStart) > 1e-6 || pm.NewEnd.DistanceTo(pm.OldEnd) > 1e-6)).ToList();
    if (lockedPipes.Count > 0)
    {
      foreach (var pm in lockedPipes) plan.Locked.Add(LockedEntry(pm.Pipe, "move"));
      return new Dictionary<string, object?>
      {
        ["moved"] = new List<object>(), ["linkedMoved"] = new List<object>(), ["pipes"] = new List<object>(), ["locked"] = plan.Locked,
        ["refused"] = $"locked pipes would change: {string.Join(", ", lockedPipes.Select(pm => pm.Network + "/" + pm.Pipe.Name))}", ["warnings"] = plan.Warnings,
      };
    }
    var (structures, pipes) = ApplyPlan(plan, index, predicted, transaction, rimFromSurface);
    var moved = structures.Where(s => plan.Structures.Any(kv => plan.Main.Contains(kv.Key) && kv.Value.Name == (string)s["name"]! && kv.Value.Network == (string)s["network"]!)).ToList();
    var linked = structures.Except(moved).ToList();
    var freeEnds = plan.FreeEnds.Select(kv => new Dictionary<string, object?>
    {
      ["network"] = kv.Value.Network, ["name"] = kv.Value.Name, ["from"] = R3(kv.Value.From), ["to"] = R3(kv.Value.To),
      ["shift"] = Math.Round((kv.Value.To - kv.Value.From).Length, 3), ["how"] = kv.Value.How, ["main"] = plan.Main.Contains(kv.Key.Pipe),
    }).ToList();
    moved.AddRange(freeEnds.Where(f => (bool)f["main"]!));
    linked.AddRange(freeEnds.Where(f => !(bool)f["main"]!));
    var (after, through) = ComputeCrossings(transaction, allNets, rules, minClear, true, al);
    var involved = predicted.Values.Select(pm => $"{pm.Network}/{pm.Pipe.Name}").ToHashSet(StringComparer.Ordinal);
    var crossings = CrossingsInvolving(after, before, involved);
    return new Dictionary<string, object?>
    {
      ["moved"] = moved, ["linkedMoved"] = linked, ["pipes"] = pipes, ["locked"] = plan.Locked, ["warnings"] = plan.Warnings,
      ["crossings"] = crossings, ["newCrossings"] = crossings.Count(c => (bool)c["new"]!),
      ["underRequired"] = crossings.Count(c => !(bool)c["ok"]!),
      ["pipesThroughStructures"] = through.Where(t => involved.Contains((string)t["pipe"]!)).ToList(),
      ["touchedNetworks"] = predicted.Values.Select(pm => pm.Network).Concat(plan.Structures.Values.Select(t => t.Network)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
    };
  }

  // ---------- C231 moveNetworkNode ----------

  /// <summary>C231: moves a node of a network in plan with everything attached: {networks? (default all), structure
  /// ("network/name" or name) | at {x, y} (structures and free pipe ends within tolerance 0.05 of the point), to {x, y} |
  /// dx, dy, z: "keep" (default: every end keeps its elevation) | "interpolate" (a node with two pipes on the straight
  /// grade between their far ends, one pipe keeps its slope), includeLinked=true (structures linked to the node or to a
  /// moved pipe, C199, and free pipe ends of any network on a moved pipe follow, keeping their place along it),
  /// rimFromSurface=true, tolerance?, alignment?, minClearance? (0.10), rules?, profile?, dryRun?}. Pipe ends trimmed to a
  /// structure edge (C190) stay at that distance from the new centre. Locked parts (C226): a locked node or pipe refuses
  /// the move, a locked follower stays. Answer: moved, linkedMoved, pipes (new ends), crossings of the moved pipes after
  /// the move (new: the pair did not cross before; clearanceBefore), newCrossings, underRequired, pipesThroughStructures.
  /// Use case: moveStructures did not follow linked service ends or free joints; project scripts did it in
  /// Python.</summary>
  public static Task<object?> MoveNetworkNodeAsync(JsonObject? parameters)
  {
    var networks = OptionalStringList(parameters, "networks");
    var structureSpec = PluginRuntime.GetOptionalString(parameters, "structure");
    (double X, double Y)? at = PluginRuntime.GetParameter(parameters, "at") is JsonNode an ? ReadXY(an, "at") : null;
    if ((string.IsNullOrWhiteSpace(structureSpec)) == (at == null))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Give structure (network/name or name) or at {x, y}. Nothing was changed.");
    (double X, double Y)? to = PluginRuntime.GetParameter(parameters, "to") is JsonNode tn ? ReadXY(tn, "to") : null;
    var dx = PluginRuntime.GetOptionalDouble(parameters, "dx");
    var dy = PluginRuntime.GetOptionalDouble(parameters, "dy");
    if ((to == null) == (dx == null && dy == null))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Give to {x, y} or dx/dy. Nothing was changed.");
    var zMode = (PluginRuntime.GetOptionalString(parameters, "z") ?? "keep").Trim().ToLowerInvariant();
    if (zMode is not ("keep" or "interpolate"))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "z must be keep or interpolate. Nothing was changed.");
    var includeLinked = PluginRuntime.GetOptionalBool(parameters, "includeLinked") ?? true;
    var rimFromSurface = PluginRuntime.GetOptionalBool(parameters, "rimFromSurface") ?? true;
    var tolerance = PluginRuntime.GetOptionalDouble(parameters, "tolerance") ?? 0.05;
    var dryRun = PluginRuntime.GetOptionalBool(parameters, "dryRun") ?? false;
    var alignmentName = PluginRuntime.GetOptionalString(parameters, "alignment");
    var minClear = PluginRuntime.GetOptionalDouble(parameters, "minClearance") ?? 0.10;
    var rules = ReadCrossingRules(PluginRuntime.GetParameter(parameters, "rules") as JsonArray, "rules");

    return RunWriteOrDry(async () =>
    {
      var answer = await CivilExecution.WriteAsync((doc, civilDoc, database, transaction) =>
      {
        var all = ListPipeNetworkNames(civilDoc, transaction).Where(n => !string.IsNullOrEmpty(n)).ToList();
        var index = new PartIndex(all.Select(n => FindPipeNetworkByName(civilDoc, transaction, n, OpenMode.ForRead)), transaction);
        var scope = networks is { Count: > 0 } ? networks.Select(n => FindPipeNetworkByName(civilDoc, transaction, n, OpenMode.ForRead).Name).ToHashSet(StringComparer.OrdinalIgnoreCase)
          : all.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var plan = new NodePlan();
        Point2d from;
        if (!string.IsNullOrWhiteSpace(structureSpec))
        {
          var (net, s) = ResolveStructure(index, structureSpec!);
          from = XY(s.Location);
          var target = to != null ? new Point2d(to.Value.X, to.Value.Y) : from + new Vector2d(dx ?? 0, dy ?? 0);
          plan.Structures[s.ObjectId] = new NodeTarget(net, s.Name, from, target, "node");
          plan.Main.Add(s.ObjectId);
        }
        else
        {
          from = new Point2d(at!.Value.X, at.Value.Y);
          var delta = to != null ? new Point2d(to.Value.X, to.Value.Y) - from : new Vector2d(dx ?? 0, dy ?? 0);
          foreach (var (net, s) in index.Structures.Where(x => scope.Contains(x.Network) && XY(x.Structure.Location).GetDistanceTo(from) <= tolerance))
          {
            plan.Structures[s.ObjectId] = new NodeTarget(net, s.Name, XY(s.Location), XY(s.Location) + delta, "node");
            plan.Main.Add(s.ObjectId);
          }
          foreach (var (net, p) in index.Pipes.Where(x => scope.Contains(x.Network)))
            foreach (var start in new[] { true, false })
            {
              var sid = start ? p.StartStructureId : p.EndStructureId;
              if (!sid.IsNull && index.Parts.ContainsKey(sid)) continue;
              var q = XY(start ? p.StartPoint : p.EndPoint);
              if (q.GetDistanceTo(from) > tolerance) continue;
              plan.FreeEnds[(p.ObjectId, start)] = new NodeTarget(net, $"{p.Name} {(start ? "start" : "end")}", q, q + delta, "node");
              plan.Main.Add(p.ObjectId);
            }
          if (plan.Structures.Count == 0 && plan.FreeEnds.Count == 0)
            throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"No structure or free pipe end within {tolerance} of ({from.X:F3}, {from.Y:F3}) in {string.Join(", ", scope)}. Nothing was changed.");
        }
        // a locked node, or a node with a locked pipe, refuses the move (C226)
        foreach (var sid in plan.Structures.Keys.ToList())
        {
          var s = (Structure)index.Parts[sid].Part;
          var lp = index.PipesAt(sid).Select(x => x.Pipe).FirstOrDefault(IsLocked);
          if (IsLocked(s) || lp != null)
          {
            var e = LockedEntry(s, "move");
            if (lp != null) e["note"] = $"locked pipe {lp.Name}: {ReadLock(lp)?.Note}";
            plan.Locked.Add(e);
          }
        }
        foreach (var k in plan.FreeEnds.Keys.Where(k => IsLocked(index.Parts[k.Pipe].Part)).ToList())
          plan.Locked.Add(LockedEntry(index.Parts[k.Pipe].Part, "move"));
        Alignment? al = string.IsNullOrWhiteSpace(alignmentName) ? null : CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName!);
        if (plan.Locked.Count > 0)
          return new Dictionary<string, object?> { ["dryRun"] = dryRun, ["moved"] = new List<object>(), ["linkedMoved"] = new List<object>(), ["locked"] = plan.Locked,
            ["refused"] = "the node or one of its pipes is locked" };
        var r = MoveCore(civilDoc, transaction, index, plan, zMode, includeLinked, rimFromSurface, tolerance, rules, minClear, al);
        r["dryRun"] = dryRun;
        r["z"] = zMode;
        return r;
      });
      if (!dryRun && answer.GetValueOrDefault("touchedNetworks") is List<string> touched) await RefreshBands(touched, answer);
      return answer;
    }, dryRun, "moveNetworkNode");
  }

  // ---------- C232 offsetPipeRun ----------

  /// <summary>A run of pipes in order: the nodes (structure id or null for a free end, position) and the pipes between
  /// them, each with whether it runs forward (start at the earlier node).</summary>
  private sealed record NetRun(List<(ObjectId Structure, Point2d At, string Name)> Nodes, List<(Pipe Pipe, bool Forward)> Pipes);

  /// <summary>Orders pipes into one unbranched run. From names: the run starts at the end of firstPipe (when it is an
  /// end of the run); from fromStructure/toStructure: the path between them through the network.</summary>
  private static NetRun OrderRun(Network network, Transaction transaction, List<string>? names, string? fromStructure, string? toStructure, string what)
  {
    var pipes = network.GetPipeIds().Cast<ObjectId>().Select(id => CivilObjectUtils.GetRequiredObject<Pipe>(transaction, id, OpenMode.ForRead)).ToList();
    string Key(Pipe p, bool start) { var sid = start ? p.StartStructureId : p.EndStructureId; return sid.IsNull ? $"free:{p.Handle}:{(start ? "s" : "e")}" : sid.Handle.ToString(); }
    List<Pipe> chosen;
    string? first = null;
    if (names is { Count: > 0 })
    {
      chosen = names.Select(n => pipes.FirstOrDefault(p => string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase))
        ?? throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"{what}: network '{network.Name}' has no pipe '{n}'. Nothing was changed.")).ToList();
    }
    else if (!string.IsNullOrWhiteSpace(fromStructure) && !string.IsNullOrWhiteSpace(toStructure))
    {
      var a = FindStructureByName(network, transaction, fromStructure!, OpenMode.ForRead);
      var b = FindStructureByName(network, transaction, toStructure!, OpenMode.ForRead);
      // breadth-first through the structures
      var prev = new Dictionary<ObjectId, (ObjectId From, Pipe Via)>();
      var queue = new Queue<ObjectId>(); queue.Enqueue(a.ObjectId); prev[a.ObjectId] = (ObjectId.Null, null!);
      while (queue.Count > 0 && !prev.ContainsKey(b.ObjectId))
      {
        var cur = queue.Dequeue();
        foreach (var p in pipes.Where(p => p.StartStructureId == cur || p.EndStructureId == cur))
        {
          var other = p.StartStructureId == cur ? p.EndStructureId : p.StartStructureId;
          if (other.IsNull || prev.ContainsKey(other)) continue;
          prev[other] = (cur, p);
          queue.Enqueue(other);
        }
      }
      if (!prev.ContainsKey(b.ObjectId))
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{what}: no path from {a.Name} to {b.Name} in '{network.Name}'. Nothing was changed.");
      chosen = new List<Pipe>();
      for (var cur = b.ObjectId; cur != a.ObjectId; cur = prev[cur].From) chosen.Add(prev[cur].Via);
      chosen.Reverse();
      first = a.Handle.ToString();
    }
    else throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{what}: give pipes or fromStructure and toStructure. Nothing was changed.");

    var degree = new Dictionary<string, int>();
    foreach (var p in chosen) foreach (var s in new[] { true, false }) { var k = Key(p, s); degree[k] = degree.GetValueOrDefault(k) + 1; }
    if (degree.Values.Any(d => d > 2))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{what}: the pipes branch at {string.Join(", ", degree.Where(kv => kv.Value > 2).Select(kv => kv.Key))}; give one unbranched run. Nothing was changed.");
    var ends = degree.Where(kv => kv.Value == 1).Select(kv => kv.Key).ToList();
    if (ends.Count != 2)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{what}: the pipes are not one connected open run ({ends.Count} ends). Nothing was changed.");
    if (first == null)
    {
      var p0 = chosen[0];
      first = ends.Contains(Key(p0, true)) ? Key(p0, true) : ends.Contains(Key(p0, false)) ? Key(p0, false) : ends[0];
    }
    var nodes = new List<(ObjectId, Point2d, string)>();
    var ordered = new List<(Pipe, bool)>();
    var left = chosen.ToList();
    var curKey = first;
    (ObjectId, Point2d, string) Node(Pipe p, bool start)
    {
      var sid = start ? p.StartStructureId : p.EndStructureId;
      if (sid.IsNull) return (ObjectId.Null, XY(start ? p.StartPoint : p.EndPoint), $"{p.Name} {(start ? "start" : "end")}");
      var s = CivilObjectUtils.GetRequiredObject<Structure>(transaction, sid, OpenMode.ForRead);
      return (sid, XY(s.Location), s.Name);
    }
    while (left.Count > 0)
    {
      var next = left.FirstOrDefault(p => Key(p, true) == curKey || Key(p, false) == curKey)
        ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{what}: the run breaks at {curKey}. Nothing was changed.");
      var forward = Key(next, true) == curKey;
      if (nodes.Count == 0) nodes.Add(Node(next, forward));
      nodes.Add(Node(next, !forward));
      ordered.Add((next, forward));
      curKey = Key(next, !forward);
      left.Remove(next);
    }
    return new NetRun(nodes, ordered);
  }

  /// <summary>The offset of a polyline: each segment shifted by sign x distance along its left normal, the corners
  /// mitred (the two shifted lines intersected; nearly parallel segments keep the shifted point).</summary>
  private static List<Point2d> OffsetPolyline(List<Point2d> pts, double signedDistance)
  {
    Vector2d N(int i) { var v = pts[i + 1] - pts[i]; return new Vector2d(-v.Y, v.X).GetNormal() * signedDistance; }
    var res = new List<Point2d> { pts[0] + N(0) };
    for (var k = 1; k < pts.Count - 1; k++)
    {
      Point2d a1 = pts[k - 1] + N(k - 1), a2 = pts[k] + N(k - 1), b1 = pts[k] + N(k), b2 = pts[k + 1] + N(k);
      Vector2d da = a2 - a1, db = b2 - b1;
      var cross = da.X * db.Y - da.Y * db.X;
      if (Math.Abs(cross) < 1e-9 * da.Length * db.Length) { res.Add(pts[k] + N(k)); continue; }
      var t = ((b1.X - a1.X) * db.Y - (b1.Y - a1.Y) * db.X) / cross;
      res.Add(a1 + da * t);
    }
    res.Add(pts[^1] + N(pts.Count - 2));
    return res;
  }

  /// <summary>A point's place along a polyline: segment index and fraction (the first and last segment extend past
  /// their ends), and its signed offset (left positive).</summary>
  private static (int Seg, double T, double Offset) PlaceOn(List<Point2d> pts, Point2d q)
  {
    var best = (Seg: 0, T: 0.0, Offset: 0.0, D: double.MaxValue);
    for (var i = 0; i < pts.Count - 1; i++)
    {
      var v = pts[i + 1] - pts[i];
      if (v.LengthSqrd < 1e-12) continue;
      var t = (q - pts[i]).DotProduct(v) / v.LengthSqrd;
      var tc = Math.Clamp(t, i == 0 ? double.NegativeInfinity : 0, i == pts.Count - 2 ? double.PositiveInfinity : 1);
      var foot = pts[i] + v * tc;
      var d = foot.GetDistanceTo(q);
      if (d < best.D - 1e-9)
      {
        var off = (v.X * (q.Y - pts[i].Y) - v.Y * (q.X - pts[i].X)) / v.Length;
        best = (i, tc, off, d);
      }
    }
    return (best.Seg, best.T, best.Offset);
  }

  /// <summary>C232: lays a run of pipes parallel to a reference run at a fixed centre-to-centre distance in plan
  /// (Naviate Offset Pipe Network): {networkName, pipes | fromStructure + toStructure, reference: {network, pipes |
  /// fromStructure + toStructure}, distance (m, c/c), side? left|right of the reference's direction (from its first pipe
  /// or fromStructure; default the side the run lies on now), keepGrade=false (true: each pipe keeps its slope, inverts
  /// recomputed from the run's lower end with the new lengths; false: every end keeps its elevation), insertCorners=true
  /// (a corner of the offset line between two nodes of the run becomes a break point, cornerPartName "Brytpunkt", named
  /// "{pipe}-OH{n}"), includeLinked=true, rimFromSurface=true, tolerance (0.05), alignment?, minClearance?, rules?,
  /// profile?, dryRun?}. Each node of the run goes to the offset line at its place along the reference (a node at a
  /// reference corner to the mitred corner). A rerun finds the corners on nodes and changes nothing. Answer: nodes
  /// (from, to, shift), corners (inserted break points), the moveNetworkNode answer (moved, linkedMoved, pipes,
  /// crossings after).</summary>
  public static Task<object?> OffsetPipeRunAsync(JsonObject? parameters, CancellationToken cancellationToken)
  {
    var networkName = PluginRuntime.GetRequiredString(parameters, "networkName");
    var pipeNames = OptionalStringList(parameters, "pipes");
    var fromStructure = PluginRuntime.GetOptionalString(parameters, "fromStructure");
    var toStructure = PluginRuntime.GetOptionalString(parameters, "toStructure");
    if (PluginRuntime.GetParameter(parameters, "reference") is not JsonObject reference || reference["network"]?.GetValue<string>() is not { Length: > 0 } refNetwork)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "reference must be {network, pipes | fromStructure + toStructure}. Nothing was changed.");
    var refPipes = (reference["pipes"] as JsonArray)?.Select(n => n?.GetValue<string>()).OfType<string>().ToList();
    var refFrom = reference["fromStructure"]?.GetValue<string>();
    var refTo = reference["toStructure"]?.GetValue<string>();
    var distance = PluginRuntime.GetRequiredDouble(parameters, "distance");
    if (!(distance > 0) || double.IsInfinity(distance))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "distance must be a centre-to-centre distance > 0 in metres. Nothing was changed.");
    var side = PluginRuntime.GetOptionalString(parameters, "side")?.Trim().ToLowerInvariant();
    if (side is not (null or "left" or "right"))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "side must be left or right (of the reference's direction). Nothing was changed.");
    var keepGrade = PluginRuntime.GetOptionalBool(parameters, "keepGrade") ?? false;
    var insertCorners = PluginRuntime.GetOptionalBool(parameters, "insertCorners") ?? true;
    var cornerPart = PluginRuntime.GetOptionalString(parameters, "cornerPartName") ?? "Brytpunkt";
    var includeLinked = PluginRuntime.GetOptionalBool(parameters, "includeLinked") ?? true;
    var rimFromSurface = PluginRuntime.GetOptionalBool(parameters, "rimFromSurface") ?? true;
    var tolerance = PluginRuntime.GetOptionalDouble(parameters, "tolerance") ?? 0.05;
    var dryRun = PluginRuntime.GetOptionalBool(parameters, "dryRun") ?? false;
    var alignmentName = PluginRuntime.GetOptionalString(parameters, "alignment");
    var minClear = PluginRuntime.GetOptionalDouble(parameters, "minClearance") ?? 0.10;
    var rules = ReadCrossingRules(PluginRuntime.GetParameter(parameters, "rules") as JsonArray, "rules");

    // the run's nodes mapped onto the offset line; corners between two nodes
    (List<Point2d> Ref, List<Point2d> Off, double Sign, List<(int Node, Point2d To)> Targets, List<(Pipe Pipe, int Vertex, Point2d At, Point2d Split)> Corners, NetRun Run)
      Plan(Network net, Network refNet, Transaction transaction, List<string>? names, double? fixedSign = null)
    {
      var run = OrderRun(net, transaction, names, names == null ? fromStructure : null, names == null ? toStructure : null, "the run");
      var refRun = OrderRun(refNet, transaction, refPipes, refFrom, refTo, "reference");
      var pts = refRun.Nodes.Select(n => n.At).ToList();
      double sign;
      if (fixedSign != null) sign = fixedSign.Value;   // the side found before corners were inserted
      else if (side == "left") sign = 1;
      else if (side == "right") sign = -1;
      else
      {
        var mean = run.Nodes.Average(n => PlaceOn(pts, n.At).Offset);
        if (Math.Abs(mean) < 1e-6)
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "The run lies on the reference line: give side (left or right). Nothing was changed.");
        sign = Math.Sign(mean);
      }
      var off = OffsetPolyline(pts, sign * distance);
      var targets = new List<(int, Point2d)>();
      var place = new List<double>();
      for (var j = 0; j < run.Nodes.Count; j++)
      {
        // perpendicular to the reference at the node's foot point (so that a rerun finds the same place), kept between
        // the mitred corners of the offset segment (an inner corner); a node at a reference corner goes to the mitre
        var (seg, t, _) = PlaceOn(pts, run.Nodes[j].At);
        var v = pts[seg + 1] - pts[seg];
        var foot = pts[seg] + v * t;
        var to = foot + new Vector2d(-v.Y, v.X).GetNormal() * (sign * distance);
        var ov = off[seg + 1] - off[seg];
        if (ov.LengthSqrd > 1e-12)
        {
          var u = (to - off[seg]).DotProduct(ov) / ov.LengthSqrd;
          var lo = seg == 0 ? double.NegativeInfinity : 0.0;
          var hi = seg == pts.Count - 2 ? double.PositiveInfinity : 1.0;
          if (u < lo || u > hi) to = off[seg] + ov * Math.Clamp(u, lo, hi);
        }
        // nearest to a reference corner itself (the foot at an inner vertex): the mitred corner
        if (t <= 1e-9 && seg > 0) to = off[seg];
        else if (t >= 1 - 1e-9 && seg < pts.Count - 2) to = off[seg + 1];
        targets.Add((j, to));
        place.Add(seg + t);
      }
      var corners = new List<(Pipe, int, Point2d, Point2d)>();
      for (var j = 0; j < run.Pipes.Count; j++)
      {
        double a = place[j], b = place[j + 1];
        var (lo, hi) = a < b ? (a, b) : (b, a);
        var pipe = run.Pipes[j].Pipe;
        var verts = Enumerable.Range(1, Math.Max(0, pts.Count - 2)).Where(k => k > lo && k < hi)
          .Where(k => off[k].GetDistanceTo(targets[j].Item2) > tolerance && off[k].GetDistanceTo(targets[j + 1].Item2) > tolerance);
        // split where the corner projects onto the pipe as it lies now (the new node then goes to the corner), in order
        // from the pipe's start: the part after a split is a new pipe
        var s2 = XY(pipe.StartPoint); var e2 = XY(pipe.EndPoint);
        foreach (var (k, tt) in verts.Select(k => (k, Math.Clamp(Project(off[k], s2, e2), 0.02, 0.98))).OrderBy(x => x.Item2))
          corners.Add((pipe, k, off[k], Lerp(s2, e2, tt)));
      }
      return (pts, off, sign, targets, corners, run);
    }

    return RunWriteOrDry(async () =>
    {
      // 1. corners: break points first (splitPipe), so that the move below sees them as nodes
      var first = await CivilExecution.ReadAsync((doc, civilDoc, database, transaction) =>
      {
        var net = FindPipeNetworkByName(civilDoc, transaction, networkName, OpenMode.ForRead);
        var refNet = FindPipeNetworkByName(civilDoc, transaction, refNetwork, OpenMode.ForRead);
        var p = Plan(net, refNet, transaction, pipeNames);
        var taken = net.GetPipeIds().Cast<ObjectId>().Concat(net.GetStructureIds().Cast<ObjectId>())
          .Select(id => CivilObjectUtils.GetName(transaction.GetObject(id, OpenMode.ForRead)) ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase);
        return (Corners: p.Corners.Select(c => (Pipe: c.Pipe.Name, c.Vertex, c.At, c.Split, Locked: IsLocked(c.Pipe))).ToList(),
          Pipes: p.Run.Pipes.Select(x => x.Pipe.Name).ToList(), Taken: taken, Sign: p.Sign);
      });
      var inserted = new List<Dictionary<string, object?>>();
      var forced = new Dictionary<string, Point2d>(StringComparer.OrdinalIgnoreCase);
      var runPipes = first.Pipes.ToList();
      var lockedCorners = new List<Dictionary<string, object?>>();
      if (insertCorners)
      {
        // the remaining part of a split pipe is "{pipe}b"; later corners of the same pipe are on that part
        var current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var n = 0;
        foreach (var c in first.Corners)
        {
          if (c.Locked) { lockedCorners.Add(new() { ["name"] = c.Pipe, ["kind"] = "pipe", ["skipped"] = "corner break point" }); continue; }
          var pipe = current.TryGetValue(c.Pipe, out var cur) ? cur : c.Pipe;
          string Free(string stem) { var nm = stem; for (var i = 2; first.Taken.Contains(nm); i++) nm = $"{stem}{i}"; first.Taken.Add(nm); return nm; }
          var bp = Free($"{c.Pipe}-OH{++n}");
          var rest = Free($"{pipe}b");
          var split = await CommandDispatcher.DispatchAsync("splitPipe", new JsonObject
          {
            ["networkName"] = networkName, ["pipe"] = pipe, ["at"] = new JsonObject { ["x"] = c.Split.X, ["y"] = c.Split.Y },
            ["partName"] = cornerPart, ["name"] = bp, ["newPipeName"] = rest,
          }, cancellationToken) as Dictionary<string, object?>;
          current[c.Pipe] = rest;
          runPipes.Insert(runPipes.FindIndex(x => string.Equals(x, pipe, StringComparison.OrdinalIgnoreCase)) + 1, rest);
          forced[bp] = c.At;
          inserted.Add(new() { ["pipe"] = pipe, ["structure"] = split?.GetValueOrDefault("structure") ?? bp, ["newPipe"] = rest, ["corner"] = R3(c.At), ["referenceVertex"] = c.Vertex });
        }
      }
      // 2. the move: every node of the run to the offset line
      var answer = await CivilExecution.WriteAsync((doc, civilDoc, database, transaction) =>
      {
        var net = FindPipeNetworkByName(civilDoc, transaction, networkName, OpenMode.ForRead);
        var refNet = FindPipeNetworkByName(civilDoc, transaction, refNetwork, OpenMode.ForRead);
        var p = Plan(net, refNet, transaction, runPipes, first.Sign);
        var all = ListPipeNetworkNames(civilDoc, transaction).Where(x => !string.IsNullOrEmpty(x)).ToList();
        var index = new PartIndex(all.Select(x => FindPipeNetworkByName(civilDoc, transaction, x, OpenMode.ForRead)), transaction);
        var plan = new NodePlan();
        var nodes = new List<Dictionary<string, object?>>();
        foreach (var (j, mapped) in p.Targets)
        {
          var node = p.Run.Nodes[j];
          var to = forced.TryGetValue(node.Name, out var f) ? f : mapped;
          nodes.Add(new() { ["name"] = node.Name, ["from"] = R3(node.At), ["to"] = R3(to), ["shift"] = Math.Round(to.GetDistanceTo(node.At), 3) });
          if (to.GetDistanceTo(node.At) < 1e-6) continue;
          if (!node.Structure.IsNull)
          {
            plan.Structures[node.Structure] = new NodeTarget(net.Name, node.Name, node.At, to, "offset");
            plan.Main.Add(node.Structure);
          }
          else
          {
            // a free end: the pipe at this end of the run
            var (pipe, forward) = j == 0 ? p.Run.Pipes[0] : p.Run.Pipes[j - 1];
            var start = j == 0 ? forward : !forward;
            plan.FreeEnds[(pipe.ObjectId, start)] = new NodeTarget(net.Name, node.Name, node.At, to, "offset");
            plan.Main.Add(pipe.ObjectId);
          }
        }
        foreach (var (pipe, _) in p.Run.Pipes.Where(x => IsLocked(x.Pipe))) plan.Locked.Add(LockedEntry(pipe, "offset"));
        foreach (var sid in plan.Structures.Keys.Where(id => IsLocked(index.Parts[id].Part))) plan.Locked.Add(LockedEntry(index.Parts[sid].Part, "offset"));
        Alignment? al = string.IsNullOrWhiteSpace(alignmentName) ? null : CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName!);
        Dictionary<string, object?> r;
        if (plan.Locked.Count > 0)
          r = new Dictionary<string, object?> { ["moved"] = new List<object>(), ["linkedMoved"] = new List<object>(), ["locked"] = plan.Locked, ["refused"] = "locked parts in the run" };
        else
        {
          if (keepGrade)
          {
            // each pipe keeps its slope and the drop through each node; from the run's lower end upward
            var predicted = PredictPipes(plan, index);
            var ends = p.Run.Pipes.Select(x => (x.Pipe, x.Forward,
              Near: x.Forward ? x.Pipe.StartPoint : x.Pipe.EndPoint, Far: x.Forward ? x.Pipe.EndPoint : x.Pipe.StartPoint)).ToList();
            double Inv(Point3d q, Pipe pp) => q.Z - pp.InnerDiameterOrWidth / 2;
            var reverse = Inv(ends[^1].Far, ends[^1].Pipe) < Inv(ends[0].Near, ends[0].Pipe);
            var order = reverse ? Enumerable.Range(0, ends.Count).Reverse().ToList() : Enumerable.Range(0, ends.Count).ToList();
            double? prevOld = null, prevNew = null;
            foreach (var i in order)
            {
              var (pipe, forward, near, far) = ends[i];
              // walking from the lower end: "down" is the end met first
              var downAtStart = reverse ? !forward : forward;
              Point3d oldDown = downAtStart ? pipe.StartPoint : pipe.EndPoint, oldUp = downAtStart ? pipe.EndPoint : pipe.StartPoint;
              var pm = predicted.TryGetValue(pipe.ObjectId, out var m) ? m : new PipeMove(pipe, net.Name, pipe.StartPoint, pipe.EndPoint, pipe.StartPoint, pipe.EndPoint);
              Point3d newDown = downAtStart ? pm.NewStart : pm.NewEnd, newUp = downAtStart ? pm.NewEnd : pm.NewStart;
              double lOld = XY(oldDown).GetDistanceTo(XY(oldUp)), lNew = XY(newDown).GetDistanceTo(XY(newUp));
              var rise = lOld < 1e-6 ? 0 : (Inv(oldUp, pipe) - Inv(oldDown, pipe)) / lOld;
              var downInv = prevOld == null ? Inv(oldDown, pipe) : prevNew!.Value + (Inv(oldDown, pipe) - prevOld.Value);
              var upInv = downInv + rise * lNew;
              plan.EndZ[(pipe.ObjectId, downAtStart)] = downInv + pipe.InnerDiameterOrWidth / 2;
              plan.EndZ[(pipe.ObjectId, !downAtStart)] = upInv + pipe.InnerDiameterOrWidth / 2;
              prevOld = Inv(oldUp, pipe);
              prevNew = upInv;
            }
          }
          r = MoveCore(civilDoc, transaction, index, plan, "keep", includeLinked, rimFromSurface, tolerance, rules, minClear, al);
        }
        r["networkName"] = net.Name;
        r["reference"] = refNet.Name;
        r["distance"] = distance;
        r["side"] = p.Sign > 0 ? "left" : "right";
        r["keepGrade"] = keepGrade;
        r["nodes"] = nodes;
        r["dryRun"] = dryRun;
        return r;
      });
      answer["corners"] = inserted;
      answer["cornersFound"] = first.Corners.Count;
      if (!insertCorners && first.Corners.Count > 0)
        answer["cornersNotInserted"] = first.Corners.Select(c => new Dictionary<string, object?> { ["pipe"] = c.Pipe, ["corner"] = R3(c.At) }).ToList();
      if (lockedCorners.Count > 0) answer["lockedCorners"] = lockedCorners;
      if (!dryRun && answer.GetValueOrDefault("touchedNetworks") is List<string> touched) await RefreshBands(touched, answer);
      return answer;
    }, dryRun, "offsetPipeRun");
  }
}
