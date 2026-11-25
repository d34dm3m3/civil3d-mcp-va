using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.DatabaseServices;
using Autodesk.Civil.DatabaseServices.Styles;

namespace Civil3DMcpPlugin;

/// <summary>
/// C240 buildPlanProfileSheet (F9, 2026-11-25; design decision of 2026-11-24: the sheet tool moves into the plugin
/// once drawEntities and the rule profile exist): one plan and profile sheet as one rerunnable call, the geometry of
/// the original Python sheet script (build, frames, slide, connection lines, legend rows; the pure parts in
/// SheetMath.cs). Use case: that script (803 lines) was used by every project; it stays for the older projects.
///
/// The layout comes from the template "A1 Plan+Profil" (or a copy of another layout); its plan viewport (the higher
/// of the viewports wider than 600 mm) is turned after the sheet's chord so the stations grow the way the profile
/// runs and centred so the slide between plan and profile is the same both ways; the profile viewport (the lower one)
/// shows the same stations with the band titles and the right axis; the plan freezes the template's layers, the given
/// ones and the other sheets' own layers (NOT-KONNEKTION-R{nr}, NOT-FLAGGA-R{nr}, NOT-HV-R{nr}, NOT-ARBETSOMRADE-R{nr}),
/// _Bladindelning* and NOT-DETALJ*. The legend rows are chosen by what the drawing holds (legend_rows.json, as
/// the Python sheet script chose them) and stacked, the right column's height note follows the profile view's bands, the stamp gets the
/// drawing number and the stations, the north arrow follows the twist, the scale bar the scale. In the model the
/// connection lines and texts towards the neighbouring sheets and the sheet frame for the overview are drawn with
/// drawEntities under "blad/konnektion/{nr}/" and "blad/ram/{nr}/" (script id prefixes kept as data: the sheet overview reads them).
/// Rerunnable: an existing layout is updated in place (replace: a fresh copy, tab kept); viewports are set, not
/// recreated; everything drawn goes through drawEntities.
/// </summary>
public static class PlanProfileSheetCommands
{
  private const string DefaultTemplate = "A1 Plan+Profil";
  private static readonly string[] DefaultPerSheet = { "NOT-KONNEKTION-R{nr}", "NOT-FLAGGA-R{nr}", "NOT-HV-R{nr}", "NOT-ARBETSOMRADE-R{nr}" };
  private static readonly string[] DefaultFreezePrefixes = { "_Bladindelning", "NOT-DETALJ" };

  private sealed record VpInfo(string Handle, double Width, double Height, double CenterX, double CenterY, List<string> Frozen);

  private sealed record Plan(double S0, double S1, double Twist, double MidStation, (double X, double Y) PlanCenter, (double X, double Y) ProfileCenter,
    double SlideMin, double SlideMax, (string Status, double Length, double Normal, double Max) Length, double? Exaggeration, double BandsMm,
    VpInfo? PlanVp, VpInfo? ProfileVp, string Source, bool LayoutExists, List<string> PlanFrozen, List<string>? ProfileFrozen, List<string> Rows,
    Dictionary<string, string> HeightNote, string? CatalogFile, List<string> BandStyles, Func<double, (double X, double Y)> Point, List<string> Notes,
    HashSet<string> Layers, List<string> MissingFrozen);

  private static double Num(JsonNode? n, string what) =>
    n is JsonValue v && v.TryGetValue<double>(out var d) && double.IsFinite(d) ? d
    : throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{what} must be a number. Nothing was changed.");

  private static List<string> Strings(JsonNode? n) => (n as JsonArray)?.Select(x => x is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : null)
    .OfType<string>().Where(s => s.Length > 0).ToList() ?? new List<string>();

  private static int? TrailingNumber(string? s) => s != null && Regex.Match(s, @"(\d+)\s*$") is { Success: true } m && int.TryParse(m.Groups[1].Value, out var n) ? n : null;

  private static int? SheetNumber(JsonNode? n) => n switch
  {
    JsonValue v when v.TryGetValue<int>(out var i) => i,
    JsonValue v when v.TryGetValue<double>(out var d) => (int)d,
    JsonValue v when v.TryGetValue<string>(out var s) => TrailingNumber(s),
    _ => null,
  };

  /// <summary>fnmatch (as the Python legend script matched): * ? [..], case-insensitive.</summary>
  private static bool FnMatch(string pattern, string name)
  {
    var rx = "^" + Regex.Replace(pattern, @"\\\[|\[|\]|\*|\?|[^\[\]\*\?]+", m => m.Value switch { "*" => ".*", "?" => ".", "[" => "[", "]" => "]", _ => Regex.Escape(m.Value) }) + "$";
    try { return Regex.IsMatch(name, rx, RegexOptions.IgnoreCase); } catch (ArgumentException) { return string.Equals(pattern, name, StringComparison.OrdinalIgnoreCase); }
  }

  private static bool Matches(IEnumerable<string> patterns, string name)
  {
    var shortName = name.Split('|')[^1];
    return patterns.Any(p => FnMatch(p, shortName) || FnMatch(p, name));
  }

  private static List<VpInfo> Viewports(Layout layout, Transaction transaction)
  {
    var paper = ViewportCommands.PaperViewportId(layout, transaction);
    var list = new List<VpInfo>();
    foreach (ObjectId id in (BlockTableRecord)transaction.GetObject(layout.BlockTableRecordId, OpenMode.ForRead))
      if (id != paper && transaction.GetObject(id, OpenMode.ForRead) is Viewport vp)
      {
        var frozen = new List<string>();
        foreach (ObjectId lid in vp.GetFrozenLayers())
          if (transaction.GetObject(lid, OpenMode.ForRead) is LayerTableRecord l) frozen.Add(l.Name);
        list.Add(new VpInfo(vp.Handle.ToString(), vp.Width, vp.Height, vp.CenterPoint.X, vp.CenterPoint.Y, frozen));
      }
    return list;
  }

  /// <summary>As the Python sheet script: the plan viewport is the higher, the profile viewport the lower of those wider than 600 mm;
  /// the miniature is narrower than 200 mm.</summary>
  private static (VpInfo? Plan, VpInfo? Profile, VpInfo? Mini) Roles(List<VpInfo> vps)
  {
    var big = vps.Where(v => v.Width > 600).ToList();
    var plan = big.OrderByDescending(v => v.CenterY).FirstOrDefault();
    var profile = big.Count > 1 ? big.OrderBy(v => v.CenterY).First() : null;
    return (plan, profile, vps.FirstOrDefault(v => v.Width < 200));
  }

  /// <summary>C240: {layout, template? (default "A1 Plan+Profil") | copyFrom?, replace (false: in place), alignment,
  /// profileView, startStation, endStation? (the alignment's end), scale (400), direction ("rl": the profile runs right
  /// to left, alias "hv"; "lr", alias "vh"), frozenLayers? (names or fnmatch patterns, frozen in the plan), perSheetLayers?,
  /// number? (the sheet number, default the layout name's trailing digits), previous?, next? (the neighbouring sheets:
  /// connection lines at startStation and endStation), connection (true), frame (true), legend? {rows: "auto" | [blocks],
  /// extraRows, withoutRows, values, catalogPath}, stamp?, stampBlock ("Stamp"), showStructuresInProfile (false),
  /// profileTitleMm (0), profileViewportMm (681), normalLengthMm (537.5), bandTitleMm (68), rightAxisMm (26), maxSlideMm
  /// (15), verticalScale? (checked), miniature? {center, scale}, northArrow (true), scaleBar (true), keepManual (true),
  /// dryRun?, profile?}.</summary>
  public static async Task<object?> BuildPlanProfileSheetAsync(JsonObject? parameters, CancellationToken cancellationToken)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "layout").Trim();
    var template = PluginRuntime.GetOptionalString(parameters, "template");
    var copyFrom = PluginRuntime.GetOptionalString(parameters, "copyFrom");
    if (template != null && copyFrom != null)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Give template or copyFrom, not both. Nothing was changed.");
    var source = copyFrom ?? template ?? DefaultTemplate;
    var replace = PluginRuntime.GetOptionalBool(parameters, "replace") ?? false;
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignment");
    var pvName = PluginRuntime.GetRequiredString(parameters, "profileView");
    var s0 = PluginRuntime.GetRequiredDouble(parameters, "startStation");
    var s1Given = PluginRuntime.GetOptionalDouble(parameters, "endStation");
    var scale = PluginRuntime.GetOptionalDouble(parameters, "scale") ?? 400.0;
    if (!(scale > 0)) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "scale must be > 0 (400 for 1:400). Nothing was changed.");
    var dir = (PluginRuntime.GetOptionalString(parameters, "direction") ?? "rl").Trim().ToLowerInvariant();
    if (dir is not ("rl" or "lr" or "hv" or "vh")) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "direction must be rl (right to left, alias hv) or lr (alias vh). Nothing was changed.");
    var rl = dir is "rl" or "hv";
    var frozenGiven = Strings(PluginRuntime.GetParameter(parameters, "frozenLayers") as JsonNode);
    var perSheet = PluginRuntime.GetParameter(parameters, "perSheetLayers") is JsonArray psa ? Strings(psa) : DefaultPerSheet.ToList();
    var number = PluginRuntime.GetOptionalInt(parameters, "number") ?? TrailingNumber(name)
      ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Give number (the sheet number): '{name}' does not end in digits. Nothing was changed.");
    var prevNode = PluginRuntime.GetParameter(parameters, "previous") as JsonNode;
    var nextNode = PluginRuntime.GetParameter(parameters, "next") as JsonNode;
    int? previous = prevNode == null ? null : (SheetNumber(prevNode) ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "previous must be a sheet number or a layout name ending in digits. Nothing was changed."));
    int? next = nextNode == null ? null : (SheetNumber(nextNode) ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "next must be a sheet number or a layout name ending in digits. Nothing was changed."));
    var connection = PluginRuntime.GetOptionalBool(parameters, "connection") ?? true;
    var drawFrame = PluginRuntime.GetOptionalBool(parameters, "frame") ?? true;
    var legend = PluginRuntime.GetParameter(parameters, "legend") as JsonObject;
    var legendOff = PluginRuntime.GetParameter(parameters, "legend") is JsonValue lv0 && lv0.TryGetValue<bool>(out var lb0) && !lb0;
    var stamp = PluginRuntime.GetParameter(parameters, "stamp") as JsonObject;
    var stampBlock = PluginRuntime.GetOptionalString(parameters, "stampBlock") ?? "Stamp";
    var showStructures = PluginRuntime.GetOptionalBool(parameters, "showStructuresInProfile") ?? false;
    var titleMmTop = PluginRuntime.GetOptionalDouble(parameters, "profileTitleMm") ?? 0.0;
    var vpMm = PluginRuntime.GetOptionalDouble(parameters, "profileViewportMm") ?? 681.0;
    var normalMm = PluginRuntime.GetOptionalDouble(parameters, "normalLengthMm") ?? 537.5;
    var bandTitleMm = PluginRuntime.GetOptionalDouble(parameters, "bandTitleMm") ?? 68.0;
    var axisMm = PluginRuntime.GetOptionalDouble(parameters, "rightAxisMm") ?? 26.0;
    var maxSlide = PluginRuntime.GetOptionalDouble(parameters, "maxSlideMm") ?? 15.0;
    var vScale = PluginRuntime.GetOptionalDouble(parameters, "verticalScale");
    var mini = PluginRuntime.GetParameter(parameters, "miniature") as JsonObject;
    var northArrow = PluginRuntime.GetOptionalBool(parameters, "northArrow") ?? true;
    var scaleBar = PluginRuntime.GetOptionalBool(parameters, "scaleBar") ?? true;
    var keepManual = PluginRuntime.GetOptionalBool(parameters, "keepManual") ?? true;
    var dryRun = PluginRuntime.GetOptionalBool(parameters, "dryRun") ?? false;
    var rulesPath = PluginRuntime.GetOptionalString(parameters, "rulesPath");
    var mm = scale / 1000.0;
    var nrText = number.ToString(CultureInfo.InvariantCulture);
    var perSheetRx = perSheet.Select(f => new Regex("^" + Regex.Escape(f).Replace("\\{nr}", @"(\d+)") + "$", RegexOptions.IgnoreCase)).ToList();

    // ---- plan: geometry, viewports of the template, frozen layers, legend rows
    var plan = await CivilExecution.ReadAsync((doc, civilDoc, database, transaction) =>
    {
      var notes = new List<string>();
      var al = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var s1 = s1Given ?? al.EndingStation;
      if (!(s1 > s0 + 1e-6)) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"endStation {s1:F3} must be greater than startStation {s0:F3}. Nothing was changed.");
      if (s0 < al.StartingStation - 1e-6 || s1 > al.EndingStation + 1e-6)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{s0:F3}-{s1:F3} is outside the alignment ({al.StartingStation:F3}-{al.EndingStation:F3}). Nothing was changed.");
      var cache = new Dictionary<double, (double X, double Y)>();
      (double X, double Y) Point(double s)
      {
        var key = Math.Round(s, 6);
        if (cache.TryGetValue(key, out var q)) return q;
        double x = 0, y = 0;
        al.PointLocation(Math.Clamp(s, al.StartingStation, al.EndingStation), 0, ref x, ref y);
        return cache[key] = (x, y);
      }
      var twist = SheetMath.Twist(SheetMath.Chord(Point(s0), Point(s1)).AngleDeg, rl);

      // the profile view: stations are linear in x; the graph from ElevationMin to ElevationMax
      var view = ProfileEditCommands.FindProfileViewByName(civilDoc, transaction, pvName);
      if (view.AlignmentId != al.ObjectId) notes.Add($"profile view '{pvName}' belongs to another alignment");
      double xa = 0, ya = 0, xb = 0, yb = 0, xt = 0, yt = 0;
      if (!view.FindXYAtStationAndElevation(view.StationStart, view.ElevationMin, ref xa, ref ya)
          | !view.FindXYAtStationAndElevation(view.StationEnd, view.ElevationMin, ref xb, ref yb)
          | !view.FindXYAtStationAndElevation(view.StationStart, view.ElevationMax, ref xt, ref yt))
        throw new JsonRpcDispatchException("CIVIL3D.OPERATION_FAILED", $"Profile view '{pvName}' cannot place its own corners (no elevation range?). Nothing was changed.");
      double X(double s) => xa + (s - view.StationStart) * (xb - xa) / (view.StationEnd - view.StationStart);
      double S(double x) => view.StationStart + (x - xa) * (view.StationEnd - view.StationStart) / (xb - xa);
      if (s0 < view.StationStart - 0.01 || s1 > view.StationEnd + 0.01)
        notes.Add($"the sheet {s0:F2}-{s1:F2} reaches outside the profile view's stations ({view.StationStart:F2}-{view.StationEnd:F2}); set its range (setProfileViewRange)");
      double left = Math.Min(X(s0), X(s1)), right = Math.Max(X(s0), X(s1));
      var xc = SheetMath.ProfileCenterX(left, right, mm, bandTitleMm, axisMm);
      var m = S(xc);
      // the bottom bands below the graph (as the Python band script: gap from the previous band's bottom, then the style's height)
      double y = 0, band = 0;
      var styles = new List<string>();
      foreach (ProfileViewBandItem item in view.Bands.GetBottomBandItems())
      {
        BandSetItem bi = item;
        double gap = 0, height = 0;
        try { gap = bi.Gap; } catch (Exception) { }
        try
        {
          if (transaction.GetObject(bi.BandStyleId, OpenMode.ForRead) is BandStyle st) { height = st.BandHeight; styles.Add(st.Name); }
        }
        catch (Exception) { }
        var top = y + gap * 1000.0;
        y = top + height * 1000.0;
        band = Math.Max(band, y);
      }
      var topModel = yt + titleMmTop * mm;
      var bottomModel = ya - band * mm;
      var profileCenter = (xc, (topModel + bottomModel) / 2.0);
      var planCenter = SheetMath.PlanCenter(Point, s0, s1, m, twist, rl);
      var slide = SheetMath.SlideErrors(Point, s0, s1, m, planCenter, twist, rl).Select(e => e / mm).ToList();
      double? exag = null;
      try { exag = (transaction.GetObject(view.StyleId, OpenMode.ForRead) as ProfileViewStyle)?.GraphStyle.VerticalExaggeration; } catch (Exception) { }

      // the layouts: the source's viewports and the existing layout
      var layouts = LayoutCommands.Layouts(database, transaction).Where(l => !l.ModelType).ToList();
      var src = layouts.FirstOrDefault(l => string.Equals(l.LayoutName, source, StringComparison.OrdinalIgnoreCase));
      var own = layouts.FirstOrDefault(l => string.Equals(l.LayoutName, name, StringComparison.OrdinalIgnoreCase));
      if (src == null && (own == null || replace))
        throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Layout '{source}' is not in the drawing (layouts: {string.Join(", ", layouts.Select(l => l.LayoutName))}). Nothing was changed.");
      var (planVp, profileVp, _) = Roles(Viewports(src ?? own!, transaction));
      if (planVp == null) throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Layout '{(src ?? own)!.LayoutName}' has no viewport wider than 600 mm for the plan. Nothing was changed.");
      if (profileVp == null) notes.Add($"layout '{(src ?? own)!.LayoutName}' has no second large viewport for the profile");

      // the plan's frozen layers: the source's plan viewport, the given ones, the other sheets' layers, frames and details
      var lt = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
      var layers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      foreach (ObjectId lid in lt) if (transaction.GetObject(lid, OpenMode.ForRead) is LayerTableRecord l) layers.Add(l.Name);
      var given = layers.Where(l => frozenGiven.Any(p => FnMatch(p, l))).ToList();
      var missing = frozenGiven.Where(p => !p.Contains('*') && !p.Contains('?') && !p.Contains('[') && !layers.Contains(p)).ToList();
      var others = layers.Where(l => perSheetRx.Any(r => r.Match(l) is { Success: true } mt && mt.Groups[1].Value != nrText)).ToList();
      var prefixed = layers.Where(l => DefaultFreezePrefixes.Any(p => l.StartsWith(p, StringComparison.OrdinalIgnoreCase))).ToList();
      var planFrozen = planVp.Frozen.Concat(given).Concat(others).Concat(prefixed).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.Ordinal).ToList();
      List<string>? profileFrozen = showStructures && profileVp != null ? profileVp.Frozen.Where(n => !n.Contains("BRUNN", StringComparison.OrdinalIgnoreCase)).ToList() : null;

      // ---- the legend rows by what the drawing holds (as the Python sheet script; frozen: the source's plan and the given layers)
      var rows = new List<string>();
      var heightNote = new Dictionary<string, string>();
      string? catalogFile = null;
      if (!legendOff)
      {
        catalogFile = DetailSheetCommands.CatalogPath(legend?["catalogPath"]?.GetValue<string>(), rulesPath);
        var cat = JsonNode.Parse(File.ReadAllText(catalogFile)) as JsonObject
          ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{catalogFile} is not a JSON object. Nothing was changed.");
        var rowsNode = legend?["rows"];
        if (rowsNode is JsonArray explicitRows) rows = Strings(explicitRows);
        else
        {
          var frys = new HashSet<string>(planVp.Frozen.Concat(given), StringComparer.OrdinalIgnoreCase);
          var catRows = (cat["rader"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
          List<string> Cond(JsonObject r, string key) => Strings(r["villkor"]?[key]);
          var layerPatterns = catRows.SelectMany(r => Cond(r, "lager")).ToList();
          // one pass over model space: layers with objects, block names on visible layers
          var withObjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
          var blocks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
          foreach (ObjectId oid in (BlockTableRecord)transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(database), OpenMode.ForRead))
          {
            if (transaction.GetObject(oid, OpenMode.ForRead) is not Autodesk.AutoCAD.DatabaseServices.Entity e) continue;
            withObjects.Add(e.Layer);
            if (e is BlockReference br && !frys.Contains(e.Layer))
              try { blocks.Add(DetailSheetCommands.BlockName(br, transaction)); } catch (Exception) { }
          }
          var layersWith = layers.Where(l => !frys.Contains(l) && Matches(layerPatterns, l) && (l.Contains('|') || withObjects.Contains(l))).ToList();
          var pipeStyles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
          var structureStyles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
          foreach (var nn in PipeNetworkCommands.ListPipeNetworkNames(civilDoc, transaction))
          {
            var net = PipeNetworkCommands.FindPipeNetworkByName(civilDoc, transaction, nn, OpenMode.ForRead);
            foreach (ObjectId pid in net.GetPipeIds())
              if (transaction.GetObject(pid, OpenMode.ForRead) is Pipe p && transaction.GetObject(p.StyleId, OpenMode.ForRead) is StyleBase ps) pipeStyles.Add(ps.Name);
            foreach (ObjectId sid in net.GetStructureIds())
              if (transaction.GetObject(sid, OpenMode.ForRead) is Structure s && transaction.GetObject(s.StyleId, OpenMode.ForRead) is StyleBase ss) structureStyles.Add(ss.Name);
          }
          var upper = frys.Select(f => f.ToUpperInvariant()).ToHashSet();
          if (new[] { "VA-SPILL-LEDN", "VA-DAGV-LEDN", "VA-VATTEN-LEDN" }.All(upper.Contains)) pipeStyles.Clear();
          if (new[] { "VA-SPILL-BRUNN", "VA-DAGV-BRUNN", "VA-VATTEN-BRUNN" }.All(upper.Contains)) structureStyles.Clear();
          foreach (var r in catRows)
          {
            var block = r["block"]?.GetValue<string>();
            if (block == null) continue;
            if (layersWith.Any(l => Matches(Cond(r, "lager"), l)) || pipeStyles.Any(s => Matches(Cond(r, "rorstil"), s))
                || structureStyles.Any(s => Matches(Cond(r, "brunnsstil"), s)) || blocks.Any(b => Matches(Cond(r, "block"), b)))
              rows.Add(block);
          }
        }
        foreach (var x in Strings(legend?["extraRows"])) if (!rows.Contains(x)) rows.Add(x);
        var without = Strings(legend?["withoutRows"]);
        rows = rows.Where(r => !without.Contains(r)).ToList();
        // the height note after the profile view's bands (catalogue key hojdreferens, the height reference note)
        if (cat["hojdreferens"] is JsonObject hr)
        {
          var okBand = Strings(hr["ok_band"]); var vgBand = Strings(hr["vg_band"]);
          var ok = styles.Any(b => okBand.Any(x => b.Contains(x, StringComparison.Ordinal)));
          var vg = styles.Any(b => vgBand.Any(x => b.Contains(x, StringComparison.Ordinal)));
          if ((ok && vg ? hr["blandat"] : ok ? hr["tryck"] : hr["sjalvfall"]) is JsonObject values)
            foreach (var (k, v) in values) heightNote[k] = v?.GetValue<string>() ?? "";
        }
        DetailSheetCommands.Stack(cat, rows);   // fails early when the rows do not fit
      }
      return new Plan(s0, s1, twist, m, planCenter, profileCenter, slide.Min(), slide.Max(), SheetMath.SheetLength(s0, s1, scale, vpMm, normalMm, bandTitleMm, axisMm),
        exag, band, planVp, profileVp, src?.LayoutName ?? name, own != null, planFrozen, profileFrozen, rows, heightNote, catalogFile, styles, Point, notes, layers, missing);
    });

    var warnings = new List<string>(plan.Notes);
    if (plan.MissingFrozen.Count > 0) warnings.Add($"frozenLayers not in the drawing: {string.Join(", ", plan.MissingFrozen)}");
    if (Math.Max(Math.Abs(plan.SlideMin), Math.Abs(plan.SlideMax)) > maxSlide)
      warnings.Add($"the slide between plan and profile reaches {Math.Max(Math.Abs(plan.SlideMin), Math.Abs(plan.SlideMax)):F1} mm (over {maxSlide} mm): split the sheet where the line bends");
    if (plan.Length.Status == "warning") warnings.Add($"{plan.Length.Length:F1} m is over the normal sheet length ({plan.Length.Normal:F0} m at 1:{scale:F0})");
    double? vertical = plan.Exaggeration is double ex && ex > 0 ? scale / ex : null;
    if (vScale != null && vertical != null && Math.Abs(vertical.Value - vScale.Value) > 1e-6)
      warnings.Add($"the profile's vertical scale is 1:{vertical:0.##} (style exaggeration {plan.Exaggeration}), not 1:{vScale:0.##}");
    var stationText = $"{SheetMath.StationText(plan.S0)}-{SheetMath.StationText(plan.S1)}";
    var frameW = plan.PlanVp!.Width * mm;
    var frameH = plan.PlanVp.Height * mm;
    var corners = SheetMath.FrameCorners(plan.PlanCenter, plan.Twist, frameW, frameH);

    static Dictionary<string, object?> Paper(VpInfo v) => new()
    {
      ["centerX"] = Math.Round(v.CenterX, 2), ["centerY"] = Math.Round(v.CenterY, 2), ["width"] = Math.Round(v.Width, 2), ["height"] = Math.Round(v.Height, 2),
    };
    Dictionary<string, object?> Answer(string made) => new()
    {
      ["layout"] = name, ["made"] = made, ["source"] = plan.Source, ["dryRun"] = dryRun, ["number"] = number, ["alignment"] = alignmentName, ["profileView"] = pvName,
      ["startStation"] = Math.Round(plan.S0, 3), ["endStation"] = Math.Round(plan.S1, 3), ["stations"] = stationText, ["direction"] = rl ? "rl" : "lr",
      ["twist"] = Math.Round(plan.Twist, 6), ["midStation"] = Math.Round(plan.MidStation, 3),
      ["plan"] = new Dictionary<string, object?>
      {
        ["handle"] = plan.PlanVp.Handle, ["viewCenter"] = new[] { Math.Round(plan.PlanCenter.X, 4), Math.Round(plan.PlanCenter.Y, 4) }, ["scale"] = scale,
        ["paper"] = Paper(plan.PlanVp),
        ["frozenLayers"] = plan.PlanFrozen.Count, ["frame"] = corners.Select(c => new[] { Math.Round(c.X, 3), Math.Round(c.Y, 3) }).ToList(),
      },
      ["profileVp"] = plan.ProfileVp == null ? null : new Dictionary<string, object?>   // not "profile": the profile report takes that key
      {
        ["handle"] = plan.ProfileVp.Handle, ["viewCenter"] = new[] { Math.Round(plan.ProfileCenter.X, 4), Math.Round(plan.ProfileCenter.Y, 4) }, ["scale"] = scale,
        ["verticalScale"] = vertical == null ? null : Math.Round(vertical.Value, 3), ["exaggeration"] = plan.Exaggeration, ["bandsMm"] = Math.Round(plan.BandsMm, 2),
        ["paper"] = Paper(plan.ProfileVp),
      },
      ["slideMm"] = new[] { Math.Round(plan.SlideMin, 2), Math.Round(plan.SlideMax, 2) },
      ["sheetLength"] = new Dictionary<string, object?>
      {
        ["status"] = plan.Length.Status, ["length"] = Math.Round(plan.Length.Length, 3), ["normal"] = Math.Round(plan.Length.Normal, 3), ["max"] = Math.Round(plan.Length.Max, 3),
        ["profileViewportMm"] = vpMm, ["normalLengthMm"] = normalMm, ["bandTitleMm"] = bandTitleMm, ["rightAxisMm"] = axisMm,
        ["profileViewportActualMm"] = plan.ProfileVp == null ? null : Math.Round(plan.ProfileVp.Width, 2),
      },
      ["legendRows"] = plan.Rows, ["heightNote"] = plan.HeightNote, ["bandStyles"] = plan.BandStyles, ["warnings"] = warnings,
    };
    if (dryRun) return Answer(plan.LayoutExists ? "inPlace (dry run)" : "created (dry run)");
    if (plan.Length.Status == "stop")
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{plan.Length.Length:F1} m does not fit the profile viewport (at most {plan.Length.Max:F1} m at 1:{scale:F0}): split the sheet. Nothing was changed.");

    // ---- the layout
    string made;
    if (!plan.LayoutExists || replace)
    {
      await LayoutCommands.CreateLayoutAsync(new JsonObject { ["name"] = name, ["copyFrom"] = source, ["replace"] = plan.LayoutExists });
      made = plan.LayoutExists ? "replaced" : "created";
    }
    else made = "inPlace";
    var lay = await CivilExecution.ReadAsync((doc, civilDoc, database, transaction) => Roles(Viewports(LayoutCommands.FindLayout(database, transaction, name), transaction)));
    if (lay.Plan == null) throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Layout '{name}' has no plan viewport (wider than 600 mm). The layout is made; nothing else was changed.");

    // ---- viewports
    var planFrozen = plan.PlanFrozen.Where(plan.Layers.Contains).ToList();
    await ViewportCommands.SetViewportAsync(new JsonObject
    {
      ["layout"] = name, ["handle"] = lay.Plan.Handle, ["viewCenterX"] = plan.PlanCenter.X, ["viewCenterY"] = plan.PlanCenter.Y, ["twistDegrees"] = plan.Twist,
      ["scaleDenominator"] = scale, ["frozenLayers"] = new JsonArray(planFrozen.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()), ["locked"] = true,
    });
    if (lay.Profile != null)
    {
      var pvp = new JsonObject
      {
        ["layout"] = name, ["handle"] = lay.Profile.Handle, ["viewCenterX"] = plan.ProfileCenter.X, ["viewCenterY"] = plan.ProfileCenter.Y, ["twistDegrees"] = 0.0,
        ["scaleDenominator"] = scale, ["locked"] = true,
      };
      if (plan.ProfileFrozen != null) pvp["frozenLayers"] = new JsonArray(plan.ProfileFrozen.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());
      await ViewportCommands.SetViewportAsync(pvp);
    }
    var readBack = await CivilExecution.ReadAsync((doc, civilDoc, database, transaction) =>
    {
      var (layout, vp) = ViewportCommands.FindViewport(database, transaction, name, lay.Plan.Handle, OpenMode.ForRead);
      var d = ViewportCommands.Describe(vp, transaction);
      Dictionary<string, object?>? p = null;
      if (lay.Profile != null) p = ViewportCommands.Describe(ViewportCommands.FindViewport(database, transaction, name, lay.Profile.Handle, OpenMode.ForRead).Viewport, transaction);
      return (Plan: d, Profile: p);
    });
    var tw = Convert.ToDouble(readBack.Plan["twistDegrees"], CultureInfo.InvariantCulture);
    if (Math.Abs(((tw - plan.Twist) + 540) % 360 - 180) > 0.01) warnings.Add($"the plan viewport reads twist {tw:F3}, set {plan.Twist:F3}");

    // ---- legend rows (drawEntities under sheet/{layout}/, the template's untagged rows erased), right column, stamp
    var drawnPrefix = $"sheet/{name}/";
    var layoutItems = new JsonArray();
    Dictionary<string, object?>? column = null;
    var blockAt = await CivilExecution.ReadAsync((doc, civilDoc, database, transaction) =>
    {
      var found = new Dictionary<string, (double X, double Y, string Handle)>(StringComparer.OrdinalIgnoreCase);
      foreach (ObjectId id in (BlockTableRecord)transaction.GetObject(LayoutCommands.FindLayout(database, transaction, name).BlockTableRecordId, OpenMode.ForRead))
        if (transaction.GetObject(id, OpenMode.ForRead) is BlockReference br && !(ScriptIdCommands.Read(br).Id ?? "").StartsWith(drawnPrefix, StringComparison.Ordinal))
          found.TryAdd(DetailSheetCommands.BlockName(br, transaction), (br.Position.X, br.Position.Y, br.Handle.ToString()));
      return found;
    });
    if (plan.CatalogFile != null)
    {
      var cat = (JsonObject)JsonNode.Parse(File.ReadAllText(plan.CatalogFile))!;
      var spalt = cat["spalt"] as JsonObject ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{plan.CatalogFile} has no 'spalt'.");
      var colBlock = spalt["block"]?.GetValue<string>() ?? "Högerspalt VA";
      var colLayer = spalt["lager"]?.GetValue<string>();
      if (!blockAt.TryGetValue(colBlock, out var col))
        warnings.Add($"layout '{name}' has no '{colBlock}' block: no legend rows");
      else
      {
        var rowBlocks = (cat["rader"] as JsonArray ?? new JsonArray()).Select(r => r?["block"]?.GetValue<string>()).OfType<string>()
          .Append(cat["fot"]?["block"]?.GetValue<string>() ?? "").Where(x => x.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var untagged = await CivilExecution.WriteAsync((doc, civilDoc, database, transaction) =>
        {
          var n = 0;
          foreach (ObjectId id in (BlockTableRecord)transaction.GetObject(LayoutCommands.FindLayout(database, transaction, name).BlockTableRecordId, OpenMode.ForRead))
            if (transaction.GetObject(id, OpenMode.ForRead) is BlockReference br && (rowBlocks.Contains(DetailSheetCommands.BlockName(br, transaction))
                  || DetailSheetCommands.BlockName(br, transaction).StartsWith("TF-", StringComparison.OrdinalIgnoreCase))
                && !(ScriptIdCommands.Read(br).Id ?? "").StartsWith(drawnPrefix, StringComparison.Ordinal))
            { br.UpgradeOpen(); br.Erase(); n++; }
          return n;
        });
        foreach (var (block, dy) in DetailSheetCommands.Stack(cat, plan.Rows))
        {
          var it = new JsonObject { ["id"] = $"tf/{block}", ["type"] = "block", ["blockName"] = block, ["x"] = col.X, ["y"] = col.Y + dy };
          if (colLayer != null) it["layer"] = colLayer;
          layoutItems.Add(it);
        }
        var values = new JsonObject();
        foreach (var (k, v) in plan.HeightNote) values[k] = v;
        if (legend?["values"] is JsonObject given) foreach (var (k, v) in given) values[k] = v?.DeepClone();
        column = new Dictionary<string, object?> { ["block"] = colBlock, ["at"] = new[] { Math.Round(col.X, 2), Math.Round(col.Y, 2) }, ["rows"] = plan.Rows, ["templateRowsErased"] = untagged, ["catalog"] = plan.CatalogFile };
        if (values.Count > 0)
          column["attributes"] = await LayoutCommands.SetBlockAttributesAsync(new JsonObject { ["layout"] = name, ["blockName"] = colBlock, ["values"] = values });
      }
    }
    var drawnLayout = await DrawEntitiesCommands.DrawEntitiesAsync(new JsonObject
    {
      ["idPrefix"] = drawnPrefix, ["layout"] = name, ["items"] = layoutItems, ["replace"] = true, ["keepManual"] = keepManual,
    }, cancellationToken) as Dictionary<string, object?>;

    object? stampReport = null;
    {
      var values = new JsonObject { ["RITNINGSNR"] = name, ["BESKRIVNING1"] = stationText };
      if (stamp != null) foreach (var (k, v) in stamp) values[k] = v?.DeepClone();
      if (blockAt.ContainsKey(stampBlock))
        stampReport = await LayoutCommands.SetBlockAttributesAsync(new JsonObject { ["layout"] = name, ["blockName"] = stampBlock, ["values"] = values });
      else warnings.Add($"layout '{name}' has no '{stampBlock}' block: stamp not set");
    }

    // ---- scale bar after the scale (the template's "Skalstock Meter" is drawn for 1:400)
    string? scaleBarReport = null;
    if (scaleBar && Math.Abs(scale - 400) > 1e-9)
    {
      var want = $"Skalstock 1-{scale:0}";
      var have = blockAt.FirstOrDefault(kv => kv.Key.StartsWith("Skalstock", StringComparison.OrdinalIgnoreCase));
      if (have.Key != null && !string.Equals(have.Key, want, StringComparison.OrdinalIgnoreCase))
      {
        var exists = await CivilExecution.ReadAsync((doc, civilDoc, database, transaction) => ((BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead)).Has(want));
        if (!exists) scaleBarReport = $"block {want} is not in the drawing; {have.Key} stays";
        else
        {
          await CommandDispatcher.DispatchAsync("eraseEntities", new JsonObject { ["handles"] = new JsonArray(have.Value.Handle) }, cancellationToken);
          await CommandDispatcher.DispatchAsync("insertBlock", new JsonObject { ["blockName"] = want, ["x"] = have.Value.X, ["y"] = have.Value.Y, ["z"] = 0.0, ["rotationDegrees"] = 0.0, ["layer"] = "RIT-SKALST", ["createLayer"] = true, ["layout"] = name }, cancellationToken);
          scaleBarReport = $"{have.Key} -> {want}";
        }
      }
    }

    // ---- north arrow (the template's Kompassros, Norrpil in older drawings), miniature
    var arrows = new List<string>();
    if (northArrow)
      foreach (var blockName in blockAt.Keys.Where(k => k.StartsWith("Norrpil", StringComparison.OrdinalIgnoreCase) || k.StartsWith("Kompassros", StringComparison.OrdinalIgnoreCase)).ToList())
          try
          {
            await CommandDispatcher.DispatchAsync("northArrowAlign", new JsonObject { ["layout"] = name, ["handle"] = lay.Plan.Handle, ["blockName"] = blockName }, cancellationToken);
            arrows.Add(blockName);
          }
          catch (JsonRpcDispatchException err) { warnings.Add($"north arrow {blockName}: {err.Message}"); }
    if (mini != null && lay.Mini != null)
    {
      var center = mini["center"] as JsonArray ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "miniature.center must be [x, y].");
      var frozenMini = plan.Layers.Where(n => (n.StartsWith("_Bladindelning", StringComparison.OrdinalIgnoreCase) && !string.Equals(n, $"_Bladindelning-R{nrText}", StringComparison.OrdinalIgnoreCase))
          || n.Contains("BRUNN", StringComparison.OrdinalIgnoreCase) || n.EndsWith("-TEXT", StringComparison.OrdinalIgnoreCase) || n.StartsWith("NOT-", StringComparison.OrdinalIgnoreCase))
        .Concat(plan.PlanVp.Frozen.Where(n => !n.StartsWith("_Bladindelning", StringComparison.OrdinalIgnoreCase))).Where(plan.Layers.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
      await ViewportCommands.SetViewportAsync(new JsonObject { ["layout"] = name, ["handle"] = lay.Mini.Handle, ["frozenLayers"] = new JsonArray(frozenMini.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()) });
      await ViewportCommands.SetViewportAsync(new JsonObject
      {
        ["layout"] = name, ["handle"] = lay.Mini.Handle, ["viewCenterX"] = Num(center[0], "miniature.center[0]"), ["viewCenterY"] = Num(center[1], "miniature.center[1]"),
        ["twistDegrees"] = 0.0, ["scaleDenominator"] = mini["scale"] is JsonNode ms ? Num(ms, "miniature.scale") : 6000.0, ["locked"] = true,
      });
      try { await CommandDispatcher.DispatchAsync("viewportLayerOverrides", new JsonObject { ["layout"] = name, ["handle"] = lay.Mini.Handle, ["lineweight"] = 5 }, cancellationToken); }
      catch (JsonRpcDispatchException err) { warnings.Add("miniature lineweight: " + err.Message); }
    }

    // ---- model: connection lines towards the neighbours, the sheet frame (as the Python sheet script)
    var model = new Dictionary<string, object?>();
    if (connection && (previous != null || next != null))
    {
      var layer = $"NOT-KONNEKTION-R{nrText}";
      var items = new JsonArray();
      foreach (var (key, other, gs) in new[] { ("start", previous, plan.S0), ("end", next, plan.S1) })
      {
        if (other == null) continue;
        var text = key == "start" ? SheetMath.ConnectionText(other.Value, number, rl) : SheetMath.ConnectionText(number, other.Value, rl);
        var g = plan.Point(gs);
        var c = SheetMath.Connection(g, plan.PlanCenter, plan.Twist, frameH, mm, text.Length);
        items.Add(new JsonObject { ["id"] = $"{key}/linje", ["type"] = "line", ["points"] = new JsonArray(new JsonArray(c.A.X, c.A.Y), new JsonArray(c.B.X, c.B.Y)), ["layer"] = layer, ["linetypeScale"] = 0.15 });
        items.Add(new JsonObject { ["id"] = $"{key}/text", ["type"] = "mtext", ["text"] = text, ["x"] = c.Text.X, ["y"] = c.Text.Y, ["height"] = c.TextHeight, ["width"] = 0.0, ["rotation"] = -plan.Twist, ["layer"] = layer });
      }
      var r = await DrawEntitiesCommands.DrawEntitiesAsync(new JsonObject { ["idPrefix"] = $"blad/konnektion/{nrText}/", ["items"] = items, ["replace"] = true, ["keepManual"] = keepManual }, cancellationToken) as Dictionary<string, object?>;
      try { await CommandDispatcher.DispatchAsync("setLayers", new JsonObject { ["layers"] = new JsonArray(layer), ["color"] = 7, ["linetype"] = "ACAD_ISO05W100" }, cancellationToken); }
      catch (JsonRpcDispatchException err) { warnings.Add($"connection layer {layer}: {err.Message}"); }
      model["connection"] = new Dictionary<string, object?> { ["layer"] = layer, ["previous"] = previous, ["next"] = next, ["counts"] = r?.GetValueOrDefault("counts"), ["manual"] = r?.GetValueOrDefault("manual") };
    }
    if (drawFrame)
    {
      var hx = SheetMath.SheetX(plan.Twist);
      var up = (X: -hx.Y, Y: hx.X);
      var items = new JsonArray
      {
        new JsonObject { ["id"] = "ram", ["type"] = "polyline", ["closed"] = true, ["layer"] = $"_Bladindelning-R{nrText}",
          ["points"] = new JsonArray(corners.Select(q => (JsonNode?)new JsonArray(q.X, q.Y)).ToArray()) },
        new JsonObject { ["id"] = "text", ["type"] = "mtext", ["text"] = name, ["x"] = (corners[2].X + corners[3].X) / 2 - 1.5 * up.X, ["y"] = (corners[2].Y + corners[3].Y) / 2 - 1.5 * up.Y,
          ["height"] = 3.5, ["width"] = 0.0, ["rotation"] = -plan.Twist, ["layer"] = "_Bladindelning-TEXT", ["attachment"] = "TopCenter", ["backgroundMask"] = true, ["maskScale"] = 1.2 },
      };
      var r = await DrawEntitiesCommands.DrawEntitiesAsync(new JsonObject { ["idPrefix"] = $"blad/ram/{nrText}/", ["items"] = items, ["replace"] = true, ["keepManual"] = keepManual }, cancellationToken) as Dictionary<string, object?>;
      // the frame the Python sheet script drew under the id "blad/ram/{nr}" itself
      var legacy = await CivilExecution.WriteAsync((doc, civilDoc, database, transaction) =>
      {
        var n = 0;
        foreach (ObjectId id in (BlockTableRecord)transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(database), OpenMode.ForRead))
          if (transaction.GetObject(id, OpenMode.ForRead) is Autodesk.AutoCAD.DatabaseServices.Entity e && ScriptIdCommands.Read(e).Id == $"blad/ram/{nrText}")
          { e.UpgradeOpen(); e.Erase(); n++; }
        return n;
      });
      model["frame"] = new Dictionary<string, object?> { ["layer"] = $"_Bladindelning-R{nrText}", ["counts"] = r?.GetValueOrDefault("counts"), ["legacyErased"] = legacy };
    }

    var answer = Answer(made);
    answer["planViewport"] = readBack.Plan;
    answer["profileViewport"] = readBack.Profile;
    answer["rightColumn"] = column;
    answer["drawn"] = drawnLayout == null ? null : new Dictionary<string, object?> { ["idPrefix"] = drawnPrefix, ["counts"] = drawnLayout.GetValueOrDefault("counts"), ["manual"] = drawnLayout.GetValueOrDefault("manual") };
    answer["stamp"] = stampReport;
    answer["scaleBar"] = scaleBarReport;
    answer["northArrow"] = arrows;
    answer["model"] = model;
    return answer;
  }
}
