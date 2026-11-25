using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.DatabaseServices;

namespace Civil3DMcpPlugin;

/// <summary>C228 listServiceConnections (F7, 2026-11-24): the service connections of a drawing as data. Use case:
/// the service codes and the table per sheet were computed in Python from the raw networks in several project
/// scripts.</summary>
public static partial class PipeNetworkCommands
{
  /// <summary>C228: {serviceNetworks (names, * as a wildcard), hostNetworks? (default the hosts the links name, else
  /// every other network), alignment?, pipeTolerance=0.35 (an end this near a host pipe in plan is its connection),
  /// structureTolerance=1.0 (from a host structure's centre), freeEndTolerance=0.05 (pipe ends this near count as joined),
  /// limit?}. A service = the pipes of a service network joined by structures or ends within freeEndTolerance. Its
  /// connection: a structure linked to a host part (C199), else the end nearest a host pipe or structure within the
  /// tolerances. Its end (the property side): the end farthest from the connection, with the structure there.
  /// Per service: name (the stem of "{name} ans" or the end structure), network, pipes, host {network, part, kind,
  /// exists}, link status (linked | linkedHostMissing | unlinked | none), connection {x, y, invert, station, offset,
  /// inStructure}, dimension (nominal mm at the connection, "315/200" when it changes), outerDiameterMm, partFamily,
  /// material, end {kind cleanout|valve|gully|structure|free, name, partFamily, x, y, invert, rim, station}, length.
  /// Sorted by station (else name). Reads only.</summary>
  public static Task<object?> ListServiceConnectionsAsync(JsonObject? parameters)
  {
    var servicePatterns = OptionalStringList(parameters, "serviceNetworks")
      ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "serviceNetworks must list the service networks (names, * as a wildcard).");
    var hostPatterns = OptionalStringList(parameters, "hostNetworks");
    var alignmentName = PluginRuntime.GetOptionalString(parameters, "alignment");
    var pipeTol = PluginRuntime.GetOptionalDouble(parameters, "pipeTolerance") ?? 0.35;
    var structTol = PluginRuntime.GetOptionalDouble(parameters, "structureTolerance") ?? 1.0;
    var joinTol = PluginRuntime.GetOptionalDouble(parameters, "freeEndTolerance") ?? 0.05;
    var limit = Math.Clamp(PluginRuntime.GetOptionalInt(parameters, "limit") ?? 2000, 1, 20000);

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var all = ListPipeNetworkNames(civilDoc, transaction).Where(n => !string.IsNullOrEmpty(n)).ToList();
      var serviceNames = all.Where(n => servicePatterns.Any(p => NetworkNameMatch(p, n))).ToList();
      if (serviceNames.Count == 0)
        throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"No network matches {string.Join(", ", servicePatterns)}. Networks: {string.Join(", ", all)}.");
      var index = new PartIndex(all.Select(n => FindPipeNetworkByName(civilDoc, transaction, n, OpenMode.ForRead)), transaction);
      Alignment? al = string.IsNullOrWhiteSpace(alignmentName) ? null : CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName!);
      (double? St, double? Off) Station(Point2d q)
      {
        if (al == null) return (null, null);
        try { double st = 0, off = 0; al.StationOffset(q.X, q.Y, ref st, ref off); return (Math.Round(st, 2), Math.Round(off, 2)); }
        catch (Autodesk.AutoCAD.Runtime.Exception) { return (null, null); }
      }
      var byHandle = index.Parts.Values.ToDictionary(x => x.Part.Handle.ToString(), x => x, StringComparer.OrdinalIgnoreCase);
      var services = new List<Dictionary<string, object?>>();
      var counts = new Dictionary<string, int> { ["linked"] = 0, ["linkedHostMissing"] = 0, ["unlinked"] = 0, ["none"] = 0 };

      foreach (var sn in serviceNames)
      {
        var hostNames = hostPatterns is { Count: > 0 } ? all.Where(n => hostPatterns.Any(p => NetworkNameMatch(p, n))).ToHashSet(StringComparer.OrdinalIgnoreCase) : null;
        var pipes = index.Pipes.Where(x => x.Network == sn && XY(x.Pipe.StartPoint).GetDistanceTo(XY(x.Pipe.EndPoint)) > 1e-3).Select(x => x.Pipe).ToList();
        // the services: union-find over shared structures and joined free ends
        var parent = Enumerable.Range(0, pipes.Count).ToArray();
        int Root(int i) { while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; } return i; }
        for (var i = 0; i < pipes.Count; i++)
          for (var j = 0; j < i; j++)
          {
            var a = pipes[i]; var b = pipes[j];
            var shared = new[] { a.StartStructureId, a.EndStructureId }.Where(x => !x.IsNull).Intersect(new[] { b.StartStructureId, b.EndStructureId }.Where(x => !x.IsNull)).Any();
            var near = new[] { a.StartPoint, a.EndPoint }.Any(p => new[] { b.StartPoint, b.EndPoint }.Any(q => XY(p).GetDistanceTo(XY(q)) < joinTol));
            if (shared || near) parent[Root(i)] = Root(j);
          }
        foreach (var group in Enumerable.Range(0, pipes.Count).GroupBy(Root).Select(g => g.Select(i => pipes[i]).ToList()))
        {
          var structIds = group.SelectMany(p => new[] { p.StartStructureId, p.EndStructureId }).Where(id => !id.IsNull && index.Parts.ContainsKey(id)).Distinct().ToList();
          var structs = structIds.Select(id => (Structure)index.Parts[id].Part).ToList();
          // the connection: a linked structure first
          Structure? connS = null; PartLink? link = null;
          foreach (var s in structs)
          {
            var l = ReadLink(s);
            if (l == null) continue;
            if (hostNames != null && !hostNames.Contains(l.HostNetwork)) continue;
            connS = s; link = l; break;
          }
          string status;
          Point2d conn;
          (string Network, Part Part)? host = null;
          var hostExists = false;
          Pipe connPipe;
          bool connAtStart;
          if (connS != null)
          {
            hostExists = byHandle.TryGetValue(link!.HostHandle, out var h);
            if (hostExists) host = h;
            status = hostExists ? "linked" : "linkedHostMissing";
            conn = XY(connS.Location);
            connPipe = group.First(p => p.StartStructureId == connS.ObjectId || p.EndStructureId == connS.ObjectId);
            connAtStart = connPipe.StartStructureId == connS.ObjectId;
          }
          else
          {
            // geometric: the end nearest a host pipe (pipeTolerance) or host structure centre (structureTolerance)
            var hosts = index.Parts.Values.Where(x => x.Network != sn && (hostNames == null ? !serviceNames.Contains(x.Network) : hostNames.Contains(x.Network))).ToList();
            var best = (D: double.MaxValue, Pipe: (Pipe?)null, Start: false, Host: ((string, Part)?)null);
            foreach (var p in group)
              foreach (var start in new[] { true, false })
              {
                var q = XY(start ? p.StartPoint : p.EndPoint);
                foreach (var hx in hosts)
                {
                  double d;
                  if (hx.Part is Pipe hp)
                  {
                    var a = XY(hp.StartPoint); var b = XY(hp.EndPoint);
                    d = Lerp(a, b, Project(q, a, b)).GetDistanceTo(q);
                    if (d > pipeTol) continue;
                  }
                  else
                  {
                    var hs = (Structure)hx.Part;
                    if (InlineFamilies.Contains(hs.PartFamilyName ?? "")) continue;
                    d = XY(hs.Location).GetDistanceTo(q);
                    if (d > structTol) continue;
                  }
                  if (d < best.D) best = (d, p, start, hx);
                }
              }
            if (best.Pipe != null)
            {
              status = "unlinked"; host = best.Host; hostExists = true;
              connPipe = best.Pipe; connAtStart = best.Start;
              conn = XY(connAtStart ? connPipe.StartPoint : connPipe.EndPoint);
            }
            else
            {
              // none: the lowest end is taken as the connection side
              status = "none";
              var low = group.SelectMany(p => new[] { (p, true, p.StartPoint.Z - p.InnerDiameterOrWidth / 2), (p, false, p.EndPoint.Z - p.InnerDiameterOrWidth / 2) }).OrderBy(x => x.Item3).First();
              connPipe = low.p; connAtStart = low.Item2;
              conn = XY(connAtStart ? connPipe.StartPoint : connPipe.EndPoint);
            }
          }
          counts[status]++;
          var connEnd = connAtStart ? connPipe.StartPoint : connPipe.EndPoint;
          var invert = connEnd.Z - connPipe.InnerDiameterOrWidth / 2;
          // the end: the pipe end farthest from the connection
          var far = group.SelectMany(p => new[] { (P: p, Start: true), (P: p, Start: false) })
            .OrderByDescending(x => XY(x.Start ? x.P.StartPoint : x.P.EndPoint).GetDistanceTo(conn)).First();
          var farPt = far.Start ? far.P.StartPoint : far.P.EndPoint;
          var farSid = far.Start ? far.P.StartStructureId : far.P.EndStructureId;
          var endS = !farSid.IsNull && index.Parts.TryGetValue(farSid, out var es) ? (Structure)es.Part : null;
          string endKind = "free";
          if (endS != null)
          {
            var fam = (endS.PartFamilyName ?? "") + " " + (endS.PartSizeName ?? "");
            endKind = fam.Contains("rensbrunn", StringComparison.OrdinalIgnoreCase) || fam.Contains("spolbrunn", StringComparison.OrdinalIgnoreCase) || fam.Contains("spolpost", StringComparison.OrdinalIgnoreCase) ? "cleanout"
              : fam.Contains("ventil", StringComparison.OrdinalIgnoreCase) ? "valve"
              : fam.Contains("rännsten", StringComparison.OrdinalIgnoreCase) || fam.Contains("dagvattenbrunn", StringComparison.OrdinalIgnoreCase) ? "gully"
              : InlineFamilies.Contains(endS.PartFamilyName ?? "") ? "free" : "structure";
          }
          var dimA = NominalDiameterMm(connPipe).Mm;
          var dimF = NominalDiameterMm(far.P).Mm;
          static string Mm(double v) => v.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
          var nameS = connS?.Name ?? structs.FirstOrDefault(s => s.Name.EndsWith(" ans", StringComparison.OrdinalIgnoreCase))?.Name;
          var name = nameS != null && nameS.EndsWith(" ans", StringComparison.OrdinalIgnoreCase) ? nameS[..^4] : endS?.Name ?? group[0].Name;
          var (cst, coff) = Station(conn);
          var (est, _) = Station(XY(farPt));
          var inStructure = host?.Part is Structure;
          services.Add(new Dictionary<string, object?>
          {
            ["name"] = name, ["network"] = sn, ["pipes"] = group.Select(p => p.Name).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(),
            ["status"] = status,
            ["host"] = host == null ? (link == null ? null : new Dictionary<string, object?> { ["network"] = link.HostNetwork, ["part"] = link.HostPart, ["exists"] = false })
              : new Dictionary<string, object?> { ["network"] = host.Value.Network, ["part"] = host.Value.Part.Name, ["kind"] = host.Value.Part is Pipe ? "pipe" : "structure", ["exists"] = hostExists,
                  ["nominalDiameterMm"] = host.Value.Part is Pipe hpp ? NominalDiameterMm(hpp).Mm : null },
            ["connection"] = new Dictionary<string, object?>
            {
              ["x"] = Math.Round(conn.X, 3), ["y"] = Math.Round(conn.Y, 3), ["invert"] = Math.Round(invert, 3), ["station"] = cst, ["offset"] = coff,
              ["structure"] = connS?.Name, ["inStructure"] = inStructure, ["pipe"] = connPipe.Name,
            },
            ["dimension"] = Math.Abs(dimA - dimF) < 0.05 ? Mm(dimA) : $"{Mm(dimA)}/{Mm(dimF)}",
            ["nominalDiameterMm"] = dimA, ["outerDiameterMm"] = Math.Round(connPipe.OuterDiameterOrWidth * 1000, 1),
            ["partFamily"] = ReadField(() => connPipe.PartFamilyName), ["material"] = ReadField(() => connPipe.Material),
            ["end"] = new Dictionary<string, object?>
            {
              ["kind"] = endKind, ["name"] = endS?.Name, ["partFamily"] = endS?.PartFamilyName,
              ["x"] = Math.Round(farPt.X, 3), ["y"] = Math.Round(farPt.Y, 3), ["invert"] = Math.Round(farPt.Z - far.P.InnerDiameterOrWidth / 2, 3),
              ["rim"] = endS == null ? null : Math.Round(endS.RimElevation, 3), ["station"] = est,
            },
            ["length"] = Math.Round(group.Sum(p => XY(p.StartPoint).GetDistanceTo(XY(p.EndPoint))), 3),
          });
        }
      }
      var sorted = services.OrderBy(s => ((Dictionary<string, object?>)s["connection"]!)["station"] as double? ?? double.MaxValue)
        .ThenBy(s => (string)s["network"]!, StringComparer.OrdinalIgnoreCase).ThenBy(s => (string)s["name"]!, StringComparer.OrdinalIgnoreCase).ToList();
      return new Dictionary<string, object?>
      {
        ["serviceNetworks"] = serviceNames, ["alignment"] = al?.Name, ["count"] = sorted.Count, ["counts"] = counts,
        ["services"] = sorted.Take(limit).ToList(),
      };
    });
  }
}
