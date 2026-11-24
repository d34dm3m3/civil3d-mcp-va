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
/// edges (C190), surface elevation, plan crossings with clearance.</summary>
public static partial class PipeNetworkCommands
{
  // ---------- C191 syncPipeNetwork ----------

  private sealed record SyncStructure(string Name, string PartName, double? Dia, double X, double Y, double? Rim, double? Sump);

  private sealed record SyncPipe(string Name, string PartName, double? Dia, string? StartStructure, string? EndStructure,
    (double X, double Y)? StartXY, (double X, double Y)? EndXY, double? StartInvert, double? EndInvert, double? StartZ, double? EndZ);
  /// <summary>C191: brings a gravity network to a described state in one transaction, matching parts by name, instead of
  /// erasing and rebuilding it: {networkName, partsList?, referenceSurface?, structures:[{name, partName, structureDiameter?,
  /// x, y, rim? (else the reference surface), sump?}], pipes:[{name, partName, diameter?, startStructure?, endStructure?,
  /// start?:{x,y}, end?:{x,y} (default the structure centre), startInvert|startZ, endInvert|endZ}], deleteMissing? (false),
  /// trimToStructures? {radii?, familyRadii?} (C190 after the sync), dryRun?}. Existing parts keep their style, labels and
  /// profile-view parts; a part whose family or size differs is swapped (SwapPartFamilyAndSize), a moved structure is moved,
  /// pipes are reconnected when their structures change and get the given ends after connecting. The network is created
  /// when missing (partsList needed). Answer: created, updated (with what changed), unchanged, deleted.
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

    var sIn = ((PluginRuntime.GetParameter(parameters, "structures") as JsonArray) ?? new JsonArray()).Select((n, i) =>
    {
      if (n is not JsonObject o || o["name"]?.GetValue<string>() is not string nm || o["partName"]?.GetValue<string>() is not string pn
          || o["x"] == null || o["y"] == null)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"structures[{i}] needs name, partName, x and y. Nothing was changed.");
      return new SyncStructure(nm, pn, o["structureDiameter"]?.GetValue<double>(), o["x"]!.GetValue<double>(), o["y"]!.GetValue<double>(),
        o["rim"]?.GetValue<double>(), o["sump"]?.GetValue<double>());
    }).ToList();
    var pIn = ((PluginRuntime.GetParameter(parameters, "pipes") as JsonArray) ?? new JsonArray()).Select((n, i) =>
    {
      if (n is not JsonObject o || o["name"]?.GetValue<string>() is not string nm || o["partName"]?.GetValue<string>() is not string pn)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"pipes[{i}] needs name and partName. Nothing was changed.");
      var p = new SyncPipe(nm, pn, o["diameter"]?.GetValue<double>(), o["startStructure"]?.GetValue<string>(), o["endStructure"]?.GetValue<string>(),
        o["start"] == null ? null : ReadXY(o["start"], $"pipes[{i}].start"), o["end"] == null ? null : ReadXY(o["end"], $"pipes[{i}].end"),
        o["startInvert"]?.GetValue<double>(), o["endInvert"]?.GetValue<double>(), o["startZ"]?.GetValue<double>(), o["endZ"]?.GetValue<double>());
      if ((p.StartInvert == null && p.StartZ == null) || (p.EndInvert == null && p.EndZ == null))
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"pipes[{i}] ({nm}): give startInvert or startZ, and endInvert or endZ. Nothing was changed.");
      if ((p.StartStructure == null && p.StartXY == null) || (p.EndStructure == null && p.EndXY == null))
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"pipes[{i}] ({nm}): each end needs a structure or a point. Nothing was changed.");
      return p;
    }).ToList();
    foreach (var dup in sIn.Select(s => s.Name).Concat(pIn.Select(p => p.Name)).GroupBy(n => n, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Name '{dup.Key}' is given twice. Nothing was changed.");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      Network? network = null;
      try { network = FindPipeNetworkByName(civilDoc, transaction, networkName, OpenMode.ForWrite); }
      catch (JsonRpcDispatchException) { }
      var createdNetwork = false;
      if (network == null)
      {
        if (string.IsNullOrWhiteSpace(partsListName))
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Network '{networkName}' does not exist; give partsList to create it. Nothing was changed.");
        if (dryRun)
          return new Dictionary<string, object?> { ["networkName"] = networkName, ["dryRun"] = true, ["createsNetwork"] = true,
            ["created"] = sIn.Select(s => s.Name).Concat(pIn.Select(p => p.Name)).ToList() };
        network = CivilObjectUtils.GetRequiredObject<Network>(transaction, CreatePipeNetwork(civilDoc, networkName), OpenMode.ForWrite);
        network.PartsListId = FindPartsListId(civilDoc, transaction, partsListName!);
        createdNetwork = true;
      }
      if (!string.IsNullOrWhiteSpace(surfaceName))
      {
        var sf = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, surfaceName!, OpenMode.ForRead);
        if (!dryRun && ReferenceSurfaceOrNull(network) != sf.ObjectId) network.ReferenceSurfaceId = sf.ObjectId;
      }
      var surface = !string.IsNullOrWhiteSpace(surfaceName)
        ? CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, surfaceName!, OpenMode.ForRead) : NetworkSurface(network, transaction);
      var core = SyncCore(network, transaction, surface, sIn, pIn, deleteMissing ? (_ => true) : null, dryRun);
      var (created, updated, unchanged, deleted, syncedPipes) = (core.Created, core.Updated, core.Unchanged, core.Deleted, core.Synced);
      List<Dictionary<string, object?>>? trimmed = null;
      if (trim != null && !dryRun)
      {
        var all = network.GetStructureIds().Cast<ObjectId>().Select(id => CivilObjectUtils.GetRequiredObject<Structure>(transaction, id, OpenMode.ForRead))
          .ToDictionary(s => s.ObjectId);
        trimmed = TrimPipeEnds(all, syncedPipes, RadiusRule(byName, byFamily), false);
      }
      return new Dictionary<string, object?>
      {
        ["networkName"] = network.Name, ["dryRun"] = dryRun, ["createdNetwork"] = createdNetwork,
        ["created"] = created, ["updated"] = updated, ["unchanged"] = unchanged, ["deleted"] = deleted,
        ["trimmed"] = trimmed?.Count(t => t.TryGetValue("moved", out var m) && m is double v && v > 1e-6),
        ["counts"] = new Dictionary<string, int> { ["created"] = created.Count, ["updated"] = updated.Count, ["unchanged"] = unchanged.Count, ["deleted"] = deleted.Count },
      };
    });
  }

  private sealed record SyncResult(List<string> Created, List<Dictionary<string, object?>> Updated, List<string> Unchanged,
    List<string> Deleted, List<Pipe> Synced);
  /// <summary>C191 core, shared with C192: brings the named parts of a network to the given state. deleteScope: parts not
  /// given whose name it accepts are erased (null: nothing erased).</summary>
  private static SyncResult SyncCore(Network network, Transaction transaction, CivilSurface? surface, List<SyncStructure> sIn,
    List<SyncPipe> pIn, Func<string, bool>? deleteScope, bool dryRun)
  {
    var sNames = new HashSet<string>(sIn.Select(s => s.Name), StringComparer.OrdinalIgnoreCase);
      var mode = dryRun ? OpenMode.ForRead : OpenMode.ForWrite;
      var exS = network.GetStructureIds().Cast<ObjectId>().Select(id => CivilObjectUtils.GetRequiredObject<Structure>(transaction, id, mode))
        .ToDictionary(s => s.Name ?? "", StringComparer.OrdinalIgnoreCase);
      var exP = network.GetPipeIds().Cast<ObjectId>().Select(id => CivilObjectUtils.GetRequiredObject<Pipe>(transaction, id, mode))
        .ToDictionary(p => p.Name ?? "", StringComparer.OrdinalIgnoreCase);
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
      var structById = new Dictionary<string, Structure>(StringComparer.OrdinalIgnoreCase);

      foreach (var s in sIn)
      {
        var rim = s.Rim ?? SurfaceZ(surface, s.X, s.Y)
          ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Structure '{s.Name}': no rim given and no surface elevation at ({s.X:F2}, {s.Y:F2}). Nothing was changed.");
        var part = FindPartForNetwork(network, transaction, s.PartName, DomainType.Structure, s.Dia);
        if (exS.TryGetValue(s.Name, out var st))
        {
          var what = new List<string>();
          var famSize = !string.Equals(st.PartFamilyName, s.PartName, StringComparison.OrdinalIgnoreCase)
            || (s.Dia != null && Math.Abs(st.InnerDiameterOrWidth * 1000 - s.Dia.Value) > 0.5 && Math.Abs(GetOuter(st) * 1000 - s.Dia.Value) > 0.5);
          if (famSize) { what.Add("part"); if (!dryRun) st.SwapPartFamilyAndSize(part.FamilyId, part.SizeId); }
          if (new Point2d(st.Location.X, st.Location.Y).GetDistanceTo(new Point2d(s.X, s.Y)) > 0.001)
          { what.Add("location"); if (!dryRun) st.Location = new Point3d(s.X, s.Y, st.Location.Z); }
          if (Math.Abs(st.RimElevation - rim) > 0.0005 || st.AutomaticRimSurfaceAdjustment)
          { what.Add("rim"); if (!dryRun) { st.AutomaticRimSurfaceAdjustment = false; st.RimElevation = rim; } }
          // Civil lowers the sump to the lowest connected pipe invert (at the wall when trimmed, C190), so the sump counts as
          // unchanged when it equals min(wanted sump, lowest invert)
          var sumpExpected = s.Sump == null ? (double?)null
            : lowInvert.TryGetValue(st.ObjectId, out var li) ? Math.Min(s.Sump.Value, li) : s.Sump.Value;
          if (s.Sump != null && Math.Abs(st.SumpElevation - sumpExpected!.Value) > 0.002 && Math.Abs(st.SumpElevation - s.Sump.Value) > 0.002)
          { what.Add("sump"); if (!dryRun) { st.ControlSumpBy = StructureControlSumpType.ByElevation; st.SumpElevation = s.Sump.Value; } }
          if (what.Count > 0) updated.Add(new Dictionary<string, object?> { ["name"] = s.Name, ["kind"] = "structure", ["changed"] = what });
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
        if (exP.TryGetValue(p.Name, out var ep))
        {
          pipe = ep;
          var famSize = !string.Equals(pipe.PartFamilyName, p.PartName, StringComparison.OrdinalIgnoreCase)
            || (p.Dia != null && Math.Abs(pipe.InnerDiameterOrWidth * 1000 - p.Dia.Value) > 0.5);
          if (famSize) { what.Add("part"); if (!dryRun) pipe.SwapPartFamilyAndSize(part.FamilyId, part.SizeId); }
          var wantS = ss?.ObjectId ?? ObjectId.Null;
          var wantE = es?.ObjectId ?? ObjectId.Null;
          if (pipe.StartStructureId != wantS)
          {
            what.Add("startStructure");
            if (!dryRun) { if (!pipe.StartStructureId.IsNull) pipe.Disconnect(ConnectorPositionType.Start); if (!wantS.IsNull) pipe.ConnectToStructure(ConnectorPositionType.Start, wantS, true); }
          }
          if (pipe.EndStructureId != wantE)
          {
            what.Add("endStructure");
            if (!dryRun) { if (!pipe.EndStructureId.IsNull) pipe.Disconnect(ConnectorPositionType.End); if (!wantE.IsNull) pipe.ConnectToStructure(ConnectorPositionType.End, wantE, true); }
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
        if (!dryRun && geomOff)
        {
          // ends after connecting: ConnectToStructure(force) may move them (C176)
          if (pipe.StartPoint.DistanceTo(s1) > 1e-6) pipe.StartPoint = s1;
          if (pipe.EndPoint.DistanceTo(e1) > 1e-6) pipe.EndPoint = e1;
          syncedPipes.Add(pipe);
        }
        if (exP.ContainsKey(p.Name))
        {
          if (what.Count > 0) updated.Add(new Dictionary<string, object?> { ["name"] = p.Name, ["kind"] = "pipe", ["changed"] = what });
          else unchanged.Add(p.Name);
        }
      }

      // sumps again after the pipes: min(wanted, lowest connected invert)
      if (!dryRun)
        foreach (var sw in sIn.Where(x => x.Sump != null))
        {
          var st = S(sw.Name);
          if (st == null) continue;
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
        foreach (var (n, p) in exP) if (!keep.Contains(n) && deleteScope(n)) { deleted.Add(n); if (!dryRun) p.Erase(); }
        foreach (var (n, s) in exS) if (!keep.Contains(n) && deleteScope(n)) { deleted.Add(n); if (!dryRun) s.Erase(); }
      }

    return new SyncResult(created, updated, unchanged, deleted, syncedPipes);
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
  /// Use case: the same service geometry was computed in Python in 3-4 copies per project (the project scripts);
  /// requested 2026-11-24 as one of the first two items of the gap analysis.</summary>
  public static Task<object?> AddServiceConnectionAsync(JsonObject? parameters)
  {
    var networkName = PluginRuntime.GetRequiredString(parameters, "networkName");
    var name = PluginRuntime.GetRequiredString(parameters, "name").Trim();
    var hostName = PluginRuntime.GetRequiredString(parameters, "hostNetwork");
    var hostStructureName = PluginRuntime.GetOptionalString(parameters, "hostStructure");
    var hostPipeName = PluginRuntime.GetOptionalString(parameters, "hostPipe");
    var partsListName = PluginRuntime.GetOptionalString(parameters, "partsList");
    var surfaceName = PluginRuntime.GetOptionalString(parameters, "referenceSurface");
    var pipePart = PluginRuntime.GetOptionalString(parameters, "partName") ?? "Markrör PP";
    var dia = PluginRuntime.GetOptionalDouble(parameters, "diameter") ?? 160.0;
    var slope = (PluginRuntime.GetOptionalDouble(parameters, "slopePermille") ?? 10.0) / 1000.0;
    var rule = (PluginRuntime.GetOptionalString(parameters, "invertRule") ?? "centered").ToLowerInvariant();
    var endBelowRim = PluginRuntime.GetOptionalDouble(parameters, "endBelowRim");
    var trim = PluginRuntime.GetOptionalBool(parameters, "trim") ?? true;
    var dryRun = PluginRuntime.GetOptionalBool(parameters, "dryRun") ?? false;
    if (rule != "centered" && rule != "bottom")
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "invertRule must be centered or bottom. Nothing was changed.");
    if (PluginRuntime.GetParameter(parameters, "end") is not JsonObject endO)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "end {x, y, partName?, structureDiameter?, name?, rim?} is required. Nothing was changed.");
    var E = ReadXY(endO, "end");
    var endPart = endO["partName"]?.GetValue<string>() ?? "Rensbrunn";
    var endDia = endO["structureDiameter"]?.GetValue<double>() ?? (endPart.Equals("Rensbrunn", StringComparison.OrdinalIgnoreCase) ? 200.0 : (double?)null);
    var endName = endO["name"]?.GetValue<string>() ?? name;
    var endRim = endO["rim"]?.GetValue<double>();
    var sumpDepth = endO["sumpDepth"]?.GetValue<double>() ?? 0.0;
    (double X, double Y)? C = PluginRuntime.GetParameter(parameters, "continueTo") is JsonNode cn ? ReadXY(cn, "continueTo") : null;
    (double X, double Y)? at = PluginRuntime.GetParameter(parameters, "at") is JsonNode an ? ReadXY(an, "at") : null;
    var avoid = ((PluginRuntime.GetParameter(parameters, "avoid") as JsonArray) ?? new JsonArray()).Select((n, i) =>
    {
      if (n is not JsonObject o || o["network"]?.GetValue<string>() is not string nw)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"avoid[{i}] needs network. Nothing was changed.");
      return (Network: nw, Clearance: o["clearance"]?.GetValue<double>() ?? 0.10, KneeAfter: o["kneeAfter"]?.GetValue<double>() ?? 0.35);
    }).ToList();
    var check = OptionalStringList(parameters, "check");
    var dn = dia / 1000.0;

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var host = FindPipeNetworkByName(civilDoc, transaction, hostName, OpenMode.ForRead);
      var hostPipes = host.GetPipeIds().Cast<ObjectId>().Select(id => CivilObjectUtils.GetRequiredObject<Pipe>(transaction, id, OpenMode.ForRead)).ToList();
      Point2d m;
      double hostInv;
      string? hostPartName;
      string hostHandle;
      var e2 = new Point2d(E.X, E.Y);
      if (!string.IsNullOrWhiteSpace(hostStructureName))
      {
        var hs = FindStructureByName(host, transaction, hostStructureName!, OpenMode.ForRead);
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
        var q = at != null ? new Point2d(at.Value.X, at.Value.Y) : e2;
        Pipe hp;
        if (!string.IsNullOrWhiteSpace(hostPipeName))
          hp = hostPipes.FirstOrDefault(p => string.Equals(p.Name, hostPipeName, StringComparison.OrdinalIgnoreCase))
            ?? throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Pipe '{hostPipeName}' is not in '{hostName}'. Nothing was changed.");
        else
          hp = hostPipes.OrderBy(p => new LineSegment2d(new Point2d(p.StartPoint.X, p.StartPoint.Y), new Point2d(p.EndPoint.X, p.EndPoint.Y)).GetDistanceTo(q))
            .FirstOrDefault() ?? throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Network '{hostName}' has no pipes. Nothing was changed.");
        var hs2 = new LineSegment2d(new Point2d(hp.StartPoint.X, hp.StartPoint.Y), new Point2d(hp.EndPoint.X, hp.EndPoint.Y));
        m = hs2.GetClosestPointTo(q).Point;
        var t = hs2.Length < 1e-9 ? 0 : hs2.StartPoint.GetDistanceTo(m) / hs2.Length;
        var zc = hp.StartPoint.Z + (hp.EndPoint.Z - hp.StartPoint.Z) * t;
        var bottom = zc - hp.InnerDiameterOrWidth / 2;
        hostInv = rule == "centered" ? bottom + (hp.InnerDiameterOrWidth - dn) / 2 : bottom;
        hostPartName = hp.Name;
        hostHandle = hp.Handle.ToString();
      }
      Network? net = null;
      try { net = FindPipeNetworkByName(civilDoc, transaction, networkName, OpenMode.ForWrite); }
      catch (JsonRpcDispatchException) { }
      // wall of the service pipe: parts list size data, else a pipe of the same family and size in the drawing, else 3 %
      double wall = dn * 0.03;
      try
      {
        var ids = FindPartForNetwork(net ?? host, transaction, pipePart, DomainType.Pipe, dia);
        var ps = CivilObjectUtils.GetRequiredObject<PartSize>(transaction, ids.SizeId, OpenMode.ForRead);
        using var rec = ps.SizeDataRecord;
        var (pi, po, _) = PipeDiameters(rec);
        if (pi is > 0 && po is > 0 && po > pi) wall = dn * (po.Value / pi.Value - 1) / 2;
        else
        {
          var same = ListPipeNetworkNames(civilDoc, transaction).Where(n => !string.IsNullOrEmpty(n)).Select(n => FindPipeNetworkByName(civilDoc, transaction, n, OpenMode.ForRead))
            .SelectMany(n => n.GetPipeIds().Cast<ObjectId>()).Select(id => CivilObjectUtils.GetRequiredObject<Pipe>(transaction, id, OpenMode.ForRead))
            .FirstOrDefault(x => string.Equals(x.PartFamilyName, pipePart, StringComparison.OrdinalIgnoreCase) && Math.Abs(x.InnerDiameterOrWidth - dn) < 0.001);
          if (same != null) wall = (same.OuterDiameterOrWidth - same.InnerDiameterOrWidth) / 2;
        }
      }
      catch (JsonRpcDispatchException) { }
      if (net == null && string.IsNullOrWhiteSpace(partsListName))
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Network '{networkName}' does not exist; give partsList to create it. Nothing was changed.");
      var surface = !string.IsNullOrWhiteSpace(surfaceName) ? CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, surfaceName!, OpenMode.ForRead)
        : net != null && !ReferenceSurfaceOrNull(net).IsNull ? NetworkSurface(net, transaction) : NetworkSurface(host, transaction);
      var rim = endRim ?? SurfaceZ(surface, E.X, E.Y)
        ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"No rim for the end and no surface elevation at ({E.X:F2}, {E.Y:F2}). Nothing was changed.");
      var L1 = e2.GetDistanceTo(m);
      if (L1 < 0.05)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"The end lies on the host ({L1:F3} m). Nothing was changed.");
      var invE = hostInv + slope * L1;
      if (endBelowRim != null) invE = Math.Max(rim - endBelowRim.Value, invE);
      Point2d? K = null;
      double invK = 0;
      string? kneeOver = null;
      var seg = new LineSegment2d(e2, m);
      foreach (var av in avoid)
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
      var invC = C == null ? 0 : invE + slope * e2.GetDistanceTo(new Point2d(C.Value.X, C.Value.Y));
      var nAns = $"{name} ans";
      var nKnee = $"{name} knä";
      var nBef = $"{name} bef";
      var sIn = new List<SyncStructure>
      {
        new(endName, endPart, endDia, E.X, E.Y, rim, invE - sumpDepth),
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
        pIn.Add(new($"{name}-1", pipePart, dia, endName, nKnee, null, null, invE, invK, null, null));
        pIn.Add(new($"{name}-2", pipePart, dia, nKnee, nAns, null, null, invK, hostInv, null, null));
      }
      else pIn.Add(new($"{name}-1", pipePart, dia, endName, nAns, null, null, invE, hostInv, null, null));
      if (C != null) pIn.Add(new($"{name}-bef", pipePart, dia, nBef, endName, null, null, invC, invE, null, null));

      var others = (check ?? ListPipeNetworkNames(civilDoc, transaction).Where(n => !string.IsNullOrEmpty(n)).Where(n => !string.Equals(n, networkName, StringComparison.OrdinalIgnoreCase)).ToList())
        .Select(n => FindPipeNetworkByName(civilDoc, transaction, n, OpenMode.ForRead)).ToList();
      var skip = new HashSet<ObjectId>(hostPipes.Where(p => p.Name == hostPartName
        || (!string.IsNullOrWhiteSpace(hostStructureName) && (ResolveObjectName(transaction, p.StartStructureId) == hostStructureName
                                                             || ResolveObjectName(transaction, p.EndStructureId) == hostStructureName)))
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
        ["name"] = name, ["networkName"] = networkName, ["dryRun"] = dryRun,
        ["host"] = new Dictionary<string, object?> { ["network"] = host.Name, [string.IsNullOrWhiteSpace(hostStructureName) ? "pipe" : "structure"] = hostPartName,
          ["at"] = new[] { Math.Round(m.X, 3), Math.Round(m.Y, 3) }, ["invert"] = Math.Round(hostInv, 3), ["rule"] = rule },
        ["end"] = new Dictionary<string, object?> { ["name"] = endName, ["partName"] = endPart, ["rim"] = Math.Round(rim, 3),
          ["invert"] = Math.Round(invE, 3), ["depth"] = Math.Round(rim - invE, 3) },
        ["knee"] = K == null ? null : new Dictionary<string, object?> { ["over"] = kneeOver, ["at"] = new[] { Math.Round(K.Value.X, 3), Math.Round(K.Value.Y, 3) }, ["invert"] = Math.Round(invK, 3) },
        ["continueTo"] = C == null ? null : new Dictionary<string, object?> { ["invert"] = Math.Round(invC, 3), ["length"] = Math.Round(e2.GetDistanceTo(new Point2d(C.Value.X, C.Value.Y)), 3) },
        ["length"] = Math.Round(L1, 3),
        ["slopePermille"] = Math.Round((invE - hostInv) / L1 * 1000, 1),
        ["crossings"] = crossings,
      };
      if (dryRun && net == null) { answer["createsNetwork"] = true; return answer; }
      if (net == null)
      {
        net = CivilObjectUtils.GetRequiredObject<Network>(transaction, CreatePipeNetwork(civilDoc, networkName), OpenMode.ForWrite);
        net.PartsListId = FindPartsListId(civilDoc, transaction, partsListName!);
        if (surface != null) net.ReferenceSurfaceId = surface.ObjectId;
        answer["createdNetwork"] = true;
      }
      var stem = new[] { $"{name}-", $"{name} " };
      var core = SyncCore(net, transaction, surface, sIn, pIn,
        n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase) || stem.Any(x => n.StartsWith(x, StringComparison.OrdinalIgnoreCase)), dryRun);
      // C199: the connection point is linked to the host pipe or structure
      if (!dryRun)
      {
        var ansId = net.GetStructureIds().Cast<ObjectId>().FirstOrDefault(id => string.Equals(CivilObjectUtils.GetName(transaction.GetObject(id, OpenMode.ForRead)), nAns, StringComparison.OrdinalIgnoreCase));
        if (!ansId.IsNull) WriteLink(database, transaction, transaction.GetObject(ansId, OpenMode.ForWrite), host.Name, hostPartName ?? "", hostHandle);
        answer["linkedTo"] = $"{host.Name}/{hostPartName}";
      }
      answer["created"] = core.Created;
      answer["updated"] = core.Updated;
      answer["deleted"] = core.Deleted;
      if (trim && !dryRun)
      {
        var all = net.GetStructureIds().Cast<ObjectId>().Select(id => CivilObjectUtils.GetRequiredObject<Structure>(transaction, id, OpenMode.ForRead))
          .ToDictionary(s => s.ObjectId);
        var tr = TrimPipeEnds(all, core.Synced, st =>
          string.Equals(st.PartFamilyName, "Brytpunkt", StringComparison.OrdinalIgnoreCase) ? 0 : st.InnerDiameterOrWidth / 2, false);
        answer["trimmed"] = tr.Count(t => t.TryGetValue("moved", out var mv) && mv is double v && v > 1e-6);
      }
      return answer;
    });
  }
}
