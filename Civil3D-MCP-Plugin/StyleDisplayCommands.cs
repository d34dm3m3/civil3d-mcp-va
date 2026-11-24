using System.Collections;
using System.Globalization;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices.Styles;

namespace Civil3DMcpPlugin;

/// <summary>
/// C98e: the display components of a style (Display tab) and, where the style has them, its marker styles. Use case
/// (pilot project, 2026-10-10): the profile style "Konstruktion - VA" plots only the break points, so designed
/// profiles do not show in the plot; its lines, curves and extensions have to be made visible and plottable.
/// Generic over every style with GetDisplayStyle* methods (found through Civil3DCompatibility.GetDisplayComponentGetters):
/// a profile style has the view Profile (Line, Curve, Arrow, LineExtension, SymmetricalParabola, AsymmetricalParabola,
/// ParabolicCurveExtension, WarningSymbol), Section (ProfileMarker) and Model (Chain3D); there is no plan view. Each
/// component (DisplayStyle) has visible, layer, color, linetype, linetypeScale, lineweight and plotStyle. The style is
/// addressed by its collection path under CivilDocument.Styles (default ProfileStyles; also AlignmentStyles,
/// ProfileViewStyles, BandStyles.ProfileViewPipeNetworkBandStyles, …). Marker styles: the ObjectId properties named
/// …MarkerStyle or …MarkerStyleId (a profile style's BeginPoint, VIntersectionPoint, ThroughPoint, …), set by the name of
/// a style in MarkerStyles. Layers and linetypes must exist, a plot style must be one of the drawing's named plot styles;
/// everything is checked before the first write, the style is opened for write in the transaction and read back, and a
/// value Civil 3D does not keep undoes the whole call.
/// </summary>
public static class StyleDisplayCommands
{
  private const string DefaultCollection = "ProfileStyles";
  private const int MaxDepth = 3;
  private static readonly string[] DisplayProperties = { "visible", "layer", "color", "linetype", "linetypeScale", "lineweight", "plotStyle" };
  private static readonly string[] MarkerSuffixes = { "MarkerStyleId", "MarkerStyle" };

  private static T? Safe<T>(Func<T> read)
  {
    try
    {
      return read();
    }
    catch (Exception ex) when (ex is not JsonRpcDispatchException)
    {
      return default;   // Civil's wrappers throw several types (CivilException derives from Exception itself)
    }
  }

  /// <summary>C98e: a style's display components per view (visible, layer, color, linetype, linetypeScale, lineweight,
  /// plotStyle) and its marker styles; values as style_display_set takes them.</summary>
  public static Task<object?> GetStyleDisplayAsync(JsonObject? parameters)
  {
    var path = PluginRuntime.GetOptionalString(parameters, "collection")?.Trim();
    var styleName = PluginRuntime.GetRequiredString(parameters, "style").Trim();
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var (collection, resolved) = ResolveCollection(civilDoc, string.IsNullOrEmpty(path) ? DefaultCollection : path);
      var style = FindStyle(collection, resolved, styleName, transaction, OpenMode.ForRead);
      return Describe(resolved, style, transaction);
    });
  }

  /// <summary>
  /// C98e: changes display components (display: {view: {component: {visible?, layer?, color?, linetype?, linetypeScale?,
  /// lineweight?, plotStyle?}}}) and marker styles (markers: {name: marker style}) of one style. Views, components and
  /// marker names without case; colour ByLayer/ByBlock/ACI/"r,g,b"; lineweight in hundredths of a mm (35 = 0.35 mm) or
  /// ByLayer/ByBlock/Default. dryRun reports before/after without writing.
  /// </summary>
  public static Task<object?> SetStyleDisplayAsync(JsonObject? parameters)
  {
    var path = PluginRuntime.GetOptionalString(parameters, "collection")?.Trim();
    var styleName = PluginRuntime.GetRequiredString(parameters, "style").Trim();
    var dryRun = PluginRuntime.GetOptionalBool(parameters, "dryRun") ?? false;
    var display = PluginRuntime.GetParameter(parameters, "display") as JsonObject;
    var markers = PluginRuntime.GetParameter(parameters, "markers") as JsonObject;
    if ((display == null || display.Count == 0) && (markers == null || markers.Count == 0))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
        "Give display {view: {component: {visible, layer, color, …}}} and/or markers {name: marker style}. Nothing was changed.");

    object? Body(Autodesk.AutoCAD.ApplicationServices.Document doc, CivilDocument civilDoc, Database database, Transaction transaction)
    {
      var (collection, resolved) = ResolveCollection(civilDoc, string.IsNullOrEmpty(path) ? DefaultCollection : path);
      // Opened for write from the start in a real run: the display styles act on the object they come from, and an
      // error before or during the writes aborts the transaction, so nothing stays.
      var style = FindStyle(collection, resolved, styleName, transaction, dryRun ? OpenMode.ForRead : OpenMode.ForWrite);
      var name = CivilObjectUtils.GetName(style) ?? styleName;
      var getters = Civil3DCompatibility.GetDisplayComponentGetters(style.GetType(), typeof(DisplayStyle));

      // Check everything first.
      var plan = new List<(Civil3DCompatibility.DisplayComponentGetter Getter, string Property, object Target)>();
      foreach (var (viewKey, viewNode) in display ?? new JsonObject())
      {
        var views = getters.Select(g => g.View).Distinct().ToList();
        var view = views.FirstOrDefault(v => string.Equals(v, viewKey.Trim(), StringComparison.OrdinalIgnoreCase))
          ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", views.Count == 0
            ? $"'{name}' ({style.GetType().Name}) has no display components. Nothing was changed."
            : $"'{name}' has no view '{viewKey}'. Views: {string.Join(", ", views)}. Nothing was changed.");
        if (viewNode is not JsonObject components || components.Count == 0)
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"display.{view} needs {{component: {{…}}}}. Nothing was changed.");
        foreach (var (componentKey, propertiesNode) in components)
        {
          var inView = getters.Where(g => g.View == view).ToList();
          var getter = inView.FirstOrDefault(g => string.Equals(g.Component, componentKey.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
              $"'{name}' has no {view} component '{componentKey}'. Components: {string.Join(", ", inView.Select(g => g.Component))}. Nothing was changed.");
          if (Component(getter, style) == null)
            throw new JsonRpcDispatchException("CIVIL3D.OPERATION_FAILED", $"The {view} component {getter.Component} of '{name}' cannot be read. Nothing was changed.");
          if (propertiesNode is not JsonObject properties || properties.Count == 0)
            throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
              $"display.{view}.{getter.Component} needs properties: {string.Join(", ", DisplayProperties)}. Nothing was changed.");
          foreach (var (propertyKey, valueNode) in properties)
          {
            var property = DisplayProperties.FirstOrDefault(p => string.Equals(p, propertyKey.Trim(), StringComparison.OrdinalIgnoreCase))
              ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
                $"'{propertyKey}' is not a display property; use {string.Join(", ", DisplayProperties)}. Nothing was changed.");
            if (plan.Any(p => p.Getter == getter && p.Property == property))
              throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{view}.{getter.Component}.{property} is given twice. Nothing was changed.");
            plan.Add((getter, property, Target(property, valueNode, $"{view}.{getter.Component}.{property}", database, transaction)));
          }
        }
      }

      var markerPlan = new List<(string Property, string Key, ObjectId Before, ObjectId Target)>();
      if (markers is { Count: > 0 })
      {
        var available = Civil3DCompatibility.GetReadWritePropertyNames(style.GetType(), typeof(ObjectId), MarkerSuffixes);
        foreach (var (markerKey, valueNode) in markers)
        {
          var property = available.FirstOrDefault(p => string.Equals(MarkerKey(p), markerKey.Trim(), StringComparison.OrdinalIgnoreCase)
              || string.Equals(p, markerKey.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", available.Count == 0
              ? $"'{name}' has no marker styles. Nothing was changed."
              : $"'{name}' has no marker '{markerKey}'. Markers: {string.Join(", ", available.Select(MarkerKey))}. Nothing was changed.");
          if (markerPlan.Any(m => m.Property == property))
            throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Marker {MarkerKey(property)} is given twice. Nothing was changed.");
          var wanted = valueNode is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : "";
          if (wanted.Length == 0)
            throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Marker {MarkerKey(property)} needs the name of a marker style. Nothing was changed.");
          var current = Civil3DCompatibility.GetPropertyValue(style, property) is ObjectId id ? id : ObjectId.Null;
          markerPlan.Add((property, MarkerKey(property), current, FindMarkerStyle(civilDoc, transaction, wanted, MarkerKey(property))));
        }
      }

      var changed = new List<Dictionary<string, object?>>();
      if (dryRun)
      {
        foreach (var (getter, property, target) in plan)
          changed.Add(Change($"{getter.View}.{getter.Component}.{property}", Read(Component(getter, style), property), Plain(target)));
        foreach (var (_, key, current, target) in markerPlan)
          changed.Add(Change($"markers.{key}", StyleName(transaction, current), StyleName(transaction, target)));
        return new Dictionary<string, object?>
        {
          ["collection"] = resolved, ["style"] = name, ["dryRun"] = true, ["changed"] = changed, ["note"] = "Dry run: nothing was changed.",
        };
      }

      var before = plan.ToDictionary(p => (p.Getter, p.Property), p => Read(Component(p.Getter, style), p.Property));
      foreach (var (getter, property, target) in plan)
      {
        var component = Component(getter, style)
          ?? throw new JsonRpcDispatchException("CIVIL3D.OPERATION_FAILED", $"The {getter.View} component {getter.Component} of '{name}' cannot be read. Nothing was changed.");
        try
        {
          Write(component, property, target);
        }
        catch (Exception ex) when (ex is not JsonRpcDispatchException)
        {
          throw new JsonRpcDispatchException("CIVIL3D.OPERATION_FAILED",
            $"Civil 3D refused {getter.View}.{getter.Component}.{property} = {Plain(target)} for '{name}': {ex.Message} Nothing was changed.");
        }
      }
      foreach (var (property, key, _, target) in markerPlan)
        if (!Civil3DCompatibility.TrySetProperty(style, property, target))
          throw new JsonRpcDispatchException("CIVIL3D.OPERATION_FAILED", $"Civil 3D did not accept the marker style for {key} of '{name}'. Nothing was changed.");

      // Read back from fresh display style objects: a value Civil 3D does not keep undoes the call.
      var refused = new List<string>();
      foreach (var (getter, property, target) in plan)
      {
        var after = Read(Component(getter, style), property);
        var label = $"{getter.View}.{getter.Component}.{property}";
        if (!Same(after, Plain(target))) refused.Add($"{label} (wanted {Plain(target)}, read back {after ?? "nothing"})");
        changed.Add(Change(label, before[(getter, property)], after));
      }
      foreach (var (property, key, previous, target) in markerPlan)
      {
        var after = Civil3DCompatibility.GetPropertyValue(style, property) is ObjectId id ? id : ObjectId.Null;
        if (after != target) refused.Add($"markers.{key} (wanted {StyleName(transaction, target)}, read back {StyleName(transaction, after) ?? "nothing"})");
        changed.Add(Change($"markers.{key}", StyleName(transaction, previous), StyleName(transaction, after)));
      }
      if (refused.Count > 0)
        throw new JsonRpcDispatchException("CIVIL3D.OPERATION_FAILED", $"Civil 3D did not keep {string.Join("; ", refused)} in '{name}'. Nothing was changed.");

      var result = Describe(resolved, style, transaction);
      result["changed"] = changed;
      return result;
    }

    return dryRun ? CivilExecution.ReadAsync<object?>(Body) : CivilExecution.WriteAsync<object?>(Body);
  }

  private static Dictionary<string, object?> Change(string what, object? before, object? after) =>
    new() { ["property"] = what, ["before"] = before, ["after"] = after };

  // ---------------------------------------------------------------- description

  private static Dictionary<string, object?> Describe(string collection, DBObject style, Transaction transaction)
  {
    var views = new Dictionary<string, object?>();
    foreach (var getter in Civil3DCompatibility.GetDisplayComponentGetters(style.GetType(), typeof(DisplayStyle)))
    {
      if (!views.TryGetValue(getter.View, out var node)) views[getter.View] = node = new Dictionary<string, object?>();
      var components = (Dictionary<string, object?>)node!;
      DisplayStyle? component;
      try
      {
        component = getter.Get(style) as DisplayStyle;
      }
      catch (Exception ex)
      {
        // One component Civil 3D cannot give (an enum value without a component) does not hide the others.
        components[getter.Component] = new Dictionary<string, object?> { ["error"] = $"{ex.GetType().Name}: {ex.Message}" };
        continue;
      }
      components[getter.Component] = component == null ? null : DisplayProperties.ToDictionary(p => p, p => Read(component, p));
    }

    var markers = new Dictionary<string, object?>();
    foreach (var property in Civil3DCompatibility.GetReadWritePropertyNames(style.GetType(), typeof(ObjectId), MarkerSuffixes))
      markers[MarkerKey(property)] = Civil3DCompatibility.GetPropertyValue(style, property) is ObjectId id ? StyleName(transaction, id) : null;

    return new Dictionary<string, object?>
    {
      ["collection"] = collection,
      ["style"] = CivilObjectUtils.GetName(style),
      ["type"] = style.GetType().Name,
      ["views"] = views,
      ["markers"] = markers.Count > 0 ? markers : null,
      ["note"] = views.Count == 0 ? "This style has no display components." : "lineweight in hundredths of a mm (35 = 0.35 mm) or ByLayer/ByBlock/Default.",
    };
  }

  private static DisplayStyle? Component(Civil3DCompatibility.DisplayComponentGetter getter, DBObject style)
  {
    try
    {
      return getter.Get(style) as DisplayStyle;
    }
    catch (Exception)
    {
      return null;   // reported by the caller as a component that cannot be read
    }
  }

  private static object? Read(DisplayStyle? component, string property) => component == null ? null : property switch
  {
    "visible" => Safe<bool?>(() => component.Visible),
    "layer" => Safe(() => component.Layer),
    "color" => Safe(() => component.Color is { } color ? LabelStylePropertyCommands.ColorText(color) : null),
    "linetype" => Safe(() => component.Linetype),
    "linetypeScale" => Safe<double?>(() => component.LinetypeScale),
    "lineweight" => Safe(() => LineweightText(component.Lineweight)),
    "plotStyle" => Safe(() => component.PlotStyle),
    _ => null,
  };

  private static void Write(DisplayStyle component, string property, object target)
  {
    switch (property)
    {
      case "visible": component.Visible = (bool)target; break;
      case "layer": component.Layer = (string)target; break;
      case "color": component.Color = (Color)target; break;
      case "linetype": component.Linetype = (string)target; break;
      case "linetypeScale": component.LinetypeScale = (double)target; break;
      case "lineweight": component.Lineweight = (LineWeight)target; break;
      case "plotStyle": component.PlotStyle = (string)target; break;
    }
  }

  /// <summary>A value as style_display_get shows it (colour as text, lineweight as hundredths of a mm or ByLayer/ByBlock/Default).</summary>
  private static object Plain(object target) => target switch
  {
    Color c => LabelStylePropertyCommands.ColorText(c),
    LineWeight w => LineweightText(w),
    _ => target,
  };

  private static object LineweightText(LineWeight weight) => weight switch
  {
    LineWeight.ByLayer => "ByLayer",
    LineWeight.ByBlock => "ByBlock",
    LineWeight.ByLineWeightDefault => "Default",
    _ when (int)weight >= 0 => (int)weight,
    _ => weight.ToString(),
  };

  private static bool Same(object? after, object target) => (after, target) switch
  {
    (null, _) => false,
    (double a, double t) => Math.Abs(a - t) <= 1e-9 * Math.Max(1.0, Math.Abs(t)),
    (string a, string t) => string.Equals(a, t, StringComparison.OrdinalIgnoreCase),
    _ => Equals(after, target),
  };

  // ---------------------------------------------------------------- values

  /// <summary>The value to write, checked: visible a boolean, layer and linetype names that exist, a colour, a
  /// linetype scale above 0, a standard lineweight, a named plot style of the drawing.</summary>
  private static object Target(string property, JsonNode? node, string what, Database database, Transaction transaction)
  {
    var text = node is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : node?.ToJsonString() ?? "";
    switch (property)
    {
      case "visible":
        if (bool.TryParse(text, out var visible)) return visible;
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{what} takes true or false, not '{text}'. Nothing was changed.");
      case "layer":
        return DrawingNames.Layer(database, transaction, text, what);
      case "linetype":
        return DrawingNames.Linetype(database, transaction, text, what);
      case "color":
        return PartStyleCommands.ParseColor(JsonValue.Create(text)!);
      case "linetypeScale":
        if (double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var scale) && double.IsFinite(scale) && scale > 0) return scale;
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{what} takes a number above 0, not '{text}'. Nothing was changed.");
      case "lineweight":
        return Lineweight(text, what);
      default:
        return PlotStyle(text, what, database, transaction);
    }
  }

  private static LineWeight Lineweight(string text, string what)
  {
    if (string.Equals(text, "ByLayer", StringComparison.OrdinalIgnoreCase)) return LineWeight.ByLayer;
    if (string.Equals(text, "ByBlock", StringComparison.OrdinalIgnoreCase)) return LineWeight.ByBlock;
    if (string.Equals(text, "Default", StringComparison.OrdinalIgnoreCase) || string.Equals(text, "ByLineWeightDefault", StringComparison.OrdinalIgnoreCase))
      return LineWeight.ByLineWeightDefault;
    if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hundredths) && hundredths >= 0 && Enum.IsDefined(typeof(LineWeight), hundredths))
      return (LineWeight)hundredths;
    if (Enum.TryParse<LineWeight>(text, true, out var named) && Enum.IsDefined(named) && (int)named >= 0) return named;
    var standard = Enum.GetValues<LineWeight>().Where(w => (int)w >= 0).Select(w => ((int)w).ToString(CultureInfo.InvariantCulture));
    throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
      $"{what}: '{text}' is not a lineweight. Use ByLayer, ByBlock, Default or hundredths of a mm: {string.Join(", ", standard)}. Nothing was changed.");
  }

  /// <summary>A named plot style of the drawing (or ByLayer/ByBlock); a drawing with colour-dependent plot styles
  /// (PSTYLEMODE 1) plots by colour, so its components take no plot style name.</summary>
  private static string PlotStyle(string text, string what, Database database, Transaction transaction)
  {
    if (database.PlotStyleMode)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
        $"{what}: the drawing uses colour-dependent plot styles (PSTYLEMODE 1), which plot by colour; change color or the layer instead. Nothing was changed.");
    if (string.Equals(text, "ByLayer", StringComparison.OrdinalIgnoreCase)) return "ByLayer";
    if (string.Equals(text, "ByBlock", StringComparison.OrdinalIgnoreCase)) return "ByBlock";
    var names = new List<string>();
    if (!database.PlotStyleNameDictionaryId.IsNull && transaction.GetObject(database.PlotStyleNameDictionaryId, OpenMode.ForRead) is DBDictionary dictionary)
      foreach (DBDictionaryEntry entry in dictionary)
      {
        if (string.Equals(entry.Key, text, StringComparison.OrdinalIgnoreCase)) return entry.Key;
        names.Add(entry.Key);
      }
    throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
      $"{what}: the drawing has no plot style '{text}'. Plot styles: ByLayer, ByBlock{(names.Count > 0 ? ", " + string.Join(", ", names) : "")}. Nothing was changed.");
  }

  // ---------------------------------------------------------------- collections and styles

  /// <summary>A style collection by its property path under CivilDocument.Styles (a leading "Styles." is accepted).</summary>
  private static (object Collection, string Path) ResolveCollection(CivilDocument civilDoc, string raw)
  {
    var parts = raw.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    if (parts.Count > 0 && string.Equals(parts[0], "Styles", StringComparison.OrdinalIgnoreCase)) parts.RemoveAt(0);
    if (parts.Count == 0 || parts.Count > MaxDepth)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
        $"'{raw}' names no style collection; give a path under CivilDocument.Styles such as ProfileStyles or AlignmentStyles. Nothing was changed.");
    object current = civilDoc.Styles;
    var walked = new List<string>();
    foreach (var part in parts)
    {
      object? value;
      bool found;
      try
      {
        (found, value) = Civil3DCompatibility.GetPropertyIgnoreCase(current, part);
      }
      catch (Exception ex) when (ex is not JsonRpcDispatchException)
      {
        throw new JsonRpcDispatchException("CIVIL3D.OPERATION_FAILED", $"'{part}' cannot be read in this drawing ({ex.Message}). Nothing was changed.");
      }
      if (!found || value == null)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
          $"No '{part}' under {(walked.Count == 0 ? "Styles" : string.Join(".", walked))}. Under it: "
          + $"{string.Join(", ", Civil3DCompatibility.GetCivilReferencePropertyNames(current))}. Nothing was changed.");
      walked.Add(Civil3DCompatibility.GetCivilReferencePropertyNames(current).FirstOrDefault(n => string.Equals(n, part, StringComparison.OrdinalIgnoreCase)) ?? part);
      current = value;
    }

    if (current is not IEnumerable)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
        $"'{string.Join(".", walked)}' is a group of collections: {string.Join(", ", Civil3DCompatibility.GetCivilReferencePropertyNames(current))}. Nothing was changed.");
    return (current, string.Join(".", walked));
  }

  /// <summary>A style by name: exact first, then without case; unknown names are refused with the styles there are.</summary>
  private static DBObject FindStyle(object collection, string path, string name, Transaction transaction, OpenMode mode)
  {
    var loose = ObjectId.Null;
    var names = new List<string>();
    foreach (var id in CivilObjectUtils.ToObjectIds(collection))
    {
      if (id.IsNull || id.IsErased) continue;
      var styleName = CivilObjectUtils.GetName(transaction.GetObject(id, OpenMode.ForRead)) ?? "";
      names.Add(styleName);
      if (string.Equals(styleName, name, StringComparison.Ordinal)) { loose = id; break; }
      if (loose.IsNull && string.Equals(styleName, name, StringComparison.OrdinalIgnoreCase)) loose = id;
    }
    if (loose.IsNull)
      throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"{path} has no style '{name}'. Styles: {string.Join(", ", names)}. Nothing was changed.");
    return transaction.GetObject(loose, mode);
  }

  private static ObjectId FindMarkerStyle(CivilDocument civilDoc, Transaction transaction, string name, string marker)
  {
    var loose = ObjectId.Null;
    var names = new List<string>();
    foreach (ObjectId id in civilDoc.Styles.MarkerStyles)
    {
      if (id.IsNull || id.IsErased) continue;
      var styleName = CivilObjectUtils.GetName(transaction.GetObject(id, OpenMode.ForRead)) ?? "";
      names.Add(styleName);
      if (string.Equals(styleName, name, StringComparison.Ordinal)) return id;
      if (loose.IsNull && string.Equals(styleName, name, StringComparison.OrdinalIgnoreCase)) loose = id;
    }
    return loose.IsNull
      ? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Marker {marker}: the drawing has no marker style '{name}'. Marker styles: {string.Join(", ", names)}. Nothing was changed.")
      : loose;
  }

  private static string MarkerKey(string property) =>
    property.EndsWith("MarkerStyleId", StringComparison.Ordinal) ? property[..^"MarkerStyleId".Length]
    : property.EndsWith("MarkerStyle", StringComparison.Ordinal) ? property[..^"MarkerStyle".Length]
    : property;

  private static string? StyleName(Transaction transaction, ObjectId id) =>
    id.IsNull || id.IsErased ? null : Safe(() => CivilObjectUtils.GetName(transaction.GetObject(id, OpenMode.ForRead)));
}
