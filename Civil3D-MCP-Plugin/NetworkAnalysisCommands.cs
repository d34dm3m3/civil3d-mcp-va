using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;

namespace Civil3DMcpPlugin;

/// <summary>
/// Design checks and quantities for pipe networks: free distance between pipes in 3D (outer wall to outer
/// wall) within and between networks, and lengths and counts per network and run ("{prefix}-…" names).
/// </summary>
public static class NetworkAnalysisCommands
{
  private sealed record PipeSeg(string Network, string Name, string Run, Point3d A, Point3d B, double Radius, ObjectId S0, ObjectId S1);

  private static string RunOf(string name)
  {
    var dash = name.LastIndexOf('-');
    return dash > 0 ? name[..dash] : name;
  }

  private static List<PipeSeg> ReadPipes(object civilDoc, Transaction transaction, IEnumerable<string> networkNames)
  {
    var segs = new List<PipeSeg>();
    foreach (var networkName in networkNames)
    {
      var network = PipeNetworkCommands.FindPipeNetworkByName(civilDoc, transaction, networkName, OpenMode.ForRead);
      foreach (ObjectId id in network.GetPipeIds())
      {
        var pipe = CivilObjectUtils.GetRequiredObject<Pipe>(transaction, id, OpenMode.ForRead);
        var name = pipe.Name ?? id.Handle.ToString();
        segs.Add(new PipeSeg(network.Name, name, RunOf(name), pipe.StartPoint, pipe.EndPoint, pipe.OuterDiameterOrWidth / 2.0,
          pipe.StartStructureId, pipe.EndStructureId));
      }
    }

    return segs;
  }

  /// <summary>Closest points between segments p0-p1 and q0-q1 (clamped), after Ericson.</summary>
  internal static (double Distance, Point3d OnP, Point3d OnQ) SegmentDistance(Point3d p0, Point3d p1, Point3d q0, Point3d q1)
  {
    var d1 = p1 - p0;
    var d2 = q1 - q0;
    var r = p0 - q0;
    double a = d1.DotProduct(d1), e = d2.DotProduct(d2), f = d2.DotProduct(r);
    double s, t;
    if (a <= 1e-12 && e <= 1e-12)
    {
      s = t = 0;
    }
    else if (a <= 1e-12)
    {
      s = 0;
      t = Math.Clamp(f / e, 0, 1);
    }
    else
    {
      var c = d1.DotProduct(r);
      if (e <= 1e-12)
      {
        t = 0;
        s = Math.Clamp(-c / a, 0, 1);
      }
      else
      {
        var b = d1.DotProduct(d2);
        var denom = a * e - b * b;
        s = denom > 1e-12 ? Math.Clamp((b * f - c * e) / denom, 0, 1) : 0;
        t = (b * s + f) / e;
        if (t < 0)
        {
          t = 0;
          s = Math.Clamp(-c / a, 0, 1);
        }
        else if (t > 1)
        {
          t = 1;
          s = Math.Clamp((b - c) / a, 0, 1);
        }
      }
    }

    var onP = p0 + d1 * s;
    var onQ = q0 + d2 * t;
    return (onP.DistanceTo(onQ), onP, onQ);
  }

  /// <summary>Pipe pairs whose free distance (outer wall to outer wall, negative = overlap) is below
  /// minClearance. Pipes sharing a structure are skipped, and so are pairs in the same run unless
  /// includeSameRun; ignorePairs lists run pairs that are meant to overlap (a pipe inside its casing).</summary>
  public static Task<object?> NetworkClearanceAsync(JsonObject? parameters)
  {
    var networks = (PluginRuntime.GetParameter(parameters, "networks") as JsonArray)?.Select(n => n?.GetValue<string>()).OfType<string>().Distinct().ToList();
    if (networks == null || networks.Count == 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "networks must list at least one pipe network.");
    var minClearance = PluginRuntime.GetOptionalDouble(parameters, "minClearance") ?? 0.0;
    var includeSameRun = PluginRuntime.GetOptionalBool(parameters, "includeSameRun") ?? false;
    var limit = Math.Clamp(PluginRuntime.GetOptionalInt(parameters, "limit") ?? 100, 1, 5000);
    var ignored = new HashSet<(string, string)>();
    if (PluginRuntime.GetParameter(parameters, "ignorePairs") is JsonArray pairs)
    {
      foreach (var pair in pairs.OfType<JsonArray>())
      {
        var a = pair.ElementAtOrDefault(0)?.GetValue<string>();
        var b = pair.ElementAtOrDefault(1)?.GetValue<string>();
        if (a == null || b == null) continue;
        ignored.Add((a.ToUpperInvariant(), b.ToUpperInvariant()));
        ignored.Add((b.ToUpperInvariant(), a.ToUpperInvariant()));
      }
    }

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var segs = ReadPipes(civilDoc, transaction, networks);
      var hits = new List<(double Clearance, PipeSeg P, PipeSeg Q, Point3d At)>();
      long pairsChecked = 0;
      for (var i = 0; i < segs.Count; i++)
      {
        var p = segs[i];
        for (var j = i + 1; j < segs.Count; j++)
        {
          var q = segs[j];
          if (!includeSameRun && p.Network == q.Network && string.Equals(p.Run, q.Run, StringComparison.OrdinalIgnoreCase)) continue;
          if (ignored.Contains((p.Run.ToUpperInvariant(), q.Run.ToUpperInvariant()))) continue;
          if ((p.S0 == q.S0 || p.S0 == q.S1 || p.S1 == q.S0 || p.S1 == q.S1) && !p.S0.IsNull && p.Network == q.Network) continue;
          // bounding box prefilter
          var reach = p.Radius + q.Radius + Math.Max(minClearance, 0) + 1e-6;
          if (Math.Min(p.A.X, p.B.X) - reach > Math.Max(q.A.X, q.B.X) || Math.Min(q.A.X, q.B.X) - reach > Math.Max(p.A.X, p.B.X)) continue;
          if (Math.Min(p.A.Y, p.B.Y) - reach > Math.Max(q.A.Y, q.B.Y) || Math.Min(q.A.Y, q.B.Y) - reach > Math.Max(p.A.Y, p.B.Y)) continue;
          pairsChecked++;
          var (distance, onP, onQ) = SegmentDistance(p.A, p.B, q.A, q.B);
          var clearance = distance - p.Radius - q.Radius;
          if (clearance < minClearance)
            hits.Add((clearance, p, q, new Point3d((onP.X + onQ.X) / 2, (onP.Y + onQ.Y) / 2, (onP.Z + onQ.Z) / 2)));
        }
      }

      // one entry per run pair: the tightest spot, plus every separate spot (hits within SpotRadius of a
      // tighter one belong to it). Only the tightest spot hid a second conflict of the same pair elsewhere
      // (live 2026-10-06: V200/V160 0,21 at J behind the accepted 0,17 at the tie-in).
      var worst = hits
        .GroupBy(h => string.CompareOrdinal(h.P.Run, h.Q.Run) <= 0 ? $"{h.P.Run}|{h.Q.Run}" : $"{h.Q.Run}|{h.P.Run}")
        .Select(g => (Count: g.Count(), Hit: g.OrderBy(h => h.Clearance).First(), Spots: Spots(g)))
        .OrderBy(x => x.Hit.Clearance)
        .ToList();

      return new Dictionary<string, object?>
      {
        ["networks"] = networks,
        ["minClearance"] = minClearance,
        ["pipes"] = segs.Count,
        ["pairsChecked"] = pairsChecked,
        ["conflictingPipePairs"] = hits.Count,
        ["runPairs"] = worst.Take(limit).Select(x => new Dictionary<string, object?>
        {
          ["runA"] = $"{x.Hit.P.Network}/{x.Hit.P.Run}",
          ["runB"] = $"{x.Hit.Q.Network}/{x.Hit.Q.Run}",
          ["clearance"] = Math.Round(x.Hit.Clearance, 3),
          ["pipeA"] = x.Hit.P.Name,
          ["pipeB"] = x.Hit.Q.Name,
          ["at"] = new[] { Math.Round(x.Hit.At.X, 3), Math.Round(x.Hit.At.Y, 3), Math.Round(x.Hit.At.Z, 3) },
          ["pipePairsBelow"] = x.Count,
          ["spots"] = x.Spots.Select(s => new Dictionary<string, object?>
          {
            ["clearance"] = Math.Round(s.Hit.Clearance, 3),
            ["pipeA"] = s.Hit.P.Name,
            ["pipeB"] = s.Hit.Q.Name,
            ["at"] = new[] { Math.Round(s.Hit.At.X, 3), Math.Round(s.Hit.At.Y, 3), Math.Round(s.Hit.At.Z, 3) },
            ["pipePairsBelow"] = s.Count,
          }).ToList(),
        }).ToList(),
        ["spots"] = worst.Sum(x => x.Spots.Count),
      };
    });
  }

  internal const double SpotRadius = 5.0;

  /// <summary>Separate conflict spots of one run pair, tightest first: hits closer than SpotRadius (plan) to
  /// any hit of a spot belong to it, so a continuous stretch is one spot reported at its tightest point.</summary>
  private static List<(int Count, (double Clearance, PipeSeg P, PipeSeg Q, Point3d At) Hit)> Spots(
    IEnumerable<(double Clearance, PipeSeg P, PipeSeg Q, Point3d At)> hits)
  {
    var list = hits.ToList();
    var spot = Enumerable.Range(0, list.Count).ToArray();          // union-find over hits
    int Find(int i) => spot[i] == i ? i : spot[i] = Find(spot[i]);
    for (var i = 0; i < list.Count; i++)
      for (var j = i + 1; j < list.Count; j++)
        if (Math.Sqrt(Math.Pow(list[i].At.X - list[j].At.X, 2) + Math.Pow(list[i].At.Y - list[j].At.Y, 2)) <= SpotRadius)
          spot[Find(i)] = Find(j);
    return Enumerable.Range(0, list.Count).GroupBy(Find)
      .Select(g => (g.Count(), list[g.OrderBy(i => list[i].Clearance).First()]))
      .OrderBy(s => s.Item2.Clearance)
      .ToList();
  }

  /// <summary>Lengths and counts per network and run: pipes (count, 2D/3D length, family, sizes) and
  /// structures per family.</summary>
  public static Task<object?> NetworkQuantitiesAsync(JsonObject? parameters)
  {
    var networks = (PluginRuntime.GetParameter(parameters, "networks") as JsonArray)?.Select(n => n?.GetValue<string>()).OfType<string>().ToList();
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var names = networks is { Count: > 0 } ? networks
        : PipeNetworkCommands.ListPipeNetworkNames(civilDoc, transaction);
      var result = new List<Dictionary<string, object?>>();
      foreach (var networkName in names)
      {
        var network = PipeNetworkCommands.FindPipeNetworkByName(civilDoc, transaction, networkName, OpenMode.ForRead);
        var runs = new SortedDictionary<string, Dictionary<string, object?>>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, object?> RunEntry(string run)
        {
          if (!runs.TryGetValue(run, out var entry))
          {
            entry = new Dictionary<string, object?>
            {
              ["run"] = run, ["pipes"] = 0, ["length2d"] = 0.0, ["length3d"] = 0.0,
              ["pipeFamilies"] = new SortedSet<string>(), ["innerDiameters"] = new SortedSet<double>(), ["outerDiameters"] = new SortedSet<double>(),
              ["structures"] = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase),
            };
            runs[run] = entry;
          }

          return entry;
        }

        foreach (ObjectId id in network.GetPipeIds())
        {
          var pipe = CivilObjectUtils.GetRequiredObject<Pipe>(transaction, id, OpenMode.ForRead);
          var e = RunEntry(RunOf(pipe.Name ?? ""));
          e["pipes"] = (int)e["pipes"]! + 1;
          e["length2d"] = (double)e["length2d"]! + pipe.Length2D;
          e["length3d"] = (double)e["length3d"]! + pipe.Length3D;
          ((SortedSet<string>)e["pipeFamilies"]!).Add(pipe.PartFamilyName ?? "");
          ((SortedSet<double>)e["innerDiameters"]!).Add(Math.Round(pipe.InnerDiameterOrWidth * 1000, 1));
          ((SortedSet<double>)e["outerDiameters"]!).Add(Math.Round(pipe.OuterDiameterOrWidth * 1000, 1));
        }

        foreach (ObjectId id in network.GetStructureIds())
        {
          var structure = CivilObjectUtils.GetRequiredObject<Structure>(transaction, id, OpenMode.ForRead);
          var counts = (SortedDictionary<string, int>)RunEntry(RunOf(structure.Name ?? ""))["structures"]!;
          var family = structure.PartFamilyName ?? "";
          counts[family] = counts.TryGetValue(family, out var n) ? n + 1 : 1;
        }

        foreach (var e in runs.Values)
        {
          e["length2d"] = Math.Round((double)e["length2d"]!, 2);
          e["length3d"] = Math.Round((double)e["length3d"]!, 2);
        }

        result.Add(new Dictionary<string, object?> { ["network"] = network.Name, ["runs"] = runs.Values.ToList() });
      }

      return new Dictionary<string, object?> { ["networks"] = result };
    });
  }
}
