using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.DatabaseServices;
using Autodesk.Civil.DatabaseServices.Styles;
using DBObject = Autodesk.AutoCAD.DatabaseServices.DBObject;

namespace Civil3DMcpPlugin;

/// <summary>C199-C202, gap analysis 2026-11-24 points 3, 4, 13 and 5:
/// links between networks, plan crossings and pipes through structures, fittings, real-size structure styles.</summary>
public static partial class PipeNetworkCommands
{
  private const string LinkApp = "C3DMCP_LINK";

  internal sealed record PartLink(string HostNetwork, string HostPart, string HostHandle);

  /// <summary>The host a network part is linked to (XData "C3DMCP_LINK": host network, host part name, host handle).</summary>
  internal static PartLink? ReadLink(DBObject part)
  {
    using var buffer = part.GetXDataForApplication(LinkApp);
    if (buffer == null) return null;
    var v = buffer.AsArray();
    if (v.Length < 4) return null;
    return new PartLink(v[1].Value as string ?? "", v[2].Value as string ?? "", v[3].Value?.ToString() ?? "");
  }

  internal static void WriteLink(Database database, Transaction transaction, DBObject part, string hostNetwork, string hostPart, string hostHandle)
  {
    XDataApps.Ensure(database, transaction, LinkApp);
    if (!part.IsWriteEnabled) part.UpgradeOpen();
    using var buffer = new ResultBuffer(
      new TypedValue((int)DxfCode.ExtendedDataRegAppName, LinkApp),
      new TypedValue((int)DxfCode.ExtendedDataAsciiString, hostNetwork),
      new TypedValue((int)DxfCode.ExtendedDataAsciiString, hostPart),
      new TypedValue((int)DxfCode.ExtendedDataAsciiString, hostHandle));
    part.XData = buffer;
  }

  private static void ClearLink(DBObject part)
  {
    if (ReadLink(part) == null) return;
    if (!part.IsWriteEnabled) part.UpgradeOpen();
    using var buffer = new ResultBuffer(new TypedValue((int)DxfCode.ExtendedDataRegAppName, LinkApp));
    part.XData = buffer;
  }

  private static Part FindPart(Network network, Transaction transaction, string name, OpenMode mode) =>
    network.GetPipeIds().Cast<ObjectId>().Concat(network.GetStructureIds().Cast<ObjectId>())
      .Select(id => (Part)transaction.GetObject(id, mode))
      .FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
    ?? throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Network '{network.Name}' has no part named '{name}'. Nothing was changed.");

  // ---------- C199 linkNetworkPart / getNetworkLinks ----------

  /// <summary>C199: links a part of one network to a part of another (a service's connection point to the host pipe or
  /// structure): {network, part, hostNetwork, hostPart} or {network, part, remove: true}. Stored as XData on the part;
  /// read by networkCrossings (no false conflict at the connection) and networkFittings (branches); addServiceConnection
  /// (C192) links its connection point itself. Use case: gap analysis point 3, a pipe cannot connect to a structure of another
  /// network, so services sat on a hidden break point and the plugin did not know they belonged to the host.</summary>
  public static Task<object?> LinkNetworkPartAsync(JsonObject? parameters)
  {
    var networkName = PluginRuntime.GetRequiredString(parameters, "network");
    var partName = PluginRuntime.GetRequiredString(parameters, "part");
    var remove = PluginRuntime.GetOptionalBool(parameters, "remove") ?? false;
    var hostName = PluginRuntime.GetOptionalString(parameters, "hostNetwork");
    var hostPartName = PluginRuntime.GetOptionalString(parameters, "hostPart");
    if (!remove && (string.IsNullOrWhiteSpace(hostName) || string.IsNullOrWhiteSpace(hostPartName)))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Give hostNetwork and hostPart, or remove: true. Nothing was changed.");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var network = FindPipeNetworkByName(civilDoc, transaction, networkName, OpenMode.ForRead);
      var part = FindPart(network, transaction, partName, OpenMode.ForWrite);
      if (remove) { ClearLink(part); return new Dictionary<string, object?> { ["network"] = network.Name, ["part"] = part.Name, ["removed"] = true }; }
      var host = FindPipeNetworkByName(civilDoc, transaction, hostName!, OpenMode.ForRead);
      var hp = FindPart(host, transaction, hostPartName!, OpenMode.ForRead);
      WriteLink(database, transaction, part, host.Name, hp.Name, hp.Handle.ToString());
      return new Dictionary<string, object?> { ["network"] = network.Name, ["part"] = part.Name, ["hostNetwork"] = host.Name, ["hostPart"] = hp.Name,
        ["hostKind"] = hp is Pipe ? "pipe" : "structure" };
    });
  }

  /// <summary>C199: lists the links of the parts of the given networks (default all): network, part, host network,
  /// host part, whether the host still exists.</summary>
  public static Task<object?> GetNetworkLinksAsync(JsonObject? parameters)
  {
    var networks = OptionalStringList(parameters, "networks");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var names = networks is { Count: > 0 } ? networks : ListPipeNetworkNames(civilDoc, transaction);
      var links = new List<Dictionary<string, object?>>();
      foreach (var nn in names)
      {
        var network = FindPipeNetworkByName(civilDoc, transaction, nn, OpenMode.ForRead);
        foreach (var id in network.GetPipeIds().Cast<ObjectId>().Concat(network.GetStructureIds().Cast<ObjectId>()))
        {
          var part = (Part)transaction.GetObject(id, OpenMode.ForRead);
          var l = ReadLink(part);
          if (l == null) continue;
          var exists = false;
          try { exists = database.TryGetObjectId(new Handle(Convert.ToInt64(l.HostHandle, 16)), out var hid) && !hid.IsErased; } catch { }
          links.Add(new Dictionary<string, object?> { ["network"] = network.Name, ["part"] = part.Name, ["hostNetwork"] = l.HostNetwork,
            ["hostPart"] = l.HostPart, ["hostExists"] = exists });
        }
      }
      return new Dictionary<string, object?> { ["links"] = links, ["count"] = links.Count };
    });
  }

  // ---------- C200 networkCrossings ----------

  private sealed record XPipe(string Network, Pipe Pipe, Point2d A, Point2d B);

  /// <summary>C200: every plan crossing between pipes of the given networks with over/under and the vertical clearance
  /// between the outer walls, and pipes that pass through a structure in plan: {networks, alignment?, minClearance=0.10,
  /// includeStructures=true, limit=500}. Pipes sharing a structure, and a service whose linked connection point (C199)
  /// sits on the other pipe or its structures, are not crossings. Station and offset along the alignment when given.
  /// Use case: gap analysis point 4, the crossing lists were computed in Python per project (several separate crossing
  /// scripts) and networkClearance did not read structures.</summary>
  public static Task<object?> NetworkCrossingsAsync(JsonObject? parameters)
  {
    var networks = OptionalStringList(parameters, "networks");
    var alignmentName = PluginRuntime.GetOptionalString(parameters, "alignment");
    var minClear = PluginRuntime.GetOptionalDouble(parameters, "minClearance") ?? 0.10;
    var withStructures = PluginRuntime.GetOptionalBool(parameters, "includeStructures") ?? true;
    var limit = Math.Clamp(PluginRuntime.GetOptionalInt(parameters, "limit") ?? 500, 1, 5000);
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var names = networks is { Count: > 0 } ? networks : ListPipeNetworkNames(civilDoc, transaction);
      var nets = names.Select(n => FindPipeNetworkByName(civilDoc, transaction, n, OpenMode.ForRead)).ToList();
      var pipes = new List<XPipe>();
      var structs = new List<(string Network, Structure S)>();
      foreach (var net in nets)
      {
        foreach (ObjectId id in net.GetPipeIds())
        {
          var p = CivilObjectUtils.GetRequiredObject<Pipe>(transaction, id, OpenMode.ForRead);
          pipes.Add(new XPipe(net.Name, p, XY(p.StartPoint), XY(p.EndPoint)));
        }
        foreach (ObjectId id in net.GetStructureIds())
          structs.Add((net.Name, CivilObjectUtils.GetRequiredObject<Structure>(transaction, id, OpenMode.ForRead)));
      }
      var byId = structs.ToDictionary(s => s.S.ObjectId, s => s.S);
      // a pipe's "anchor" handles: its own and, for a pipe at a linked connection point, the host part and the host's structures
      HashSet<string> Anchors(Pipe p)
      {
        var h = new HashSet<string> { p.Handle.ToString() };
        foreach (var sid in new[] { p.StartStructureId, p.EndStructureId })
        {
          if (sid.IsNull || !byId.TryGetValue(sid, out var s)) continue;
          h.Add(s.Handle.ToString());
          var l = ReadLink(s);
          if (l != null) h.Add(l.HostHandle);
        }
        return h;
      }
      var anchors = pipes.ToDictionary(x => x.Pipe.ObjectId, x => Anchors(x.Pipe));
      // the handles a host pipe owns: itself and its structures
      HashSet<string> Own(Pipe p)
      {
        var h = new HashSet<string> { p.Handle.ToString() };
        foreach (var sid in new[] { p.StartStructureId, p.EndStructureId })
          if (!sid.IsNull && byId.TryGetValue(sid, out var s)) h.Add(s.Handle.ToString());
        return h;
      }
      var own = pipes.ToDictionary(x => x.Pipe.ObjectId, x => Own(x.Pipe));
      Alignment? al = string.IsNullOrWhiteSpace(alignmentName) ? null : CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName!);
      (double? St, double? Off) Station(Point2d q)
      {
        if (al == null) return (null, null);
        try { double st = 0, off = 0; al.StationOffset(q.X, q.Y, ref st, ref off); return (Math.Round(st, 2), Math.Round(off, 2)); }
        catch { return (null, null); }
      }
      var crossings = new List<Dictionary<string, object?>>();
      for (var i = 0; i < pipes.Count; i++)
        for (var j = i + 1; j < pipes.Count; j++)
        {
          var a = pipes[i]; var b = pipes[j];
          if (anchors[a.Pipe.ObjectId].Overlaps(own[b.Pipe.ObjectId]) || anchors[b.Pipe.ObjectId].Overlaps(own[a.Pipe.ObjectId])) continue;
          var sa = new LineSegment2d(a.A, a.B); var sb = new LineSegment2d(b.A, b.B);
          var hits = sa.IntersectWith(sb);
          if (hits == null || hits.Length == 0) continue;
          var x = hits[0];
          double Zc(XPipe p) { var t = p.A.GetDistanceTo(p.B) < 1e-9 ? 0 : p.A.GetDistanceTo(x) / p.A.GetDistanceTo(p.B); return p.Pipe.StartPoint.Z + (p.Pipe.EndPoint.Z - p.Pipe.StartPoint.Z) * t; }
          double za = Zc(a), zb = Zc(b), ra = a.Pipe.OuterDiameterOrWidth / 2, rb = b.Pipe.OuterDiameterOrWidth / 2;
          var aOver = za > zb;
          var clear = aOver ? (za - ra) - (zb + rb) : (zb - rb) - (za + ra);
          var (st, off) = Station(x);
          crossings.Add(new Dictionary<string, object?>
          {
            ["upper"] = aOver ? $"{a.Network}/{a.Pipe.Name}" : $"{b.Network}/{b.Pipe.Name}",
            ["lower"] = aOver ? $"{b.Network}/{b.Pipe.Name}" : $"{a.Network}/{a.Pipe.Name}",
            ["clearance"] = Math.Round(clear, 3), ["ok"] = clear >= minClear - 0.0005,   // half a mm: a knee placed at exactly the clearance (C192) rounds to just under it
            ["x"] = Math.Round(x.X, 3), ["y"] = Math.Round(x.Y, 3), ["station"] = st, ["offset"] = off,
          });
        }
      var through = new List<Dictionary<string, object?>>();
      if (withStructures)
        foreach (var (sn, s) in structs)
        {
          var r = s.InnerDiameterOrWidth / 2;
          if (r < 0.01 || string.Equals(s.PartFamilyName, "Brytpunkt", StringComparison.OrdinalIgnoreCase)) continue;   // break points have no body
          var c = XY(s.Location);
          var sh = s.Handle.ToString();
          var sLink = ReadLink(s)?.HostHandle;
          foreach (var p in pipes)
          {
            if (p.Pipe.StartStructureId == s.ObjectId || p.Pipe.EndStructureId == s.ObjectId) continue;
            if (anchors[p.Pipe.ObjectId].Contains(sh) || (sLink != null && own[p.Pipe.ObjectId].Contains(sLink))) continue;
            var seg = new LineSegment2d(p.A, p.B);
            var q = seg.GetClosestPointTo(c).Point;
            var d = q.GetDistanceTo(c) - r - p.Pipe.OuterDiameterOrWidth / 2;
            if (d >= 0) continue;
            var t = seg.Length < 1e-9 ? 0 : p.A.GetDistanceTo(q) / seg.Length;
            var z = p.Pipe.StartPoint.Z + (p.Pipe.EndPoint.Z - p.Pipe.StartPoint.Z) * t;
            if (z + p.Pipe.OuterDiameterOrWidth / 2 < s.SumpElevation || z - p.Pipe.OuterDiameterOrWidth / 2 > s.RimElevation) continue;
            var (st, off) = Station(q);
            through.Add(new Dictionary<string, object?> { ["structure"] = $"{sn}/{s.Name}", ["pipe"] = $"{p.Network}/{p.Pipe.Name}",
              ["overlapPlan"] = Math.Round(-d, 3), ["pipeZ"] = Math.Round(z, 3), ["x"] = Math.Round(q.X, 3), ["y"] = Math.Round(q.Y, 3), ["station"] = st });
          }
        }
      var sorted = crossings.OrderBy(c => (double)c["clearance"]!).ToList();
      return new Dictionary<string, object?>
      {
        ["networks"] = names, ["minClearance"] = minClear, ["crossings"] = sorted.Take(limit).ToList(), ["crossingCount"] = sorted.Count,
        ["underMinClearance"] = sorted.Count(c => !(bool)c["ok"]!), ["pipesThroughStructures"] = through,
      };
    });
  }

  // ---------- C201 networkFittings ----------

  /// <summary>C201: fittings per network for the quantity list: bends at break points rounded to standard angles,
  /// tees (break points with three pipes), branches (linked service connections, C199: host size / service size, on a
  /// pipe or into a structure): {networkNames?, standardBends=[11.25, 22.5, 30, 45], tolDeg=3}. A deviation over the
  /// largest standard angle counts as several bends. Use case: gap analysis point 13, the project's quantity script left bends
  /// and couplings out.</summary>
  public static Task<object?> NetworkFittingsAsync(JsonObject? parameters)
  {
    var networks = OptionalStringList(parameters, "networkNames");
    var std = ((PluginRuntime.GetParameter(parameters, "standardBends") as JsonArray)?.Select(n => n!.GetValue<double>()).ToList()
      ?? new List<double> { 11.25, 22.5, 30, 45 }).OrderBy(x => x).ToList();
    var tol = PluginRuntime.GetOptionalDouble(parameters, "tolDeg") ?? 3;
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var names = networks is { Count: > 0 } ? networks : ListPipeNetworkNames(civilDoc, transaction);
      var result = new List<Dictionary<string, object?>>();
      foreach (var nn in names)
      {
        var net = FindPipeNetworkByName(civilDoc, transaction, nn, OpenMode.ForRead);
        var bends = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var tees = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var branches = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var detail = new List<Dictionary<string, object?>>();
        void Add(SortedDictionary<string, int> d, string k, int n = 1) => d[k] = d.TryGetValue(k, out var v) ? v + n : n;
        foreach (ObjectId sid in net.GetStructureIds())
        {
          var s = CivilObjectUtils.GetRequiredObject<Structure>(transaction, sid, OpenMode.ForRead);
          var ps = PipesAt(net, transaction, sid, OpenMode.ForRead);
          var link = ReadLink(s);
          if (link != null && ps.Count >= 1)
          {
            var dn = Math.Round(ps.Max(p => p.InnerDiameterOrWidth) * 1000);
            string hostSize = "?";
            try
            {
              if (database.TryGetObjectId(new Handle(Convert.ToInt64(link.HostHandle, 16)), out var hid) && !hid.IsErased)
              {
                var ho = transaction.GetObject(hid, OpenMode.ForRead);
                hostSize = ho is Pipe hp ? $"{Math.Round(hp.InnerDiameterOrWidth * 1000)}" : ho is Structure hs ? $"brunn {Math.Round(hs.InnerDiameterOrWidth * 1000)}" : "?";
              }
            }
            catch { }
            Add(branches, hostSize.StartsWith("brunn") ? $"anslutning i {hostSize} / {dn}" : $"grenrör {hostSize}/{dn}");
            continue;
          }
          if (!string.Equals(s.PartFamilyName, "Brytpunkt", StringComparison.OrdinalIgnoreCase)) continue;
          if (ps.Count == 3) { Add(tees, $"T {string.Join("/", ps.Select(p => Math.Round(p.InnerDiameterOrWidth * 1000)).OrderByDescending(x => x))}"); continue; }
          if (ps.Count != 2) continue;
          var c = XY(s.Location);
          Point2d Far(Pipe p) => p.StartStructureId == sid ? XY(p.EndPoint) : XY(p.StartPoint);
          var din = c - Far(ps[0]); var dout = Far(ps[1]) - c;
          if (din.Length < 1e-9 || dout.Length < 1e-9) continue;
          var dev = din.GetAngleTo(dout) * 180 / Math.PI;
          if (dev < tol) continue;
          var n = (int)Math.Ceiling((dev - tol) / std[^1]);
          var each = dev / Math.Max(n, 1);
          var nearest = std.OrderBy(x => Math.Abs(x - each)).First();
          var size = Math.Round(Math.Max(ps[0].InnerDiameterOrWidth, ps[1].InnerDiameterOrWidth) * 1000);
          Add(bends, $"böj {nearest}° DN{size}", Math.Max(n, 1));
          detail.Add(new Dictionary<string, object?> { ["structure"] = s.Name, ["deviationDeg"] = Math.Round(dev, 1), ["bends"] = $"{Math.Max(n, 1)} x {nearest}°",
            ["offStandardDeg"] = Math.Round(Math.Abs(each - nearest), 1) });
        }
        result.Add(new Dictionary<string, object?> { ["network"] = net.Name, ["bends"] = bends, ["tees"] = tees, ["branches"] = branches, ["bendDetail"] = detail });
      }
      return new Dictionary<string, object?> { ["standardBends"] = std, ["networks"] = result };
    });
  }

  // ---------- C202 applyRealSizeStructureStyles ----------

  /// <summary>C202: structure symbols at their real size in plan: {networkNames, families? (default all but Brytpunkt),
  /// sizes? {partFamily: mm} (overrides the part's inner diameter, e.g. a valve's body width), dryRun?}. For each structure
  /// a copy of its present style named "{base style} {mm}" (created once, reused) gets SizeType UseAbsoluteUnits and Size
  /// = the real size in metres; Civil scales the symbol so that its extent equals Size (plot check 2026-11-23). A style
  /// already named "{base} {mm}" counts as its base. Use case: gap analysis point 5, projects made the copies and assigned
  /// them in Python per family (the project scripts) after every network rebuild.</summary>
  public static Task<object?> ApplyRealSizeStructureStylesAsync(JsonObject? parameters)
  {
    var networks = OptionalStringList(parameters, "networkNames")
      ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "networkNames is required. Nothing was changed.");
    var families = OptionalStringList(parameters, "families");
    var sizes = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
    if (PluginRuntime.GetParameter(parameters, "sizes") is JsonObject so) foreach (var kv in so) sizes[kv.Key] = kv.Value!.GetValue<double>();
    var dryRun = PluginRuntime.GetOptionalBool(parameters, "dryRun") ?? false;
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var cdoc = (Autodesk.Civil.ApplicationServices.CivilDocument)civilDoc;
      var styles = new Dictionary<string, ObjectId>(StringComparer.OrdinalIgnoreCase);
      foreach (ObjectId id in cdoc.Styles.StructureStyles)
        if (transaction.GetObject(id, OpenMode.ForRead) is StructureStyle ss) styles[ss.Name] = id;
      var createdStyles = new List<string>();
      var assigned = new SortedDictionary<string, int>(StringComparer.Ordinal);
      var sizeType = (StructureSizeOptionsType)Enum.Parse(typeof(StructureSizeOptionsType), "UseAbsoluteUnits");
      foreach (var nn in networks)
      {
        var net = FindPipeNetworkByName(civilDoc, transaction, nn, OpenMode.ForRead);
        foreach (ObjectId sid in net.GetStructureIds())
        {
          var s = CivilObjectUtils.GetRequiredObject<Structure>(transaction, sid, OpenMode.ForRead);
          var fam = s.PartFamilyName ?? "";
          if (families != null ? !families.Contains(fam, StringComparer.OrdinalIgnoreCase) : string.Equals(fam, "Brytpunkt", StringComparison.OrdinalIgnoreCase)) continue;
          var mm = sizes.TryGetValue(fam, out var ov) ? ov : Math.Round(s.InnerDiameterOrWidth * 1000);
          if (mm <= 0) continue;
          var cur = (transaction.GetObject(s.StyleId, OpenMode.ForRead) as StructureStyle)?.Name ?? "";
          var sp = cur.LastIndexOf(' ');
          var baseName = sp > 0 && double.TryParse(cur[(sp + 1)..], out _) && styles.ContainsKey(cur[..sp]) ? cur[..sp] : cur;
          var target = $"{baseName} {mm:0}";
          if (!styles.TryGetValue(target, out var tid))
          {
            createdStyles.Add(target);
            if (dryRun) { assigned[target] = assigned.TryGetValue(target, out var c0) ? c0 + 1 : 1; continue; }
            var src = (StyleBase)transaction.GetObject(styles[baseName], OpenMode.ForRead);
            tid = src.CopyAsSibling(target);
            styles[target] = tid;
          }
          if (!dryRun)
          {
            var st = (StructureStyle)transaction.GetObject(tid, OpenMode.ForWrite);
            if (st.PlanOption.SizeType != sizeType) st.PlanOption.SizeType = sizeType;
            if (Math.Abs(st.PlanOption.Size - mm / 1000) > 1e-6) st.PlanOption.Size = mm / 1000;
            if (s.StyleId != tid) { s.UpgradeOpen(); s.StyleId = tid; }
          }
          assigned[target] = assigned.TryGetValue(target, out var c) ? c + 1 : 1;
        }
      }
      return new Dictionary<string, object?> { ["dryRun"] = dryRun, ["createdStyles"] = createdStyles, ["assigned"] = assigned };
    });
  }
}
