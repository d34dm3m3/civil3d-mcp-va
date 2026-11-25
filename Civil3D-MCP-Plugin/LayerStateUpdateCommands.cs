using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;

namespace Civil3DMcpPlugin;

/// <summary>
/// C236 layerStateUpdate (F7, 2026-11-24): one or more properties of some layers changed inside saved layer states,
/// everything else in the states left as it was. Use case: a drafting rule that a layer
/// state must not bring back colours (the outgoing lines turned coloured again in a project when an older state was
/// restored); three project scripts rebuilt states by restoring, changing and saving them again, which also changed the layers they did not mean to.
///
/// .NET has no call to change a layer inside a state (LayerStateManager saves the drawing's current layers only), so
/// the state's record is edited: the LayerStatesDictionary holds one Xrecord per state, a header (91 mask, 301
/// description, 290, 302) and per layer 330 (the layer), 90 flags (1 off, 2 frozen, 4 locked, 8 plottable, 16 frozen
/// in new viewports), 62 colour index, 370 lineweight, 331 linetype, 1 plot style, 440 transparency and 92 true colour
/// when the colour is not an index colour (read live 2026-11-24 from states saved by layerStateSave).
/// </summary>
public static class LayerStateUpdateCommands
{
  private const int FlagOff = 1, FlagFrozen = 2, FlagLocked = 4, FlagPlot = 8, FlagNewVp = 16;

  private static readonly Dictionary<string, (int Flag, LayerStateMasks Mask)> Flags = new(StringComparer.OrdinalIgnoreCase)
  {
    ["on"] = (FlagOff, LayerStateMasks.On), ["frozen"] = (FlagFrozen, LayerStateMasks.Frozen), ["locked"] = (FlagLocked, LayerStateMasks.Locked),
    ["plot"] = (FlagPlot, LayerStateMasks.Plot), ["newViewport"] = (FlagNewVp, LayerStateMasks.NewViewport),
  };

  private static readonly Dictionary<string, LayerStateMasks> Other = new(StringComparer.OrdinalIgnoreCase)
  {
    ["color"] = LayerStateMasks.Color, ["linetype"] = LayerStateMasks.LineType, ["lineweight"] = LayerStateMasks.LineWeight,
    ["plotStyle"] = LayerStateMasks.PlotStyle, ["transparency"] = LayerStateMasks.Transparency,
  };

  private static readonly Dictionary<string, int> ColorNames = new(StringComparer.OrdinalIgnoreCase)
    { ["red"] = 1, ["yellow"] = 2, ["green"] = 3, ["cyan"] = 4, ["blue"] = 5, ["magenta"] = 6, ["white"] = 7 };

  private sealed class LayerBlock
  {
    public ObjectId Layer;
    public string Name = "";
    public readonly List<TypedValue> Values = new();   // the codes after 330, in their order
  }

  private static bool Match(string pattern, string name) => PipeNetworkCommands.NetworkNameMatch(pattern, name);

  /// <summary>C236: {names: [state names, * as a wildcard], layers: [layer names, * as a wildcard], values: {on?, frozen?,
  /// locked?, plot?, newViewport? (bool), color? (index 1-255, a name red..white, or "r,g,b"), linetype? (a loaded
  /// linetype), lineweight? (hundredths of a mm as listLayers gives them, mm under 3, or "Default"), plotStyle?,
  /// transparency? (percent 0-90)}, dryRun?, rawCodes? (diagnostic: the first n group codes of each matched state)}.
  /// Only the given properties of the matched layers inside the matched states change; layers the state does not hold
  /// are listed (notInState), not added; a property the state does not save (its mask) is changed but listed under
  /// notInMask (a restore does not apply it). Answer per state: changed [{layer, before, after}], unchanged, notInState,
  /// notInMask. Nothing in the drawing's layers changes.</summary>
  public static Task<object?> UpdateLayerStateAsync(JsonObject? parameters)
  {
    List<string> List(string key) => (PluginRuntime.GetParameter(parameters, key) as JsonArray)?.Select(n => n?.GetValue<string>()).OfType<string>()
      .Where(s => !string.IsNullOrWhiteSpace(s)).ToList() ?? new List<string>();
    var statePatterns = List("names");
    var layerPatterns = List("layers");
    var rawCodes = PluginRuntime.GetOptionalInt(parameters, "rawCodes");
    var dryRun = PluginRuntime.GetOptionalBool(parameters, "dryRun") ?? false;
    if (statePatterns.Count == 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "names must list the layer states (* as a wildcard). Nothing was changed.");
    var values = PluginRuntime.GetParameter(parameters, "values") as JsonObject;
    if (rawCodes == null && (layerPatterns.Count == 0 || values == null || values.Count == 0))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Give layers and values {on, frozen, locked, plot, newViewport, color, linetype, lineweight, plotStyle, transparency}. Nothing was changed.");
    var unknown = values?.Select(kv => kv.Key).Where(k => !Flags.ContainsKey(k) && !Other.ContainsKey(k)).ToList() ?? new List<string>();
    if (unknown.Count > 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Unknown values: {string.Join(", ", unknown)}. Use {string.Join(", ", Flags.Keys.Concat(Other.Keys))}. Nothing was changed.");

    // the new values, checked before anything is opened
    var flagSet = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
    int? aci = null; int? trueColor = null; string? linetypeName = null; int? lineweight = null; string? plotStyle = null; int? transparency = null;
    foreach (var (key, node) in values ?? new JsonObject())
    {
      if (Flags.ContainsKey(key))
      {
        if (node is not JsonValue bv || !bv.TryGetValue<bool>(out var b))
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"values.{key} must be true or false. Nothing was changed.");
        flagSet[key] = b;
        continue;
      }
      switch (key.ToLowerInvariant())
      {
        case "color":
          (aci, trueColor) = ParseColor(node);
          break;
        case "linetype":
          linetypeName = node is JsonValue lv && lv.TryGetValue<string>(out var ls) && !string.IsNullOrWhiteSpace(ls) ? ls.Trim()
            : throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "values.linetype must be a linetype name. Nothing was changed.");
          break;
        case "lineweight":
          lineweight = ParseLineweight(node);
          break;
        case "plotstyle":
          plotStyle = node is JsonValue pv && pv.TryGetValue<string>(out var ps) ? ps
            : throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "values.plotStyle must be a plot style name. Nothing was changed.");
          break;
        case "transparency":
          if (node is not JsonValue tv || !tv.TryGetValue<double>(out var pct) || pct < 0 || pct > 90)
            throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "values.transparency must be a percentage 0-90. Nothing was changed.");
          transparency = pct == 0 ? 0 : 0x02000000 | (int)Math.Round(255 * (100 - pct) / 100.0);
          break;
      }
    }

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var manager = database.LayerStateManager;
      var dictId = manager.LayerStatesDictionaryId(false);
      if (dictId.IsNull)
        throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", "The drawing has no layer states. Nothing was changed.");
      var dict = (DBDictionary)transaction.GetObject(dictId, OpenMode.ForRead);
      var stateKeys = new List<string>();
      foreach (DBDictionaryEntry e in dict) stateKeys.Add(e.Key);
      var matched = stateKeys.Where(k => statePatterns.Any(p => Match(p, k))).ToList();
      if (matched.Count == 0)
        throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"No layer state matches {string.Join(", ", statePatterns)}. States: {string.Join(", ", stateKeys)}. Nothing was changed.");

      ObjectId linetypeId = ObjectId.Null;
      if (linetypeName != null)
      {
        var lt = (LinetypeTable)transaction.GetObject(database.LinetypeTableId, OpenMode.ForRead);
        if (!lt.Has(linetypeName))
          throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Linetype '{linetypeName}' is not loaded in the drawing. Nothing was changed.");
        linetypeId = lt[linetypeName];
      }
      var drawingLayers = new List<string>();
      var table = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
      foreach (ObjectId id in table)
        if (!id.IsErased) drawingLayers.Add(((LayerTableRecord)transaction.GetObject(id, OpenMode.ForRead)).Name);
      var wantedLayers = drawingLayers.Where(n => layerPatterns.Any(p => Match(p, n))).ToHashSet(StringComparer.OrdinalIgnoreCase);
      if (rawCodes == null && wantedLayers.Count == 0)
        throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"No drawing layer matches {string.Join(", ", layerPatterns)}. Nothing was changed.");

      string NameOf(ObjectId id)
      {
        if (id.IsNull || id.IsErased) return "";
        return transaction.GetObject(id, OpenMode.ForRead) is SymbolTableRecord r ? r.Name : "";
      }
      object? Describe(LayerBlock b)
      {
        int Get(int code) => b.Values.Where(v => v.TypeCode == code).Select(v => Convert.ToInt32(v.Value)).FirstOrDefault();
        var flags = Get(90);
        var tc = b.Values.Any(v => v.TypeCode == 92) ? Get(92) : (int?)null;
        var tr = Get(440);
        var lw = Get(370);
        return new Dictionary<string, object?>
        {
          ["on"] = (flags & FlagOff) == 0, ["frozen"] = (flags & FlagFrozen) != 0, ["locked"] = (flags & FlagLocked) != 0, ["plot"] = (flags & FlagPlot) != 0,
          ["newViewport"] = (flags & FlagNewVp) != 0,
          ["color"] = tc is int t ? $"{(t >> 16) & 255},{(t >> 8) & 255},{t & 255}" : (object)Get(62),
          ["linetype"] = NameOf(b.Values.Where(v => v.TypeCode == 331).Select(v => v.Value is ObjectId o ? o : ObjectId.Null).FirstOrDefault()),
          ["lineweight"] = lw == -3 ? "Default" : lw,
          ["plotStyle"] = b.Values.Where(v => v.TypeCode == 1).Select(v => v.Value as string).FirstOrDefault(),
          ["transparency"] = tr == 0 ? 0 : (int)Math.Round(100 - (tr & 255) * 100 / 255.0),
        };
      }

      var states = new List<Dictionary<string, object?>>();
      foreach (var key in matched)
      {
        var xr = transaction.GetObject(dict.GetAt(key), OpenMode.ForRead) as Xrecord
          ?? throw new JsonRpcDispatchException("CIVIL3D.OPERATION_FAILED", $"Layer state '{key}' is not stored as an Xrecord. Nothing was changed.");
        var data = xr.Data?.AsArray() ?? Array.Empty<TypedValue>();
        if (rawCodes is int rc)
        {
          states.Add(new Dictionary<string, object?>
          {
            ["name"] = key,
            ["codes"] = data.Take(rc).Select(tv => new object?[] { tv.TypeCode, tv.Value is ObjectId oid ? $"{oid.ObjectClass?.DxfName}:{NameOf(oid)}" : tv.Value?.ToString() }).ToList(),
          });
          continue;
        }
        var header = new List<TypedValue>();
        var blocks = new List<LayerBlock>();
        foreach (var tv in data)
        {
          if (tv.TypeCode == 330 && tv.Value is ObjectId lid && (lid.IsNull || lid.ObjectClass?.DxfName == "LAYER"))
          {
            blocks.Add(new LayerBlock { Layer = lid, Name = NameOf(lid) });
            continue;
          }
          if (blocks.Count == 0) header.Add(tv);
          else blocks[^1].Values.Add(tv);
        }
        if (blocks.Count == 0 || blocks.Any(b => !b.Values.Any(v => v.TypeCode == 90)))
          throw new JsonRpcDispatchException("CIVIL3D.OPERATION_FAILED", $"Layer state '{key}' has a layout this command does not know (no layer entries with flags). Nothing was changed.");
        var mask = (LayerStateMasks)(header.Where(v => v.TypeCode == 91).Select(v => Convert.ToInt32(v.Value)).FirstOrDefault());
        var notInMask = (values ?? new JsonObject()).Select(kv => kv.Key)
          .Where(k => ((Flags.TryGetValue(k, out var f) ? f.Mask : Other[k]) & mask) == 0).ToList();
        var changed = new List<Dictionary<string, object?>>();
        var unchanged = 0;
        foreach (var b in blocks.Where(b => wantedLayers.Contains(b.Name)))
        {
          var before = Describe(b);
          var vals = b.Values;
          int Index(int code) => vals.FindIndex(v => v.TypeCode == code);
          void Set(int code, object value, int afterCode)
          {
            var i = Index(code);
            var tv = new TypedValue(code, value);
            if (i >= 0) vals[i] = tv;
            else { var j = Index(afterCode); vals.Insert(j >= 0 ? j + 1 : vals.Count, tv); }
          }
          var flags = Convert.ToInt32(vals[Index(90)].Value);
          foreach (var (k, on) in flagSet)
          {
            var bit = Flags[k].Flag;
            var setBit = k.Equals("on", StringComparison.OrdinalIgnoreCase) ? !on : on;   // the record keeps "off"
            flags = setBit ? flags | bit : flags & ~bit;
          }
          Set(90, flags, 330);
          if (aci != null)
          {
            Set(62, (short)aci.Value, 90);
            var i92 = Index(92);
            if (trueColor == null) { if (i92 >= 0) vals.RemoveAt(i92); }
            else Set(92, trueColor.Value, 440);
          }
          if (lineweight != null) Set(370, (short)lineweight.Value, 62);
          if (linetypeName != null) Set(331, linetypeId, 370);
          if (plotStyle != null) Set(1, plotStyle, 331);
          if (transparency != null) Set(440, transparency.Value, 1);
          var after = Describe(b);
          if (System.Text.Json.JsonSerializer.Serialize(before) == System.Text.Json.JsonSerializer.Serialize(after)) { unchanged++; continue; }
          changed.Add(new Dictionary<string, object?> { ["layer"] = b.Name, ["before"] = before, ["after"] = after });
        }
        if (changed.Count > 0 && !dryRun)
        {
          var list = new List<TypedValue>(header);
          foreach (var b in blocks) { list.Add(new TypedValue(330, b.Layer)); list.AddRange(b.Values); }
          xr.UpgradeOpen();
          xr.Data = new ResultBuffer(list.ToArray());
        }
        var inState = blocks.Select(b => b.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        states.Add(new Dictionary<string, object?>
        {
          ["name"] = key, ["changed"] = changed, ["unchanged"] = unchanged,
          ["notInState"] = wantedLayers.Where(n => !inState.Contains(n)).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(),
          ["notInMask"] = notInMask,
        });
      }
      return new Dictionary<string, object?>
      {
        ["dryRun"] = dryRun, ["layers"] = wantedLayers.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(), ["states"] = states,
        ["changedLayers"] = states.Sum(s => s.TryGetValue("changed", out var c) && c is List<Dictionary<string, object?>> l ? l.Count : 0),
      };
    });
  }

  private static (int Aci, int? TrueColor) ParseColor(JsonNode? node)
  {
    if (node is JsonValue v && v.TryGetValue<int>(out var i))
      return i is >= 1 and <= 255 ? (i, null) : throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "values.color as a number must be an index colour 1-255. Nothing was changed.");
    var s = node is JsonValue sv && sv.TryGetValue<string>(out var t) ? t.Trim() : "";
    if (ColorNames.TryGetValue(s, out var named)) return (named, null);
    if (int.TryParse(s, out i) && i is >= 1 and <= 255) return (i, null);
    var parts = s.Split(',');
    if (parts.Length == 3 && parts.All(p => byte.TryParse(p.Trim(), out _)))
    {
      byte r = byte.Parse(parts[0].Trim()), g = byte.Parse(parts[1].Trim()), b = byte.Parse(parts[2].Trim());
      var nearest = Autodesk.AutoCAD.Colors.EntityColor.LookUpAci(r, g, b);
      return (nearest, unchecked((int)0xC2000000) | (r << 16) | (g << 8) | b);
    }
    throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "values.color must be an index colour 1-255, a name (red..white) or \"r,g,b\". Nothing was changed.");
  }

  private static int ParseLineweight(JsonNode? node)
  {
    if (node is JsonValue sv && sv.TryGetValue<string>(out var s))
    {
      if (string.Equals(s.Trim(), "Default", StringComparison.OrdinalIgnoreCase)) return (int)LineWeight.ByLineWeightDefault;
      if (!double.TryParse(s.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d))
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "values.lineweight must be a number or Default. Nothing was changed.");
      node = JsonValue.Create(d);
    }
    if (node is not JsonValue nv || !nv.TryGetValue<double>(out var w))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "values.lineweight must be a number or Default. Nothing was changed.");
    var hundredths = w < 3 ? (int)Math.Round(w * 100) : (int)Math.Round(w);
    if (!Enum.IsDefined(typeof(LineWeight), hundredths) || hundredths < 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"values.lineweight {w} is not a standard lineweight (0, 5, 9, 13, 15, 18, 20, 25, 30, 35, 40, 50, 53, 60, 70, 80, 90, 100, 106, 120, 140, 158, 200, 211 hundredths of a mm). Nothing was changed.");
    return hundredths;
  }
}
