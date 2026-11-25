using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil;
using Autodesk.Civil.DatabaseServices;
using Autodesk.Civil.DatabaseServices.Styles;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;

namespace Civil3DMcpPlugin;

/// <summary>C191 syncPipeNetwork and C192 addServiceConnection (requested 2026-11-24 for a project as the first two
/// items of the gap analysis). Shared: pipe-end trimming to structure
/// edges (C190), surface elevation, plan crossings with clearance. C224: the declarative spec (polylines, insert,
/// outlets, depth rules; NetworkSpecCommands.cs). C225: inlets, water services, several connections and pruning.
/// C226: locked parts are left as they are.</summary>
public static partial class PipeNetworkCommands
{
  // ---------- C191 syncPipeNetwork ----------

  private sealed record SyncStructure(string Name, string PartName, double? Dia, double X, double Y, double? Rim, double? Sump);

  private sealed record SyncPipe(string Name, string PartName, double? Dia, string? StartStructure, string? EndStructure,
    (double X, double Y)? StartXY, (double X, double Y)? EndXY, double? StartInvert, double? EndInvert, double? StartZ, double? EndZ);
  /// <summary>C191: brings a gravity network to a described state in one transaction, matching parts by name, instead of
  /// erasing and rebuilding it: {networkName, partsList?, referenceSurface?, structures:[{name, partName, structureDiameter?,
  /// x, y, rim? (else the reference surface), sump?, depthBelowSurface?}], pipes:[{name, partName, diameter?,
  /// startStructure?, endStructure?, start?:{x,y}, end?:{x,y} (default the structure centre), startInvert|startZ,
  /// endInvert|endZ | slopePermille | centerDepth, junction?}], deleteMissing? (false), trimToStructures? {radii?,
  /// familyRadii?} (C190 after the sync), dryRun?}. Existing parts keep their style, labels and profile-view parts; a part
  /// whose family or size differs is swapped (SwapPartFamilyAndSize), a moved structure is moved, pipes are reconnected
  /// when their structures change and get the given ends after connecting. The network is created when missing
  /// (partsList needed). Answer: created, updated (with what changed), unchanged, deleted.
  /// C224: polylines [{name, points, partName, diameter, nodePrefix ("{name}-BP"), nodePartName (Brytpunkt),
  /// pipeNameFormat ("{name}-{seg:02d}"), slopePermille | centerDepth, junction}], insert [{pipe | line, at, partName,
  /// name?, structureDiameter?, snap?, newPipeName?}] with snapTolerance (0.30) and nodeTolerance (0.01), outlets
  /// [{structure, invert?}] with dropStraight/dropBend (0; profile: sjalvfall.fall*), bendThresholdDeg (10), junction
  /// invert|centered|crown or crownToCrown (profile: sjalvfall.hjassaMotHjassa); see ExpandSpec and ApplyHeightRules. The
  /// answer adds heights {nodes, pipes} (computed ones), polylines, inserted, branches, warnings.
  /// C226: locked parts (setPartLock) are not changed or deleted; they are listed under locked with what would change.
  /// Use case: in three projects every change erased and rebuilt whole networks, so styles, labels,
  /// profile parts and hand edits were lost and the whole chain had to be rerun.</summary>
  public static Task<object?> SyncPipeNetworkAsync(JsonObject? parameters)
  {
    var networkName = PluginRuntime.GetRequiredString(parameters, "networkName");
    var partsListName = PluginRuntime.GetOptionalString(parameters, "partsList");
    var surfaceName = PluginRuntime.GetOptionalString(parameters, "referenceSurface");
    var dryRun = PluginRuntime.GetOptionalBool(parameters, "dryRun") ?? false;
    var deleteMissing = PluginRuntime.GetOptionalBool(parameters, "deleteMissing") ?? false;
    var trim = PluginRuntime.GetParameter(parameters, "trimToStructures") as JsonObject;
    var byName = trim == null ? new() : RadiusMap(trim, "radii");
    var byFamily = trim == null ? new() : RadiusMap(trim, "familyRadii");
    var spec = ParseSpec(parameters);

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      Network? network = null;
      try { network = FindPipeNetworkByName(civilDoc, transaction, networkName, dryRun ? OpenMode.ForRead : OpenMode.ForWrite); }
      catch (JsonRpcDispatchException) { }
      if (network == null && string.IsNullOrWhiteSpace(partsListName))
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Network '{networkName}' does not exist; give partsList to create it. Nothing was changed.");
      var givenSurface = string.IsNullOrWhiteSpace(surfaceName) ? null : CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, surfaceName!, OpenMode.ForRead);
      var surface = givenSurface ?? (network == null ? null : NetworkSurface(network, transaction));

      // C224: the spec expanded and its heights computed before anything is written
      var exS = network == null ? new Dictionary<string, Structure>(StringComparer.OrdinalIgnoreCase)
        : network.GetStructureIds().Cast<ObjectId>().Select(id => CivilObjectUtils.GetRequiredObject<Structure>(transaction, id, OpenMode.ForRead))
          .GroupBy(s => s.Name ?? "", StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
      var exP = network == null ? new Dictionary<string, Pipe>(StringComparer.OrdinalIgnoreCase)
        : network.GetPipeIds().Cast<ObjectId>().Select(id => CivilObjectUtils.GetRequiredObject<Pipe>(transaction, id, OpenMode.ForRead))
          .GroupBy(p => p.Name ?? "", StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
      ExpandSpec(spec, n => exS.TryGetValue(n, out var s) ? (s.Location.X, s.Location.Y) : null);
      var innerCache = new Dictionary<string, double>();
      double Inner(SpecPipe p)
      {
        var key = $"{p.PartName}|{p.Dia}";
        if (innerCache.TryGetValue(key, out var v)) return v;
        v = (p.Dia ?? 0) / 1000;
        if (network != null)
          try
          {
            var ids = FindPartForNetwork(network, transaction, p.PartName, DomainType.Pipe, p.Dia);
            var ps = CivilObjectUtils.GetRequiredObject<PartSize>(transaction, ids.SizeId, OpenMode.ForRead);
            using var rec = ps.SizeDataRecord;
            var (pi, _, _) = PipeDiameters(rec);
            if (pi is > 0) v = pi.Value / 1000;
          }
          catch (JsonRpcDispatchException) { if (exP.TryGetValue(p.Name, out var ep)) v = ep.InnerDiameterOrWidth; }
        return innerCache[key] = v;
      }
      var heights = ApplyHeightRules(spec, surface,
        n => exS.TryGetValue(n, out var s) ? (s.Location.X, s.Location.Y, s.RimElevation, s.PartFamilyName ?? "") : null, Inner);
      foreach (var dup in spec.Structures.Select(s => s.Name).Concat(spec.Pipes.Select(p => p.Name)).GroupBy(n => n, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Name '{dup.Key}' is given twice. Nothing was changed.");
      var specXY = spec.Structures.ToDictionary(s => s.Name, s => (s.X, s.Y), StringComparer.OrdinalIgnoreCase);
      (double X, double Y)? At(string? st, (double X, double Y)? xy) => xy ?? (st != null && specXY.TryGetValue(st, out var q) ? q : null);
      var sIn = spec.Structures.Select(s => new SyncStructure(s.Name, s.PartName, s.Dia, s.X, s.Y, s.Rim, s.Sump)).ToList();
      var pIn = spec.Pipes.Select(p => new SyncPipe(p.Name, p.PartName, p.Dia, p.StartStructure, p.EndStructure, At(p.StartStructure, p.StartXY),
        At(p.EndStructure, p.EndXY), p.StartInvert, p.EndInvert, p.StartZ, p.EndZ)).ToList();

      Dictionary<string, object?> Extras(Dictionary<string, object?> a)
      {
        if (heights.Nodes.Count > 0 || heights.Pipes.Count > 0)
          a["heights"] = new Dictionary<string, object?> { ["nodes"] = heights.Nodes, ["pipes"] = heights.Pipes };
        if (spec.Lines.Count > 0) a["polylines"] = spec.LineReport;
        if (spec.Inserted.Count > 0) a["inserted"] = spec.Inserted;
        if (spec.Branches.Count > 0) a["branches"] = spec.Branches;
        if (spec.Warnings.Count > 0) a["warnings"] = spec.Warnings;
        return a;
      }
      if (network == null && dryRun)
        return Extras(new Dictionary<string, object?> { ["networkName"] = networkName, ["dryRun"] = true, ["createsNetwork"] = true,
          ["created"] = sIn.Select(s => s.Name).Concat(pIn.Select(p => p.Name)).ToList() });
      var createdNetwork = false;
      if (network == null)
      {
        network = CivilObjectUtils.GetRequiredObject<Network>(transaction, CreatePipeNetwork(civilDoc, networkName), OpenMode.ForWrite);
        network.PartsListId = FindPartsListId(civilDoc, transaction, partsListName!);
        createdNetwork = true;
      }
      if (givenSurface != null && !dryRun && ReferenceSurfaceOrNull(network) != givenSurface.ObjectId) network.ReferenceSurfaceId = givenSurface.ObjectId;
      var core = SyncCore(network, transaction, surface, sIn, pIn, deleteMissing ? (_ => true) : null, dryRun);
      var (created, updated, unchanged, deleted, syncedPipes) = (core.Created, core.Updated, core.Unchanged, core.Deleted, core.Synced);
      List<Dictionary<string, object?>>? trimmed = null;
      if (trim != null && !dryRun)
      {
        var all = network.GetStructureIds().Cast<ObjectId>().Select(id => CivilObjectUtils.GetRequiredObject<Structure>(transaction, id, OpenMode.ForRead))
          .ToDictionary(s => s.ObjectId);
        trimmed = TrimPipeEnds(all, syncedPipes, RadiusRule(byName, byFamily), false);
      }
      return Extras(new Dictionary<string, object?>
      {
        ["networkName"] = network.Name, ["dryRun"] = dryRun, ["createdNetwork"] = createdNetwork,
        ["created"] = created, ["updated"] = updated, ["unchanged"] = unchanged, ["deleted"] = deleted, ["locked"] = core.Locked,
        ["trimmed"] = trimmed?.Count(t => t.TryGetValue("moved", out var m) && m is double v && v > 1e-6),
        ["counts"] = new Dictionary<string, int> { ["created"] = created.Count, ["updated"] = updated.Count, ["unchanged"] = unchanged.Count,
          ["deleted"] = deleted.Count, ["locked"] = core.Locked.Count },
      });
    });
  }

  private sealed record SyncResult(List<string> Created, List<Dictionary<string, object?>> Updated, List<string> Unchanged,
    List<string> Deleted, List<Pipe> Synced, List<Dictionary<string, object?>> Locked);
  /// <summary>C191 core, shared with C192: brings the named parts of a network to the given state. deleteScope: parts not
  /// given whose name it accepts are erased (null: nothing erased). C226: locked parts are compared but not changed,
  /// not reconnected and not erased; they are reported under Locked.</summary>
  private static SyncResult SyncCore(Network network, Transaction transaction, CivilSurface? surface, List<SyncStructure> sIn,
    List<SyncPipe> pIn, Func<string, bool>? deleteScope, bool dryRun)
  {
    var sNames = new HashSet<string>(sIn.Select(s => s.Name), StringComparer.OrdinalIgnoreCase);
      var mode = dryRun ? OpenMode.ForRead : OpenMode.ForWrite;
      var exS = network.GetStructureIds().Cast<ObjectId>().Select(id => CivilObjectUtils.GetRequiredObject<Structure>(transaction, id, OpenMode.ForRead))
        .ToDictionary(s => s.Name ?? "", StringComparer.OrdinalIgnoreCase);
      var exP = network.GetPipeIds().Cast<ObjectId>().Select(id => CivilObjectUtils.GetRequiredObject<Pipe>(transaction, id, OpenMode.ForRead))
        .ToDictionary(p => p.Name ?? "", StringComparer.OrdinalIgnoreCase);
      var lockedIds = new HashSet<ObjectId>(exS.Values.Cast<Part>().Concat(exP.Values).Where(IsLocked).Select(p => p.ObjectId));
      // unlocked parts are opened for write (locked ones stay read-only, so nothing can change them by mistake)
      if (!dryRun)
      {
        foreach (var k in exS.Keys.ToList()) if (!lockedIds.Contains(exS[k].ObjectId)) exS[k] = CivilObjectUtils.GetRequiredObject<Structure>(transaction, exS[k].ObjectId, mode);
        foreach (var k in exP.Keys.ToList()) if (!lockedIds.Contains(exP[k].ObjectId)) exP[k] = CivilObjectUtils.GetRequiredObject<Pipe>(transaction, exP[k].ObjectId, mode);
      }
      foreach (var p in pIn)
        foreach (var sn in new[] { p.StartStructure, p.EndStructure })
          if (sn != null && !sNames.Contains(sn) && !exS.ContainsKey(sn))
            throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Pipe '{p.Name}' refers to structure '{sn}', which is neither given nor in '{network.Name}'. Nothing was changed.");

      // lowest invert of the connected pipe ends per structure: Civil keeps the sump at or below it
      var lowInvert = new Dictionary<ObjectId, double>();
      foreach (var ep in exP.Values)
        foreach (var (sid, z) in new[] { (ep.StartStructureId, ep.StartPoint.Z), (ep.EndStructureId, ep.EndPoint.Z) })
        {
          if (sid.IsNull) continue;
          var inv = z - ep.InnerDiameterOrWidth / 2;
          lowInvert[sid] = lowInvert.TryGetValue(sid, out var cur) ? Math.Min(cur, inv) : inv;
        }
      var created = new List<string>();
      var updated = new List<Dictionary<string, object?>>();
      var unchanged = new List<string>();
      var deleted = new List<string>();
      var locked = new List<Dictionary<string, object?>>();
      var structById = new Dictionary<string, Structure>(StringComparer.OrdinalIgnoreCase);

      foreach (var s in sIn)
      {
        var rim = s.Rim ?? SurfaceZ(surface, s.X, s.Y)
          ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Structure '{s.Name}': no rim given and no surface elevation at ({s.X:F2}, {s.Y:F2}). Nothing was changed.");
        var part = FindPartForNetwork(network, transaction, s.PartName, DomainType.Structure, s.Dia);
        if (exS.TryGetValue(s.Name, out var st))
        {
          var isLocked = lockedIds.Contains(st.ObjectId);
          var write = !dryRun && !isLocked;
          var what = new List<string>();
          var famSize = !string.Equals(st.PartFamilyName, s.PartName, StringComparison.OrdinalIgnoreCase)
            || (s.Dia != null && Math.Abs(st.InnerDiameterOrWidth * 1000 - s.Dia.Value) > 0.5 && Math.Abs(GetOuter(st) * 1000 - s.Dia.Value) > 0.5);
          if (famSize) { what.Add("part"); if (write) st.SwapPartFamilyAndSize(part.FamilyId, part.SizeId); }
          if (new Point2d(st.Location.X, st.Location.Y).GetDistanceTo(new Point2d(s.X, s.Y)) > 0.001)
          { what.Add("location"); if (write) st.Location = new Point3d(s.X, s.Y, st.Location.Z); }
          if (Math.Abs(st.RimElevation - rim) > 0.0005 || st.AutomaticRimSurfaceAdjustment)
          { what.Add("rim"); if (write) { st.AutomaticRimSurfaceAdjustment = false; st.RimElevation = rim; } }
          // Civil lowers the sump to the lowest connected pipe invert (at the wall when trimmed, C190), so the sump counts as
          // unchanged when it equals min(wanted sump, lowest invert)
          var sumpExpected = s.Sump == null ? (double?)null
            : lowInvert.TryGetValue(st.ObjectId, out var li) ? Math.Min(s.Sump.Value, li) : s.Sump.Value;
          if (s.Sump != null && Math.Abs(st.SumpElevation - sumpExpected!.Value) > 0.002 && Math.Abs(st.SumpElevation - s.Sump.Value) > 0.002)
          { what.Add("sump"); if (write) { st.ControlSumpBy = StructureControlSumpType.ByElevation; st.SumpElevation = s.Sump.Value; } }
          if (isLocked) locked.Add(LockedEntry(st, "sync", what));
          else if (what.Count > 0) updated.Add(new Dictionary<string, object?> { ["name"] = s.Name, ["kind"] = "structure", ["changed"] = what });
          else unchanged.Add(s.Name);
          structById[s.Name] = st;
        }
        else
        {
          created.Add(s.Name);
          if (dryRun) continue;
          var id = AddStructureToNetwork(network, transaction, new Point3d(s.X, s.Y, rim), s.PartName, rim, 0.0, s.Dia);
          var ns = CivilObjectUtils.GetRequiredObject<Structure>(transaction, id, OpenMode.ForWrite);
          ns.Name = s.Name;
          ns.AutomaticRimSurfaceAdjustment = false;
          ns.RimElevation = rim;
          if (s.Sump != null) { ns.ControlSumpBy = StructureControlSumpType.ByElevation; ns.SumpElevation = s.Sump.Value; }
          structById[s.Name] = ns;
        }
      }
      Structure? S(string? n) => n == null ? null : structById.TryGetValue(n, out var x) ? x : exS.TryGetValue(n, out x) ? x : null;

      var syncedPipes = new List<Pipe>();
      foreach (var p in pIn)
      {
        var ss = S(p.StartStructure);
        var es = S(p.EndStructure);
        if (dryRun && ((p.StartStructure != null && ss == null) || (p.EndStructure != null && es == null)))
        { if (!exP.ContainsKey(p.Name)) created.Add(p.Name); else updated.Add(new() { ["name"] = p.Name, ["kind"] = "pipe", ["changed"] = new[] { "structure (new)" } }); continue; }
        var sxy = p.StartXY ?? (ss!.Location.X, ss.Location.Y);
        var exy = p.EndXY ?? (es!.Location.X, es.Location.Y);
        var part = FindPartForNetwork(network, transaction, p.PartName, DomainType.Pipe, p.Dia);
        Pipe pipe;
        var what = new List<string>();
        var isLocked = exP.TryGetValue(p.Name, out var lp) && lockedIds.Contains(lp.ObjectId);
        var write = !dryRun && !isLocked;
        if (exP.TryGetValue(p.Name, out var ep))
        {
          pipe = ep;
          var famSize = !string.Equals(pipe.PartFamilyName, p.PartName, StringComparison.OrdinalIgnoreCase)
            || (p.Dia != null && Math.Abs(pipe.InnerDiameterOrWidth * 1000 - p.Dia.Value) > 0.5);
          if (famSize) { what.Add("part"); if (write) pipe.SwapPartFamilyAndSize(part.FamilyId, part.SizeId); }
          var wantS = ss?.ObjectId ?? ObjectId.Null;
          var wantE = es?.ObjectId ?? ObjectId.Null;
          if (pipe.StartStructureId != wantS)
          {
            what.Add("startStructure");
            if (write) { if (!pipe.StartStructureId.IsNull) pipe.Disconnect(ConnectorPositionType.Start); if (!wantS.IsNull) pipe.ConnectToStructure(ConnectorPositionType.Start, wantS, true); }
          }
          if (pipe.EndStructureId != wantE)
          {
            what.Add("endStructure");
            if (write) { if (!pipe.EndStructureId.IsNull) pipe.Disconnect(ConnectorPositionType.End); if (!wantE.IsNull) pipe.ConnectToStructure(ConnectorPositionType.End, wantE, true); }
          }
        }
        else
        {
          created.Add(p.Name);
          if (dryRun) continue;
          // created at its height: a pipe created at z 0 drags the connected structures' sumps down (live)
          var r0 = (p.Dia ?? 0) / 2000;
          var id = AddPipeToNetwork(network, transaction, p.PartName, p.Dia, new Point3d(sxy.X, sxy.Y, p.StartZ ?? p.StartInvert!.Value + r0),
            new Point3d(exy.X, exy.Y, p.EndZ ?? p.EndInvert!.Value + r0),
            ss?.ObjectId ?? ObjectId.Null, es?.ObjectId ?? ObjectId.Null);
          pipe = CivilObjectUtils.GetRequiredObject<Pipe>(transaction, id, OpenMode.ForWrite);
          pipe.Name = p.Name;
        }
        var r = (dryRun ? (p.Dia ?? pipe.InnerDiameterOrWidth * 1000) / 1000 : pipe.InnerDiameterOrWidth) / 2;
        if (dryRun && exP.TryGetValue(p.Name, out var dp) && !what.Contains("part")) r = dp.InnerDiameterOrWidth / 2;
        var s1 = new Point3d(sxy.X, sxy.Y, p.StartZ ?? p.StartInvert!.Value + r);
        var e1 = new Point3d(exy.X, exy.Y, p.EndZ ?? p.EndInvert!.Value + r);
        // an existing pipe is unchanged when its ends lie on the wanted line at the wanted grade, at most the structure
        // radius from the wanted end (it may have been trimmed to the structure edge, C190)
        bool Off(Point3d now, Point3d want, Structure? st)
        {
          var a2 = new Point2d(s1.X, s1.Y);
          var v = new Point2d(e1.X, e1.Y) - a2;
          var n2 = new Point2d(now.X, now.Y);
          var t = v.LengthSqrd < 1e-12 ? 0 : (n2 - a2).DotProduct(v) / v.LengthSqrd;
          var perp = (a2 + v * t).GetDistanceTo(n2);
          var zExp = s1.Z + (e1.Z - s1.Z) * t;
          var allowed = st == null ? 0.0005 : st.InnerDiameterOrWidth / 2 + 0.002;
          return perp > 0.001 || Math.Abs(now.Z - zExp) > 0.0005 || n2.GetDistanceTo(new Point2d(want.X, want.Y)) > allowed;
        }
        var isNew = !exP.ContainsKey(p.Name);
        var geomOff = isNew || what.Count > 0 || Off(pipe.StartPoint, s1, ss) || Off(pipe.EndPoint, e1, es);
        if (!isNew && geomOff && !what.Contains("startStructure") && !what.Contains("endStructure") && !what.Contains("part")) what.Add("geometry");
        if (write && geomOff)
        {
          // ends after connecting: ConnectToStructure(force) may move them (C176)
          if (pipe.StartPoint.DistanceTo(s1) > 1e-6) pipe.StartPoint = s1;
          if (pipe.EndPoint.DistanceTo(e1) > 1e-6) pipe.EndPoint = e1;
          syncedPipes.Add(pipe);
        }
        if (exP.ContainsKey(p.Name))
        {
          if (isLocked) locked.Add(LockedEntry(pipe, "sync", what));
          else if (what.Count > 0) updated.Add(new Dictionary<string, object?> { ["name"] = p.Name, ["kind"] = "pipe", ["changed"] = what });
          else unchanged.Add(p.Name);
        }
      }

      // sumps again after the pipes: min(wanted, lowest connected invert)
      if (!dryRun)
        foreach (var sw in sIn.Where(x => x.Sump != null))
        {
          var st = S(sw.Name);
          if (st == null || lockedIds.Contains(st.ObjectId)) continue;
          var low = network.GetPipeIds().Cast<ObjectId>().Select(id => CivilObjectUtils.GetRequiredObject<Pipe>(transaction, id, OpenMode.ForRead))
            .SelectMany(pp => new[] { (pp.StartStructureId, pp.StartPoint.Z - pp.InnerDiameterOrWidth / 2), (pp.EndStructureId, pp.EndPoint.Z - pp.InnerDiameterOrWidth / 2) })
            .Where(t => t.Item1 == st.ObjectId).Select(t => t.Item2).DefaultIfEmpty(double.MaxValue).Min();
          var want = Math.Min(sw.Sump!.Value, low);
          if (Math.Abs(st.SumpElevation - want) > 0.0005)
          {
            if (!st.IsWriteEnabled) st.UpgradeOpen();
            st.ControlSumpBy = StructureControlSumpType.ByElevation;
            st.SumpElevation = want;
          }
        }
      if (deleteScope != null)
      {
        var keep = new HashSet<string>(pIn.Select(p => p.Name).Concat(sNames), StringComparer.OrdinalIgnoreCase);
        foreach (var (n, p) in exP)
          if (!keep.Contains(n) && deleteScope(n))
          {
            if (lockedIds.Contains(p.ObjectId)) { locked.Add(LockedEntry(p, "delete")); continue; }
            deleted.Add(n); if (!dryRun) p.Erase();
          }
        foreach (var (n, s) in exS)
          if (!keep.Contains(n) && deleteScope(n))
          {
            if (lockedIds.Contains(s.ObjectId)) { locked.Add(LockedEntry(s, "delete")); continue; }
            deleted.Add(n); if (!dryRun) s.Erase();
          }
      }

    return new SyncResult(created, updated, unchanged, deleted, syncedPipes, locked);
  }

  // ---------- C192 addServiceConnection ----------

  /// <summary>C192: a service or gully connection from a host network, computed and placed in a service network in one
  /// call: {networkName (service network; partsList? to create it, referenceSurface? else the host's), name, hostNetwork,
  /// hostStructure? (into that structure) | hostPipe? | nearest host pipe, at?:{x,y} (point projected on the host pipe;
  /// default the end point's projection), end:{x, y, partName="Rensbrunn", structureDiameter?, name?, rim?, sumpDepth=0},
  /// continueTo?:{x,y} (on to the existing service), partName="Markrör PP", diameter=160, slopePermille=10,
  /// invertRule="centered"|"bottom", endBelowRim? (gully: end invert = max(rim - endBelowRim, host invert + slope x L)),
  /// avoid?:[{network, clearance=0.10, kneeAfter=0.35}], check?:[networks] (default all other networks), trim=true, dryRun?}.
  /// Centered: the service's centreline meets the host's (invert = host bottom + (D - d)/2); into a structure: its sump +
  /// (largest connected pipe - d)/2. When the line to the host crosses an avoid network without clearance underneath, the
  /// service goes over it at slopePermille to a knee kneeAfter past the crossing and then down to the host. Parts are named
  /// "{name}-1", "{name}-2" (after a knee), "{name}-bef", "{name} ans", "{name} knä", "{name} bef" and the end structure
  /// (end.name or name); a rerun updates them in place (C191 core) and erases parts of the same name stem no longer needed.
  /// Answer: host point and invert, end rim/invert/depth, knee, lengths, crossings with clearance.
  /// C225: kind "water" (a pressure service: Tryckrör PE 63 at slope 0 centre to centre to a Ventil; profile
  /// servis.vServis), kind "inlet" (an existing pipe into a new structure: {hostStructure, from:{x,y}, invert}: a stub
  /// "{name}" from "{name} bef" at from, invert + slope x the distance to the structure centre, to "{name} ans" on the
  /// structure's edge 5 mm inside at invert, linked to the structure), connections:[{...}] (several in one transaction; each
  /// item takes the top-level values it leaves out) and pruneOthers: true (parts of the touched service networks that
  /// belong to none of the connections are erased; locked parts stay). C226: locked parts are left as they are.
  /// Use case: the same service geometry was computed in Python in 3-4 copies per project (the project scripts);
  /// requested 2026-11-24 as one of the first two items of the gap analysis.
  /// C225 use case: a project script built the inlets with syncPipeNetwork + linkNetworkPart and erased expired service
  /// parts by name stems.</summary>
  public static Task<object?> AddServiceConnectionAsync(JsonObject? parameters)
  {
    var dryRun = PluginRuntime.GetOptionalBool(parameters, "dryRun") ?? false;
    var prune = PluginRuntime.GetOptionalBool(parameters, "pruneOthers") ?? false;
    var list = PluginRuntime.GetParameter(parameters, "connections") as JsonArray;
    var items = new List<JsonObject>();
    if (list != null)
    {
      if (list.Count == 0)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "connections is empty. Nothing was changed.");
      for (var i = 0; i < list.Count; i++)
      {
        if (list[i] is not JsonObject item)
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"connections[{i}] must be an object. Nothing was changed.");
        items.Add(MergeConnection(parameters, item));
      }
    }
    else items.Add(MergeConnection(parameters, null));
    var parsed = items.Select((it, i) => ParseConnection(it, list == null ? "" : $"connections[{i}]: ")).ToList();
    foreach (var dup in parsed.GroupBy(c => (c.NetworkName.ToLowerInvariant(), c.Name.ToLowerInvariant())).Where(g => g.Count() > 1))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Connection '{dup.Key.Item2}' in '{dup.Key.Item1}' is given twice. Nothing was changed.");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var answers = new List<Dictionary<string, object?>>();
      var locked = new List<Dictionary<string, object?>>();
      foreach (var c in parsed)
      {
        var a = c.Kind == "inlet" ? InletCore(c, civilDoc, database, transaction, dryRun) : ServiceCore(c, civilDoc, database, transaction, dryRun);
        if (a.TryGetValue("locked", out var l) && l is List<Dictionary<string, object?>> ll) locked.AddRange(ll);
        answers.Add(a);
      }
      var pruned = new List<Dictionary<string, object?>>();
      if (prune)
        foreach (var g in parsed.GroupBy(c => c.NetworkName, StringComparer.OrdinalIgnoreCase))
        {
          Network net;
          try { net = FindPipeNetworkByName(civilDoc, transaction, g.Key, OpenMode.ForRead); }
          catch (JsonRpcDispatchException) { continue; }   // dry run of a network not created yet
          var stems = g.Select(c => c.Name).ToList();
          bool Belongs(string n) => stems.Any(s => string.Equals(n, s, StringComparison.OrdinalIgnoreCase) || n.StartsWith(s + "-", StringComparison.OrdinalIgnoreCase)
                                                   || n.StartsWith(s + " ", StringComparison.OrdinalIgnoreCase));
          var names = new List<string>();
          // pipes first, then structures (a structure with a pipe left would keep it connected)
          foreach (var id in net.GetPipeIds().Cast<ObjectId>().Concat(net.GetStructureIds().Cast<ObjectId>()).ToList())
          {
            var part = (Part)transaction.GetObject(id, OpenMode.ForRead);
            if (Belongs(part.Name ?? "")) continue;
            if (IsLocked(part)) { locked.Add(LockedEntry(part, "prune")); continue; }
            names.Add(part.Name ?? "");
            if (!dryRun) { part.UpgradeOpen(); part.Erase(); }
          }
          pruned.Add(new Dictionary<string, object?> { ["network"] = net.Name, ["erased"] = names, ["count"] = names.Count });
        }
      if (list == null)
      {
        var a = answers[0];
        if (prune) a["pruned"] = pruned;
        a["locked"] = locked;
        return a;
      }
      return new Dictionary<string, object?>
      {
        ["dryRun"] = dryRun, ["connections"] = answers, ["pruned"] = prune ? pruned : null, ["locked"] = locked,
        ["counts"] = new Dictionary<string, int>
        {
          ["connections"] = answers.Count,
          ["created"] = answers.Sum(a => (a.GetValueOrDefault("created") as List<string>)?.Count ?? 0),
          ["updated"] = answers.Sum(a => (a.GetValueOrDefault("updated") as List<Dictionary<string, object?>>)?.Count ?? 0),
          ["deleted"] = answers.Sum(a => (a.GetValueOrDefault("deleted") as List<string>)?.Count ?? 0),
          ["pruned"] = pruned.Sum(p => (int)p["count"]!), ["locked"] = locked.Count,
        },
      };
    });
  }

  /// <summary>One connection's parameters: the top level without connections/pruneOthers/dryRun, the item's own keys
  /// on top (the item wins).</summary>
  private static JsonObject MergeConnection(JsonObject? top, JsonObject? item)
  {
    var m = new JsonObject();
    if (top != null)
      foreach (var (k, v) in top)
        if (k is not ("connections" or "pruneOthers" or "dryRun")) m[k] = v?.DeepClone();
    if (item != null)
      foreach (var (k, v) in item) m[k] = v?.DeepClone();
    return m;
  }

  private sealed record ServiceSpec(string Kind, string NetworkName, string Name, string HostName, string? HostStructure, string? HostPipe,
    string? PartsList, string? Surface, string PipePart, double Dia, double Slope, string Rule, double? EndBelowRim, bool Trim,
    (double X, double Y)? End, string EndPart, double? EndDia, string EndName, double? EndRim, double SumpDepth, (double X, double Y)? ContinueTo,
    (double X, double Y)? At, List<(string Network, double Clearance, double KneeAfter)> Avoid, List<string>? Check, (double X, double Y)? From, double? Invert);

  private static ServiceSpec ParseConnection(JsonObject p, string pre)
  {
    var kind = (PluginRuntime.GetOptionalString(p, "kind") ?? "service").Trim().ToLowerInvariant();
    if (kind is not ("service" or "gully" or "water" or "inlet"))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{pre}kind must be service, gully, water or inlet. Nothing was changed.");
    var water = kind == "water";
    var networkName = PluginRuntime.GetRequiredString(p, "networkName");
    var name = PluginRuntime.GetRequiredString(p, "name").Trim();
    var hostName = PluginRuntime.GetRequiredString(p, "hostNetwork");
    var rule = (PluginRuntime.GetOptionalString(p, "invertRule") ?? "centered").ToLowerInvariant();
    if (rule != "centered" && rule != "bottom")
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{pre}invertRule must be centered or bottom. Nothing was changed.");
    var avoid = ((PluginRuntime.GetParameter(p, "avoid") as JsonArray) ?? new JsonArray()).Select((n, i) =>
    {
      if (n is not JsonObject o || o["network"]?.GetValue<string>() is not string nw)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{pre}avoid[{i}] needs network. Nothing was changed.");
      return (Network: nw, Clearance: o["clearance"]?.GetValue<double>() ?? 0.10, KneeAfter: o["kneeAfter"]?.GetValue<double>() ?? 0.35);
    }).ToList();
    (double X, double Y)? end = null;
    string endPart = water ? "Ventil" : "Rensbrunn";
    double? endDia = null, endRim = null;
    var endName = name;
    var sumpDepth = 0.0;
    (double X, double Y)? from = null;
    double? invert = null;
    var hostStructure = PluginRuntime.GetOptionalString(p, "hostStructure");
    if (kind == "inlet")
    {
      if (string.IsNullOrWhiteSpace(hostStructure))
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{pre}kind inlet needs hostStructure (the new structure the existing pipe runs into). Nothing was changed.");
      from = PluginRuntime.GetParameter(p, "from") is JsonNode fn ? ReadXY(fn, pre + "from")
        : throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{pre}kind inlet needs from {{x, y}} (a point on the existing pipe). Nothing was changed.");
      invert = PluginRuntime.GetOptionalDouble(p, "invert")
        ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{pre}kind inlet needs invert (the existing pipe's invert at the structure). Nothing was changed.");
    }
    else
    {
      if (PluginRuntime.GetParameter(p, "end") is not JsonObject endO)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{pre}end {{x, y, partName?, structureDiameter?, name?, rim?}} is required. Nothing was changed.");
      end = ReadXY(endO, pre + "end");
      endPart = endO["partName"]?.GetValue<string>() ?? endPart;
      endDia = endO["structureDiameter"]?.GetValue<double>() ?? (endPart.Equals("Rensbrunn", StringComparison.OrdinalIgnoreCase) ? 200.0 : (double?)null);
      endName = endO["name"]?.GetValue<string>() ?? name;
      endRim = endO["rim"]?.GetValue<double>();
      sumpDepth = endO["sumpDepth"]?.GetValue<double>() ?? 0.0;
    }
    return new ServiceSpec(kind, networkName, name, hostName, hostStructure, PluginRuntime.GetOptionalString(p, "hostPipe"),
      PluginRuntime.GetOptionalString(p, "partsList"), PluginRuntime.GetOptionalString(p, "referenceSurface"),
      PluginRuntime.GetOptionalString(p, "partName") ?? (water ? "Tryckrör PE" : "Markrör PP"),
      PluginRuntime.GetOptionalDouble(p, "diameter") ?? (water ? 63.0 : 160.0),
      (PluginRuntime.GetOptionalDouble(p, "slopePermille") ?? (water ? 0.0 : 10.0)) / 1000.0, rule,
      PluginRuntime.GetOptionalDouble(p, "endBelowRim"), PluginRuntime.GetOptionalBool(p, "trim") ?? true,
      end, endPart, endDia, endName, endRim, sumpDepth,
      PluginRuntime.GetParameter(p, "continueTo") is JsonNode cn ? ReadXY(cn, pre + "continueTo") : null,
      PluginRuntime.GetParameter(p, "at") is JsonNode an ? ReadXY(an, pre + "at") : null,
      avoid, OptionalStringList(p, "check"), from, invert);
  }

  /// <summary>The service network, created when missing (not in a dry run), and its surface.</summary>
  private static (Network? Net, CivilSurface? Surface, bool Created) ServiceNetwork(ServiceSpec c, Autodesk.Civil.ApplicationServices.CivilDocument civilDoc, Transaction transaction, Network host, bool dryRun)
  {
    Network? net = null;
    try { net = FindPipeNetworkByName(civilDoc, transaction, c.NetworkName, OpenMode.ForRead); }
    catch (JsonRpcDispatchException) { }
    if (net == null && string.IsNullOrWhiteSpace(c.PartsList))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Network '{c.NetworkName}' does not exist; give partsList to create it. Nothing was changed.");
    var surface = !string.IsNullOrWhiteSpace(c.Surface) ? CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, c.Surface!, OpenMode.ForRead)
      : net != null && !ReferenceSurfaceOrNull(net).IsNull ? NetworkSurface(net, transaction) : NetworkSurface(host, transaction);
    var created = false;
    if (net == null && !dryRun)
    {
      net = CivilObjectUtils.GetRequiredObject<Network>(transaction, CreatePipeNetwork(civilDoc, c.NetworkName), OpenMode.ForWrite);
      net.PartsListId = FindPartsListId(civilDoc, transaction, c.PartsList!);
      if (surface != null) net.ReferenceSurfaceId = surface.ObjectId;
      created = true;
    }
    else if (net != null && !dryRun && !net.IsWriteEnabled) net.UpgradeOpen();
    return (net, surface, created);
  }

  private static Func<string, bool> StemScope(string name)
  {
    var stem = new[] { $"{name}-", $"{name} " };
    return n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase) || stem.Any(x => n.StartsWith(x, StringComparison.OrdinalIgnoreCase));
  }

  /// <summary>C225 kind inlet: an existing pipe into a new structure of the host network.</summary>
  private static Dictionary<string, object?> InletCore(ServiceSpec c, Autodesk.Civil.ApplicationServices.CivilDocument civilDoc, Database database, Transaction transaction, bool dryRun)
  {
    var host = FindPipeNetworkByName(civilDoc, transaction, c.HostName, OpenMode.ForRead);
    var hs = FindStructureByName(host, transaction, c.HostStructure!, OpenMode.ForRead);
    var c0 = new Point2d(hs.Location.X, hs.Location.Y);
    var f = new Point2d(c.From!.Value.X, c.From.Value.Y);
    var L = f.GetDistanceTo(c0);
    if (L < 0.1)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"from lies at the structure ({L:F3} m from its centre). Nothing was changed.");
    // the connection point on the structure's edge, 5 mm inside (a pipe trimmed away from a null structure at the centre
    // is disconnected by Civil, live 2026-11-24)
    var rk = Math.Max(0, hs.InnerDiameterOrWidth / 2 - TrimMargin);
    var k = c0 + (f - c0).GetNormal() * rk;
    var invBef = c.Invert!.Value + c.Slope * L;
    var (net, surface, createdNet) = ServiceNetwork(c, civilDoc, transaction, host, dryRun);
    var nBef = $"{c.Name} bef";
    var nAns = $"{c.Name} ans";
    var sIn = new List<SyncStructure>
    {
      new(nBef, "Brytpunkt", null, f.X, f.Y, SurfaceZ(surface, f.X, f.Y), invBef),
      new(nAns, "Brytpunkt", null, k.X, k.Y, SurfaceZ(surface, k.X, k.Y), c.Invert.Value),
    };
    var noRim = sIn.FirstOrDefault(st => st.Rim == null);
    if (noRim != null)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"No surface elevation at {noRim.Name} ({noRim.X:F2}, {noRim.Y:F2}). Nothing was changed.");
    var pIn = new List<SyncPipe> { new(c.Name, c.PipePart, c.Dia, nBef, nAns, null, null, invBef, c.Invert.Value, null, null) };
    var answer = new Dictionary<string, object?>
    {
      ["name"] = c.Name, ["kind"] = "inlet", ["networkName"] = c.NetworkName, ["dryRun"] = dryRun,
      ["host"] = new Dictionary<string, object?> { ["network"] = host.Name, ["structure"] = hs.Name, ["sump"] = Math.Round(hs.SumpElevation, 3),
        ["at"] = new[] { Math.Round(k.X, 3), Math.Round(k.Y, 3) } },
      ["invert"] = Math.Round(c.Invert.Value, 3), ["invertFrom"] = Math.Round(invBef, 3), ["length"] = Math.Round(L, 3),
      ["heightAboveFloor"] = Math.Round(c.Invert.Value - hs.SumpElevation, 3),
    };
    if (net == null) { answer["createsNetwork"] = true; answer["created"] = sIn.Select(s => s.Name).Append(c.Name).ToList(); return answer; }
    if (createdNet) answer["createdNetwork"] = true;
    var core = SyncCore(net, transaction, surface, sIn, pIn, StemScope(c.Name), dryRun);
    if (!dryRun)
    {
      var ansId = net.GetStructureIds().Cast<ObjectId>().FirstOrDefault(id => string.Equals(CivilObjectUtils.GetName(transaction.GetObject(id, OpenMode.ForRead)), nAns, StringComparison.OrdinalIgnoreCase));
      if (!ansId.IsNull && !IsLocked(transaction.GetObject(ansId, OpenMode.ForRead)) && ReadLink(transaction.GetObject(ansId, OpenMode.ForRead))?.HostHandle != hs.Handle.ToString())
        WriteLink(database, transaction, transaction.GetObject(ansId, OpenMode.ForWrite), host.Name, hs.Name ?? "", hs.Handle.ToString());
      answer["linkedTo"] = $"{host.Name}/{hs.Name}";
    }
    answer["created"] = core.Created;
    answer["updated"] = core.Updated;
    answer["deleted"] = core.Deleted;
    answer["locked"] = core.Locked;
    return answer;
  }

  /// <summary>C192 core: a service, gully or (C225) water service connection.</summary>
  private static Dictionary<string, object?> ServiceCore(ServiceSpec c, Autodesk.Civil.ApplicationServices.CivilDocument civilDoc, Database database, Transaction transaction, bool dryRun)
  {
    var name = c.Name;
    var dia = c.Dia;
    var slope = c.Slope;
    var rule = c.Rule;
    var dn = dia / 1000.0;
    var E = c.End!.Value;
    var host = FindPipeNetworkByName(civilDoc, transaction, c.HostName, OpenMode.ForRead);
    var hostPipes = host.GetPipeIds().Cast<ObjectId>().Select(id => CivilObjectUtils.GetRequiredObject<Pipe>(transaction, id, OpenMode.ForRead)).ToList();
    Point2d m;
    double hostInv;
    string? hostPartName;
    string hostHandle;
    var e2 = new Point2d(E.X, E.Y);
    if (!string.IsNullOrWhiteSpace(c.HostStructure))
    {
      var hs = FindStructureByName(host, transaction, c.HostStructure!, OpenMode.ForRead);
      // the connection point sits on the structure's edge (5 mm inside): a pipe trimmed away from a null structure at the
      // centre is disconnected by Civil (live 2026-11-24)
      var c0 = new Point2d(hs.Location.X, hs.Location.Y);
      var rr = Math.Max(0, hs.InnerDiameterOrWidth / 2 - TrimMargin);
      m = e2.GetDistanceTo(c0) > rr + 0.05 ? c0 + (e2 - c0).GetNormal() * rr : c0;
      var maxInner = hostPipes.Where(p => p.StartStructureId == hs.ObjectId || p.EndStructureId == hs.ObjectId)
        .Select(p => p.InnerDiameterOrWidth).DefaultIfEmpty(dn).Max();
      hostInv = rule == "centered" ? hs.SumpElevation + (maxInner - dn) / 2 : hs.SumpElevation;
      hostPartName = hs.Name;
      hostHandle = hs.Handle.ToString();
    }
    else
    {
      var q = c.At != null ? new Point2d(c.At.Value.X, c.At.Value.Y) : e2;
      Pipe hp;
      if (!string.IsNullOrWhiteSpace(c.HostPipe))
        hp = hostPipes.FirstOrDefault(p => string.Equals(p.Name, c.HostPipe, StringComparison.OrdinalIgnoreCase))
          ?? throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Pipe '{c.HostPipe}' is not in '{c.HostName}'. Nothing was changed.");
      else
        hp = hostPipes.OrderBy(p => new LineSegment2d(new Point2d(p.StartPoint.X, p.StartPoint.Y), new Point2d(p.EndPoint.X, p.EndPoint.Y)).GetDistanceTo(q))
          .FirstOrDefault() ?? throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Network '{c.HostName}' has no pipes. Nothing was changed.");
      var hs2 = new LineSegment2d(new Point2d(hp.StartPoint.X, hp.StartPoint.Y), new Point2d(hp.EndPoint.X, hp.EndPoint.Y));
      m = hs2.GetClosestPointTo(q).Point;
      var t = hs2.Length < 1e-9 ? 0 : hs2.StartPoint.GetDistanceTo(m) / hs2.Length;
      var zc = hp.StartPoint.Z + (hp.EndPoint.Z - hp.StartPoint.Z) * t;
      var bottom = zc - hp.InnerDiameterOrWidth / 2;
      hostInv = rule == "centered" ? bottom + (hp.InnerDiameterOrWidth - dn) / 2 : bottom;
      hostPartName = hp.Name;
      hostHandle = hp.Handle.ToString();
    }
    var (net, surface, createdNet) = ServiceNetwork(c, civilDoc, transaction, host, dryRun);
    // wall of the service pipe: parts list size data, else a pipe of the same family and size in the drawing, else 3 %
    double wall = dn * 0.03;
    try
    {
      var ids = FindPartForNetwork(net ?? host, transaction, c.PipePart, DomainType.Pipe, dia);
      var ps = CivilObjectUtils.GetRequiredObject<PartSize>(transaction, ids.SizeId, OpenMode.ForRead);
      using var rec = ps.SizeDataRecord;
      var (pi, po, _) = PipeDiameters(rec);
      if (pi is > 0 && po is > 0 && po > pi) wall = dn * (po.Value / pi.Value - 1) / 2;
      else
      {
        var same = ListPipeNetworkNames(civilDoc, transaction).Where(n => !string.IsNullOrEmpty(n)).Select(n => FindPipeNetworkByName(civilDoc, transaction, n, OpenMode.ForRead))
          .SelectMany(n => n.GetPipeIds().Cast<ObjectId>()).Select(id => CivilObjectUtils.GetRequiredObject<Pipe>(transaction, id, OpenMode.ForRead))
          .FirstOrDefault(x => string.Equals(x.PartFamilyName, c.PipePart, StringComparison.OrdinalIgnoreCase) && Math.Abs(x.InnerDiameterOrWidth - dn) < 0.001);
        if (same != null) wall = (same.OuterDiameterOrWidth - same.InnerDiameterOrWidth) / 2;
      }
    }
    catch (JsonRpcDispatchException) { }
    var rim = c.EndRim ?? SurfaceZ(surface, E.X, E.Y)
      ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"No rim for the end and no surface elevation at ({E.X:F2}, {E.Y:F2}). Nothing was changed.");
    var L1 = e2.GetDistanceTo(m);
    if (L1 < 0.05)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"The end lies on the host ({L1:F3} m). Nothing was changed.");
    var invE = hostInv + slope * L1;
    if (c.EndBelowRim != null) invE = Math.Max(rim - c.EndBelowRim.Value, invE);
    Point2d? K = null;
    double invK = 0;
    string? kneeOver = null;
    var seg = new LineSegment2d(e2, m);
    foreach (var av in c.Avoid)
    {
      var an2 = FindPipeNetworkByName(civilDoc, transaction, av.Network, OpenMode.ForRead);
      foreach (ObjectId id in an2.GetPipeIds())
      {
        var p = CivilObjectUtils.GetRequiredObject<Pipe>(transaction, id, OpenMode.ForRead);
        var o = new LineSegment2d(new Point2d(p.StartPoint.X, p.StartPoint.Y), new Point2d(p.EndPoint.X, p.EndPoint.Y));
        var hits = seg.IntersectWith(o);
        if (hits == null || hits.Length == 0) continue;
        var x = hits[0];
        var dv = e2.GetDistanceTo(x);
        var zv = p.StartPoint.Z + (p.EndPoint.Z - p.StartPoint.Z) * (o.Length < 1e-9 ? 0 : o.StartPoint.GetDistanceTo(x) / o.Length);
        var od = p.OuterDiameterOrWidth;
        var zHere = invE - (invE - hostInv) * dv / L1;
        if ((zv - od / 2) - (zHere + dn + wall) >= av.Clearance) continue;      // under with clearance
        if ((zHere - wall) - (zv + od / 2) >= av.Clearance) continue;            // over with clearance
        var need = zv + od / 2 + av.Clearance + wall + slope * dv;
        if (need > invE || K == null)
        {
          invE = Math.Max(invE, need);
          var tk = Math.Min(0.95, (dv + av.KneeAfter) / L1);
          K = e2 + (m - e2) * tk;
          invK = invE - slope * tk * L1;
          kneeOver = $"{an2.Name}/{p.Name}";
        }
      }
    }
    var C = c.ContinueTo;
    var invC = C == null ? 0 : invE + slope * e2.GetDistanceTo(new Point2d(C.Value.X, C.Value.Y));
    var nAns = $"{name} ans";
    var nKnee = $"{name} knä";
    var nBef = $"{name} bef";
    var endName = c.EndName;
    var sIn = new List<SyncStructure>
    {
      new(endName, c.EndPart, c.EndDia, E.X, E.Y, rim, invE - c.SumpDepth),
      new(nAns, "Brytpunkt", null, m.X, m.Y, SurfaceZ(surface, m.X, m.Y), hostInv),
    };
    if (K != null) sIn.Add(new(nKnee, "Brytpunkt", null, K.Value.X, K.Value.Y, SurfaceZ(surface, K.Value.X, K.Value.Y), invK));
    if (C != null) sIn.Add(new(nBef, "Brytpunkt", null, C.Value.X, C.Value.Y, SurfaceZ(surface, C.Value.X, C.Value.Y), invC));
    var noRim = sIn.FirstOrDefault(st => st.Rim == null);
    if (noRim != null)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"No surface elevation at {noRim.Name} ({noRim.X:F2}, {noRim.Y:F2}). Nothing was changed.");
    var pIn = new List<SyncPipe>();
    if (K != null)
    {
      pIn.Add(new($"{name}-1", c.PipePart, dia, endName, nKnee, null, null, invE, invK, null, null));
      pIn.Add(new($"{name}-2", c.PipePart, dia, nKnee, nAns, null, null, invK, hostInv, null, null));
    }
    else pIn.Add(new($"{name}-1", c.PipePart, dia, endName, nAns, null, null, invE, hostInv, null, null));
    if (C != null) pIn.Add(new($"{name}-bef", c.PipePart, dia, nBef, endName, null, null, invC, invE, null, null));

    var others = (c.Check ?? ListPipeNetworkNames(civilDoc, transaction).Where(n => !string.IsNullOrEmpty(n)).Where(n => !string.Equals(n, c.NetworkName, StringComparison.OrdinalIgnoreCase)).ToList())
      .Select(n => FindPipeNetworkByName(civilDoc, transaction, n, OpenMode.ForRead)).ToList();
    var skip = new HashSet<ObjectId>(hostPipes.Where(p => p.Name == hostPartName
      || (!string.IsNullOrWhiteSpace(c.HostStructure) && (ResolveObjectName(transaction, p.StartStructureId) == c.HostStructure
                                                           || ResolveObjectName(transaction, p.EndStructureId) == c.HostStructure)))
      .Select(p => p.ObjectId));
    var crossings = new List<Dictionary<string, object?>>();
    if (K != null)
    {
      crossings.AddRange(Crossings(transaction, others, e2, K.Value, invE, invK, dn, wall, skip));
      crossings.AddRange(Crossings(transaction, others, K.Value, m, invK, hostInv, dn, wall, skip));
    }
    else crossings.AddRange(Crossings(transaction, others, e2, m, invE, hostInv, dn, wall, skip));

    var answer = new Dictionary<string, object?>
    {
      ["name"] = name, ["kind"] = c.Kind, ["networkName"] = c.NetworkName, ["dryRun"] = dryRun,
      ["host"] = new Dictionary<string, object?> { ["network"] = host.Name, [string.IsNullOrWhiteSpace(c.HostStructure) ? "pipe" : "structure"] = hostPartName,
        ["at"] = new[] { Math.Round(m.X, 3), Math.Round(m.Y, 3) }, ["invert"] = Math.Round(hostInv, 3), ["rule"] = rule },
      ["end"] = new Dictionary<string, object?> { ["name"] = endName, ["partName"] = c.EndPart, ["rim"] = Math.Round(rim, 3),
        ["invert"] = Math.Round(invE, 3), ["depth"] = Math.Round(rim - invE, 3) },
      ["knee"] = K == null ? null : new Dictionary<string, object?> { ["over"] = kneeOver, ["at"] = new[] { Math.Round(K.Value.X, 3), Math.Round(K.Value.Y, 3) }, ["invert"] = Math.Round(invK, 3) },
      ["continueTo"] = C == null ? null : new Dictionary<string, object?> { ["invert"] = Math.Round(invC, 3), ["length"] = Math.Round(e2.GetDistanceTo(new Point2d(C.Value.X, C.Value.Y)), 3) },
      ["length"] = Math.Round(L1, 3),
      ["slopePermille"] = Math.Round((invE - hostInv) / L1 * 1000, 1),
      ["crossings"] = crossings,
    };
    if (net == null) { answer["createsNetwork"] = true; return answer; }
    if (createdNet) answer["createdNetwork"] = true;
    var core = SyncCore(net, transaction, surface, sIn, pIn, StemScope(name), dryRun);
    // C199: the connection point is linked to the host pipe or structure
    if (!dryRun)
    {
      var ansId = net.GetStructureIds().Cast<ObjectId>().FirstOrDefault(id => string.Equals(CivilObjectUtils.GetName(transaction.GetObject(id, OpenMode.ForRead)), nAns, StringComparison.OrdinalIgnoreCase));
      if (!ansId.IsNull && !IsLocked(transaction.GetObject(ansId, OpenMode.ForRead))) WriteLink(database, transaction, transaction.GetObject(ansId, OpenMode.ForWrite), host.Name, hostPartName ?? "", hostHandle);
      answer["linkedTo"] = $"{host.Name}/{hostPartName}";
    }
    answer["created"] = core.Created;
    answer["updated"] = core.Updated;
    answer["deleted"] = core.Deleted;
    answer["locked"] = core.Locked;
    if (c.Trim && !dryRun)
    {
      var all = net.GetStructureIds().Cast<ObjectId>().Select(id => CivilObjectUtils.GetRequiredObject<Structure>(transaction, id, OpenMode.ForRead))
        .ToDictionary(s => s.ObjectId);
      var tr = TrimPipeEnds(all, core.Synced, st =>
        InlineFamilies.Contains(st.PartFamilyName ?? "") ? 0 : st.InnerDiameterOrWidth / 2, false);
      answer["trimmed"] = tr.Count(t => t.TryGetValue("moved", out var mv) && mv is double v && v > 1e-6);
    }
    return answer;
  }
}
