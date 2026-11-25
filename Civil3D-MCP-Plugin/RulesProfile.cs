using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Civil3DMcpPlugin;

/// <summary>
/// C222 (F3, 2026-11-24): the design rule profile (rules_profile.json: "bas" for every municipality, "kommuner.&lt;name&gt;"
/// overriding single keys: a company base plus municipal additions) and the source rules beside it (source_rules.json).
/// The profile's keys are its file format; docs/rules/rules_profile.example.json describes them.
/// Use case: the company's design practice (depths, slopes, clearances, structure sizes) was a constant in every
/// project script (about 150 scripts); the plugin and Node read no rule file.
///
/// Files: the parameter rulesPath (the profile file or its folder, inside the import or export roots), else the folder
/// in the environment variable C3DMCP_RULES_DIR, else &lt;import root&gt;\Rules. The source rules file is the profile's
/// bas.kallregler (relative to the profile's folder), else source_rules.json beside it. Read on every call (small files).
///
/// Municipality: the parameter kommun (a key under "kommuner" in the profile; diacritics, case, spaces and dashes do
/// not matter: "North Bay" = NorthBay), else the first folder of the drawing's path, from the drawing upwards, whose
/// name matches a municipality key (C:\Projects\&lt;Municipality&gt;\...), else the base alone. Merge: objects merge key
/// by key, anything else (lists too) is replaced by the municipality's value.
/// </summary>
internal static class RulesProfile
{
  internal const string ProfileFileName = "rules_profile.json";
  internal const string SourceFileName = "source_rules.json";
  internal const string DirVariable = "C3DMCP_RULES_DIR";

  private static readonly JsonDocumentOptions ParseOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

  internal sealed record Loaded(
    JsonObject Merged, JsonObject Base, JsonObject? Overlay, string? Kommun, string? KommunFrom, string? DrawingPath,
    string ProfilePath, string ProfileFrom, string? SourcePath, JsonObject Raw, List<string> Known);

  // ---------- files ----------

  /// <summary>The profile file and where its location came from (rulesPath, C3DMCP_RULES_DIR, importRoot).</summary>
  internal static (string Path, string From) FindProfile(string? rulesPath)
  {
    if (!string.IsNullOrWhiteSpace(rulesPath))
    {
      var raw = rulesPath.Trim();
      var candidate = Directory.Exists(raw) ? System.IO.Path.Combine(raw, ProfileFileName) : raw;
      return (FileBoundary.ResolveLocalReadPath(candidate, ".json"), "rulesPath");
    }
    var dir = Environment.GetEnvironmentVariable(DirVariable);
    if (!string.IsNullOrWhiteSpace(dir))
    {
      var file = System.IO.Path.Combine(dir.Trim(), ProfileFileName);
      FileBoundary.RequireLocalDisk(file);
      if (!File.Exists(file))
        throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"{DirVariable} is '{dir}' but {ProfileFileName} is not there. Nothing was changed.");
      return (file, DirVariable);
    }
    var tried = new List<string>();
    foreach (var root in FileBoundary.ImportRootPaths)
      foreach (var sub in new[] { "Rules" })
      {
        var file = System.IO.Path.Combine(root, sub, ProfileFileName);
        tried.Add(file);
        if (File.Exists(file)) return (file, "importRoot");
      }
    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND",
      $"No rule profile found: give rulesPath, set {DirVariable}, or put {ProfileFileName} in an import root's Rules folder (tried {string.Join("; ", tried)}). Nothing was changed.");
  }

  private static JsonObject ReadJsonObject(string path, string what)
  {
    try
    {
      return JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8), documentOptions: ParseOptions) as JsonObject
        ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{what} {path} is not a JSON object. Nothing was changed.");
    }
    catch (JsonException ex)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{what} {path} is not valid JSON ({ex.Message}). Nothing was changed.");
    }
    catch (IOException ex)
    {
      throw new JsonRpcDispatchException("CIVIL3D.FILE_IO_ERROR", $"{what} {path} could not be read ({ex.Message}). Nothing was changed.");
    }
  }

  // ---------- municipality ----------

  /// <summary>Folds a municipality name for matching: diacritics off (a marked letter becomes the plain letter), letters and digits only, lower case.</summary>
  internal static string Fold(string text)
  {
    var decomposed = text.Normalize(NormalizationForm.FormD);
    var sb = new StringBuilder(decomposed.Length);
    foreach (var c in decomposed)
    {
      if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
      if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
    }
    return sb.ToString();
  }

  private static string? MatchKommun(string text, IEnumerable<string> known)
  {
    var folded = Fold(text);
    return folded.Length == 0 ? null : known.FirstOrDefault(k => Fold(k) == folded);
  }

  /// <summary>The municipality of a path: the first folder, from the drawing upwards, whose name matches one of the
  /// profile's municipality keys (C:\Projects\&lt;Municipality&gt;\...).</summary>
  internal static string? KommunFromPath(string? path, IEnumerable<string> known)
  {
    if (string.IsNullOrWhiteSpace(path)) return null;
    var parts = path.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
    for (var i = parts.Length - 2; i >= 0; i--)
    {
      var match = MatchKommun(parts[i], known);
      if (match != null) return match;
    }
    return null;
  }

  // ---------- load and merge ----------

  /// <summary>Loads the profile and merges the municipality: kommun (parameter) wins, then drawingPath (parameter), then
  /// the active drawing's path. An unknown kommun parameter is an error; an unknown folder only means the base alone.</summary>
  internal static Loaded Load(string? rulesPath, string? kommun, string? drawingPath)
  {
    var (path, from) = FindProfile(rulesPath);
    var raw = ReadJsonObject(path, "Rule profile");
    var bas = raw["bas"] as JsonObject
      ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Rule profile {path} has no 'bas' object. Nothing was changed.");
    var kommuner = raw["kommuner"] as JsonObject ?? new JsonObject();
    var known = kommuner.Select(kv => kv.Key).ToList();

    string? chosen = null, chosenFrom = null, usedPath = null;
    if (!string.IsNullOrWhiteSpace(kommun))
    {
      chosen = MatchKommun(kommun, known)
        ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"kommun '{kommun}' is not in the profile (known: {string.Join(", ", known)}). Nothing was changed.");
      chosenFrom = "parameter";
    }
    else
    {
      usedPath = !string.IsNullOrWhiteSpace(drawingPath) ? drawingPath : DocumentSnapshot.Current.ActiveDocument;
      chosen = KommunFromPath(usedPath, known);
      if (chosen != null) chosenFrom = !string.IsNullOrWhiteSpace(drawingPath) ? "drawingPath" : "activeDrawing";
    }

    var overlay = chosen != null ? kommuner[chosen] as JsonObject : null;
    var merged = (JsonObject)bas.DeepClone();
    if (overlay != null) MergeInto(merged, overlay);

    string? sourcePath = null;
    var dir = System.IO.Path.GetDirectoryName(path)!;
    var sourceName = bas["kallregler"] is JsonValue sv && sv.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : SourceFileName;
    var sourceCandidate = System.IO.Path.GetFullPath(System.IO.Path.Combine(dir, sourceName));
    if (File.Exists(sourceCandidate)) sourcePath = sourceCandidate;

    return new Loaded(merged, bas, overlay, chosen, chosenFrom, usedPath, path, from, sourcePath, raw, known);
  }

  /// <summary>Deep merge: objects key by key, anything else (lists too) replaced.</summary>
  internal static void MergeInto(JsonObject target, JsonObject overlay)
  {
    foreach (var (key, value) in overlay)
    {
      if (value is JsonObject o && target[key] is JsonObject t) MergeInto(t, o);
      else target[key] = value?.DeepClone();
    }
  }

  /// <summary>The node at a dotted path ("servis.spolbrunn.diameterMm"), null when any step is missing.</summary>
  internal static JsonNode? At(JsonNode? root, string path)
  {
    var node = root;
    foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
    {
      if (node is not JsonObject o || !o.TryGetPropertyValue(part, out node)) return null;
    }
    return node;
  }

  /// <summary>Dotted paths of the leaves a municipality overrides.</summary>
  private static List<string> Leaves(JsonObject node, string prefix = "")
  {
    var list = new List<string>();
    foreach (var (key, value) in node)
    {
      var p = prefix.Length == 0 ? key : prefix + "." + key;
      if (value is JsonObject o && o.Count > 0) list.AddRange(Leaves(o, p));
      else list.Add(p);
    }
    return list;
  }

  /// <summary>C227: the source rules (source_rules.json) beside the profile, null when the file is not there.</summary>
  internal static JsonObject? ReadSource(Loaded loaded) => loaded.SourcePath == null ? null : ReadJsonObject(loaded.SourcePath, "Source rules");

  // ---------- getRules ----------

  /// <summary>C222 getRules {kommun?, drawingPath?, section?, rulesPath?, sourceSection?, includeSource?}: the merged
  /// profile (or one section of it), which municipality was used and where it came from, and the file paths.
  /// sourceSection (dotted path) or includeSource: true also returns source_rules.json (a section or all of it).</summary>
  public static Task<object?> GetRulesAsync(JsonObject? parameters)
  {
    var loaded = Load(PluginRuntime.GetOptionalString(parameters, "rulesPath"), PluginRuntime.GetOptionalString(parameters, "kommun"),
      PluginRuntime.GetOptionalString(parameters, "drawingPath"));
    var section = PluginRuntime.GetOptionalString(parameters, "section")?.Trim();
    var sourceSection = PluginRuntime.GetOptionalString(parameters, "sourceSection")?.Trim();
    var includeSource = PluginRuntime.GetOptionalBool(parameters, "includeSource") ?? false;
    JsonNode? rules = loaded.Merged;
    if (!string.IsNullOrEmpty(section))
      rules = At(loaded.Merged, section)
        ?? throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Section '{section}' is not in the profile (top level: {string.Join(", ", loaded.Merged.Select(kv => kv.Key))}).");
    var result = new Dictionary<string, object?>
    {
      ["kommun"] = loaded.Kommun,
      ["kommunFrom"] = loaded.KommunFrom ?? "none (base only)",
      ["drawingPath"] = loaded.DrawingPath,
      ["knownKommuner"] = loaded.Known,
      ["overrides"] = loaded.Overlay == null ? new List<string>() : Leaves(loaded.Overlay),
      ["section"] = string.IsNullOrEmpty(section) ? null : section,
      ["rules"] = rules?.DeepClone(),
      ["schemaVersion"] = At(loaded.Raw, "schema.version")?.DeepClone(),
      ["files"] = new Dictionary<string, object?>
      {
        ["profile"] = loaded.ProfilePath, ["profileFrom"] = loaded.ProfileFrom,
        ["sourceRules"] = loaded.SourcePath, ["sourceRulesFound"] = loaded.SourcePath != null,
      },
    };
    if (!string.IsNullOrEmpty(sourceSection) || includeSource)
    {
      if (loaded.SourcePath == null)
        throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"The source rules file is not beside {loaded.ProfilePath}.");
      var source = ReadJsonObject(loaded.SourcePath, "Source rules");
      result["sourceSection"] = string.IsNullOrEmpty(sourceSection) ? null : sourceSection;
      result["source"] = (string.IsNullOrEmpty(sourceSection) ? source : At(source, sourceSection!)
        ?? throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Section '{sourceSection}' is not in {loaded.SourcePath} (top level: {string.Join(", ", source.Select(kv => kv.Key))}).")).DeepClone();
    }
    return Task.FromResult<object?>(result);
  }

  // ---------- defaults for commands (profile: true or kommun) ----------

  /// <summary>The commands that take profile: true (or kommun) and fill omitted values from the profile.</summary>
  internal static readonly HashSet<string> ProfileCommands = new(StringComparer.Ordinal)
  {
    "addServiceConnection", "gradeGravityNetwork", "splitBends", "networkCrossings", "syncPipeNetwork",
    "moveNetworkNode", "offsetPipeRun", "resolveCrossings",   // C231-C233
    "tracePipeChain", "applyChainProfile", "buildDetailSheet",   // C237-C239
    "buildPlanProfileSheet", "placeFlags",   // C240-C241
  };

  /// <summary>Whether a call asks for profile defaults: profile: true, or kommun given (unless profile: false).</summary>
  internal static bool Requested(JsonObject? parameters)
  {
    if (parameters == null) return false;
    var profile = parameters["profile"] is JsonValue pv && pv.TryGetValue<bool>(out var b) ? b : (bool?)null;
    if (profile == false) return false;
    var kommun = parameters["kommun"] is JsonValue kv && kv.TryGetValue<string>(out var k) && !string.IsNullOrWhiteSpace(k);
    return profile == true || kommun;
  }

  private sealed class Filler
  {
    private readonly Loaded _loaded;
    internal readonly List<Dictionary<string, object?>> Used = new();
    internal readonly List<string> Missing = new();
    internal Filler(Loaded loaded) => _loaded = loaded;

    internal JsonNode? Value(string rule) => At(_loaded.Merged, rule);

    internal string? Text(string rule) => Value(rule) is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>Sets target[key] from the profile's rule when the caller left it out; explicit values always win.</summary>
    internal void Fill(JsonObject target, string key, string param, string rule)
    {
      if (target.ContainsKey(key) && target[key] != null) return;
      var value = Value(rule);
      if (value == null)
      {
        if (!Missing.Contains(rule)) Missing.Add(rule);
        return;
      }
      target[key] = value.DeepClone();
      var fromKommun = _loaded.Overlay != null && At(_loaded.Overlay, rule) != null;
      Used.Add(new Dictionary<string, object?>
      {
        ["param"] = param, ["value"] = value.DeepClone(), ["rule"] = rule,
        ["source"] = fromKommun ? "kommuner." + _loaded.Kommun : "bas",
      });
    }
  }

  /// <summary>C222: a copy of the parameters with omitted values filled from the profile, and what was taken. Explicit
  /// parameters always win. Rules that the profile lacks are listed under notInProfile.</summary>
  internal static (JsonObject Parameters, Dictionary<string, object?> Report) ApplyDefaults(string method, JsonObject? parameters)
  {
    var p = (JsonObject?)parameters?.DeepClone() ?? new JsonObject();
    var loaded = Load(PluginRuntime.GetOptionalString(p, "rulesPath"), PluginRuntime.GetOptionalString(p, "kommun"),
      PluginRuntime.GetOptionalString(p, "drawingPath"));
    var f = new Filler(loaded);
    static bool Same(string? a, string? b) => a != null && b != null && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
    void FillService(JsonObject t, string pre)
    {
      var kind = PluginRuntime.GetOptionalString(t, "kind");
      if (Same(kind, "water"))
      {
        // C225: the water service (PE 63 at slope 0 to a valve)
        f.Fill(t, "partName", pre + "partName", "servis.vServis.partName");
        f.Fill(t, "diameter", pre + "diameter", "servis.vServis.diameterMm");
        f.Fill(t, "slopePermille", pre + "slopePermille", "servis.vServis.slopePermille");
        f.Fill(t, "invertRule", pre + "invertRule", "servis.invertRule");
        if (t["end"] is JsonObject vend) f.Fill(vend, "partName", pre + "end.partName", "servis.vServis.ventilPartName");
        return;
      }
      f.Fill(t, "slopePermille", pre + "slopePermille", "servis.slopePermille");
      if (Same(kind, "inlet")) return;   // the existing pipe's part and size are the caller's
      f.Fill(t, "invertRule", pre + "invertRule", "servis.invertRule");
      if (t["end"] is JsonObject end)
      {
        // kind: "gully" picks the gully (profile key rannstensbrunn) as end structure; otherwise the clean-out (servis.spolbrunn)
        var gully = Same(kind, "gully");
        f.Fill(end, "partName", pre + "end.partName", gully ? "rannstensbrunn.partName" : "servis.spolbrunn.partName");
        var part = end["partName"] is JsonValue pn && pn.TryGetValue<string>(out var s) ? s : null;
        if (Same(part, f.Text("rannstensbrunn.partName")))
        {
          f.Fill(end, "structureDiameter", pre + "end.structureDiameter", "rannstensbrunn.diameterMm");
          f.Fill(end, "sumpDepth", pre + "end.sumpDepth", "rannstensbrunn.sumpDepthM");
          f.Fill(t, "endBelowRim", pre + "endBelowRim", "rannstensbrunn.endBelowRimM");
          f.Fill(t, "diameter", pre + "diameter", "rannstensbrunn.servisDiameterMm");
        }
        else if (Same(part, f.Text("servis.spolbrunn.partName")))
          f.Fill(end, "structureDiameter", pre + "end.structureDiameter", "servis.spolbrunn.diameterMm");
      }
      // C225: the service pipe itself (servis.partName / servis.diameterMm, e.g. a PP 160 pipe)
      f.Fill(t, "partName", pre + "partName", "servis.partName");
      f.Fill(t, "diameter", pre + "diameter", "servis.diameterMm");
      if (t["avoid"] is JsonArray avoid)
        for (var i = 0; i < avoid.Count; i++)
          if (avoid[i] is JsonObject a)
          {
            f.Fill(a, "clearance", $"{pre}avoid[{i}].clearance", "korsning.clearanceM");
            f.Fill(a, "kneeAfter", $"{pre}avoid[{i}].kneeAfter", "korsning.kneeAfterM");
          }
    }
    switch (method)
    {
      case "addServiceConnection":
      {
        // C225: several connections: each item gets the top-level values it leaves out, then the profile per item
        if (p["connections"] is JsonArray items)
        {
          for (var i = 0; i < items.Count; i++)
            if (items[i] is JsonObject item)
            {
              foreach (var (k, v) in p)
                if (k is not ("connections" or "pruneOthers" or "dryRun") && !item.ContainsKey(k)) item[k] = v?.DeepClone();
              FillService(item, $"connections[{i}].");
            }
        }
        else FillService(p, "");
        break;
      }
      case "gradeGravityNetwork":
        // the profile has no grading values yet (proposed keys, listed under notInProfile until added)
        f.Fill(p, "defaultPermille", "defaultPermille", "sjalvfall.standardPromille");
        f.Fill(p, "dropStraight", "dropStraight", "sjalvfall.fallRakBrunnM");
        f.Fill(p, "dropBend", "dropBend", "sjalvfall.fallBojBrunnM");
        f.Fill(p, "crownToCrown", "crownToCrown", "sjalvfall.hjassaMotHjassa");
        break;
      case "splitBends":
        f.Fill(p, "maxDeg", "maxDeg", "vatten.maxBojGrader");
        f.Fill(p, "straightBetween", "straightBetween", "vatten.rakMellanBojarM");
        break;
      case "networkCrossings":
        f.Fill(p, "minClearance", "minClearance", "korsning.clearanceM");
        break;
      case "moveNetworkNode":
      case "offsetPipeRun":
        // C231/C232: the crossings reported after the move are judged by the profile's pair rules
        f.Fill(p, "minClearance", "minClearance", "korsning.clearanceM");
        f.Fill(p, "rules", "rules", "korsning.regler");
        break;
      case "resolveCrossings":
        // C233: the required clearance per pair (an against entry's own clearance wins), the knee distance
        f.Fill(p, "minClearance", "minClearance", "korsning.clearanceM");
        f.Fill(p, "rules", "rules", "korsning.regler");
        f.Fill(p, "kneeAfter", "kneeAfter", "korsning.kneeAfterM");
        break;
      case "tracePipeChain":
      case "applyChainProfile":
        // C237/C238: the bend rule reported per node, the crossings along the chain judged by the pair rules
        f.Fill(p, "maxBendDeg", "maxBendDeg", "vatten.maxBojGrader");
        f.Fill(p, "minClearance", "minClearance", "korsning.clearanceM");
        f.Fill(p, "rules", "rules", "korsning.regler");
        break;
      case "buildDetailSheet":
        // C239: the detail sheet's layout and lettering (proposed keys, listed under notInProfile until added)
        if (p["copyFrom"] == null) f.Fill(p, "template", "template", "blad.detalj.mallLayout");
        f.Fill(p, "margin", "margin", "blad.detalj.marginalMm");
        f.Fill(p, "titleHeight", "titleHeight", "blad.detalj.rubrikMm");
        f.Fill(p, "scaleTextHeight", "scaleTextHeight", "blad.detalj.skalaTextMm");
        f.Fill(p, "planArea", "planArea", "blad.detalj.planytaMm");
        f.Fill(p, "viewportLayer", "viewportLayer", "blad.detalj.viewportLager");
        break;
      case "buildPlanProfileSheet":
        // C240: the sheet keys (blad.*: scale, viewport and length rules of the sheet layout); proposed keys listed under notInProfile
        f.Fill(p, "scale", "scale", "blad.skalaPlan");
        f.Fill(p, "verticalScale", "verticalScale", "blad.skalaProfilVertikal");
        f.Fill(p, "profileViewportMm", "profileViewportMm", "blad.profilViewportMm");
        f.Fill(p, "normalLengthMm", "normalLengthMm", "blad.normalLangdMm");
        f.Fill(p, "bandTitleMm", "bandTitleMm", "blad.bandrubrikMm");
        f.Fill(p, "rightAxisMm", "rightAxisMm", "blad.hogeraxelMm");
        if (p["copyFrom"] == null) f.Fill(p, "template", "template", "blad.planProfil.mallLayout");
        f.Fill(p, "maxSlideMm", "maxSlideMm", "blad.maxGlidningMm");
        break;
      case "placeFlags":
        // C241: the flag's text height on paper and its layer (proposed keys blad.flagga.*)
        if (p["textHeight"] == null) f.Fill(p, "textHeightMm", "textHeightMm", "blad.flagga.texthojdMm");
        break;
      case "syncPipeNetwork":
        // C224: falls through structures when the spec grades from outlets; pressure polylines at the profile's depth
        if (p["outlets"] is JsonArray { Count: > 0 })
        {
          f.Fill(p, "dropStraight", "dropStraight", "sjalvfall.fallRakBrunnM");
          f.Fill(p, "dropBend", "dropBend", "sjalvfall.fallBojBrunnM");
          if (!p.ContainsKey("junction")) f.Fill(p, "crownToCrown", "crownToCrown", "sjalvfall.hjassaMotHjassa");
        }
        if (p["polylines"] is JsonArray lines)
          for (var i = 0; i < lines.Count; i++)
            if (lines[i] is JsonObject line && line["slopePermille"] == null)
              f.Fill(line, "centerDepth", $"polylines[{i}].centerDepth", "vatten.centerDepthM");
        // structure sizes by part: clean-outs and gullies of the profile's part names
        if (p["structures"] is JsonArray structures)
          for (var i = 0; i < structures.Count; i++)
            if (structures[i] is JsonObject st && st["partName"] is JsonValue pv && pv.TryGetValue<string>(out var part))
            {
              if (Same(part, f.Text("servis.spolbrunn.partName")))
                f.Fill(st, "structureDiameter", $"structures[{i}].structureDiameter", "servis.spolbrunn.diameterMm");
              else if (Same(part, f.Text("rannstensbrunn.partName")))
                f.Fill(st, "structureDiameter", $"structures[{i}].structureDiameter", "rannstensbrunn.diameterMm");
            }
        break;
    }
    var report = new Dictionary<string, object?>
    {
      ["kommun"] = loaded.Kommun,
      ["kommunFrom"] = loaded.KommunFrom ?? "none (base only)",
      ["file"] = loaded.ProfilePath,
      ["fromProfile"] = f.Used,
      ["notInProfile"] = f.Missing,
    };
    return (p, report);
  }

  /// <summary>Adds the profile report to a command's answer: fromProfile (param, value, rule, source) and profile
  /// (kommun, kommunFrom, file, notInProfile).</summary>
  internal static object? Annotate(object? result, Dictionary<string, object?> report)
  {
    if (result is not Dictionary<string, object?> answer) return result;
    answer["fromProfile"] = report["fromProfile"];
    answer["profile"] = new Dictionary<string, object?>
    {
      ["kommun"] = report["kommun"], ["kommunFrom"] = report["kommunFrom"], ["file"] = report["file"], ["notInProfile"] = report["notInProfile"],
    };
    return answer;
  }
}
