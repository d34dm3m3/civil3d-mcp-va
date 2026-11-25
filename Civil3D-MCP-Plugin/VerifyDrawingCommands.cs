using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.DatabaseServices;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;

namespace Civil3DMcpPlugin;

/// <summary>C227 (F6, 2026-11-24): verifyDrawing, the network checks of a drawing against the rule profile and the
/// source rules, pass or fail per rule with the objects and their position. Use case: a regression script decided pass
/// by searching printed text, and correction scripts and check plots per project did the checks in Python.</summary>
public static partial class PipeNetworkCommands
{
  private sealed class VerifyRule
  {
    public string Id = "";
    public string Title = "";
    public string Level = "error";
    public string? Source;
    public object? Required;
    public int Checked;
    public bool Missing;
    public string? MissingReason;
    public List<Dictionary<string, object?>> Items = new();
  }

  /// <summary>Which medium (source_rules.json medier key) a network belongs to: the longest civilTyp (S, D, Dr, TS, LTA, V)
  /// the name starts with, followed by a non-letter or the end ("S_Main", "S-serv" = spill).</summary>
  private static string? MediumOf(string network, List<(string Medium, string Type)> types) =>
    types.Where(t => network.StartsWith(t.Type, StringComparison.OrdinalIgnoreCase)
                     && (network.Length == t.Type.Length || !char.IsLetter(network[t.Type.Length])))
      .OrderByDescending(t => t.Type.Length).Select(t => t.Medium).FirstOrDefault();

  private static string LevelOf(string? styrka) => styrka is null or "skall" ? "error" : "warning";

  /// <summary>C227 verifyDrawing {networks? (default all), checks? (rule ids or their first part: freeEnds,
  /// pipesThroughStructures, crossings, crossingOrder, cover, minSlope, maxBend), kommun? | drawingPath?, rulesPath?,
  /// alignment? (station and offset of the items), coverStep? (1.0), servicePatterns? (["*serv*"]: networks whose gravity
  /// pipes take the service slope), limit? (50 items per rule)}. Rules: free pipe ends; pipes through structures; crossings
  /// against the profile's korsning.regler (else korsning.clearanceM); the order in korsning.regler (a pair rule V* over S*
  /// fails when S lies over V); cover per medium against source_rules.json medier.X.minTackningM (styrka), for vatten else
  /// the least of the profile's vatten.tackningHjassaM; minimum slope per gravity medium against medier.X.minLutningPromille
  /// (the row with the largest dimMm at or under the pipe's inner diameter; service networks minLutningServisPromille);
  /// bends over vatten.maxBojGrader at break points and fittings with two pipes. Answer: ok (no failed rule of level
  /// error; styrka values other than "skall" in the source are level warning), rules [{id, title, status pass|fail|missing, level, count,
  /// checked, required, source, items}], summary, media per network, the profile's kommun and files. Reads only.</summary>
  public static Task<object?> VerifyDrawingAsync(JsonObject? parameters)
  {
    var networks = OptionalStringList(parameters, "networks");
    var checks = OptionalStringList(parameters, "checks");
    var alignmentName = PluginRuntime.GetOptionalString(parameters, "alignment");
    var step = PluginRuntime.GetOptionalDouble(parameters, "coverStep") ?? 1.0;
    var limit = Math.Clamp(PluginRuntime.GetOptionalInt(parameters, "limit") ?? 50, 1, 5000);
    var servicePatterns = OptionalStringList(parameters, "servicePatterns") ?? new List<string> { "*serv*" };
    var loaded = RulesProfile.Load(PluginRuntime.GetOptionalString(parameters, "rulesPath"), PluginRuntime.GetOptionalString(parameters, "kommun"),
      PluginRuntime.GetOptionalString(parameters, "drawingPath"));
    var source = RulesProfile.ReadSource(loaded);
    var prof = loaded.Merged;
    double? D(JsonNode? n) => n is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;
    string? T(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    // crossings: the profile's pair rules
    var minClear = D(RulesProfile.At(prof, "korsning.clearanceM")) ?? 0.10;
    var crossRules = new List<CrossingRule>();
    if (RulesProfile.At(prof, "korsning.regler") is JsonArray ra)
      crossRules = ReadCrossingRules(ra, "korsning.regler");
    var order = crossRules.Where(r => r.Upper != "*" && r.Lower != "*").ToList();
    var maxBend = D(RulesProfile.At(prof, "vatten.maxBojGrader"));
    var types = new List<(string Medium, string Type)>();
    if (source?["medier"] is JsonObject medier)
      foreach (var (mk, mv) in medier)
        if (mv?["civilTyper"] is JsonArray ct)
          foreach (var t in ct) if (T(t) is string ts) types.Add((mk, ts));

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var names = networks is { Count: > 0 } ? networks : ListPipeNetworkNames(civilDoc, transaction).Where(n => !string.IsNullOrEmpty(n)).ToList();
      var nets = names.Select(n => FindPipeNetworkByName(civilDoc, transaction, n, OpenMode.ForRead)).ToList();
      Alignment? al = string.IsNullOrWhiteSpace(alignmentName) ? null : CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName!);
      void Place(Dictionary<string, object?> item, Point2d q)
      {
        item["x"] = Math.Round(q.X, 3); item["y"] = Math.Round(q.Y, 3);
        if (al == null) return;
        try { double st = 0, off = 0; al.StationOffset(q.X, q.Y, ref st, ref off); item["station"] = Math.Round(st, 2); item["offset"] = Math.Round(off, 2); }
        catch { item["station"] = null; }
      }
      bool Want(string id) => checks == null || checks.Any(c => string.Equals(c, id, StringComparison.OrdinalIgnoreCase)
                                                               || id.StartsWith(c + ".", StringComparison.OrdinalIgnoreCase));
      var pipesOf = nets.ToDictionary(n => n.Name, n => n.GetPipeIds().Cast<ObjectId>().Select(id => CivilObjectUtils.GetRequiredObject<Pipe>(transaction, id, OpenMode.ForRead)).ToList());
      var media = nets.ToDictionary(n => n.Name, n => MediumOf(n.Name, types));
      var rules = new List<VerifyRule>();

      // free pipe ends
      if (Want("freeEnds"))
      {
        var r = new VerifyRule { Id = "freeEnds", Title = "Pipe ends connected to a structure or break point", Source = "plugin" };
        foreach (var n in nets)
          foreach (var p in pipesOf[n.Name])
          {
            r.Checked++;
            foreach (var (sid, end, pt) in new[] { (p.StartStructureId, "start", p.StartPoint), (p.EndStructureId, "end", p.EndPoint) })
              if (sid.IsNull) { var it = new Dictionary<string, object?> { ["network"] = n.Name, ["pipe"] = p.Name, ["end"] = end }; Place(it, XY(pt)); r.Items.Add(it); }
          }
        rules.Add(r);
      }

      // crossings and pipes through structures
      if (Want("crossings") || Want("crossingOrder") || Want("pipesThroughStructures"))
      {
        var (crossings, through) = ComputeCrossings(transaction, nets, crossRules, minClear, true, al);
        if (Want("pipesThroughStructures"))
          rules.Add(new VerifyRule { Id = "pipesThroughStructures", Title = "No pipes through structures in plan", Source = "plugin", Checked = nets.Sum(n => n.GetStructureIds().Count), Items = through });
        if (Want("crossings"))
        {
          var r = new VerifyRule { Id = "crossings", Title = "Clearance at crossings per korsning.regler", Source = crossRules.Count > 0 ? "profile korsning.regler" : "profile korsning.clearanceM",
            Required = crossRules.Count > 0 ? crossRules.Select(x => $"{x.Upper} over {x.Lower}: {x.Clearance:0.00}").Append($"others: {minClear:0.00}").ToList() : minClear, Checked = crossings.Count };
          r.Items = crossings.Where(c => !(bool)c["ok"]!).ToList();
          rules.Add(r);
        }
        if (Want("crossingOrder"))
        {
          var r = new VerifyRule { Id = "crossingOrder", Title = "Order in korsning.regler (V never under S)", Source = "profile korsning.regler (pair rules)", Checked = crossings.Count,
            Required = order.Select(x => $"{x.Upper} over {x.Lower}").ToList() };
          if (order.Count == 0) { r.Missing = true; r.MissingReason = "korsning.regler has no pair rule"; }
          foreach (var c in crossings)
          {
            var up = (string)c["upperNetwork"]!; var low = (string)c["lowerNetwork"]!;
            var broken = order.FirstOrDefault(o => NetworkNameMatch(o.Lower, up) && NetworkNameMatch(o.Upper, low));
            if (broken != null) r.Items.Add(new Dictionary<string, object?>(c) { ["rule"] = $"{broken.Upper} over {broken.Lower}" });
          }
          rules.Add(r);
        }
      }

      // cover per medium
      if (Want("cover"))
        foreach (var g in nets.GroupBy(n => media[n.Name] ?? "").OrderBy(g => g.Key))
        {
          var medium = g.Key;
          if (medium.Length == 0 && g.All(n => pipesOf[n.Name].Count == 0)) continue;   // empty networks of no medium
          var r = new VerifyRule { Id = "cover." + (medium.Length == 0 ? "unknown" : medium), Title = $"Minimum cover to the crown ({(medium.Length == 0 ? "unknown medium" : medium)})" };
          var node = medium.Length == 0 ? null : source?["medier"]?[medium]?["minTackningM"];
          var need = D(node?["varde"]);
          if (need != null) { r.Source = $"source_rules.json medier.{medium}.minTackningM ({T(node?["kalla"])})"; r.Level = LevelOf(T(node?["styrka"])); }
          else if (medium == "vatten" && RulesProfile.At(prof, "vatten.tackningHjassaM") is JsonArray tk && tk.Count > 0)
          { need = tk.Select(D).Where(x => x != null).Min(); r.Source = "profile vatten.tackningHjassaM (least)"; }
          r.Required = need;
          if (need == null)
          {
            r.Missing = true;
            r.MissingReason = medium.Length == 0 ? $"the network belongs to no medium in source_rules.json ({string.Join(", ", g.Select(n => n.Name))})" : T(node?["anm"]) ?? "not in the source rules or the profile";
            r.Checked = g.Sum(n => pipesOf[n.Name].Count);
            rules.Add(r);
            continue;
          }
          foreach (var n in g)
          {
            var surface = NetworkSurface(n, transaction);
            foreach (var p in pipesOf[n.Name])
            {
              if (surface == null) { r.Items.Add(new Dictionary<string, object?> { ["network"] = n.Name, ["pipe"] = p.Name, ["error"] = "the network has no reference surface" }); continue; }
              var c = CoverAlong(p, surface, step, need);
              r.Checked++;
              if (double.IsNaN(c.Min) || c.Min >= need.Value - 0.0005) continue;
              var L = XY(p.StartPoint).GetDistanceTo(XY(p.EndPoint));
              var at = L < 1e-9 ? XY(p.StartPoint) : XY(p.StartPoint) + (XY(p.EndPoint) - XY(p.StartPoint)) * (c.AtStation / L);
              var it = new Dictionary<string, object?> { ["network"] = n.Name, ["pipe"] = p.Name, ["minCover"] = Math.Round(c.Min, 3), ["required"] = need,
                ["atPipeStation"] = Math.Round(c.AtStation, 2), ["shareBelow"] = Math.Round(c.ShareBelow, 3) };
              Place(it, at);
              r.Items.Add(it);
            }
          }
          rules.Add(r);
        }

      // minimum slope per gravity medium
      if (Want("minSlope"))
        foreach (var g in nets.Where(n => media[n.Name] != null).GroupBy(n => media[n.Name]!).OrderBy(g => g.Key))
        {
          var medium = g.Key;
          var m = source?["medier"]?[medium];
          var table = m?["minLutningPromille"];
          if (table is not JsonArray && medium is not ("spill" or "dag" or "dran")) continue;   // pressure media: no slope rule
          var r = new VerifyRule { Id = "minSlope." + medium, Title = $"Minimum slope ({medium})", Source = $"source_rules.json medier.{medium}.minLutningPromille / minLutningServisPromille" };
          var rows = (table as JsonArray)?.OfType<JsonObject>().Select(o => (Dim: D(o["dimMm"]) ?? 0, Min: D(o["promille"]) ?? 0, Styrka: T(o["styrka"]), Kalla: T(o["kalla"])))
            .Where(x => x.Dim > 0).OrderBy(x => x.Dim).ToList();
          var serv = m?["minLutningServisPromille"];
          var servMin = D(serv?["varde"]);
          r.Required = new Dictionary<string, object?> { ["table"] = rows?.Select(x => $"{x.Dim:0}: {x.Min:0.0}").ToList(), ["service"] = servMin };
          if ((rows == null || rows.Count == 0) && servMin == null)
          {
            r.Missing = true; r.MissingReason = T(table?["anm"]) ?? "not in the source rules";
            r.Checked = g.Sum(n => pipesOf[n.Name].Count);
            rules.Add(r);
            continue;
          }
          var levels = new HashSet<string>();
          foreach (var n in g)
          {
            var service = servicePatterns.Any(sp => NetworkNameMatch(sp, n.Name));
            foreach (var p in pipesOf[n.Name])
            {
              var L = XY(p.StartPoint).GetDistanceTo(XY(p.EndPoint));
              if (L < 0.05) continue;
              var dim = Math.Round(p.InnerDiameterOrWidth * 1000);
              double? need; string? styrka; string row;
              if (service && servMin != null) { need = servMin; styrka = T(serv?["styrka"]); row = "service"; }
              else if (rows != null && rows.Count > 0)
              {
                var hit = rows.LastOrDefault(x => x.Dim <= dim + 0.5);
                if (hit.Dim == 0) hit = rows[0];
                need = hit.Min; styrka = hit.Styrka; row = $"{hit.Dim:0}";
              }
              else continue;
              r.Checked++;
              levels.Add(LevelOf(styrka));
              var slope = Math.Abs(p.Slope) * 1000;
              if (slope >= need.Value - 0.05) continue;
              var it = new Dictionary<string, object?> { ["network"] = n.Name, ["pipe"] = p.Name, ["dimMm"] = dim, ["slopePermille"] = Math.Round(slope, 2),
                ["required"] = need, ["row"] = row, ["level"] = LevelOf(styrka) };
              Place(it, XY(p.StartPoint) + (XY(p.EndPoint) - XY(p.StartPoint)) * 0.5);
              r.Items.Add(it);
            }
          }
          r.Level = r.Items.Any(i => (string?)i["level"] == "error") || (r.Items.Count == 0 && levels.Contains("error")) ? "error" : "warning";
          rules.Add(r);
        }

      // bends
      if (Want("maxBend"))
      {
        var r = new VerifyRule { Id = "maxBend", Title = "Bends at most vatten.maxBojGrader per point", Source = "profile vatten.maxBojGrader", Required = maxBend };
        if (maxBend == null) { r.Missing = true; r.MissingReason = "vatten.maxBojGrader is not in the profile"; }
        else
          foreach (var n in nets)
            foreach (ObjectId sid in n.GetStructureIds())
            {
              var s = CivilObjectUtils.GetRequiredObject<Structure>(transaction, sid, OpenMode.ForRead);
              if (!InlineFamilies.Contains(s.PartFamilyName ?? "")) continue;
              var ps = pipesOf[n.Name].Where(p => p.StartStructureId == sid || p.EndStructureId == sid).ToList();
              if (ps.Count != 2) continue;
              r.Checked++;
              var c = XY(s.Location);
              Point2d Far(Pipe p) => p.StartStructureId == sid ? XY(p.EndPoint) : XY(p.StartPoint);
              var din = c - Far(ps[0]); var dout = Far(ps[1]) - c;
              if (din.Length < 1e-9 || dout.Length < 1e-9) continue;
              var dev = din.GetAngleTo(dout) * 180 / Math.PI;
              if (dev <= maxBend.Value + 0.5) continue;
              var it = new Dictionary<string, object?> { ["network"] = n.Name, ["structure"] = s.Name, ["partFamily"] = s.PartFamilyName,
                ["deviationDeg"] = Math.Round(dev, 1), ["pipes"] = ps.Select(p => p.Name).ToList() };
              Place(it, c);
              r.Items.Add(it);
            }
        rules.Add(r);
      }

      var outRules = rules.Select(r => new Dictionary<string, object?>
      {
        ["id"] = r.Id, ["title"] = r.Title, ["status"] = r.Missing ? "missing" : r.Items.Count == 0 ? "pass" : "fail", ["level"] = r.Level,
        ["count"] = r.Items.Count, ["checked"] = r.Checked, ["required"] = r.Required, ["source"] = r.Source, ["missingReason"] = r.MissingReason,
        ["items"] = r.Items.Take(limit).ToList(),
      }).ToList();
      var failed = outRules.Where(r => (string)r["status"]! == "fail").ToList();
      return new Dictionary<string, object?>
      {
        ["ok"] = !failed.Any(r => (string)r["level"]! == "error"),
        ["failed"] = failed.Select(r => r["id"]).ToList(),
        ["summary"] = new Dictionary<string, int>
        {
          ["pass"] = outRules.Count(r => (string)r["status"]! == "pass"), ["fail"] = failed.Count,
          ["missing"] = outRules.Count(r => (string)r["status"]! == "missing"),
          ["errors"] = failed.Count(r => (string)r["level"]! == "error"), ["warnings"] = failed.Count(r => (string)r["level"]! == "warning"),
        },
        ["rules"] = outRules,
        ["networks"] = names, ["media"] = media,
        ["profile"] = new Dictionary<string, object?> { ["kommun"] = loaded.Kommun, ["kommunFrom"] = loaded.KommunFrom ?? "none (base only)",
          ["file"] = loaded.ProfilePath, ["sourceRules"] = loaded.SourcePath },
      };
    });
  }
}
