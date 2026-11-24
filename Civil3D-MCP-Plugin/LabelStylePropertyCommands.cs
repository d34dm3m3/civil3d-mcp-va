using System.Globalization;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices.Styles;

namespace Civil3DMcpPlugin;

/// <summary>
/// C98b: a label style's general properties and the label sets of alignments, profiles and sections. Use case (pilot
/// project): the station labels of an alignment stand upside down where the stationing runs from
/// east to west (a label set without radius labels); plan readability and the orientation reference are general properties of
/// the label style, which had no tool (C90 changes only the text components).
/// General properties: LabelStyle.Properties (LabelStyleBase) with the groups Label (Visibility, Layer, TextStyle,
/// DisplayMode), Behavior (OrientationReference, InsertOption = forced insertion, InsideCurveOption), PlanReadability
/// (PlanReadable, PlanReadableBias, FlipAnchorsWithText), Leader and DraggedStateComponents, each property a Civil
/// property wrapper (PropertyBoolean, PropertyDouble, PropertyString, PropertyLayer, PropertyLinetype, PropertyColor,
/// PropertyEnum&lt;T&gt;) read and written as "Group.Property"; the style is addressed as in C90 by its collection path
/// under CivilDocument.Styles.LabelStyles. PlanReadableBias is an angle: given and shown in degrees, stored in radians
/// (the stored value is in rawAngles). Layers, linetypes and text styles must exist; every value is checked before the
/// first is written and read back after, and a value Civil 3D does not keep undoes the whole call.
/// Label sets: CivilDocument.Styles.LabelSetStyles (Alignment, Profile, Section); per item the label type, the label
/// style, the collection that holds it (for label_style_properties_get/label_style_text_get), the increment and the
/// labelled geometry points.
/// </summary>
public static class LabelStylePropertyCommands
{
  private static readonly string[] SetKinds = { "Alignment", "Profile", "Section" };

  /// <summary>Properties that are angles: degrees in and out, radians in the drawing.</summary>
  private static readonly HashSet<string> AngleProperties = new(StringComparer.OrdinalIgnoreCase) { "PlanReadability.PlanReadableBias" };

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

  // ---------------------------------------------------------------- label sets

  /// <summary>C98b: the label sets of one kind (Alignment by default; name or contains narrows them) with their items:
  /// label type, label style, the label style collection that holds the style, increment, labelled geometry points.</summary>
  public static Task<object?> ListLabelSetsAsync(JsonObject? parameters)
  {
    var kindText = PluginRuntime.GetOptionalString(parameters, "kind")?.Trim();
    var kind = string.IsNullOrEmpty(kindText) ? "Alignment"
      : SetKinds.FirstOrDefault(k => string.Equals(k, kindText, StringComparison.OrdinalIgnoreCase))
        ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"kind must be {string.Join(", ", SetKinds)}, not '{kindText}'.");
    var name = PluginRuntime.GetOptionalString(parameters, "name")?.Trim();
    var contains = PluginRuntime.GetOptionalString(parameters, "contains")?.Trim();
    if (name is "") name = null;

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var root = civilDoc.Styles.LabelSetStyles;
      object collection = kind switch
      {
        "Alignment" => root.AlignmentLabelSetStyles,
        "Profile" => root.ProfileLabelSetStyles,
        _ => root.SectionLabelSetStyles,
      };

      var sets = new List<BaseLabelSetStyle>();
      foreach (var id in CivilObjectUtils.ToObjectIds(collection))
        if (!id.IsNull && !id.IsErased && transaction.GetObject(id, OpenMode.ForRead) is BaseLabelSetStyle set) sets.Add(set);
      var all = sets.Select(s => s.Name).ToList();
      if (name != null)
      {
        var match = sets.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.Ordinal))
          ?? sets.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
          ?? throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"{kind} label set '{name}' was not found. Label sets: {string.Join(", ", all)}.");
        sets = new List<BaseLabelSetStyle> { match };
      }
      else if (!string.IsNullOrEmpty(contains))
      {
        sets = sets.Where(s => s.Name.Contains(contains, StringComparison.OrdinalIgnoreCase)).ToList();
      }

      var paths = new Lazy<Dictionary<ObjectId, string>>(() => CollectionPaths(civilDoc));
      return new Dictionary<string, object?>
      {
        ["kind"] = kind,
        ["count"] = sets.Count,
        ["labelSets"] = sets.Select(s => new Dictionary<string, object?> { ["name"] = s.Name, ["items"] = Items(s, transaction, paths) }).ToList(),
        ["note"] = "collection is the path for label_style_properties_get / label_style_text_get (under CivilDocument.Styles.LabelStyles).",
      };
    });
  }

  private static List<Dictionary<string, object?>> Items(BaseLabelSetStyle set, Transaction transaction, Lazy<Dictionary<ObjectId, string>> paths)
  {
    var items = new List<Dictionary<string, object?>>();
    var count = Safe(() => set.Count);
    for (var i = 0; i < count; i++)
    {
      var index = i;
      BaseLabelSetItem? item = set switch
      {
        AlignmentLabelSetStyle a => Safe<BaseLabelSetItem?>(() => a[index]),
        ProfileLabelSetStyle p => Safe<BaseLabelSetItem?>(() => p[index]),
        SectionLabelSetStyle s => Safe<BaseLabelSetItem?>(() => s[index]),
        _ => null,
      };
      if (item == null)
      {
        items.Add(new Dictionary<string, object?> { ["index"] = index, ["error"] = "the item cannot be read" });
        continue;
      }

      var styleId = Safe(() => item.LabelStyleId);
      var styleName = Safe(() => item.LabelStyleName);
      if (string.IsNullOrEmpty(styleName) && !styleId.IsNull && !styleId.IsErased)
        styleName = (transaction.GetObject(styleId, OpenMode.ForRead) as LabelStyle)?.Name;
      var increment = item switch
      {
        AlignmentLabelSetItem a => Safe<double?>(() => a.Increment),
        ProfileLabelSetItem p => Safe<double?>(() => p.Increment),
        SectionLabelSetItem s => Safe<double?>(() => s.Increment),
        _ => null,
      };
      items.Add(new Dictionary<string, object?>
      {
        ["index"] = index,
        ["type"] = Safe(() => item.LabelStyleType.ToString()),
        ["style"] = styleName,
        ["collection"] = styleId.IsNull ? null : paths.Value.GetValueOrDefault(styleId),
        ["increment"] = increment is > 0 ? increment : null,
        ["geometryPoints"] = GeometryPoints(item),
      });
    }
    return items;
  }

  /// <summary>The labelled geometry points of an item that labels them (alignment geometry points, profile geometry
  /// points along an alignment, superelevation points, horizontal geometry points in a profile): the point types switched on.</summary>
  private static List<string>? GeometryPoints(BaseLabelSetItem item)
  {
    var type = Safe(() => item.LabelStyleType.ToString()) ?? "";
    System.Collections.IDictionary? points = (item, type) switch
    {
      (AlignmentLabelSetItem a, "AlignmentGeometryPoint") => Safe(() => a.GetLabeledAlignmentGeometryPoints()),
      (AlignmentLabelSetItem a, "AlignmentProfileGeometryPoint") => Safe(() => a.GetLabeledProfileGeometryPoints()),
      (AlignmentLabelSetItem a, "AlignmentSuperelevationCriticalPoints") => Safe(() => a.GetLabeledSuperelevationTransitionPoints()),
      (ProfileLabelSetItem p, "ProfileHorizontalGeometryPoint") => Safe(() => p.GetLabeledAlignmentGeometryPoints()),
      _ => null,
    };
    if (points == null) return null;
    var on = new List<string>();
    foreach (System.Collections.DictionaryEntry entry in points)
      if (entry.Value is true) on.Add(entry.Key.ToString() ?? "");
    return on;
  }

  /// <summary>Every label style (child styles included) by id, with the path of the collection that holds it.</summary>
  private static Dictionary<ObjectId, string> CollectionPaths(CivilDocument civilDoc)
  {
    var paths = new Dictionary<ObjectId, string>();
    foreach (var (path, collection) in LabelStyleTextCommands.Collections(civilDoc))
    {
      var ids = Safe(() => LabelStyleTextCommands.AllStyleIds(collection));
      foreach (var id in ids ?? new List<ObjectId>()) paths.TryAdd(id, path);
    }
    return paths;
  }

  // ---------------------------------------------------------------- general properties

  /// <summary>C98b: a label style's general properties by "Group.Property" (plain values, as label_style_properties_set
  /// takes them), with the allowed values of the enumerations, which properties are locked and which overridden.</summary>
  public static Task<object?> GetLabelStylePropertiesAsync(JsonObject? parameters)
  {
    var path = PluginRuntime.GetRequiredString(parameters, "collection");
    var styleName = PluginRuntime.GetRequiredString(parameters, "style");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var (collection, resolved) = LabelStyleTextCommands.ResolveCollection(civilDoc, path);
      var style = (LabelStyle)transaction.GetObject(LabelStyleTextCommands.FindStyleId(collection, resolved, styleName, transaction), OpenMode.ForRead);
      return Describe(resolved, style, transaction);
    });
  }

  /// <summary>
  /// C98b: sets general properties of one label style: values {"Group.Property": value}, e.g.
  /// {"PlanReadability.PlanReadable": true, "PlanReadability.PlanReadableBias": 90, "Behavior.OrientationReference":
  /// "View"}. Booleans and numbers as JSON values or text, enumerations by name, colours as ByLayer/ByBlock/ACI/"r,g,b";
  /// layers, linetypes and text styles must exist. dryRun reports before/after without writing.
  /// </summary>
  public static Task<object?> SetLabelStylePropertiesAsync(JsonObject? parameters)
  {
    var path = PluginRuntime.GetRequiredString(parameters, "collection");
    var styleName = PluginRuntime.GetRequiredString(parameters, "style");
    var dryRun = PluginRuntime.GetOptionalBool(parameters, "dryRun") ?? false;
    if (PluginRuntime.GetParameter(parameters, "values") is not JsonObject values || values.Count == 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
        "Give values: {\"Group.Property\": value}, e.g. {\"PlanReadability.PlanReadable\": true}. Nothing was changed.");

    object? Body(Autodesk.AutoCAD.ApplicationServices.Document doc, CivilDocument civilDoc, Database database, Transaction transaction)
    {
      var (collection, resolved) = LabelStyleTextCommands.ResolveCollection(civilDoc, path);
      var id = LabelStyleTextCommands.FindStyleId(collection, resolved, styleName, transaction);
      // Opened for write from the start in a real run: the property wrappers act on the object they come from, and an
      // error before or during the writes aborts the transaction, so nothing stays.
      var style = (LabelStyle)transaction.GetObject(id, dryRun ? OpenMode.ForRead : OpenMode.ForWrite);
      var properties = style.Properties
        ?? throw new JsonRpcDispatchException("CIVIL3D.OPERATION_FAILED", $"The general properties of '{style.Name}' cannot be read. Nothing was changed.");

      var plan = new List<(string Path, object Wrapper, object? Before, object Target)>();
      foreach (var (key, node) in values)
      {
        var (canonical, wrapper) = FindWrapper(properties, key);
        if (plan.Any(p => string.Equals(p.Path, canonical, StringComparison.OrdinalIgnoreCase)))
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"'{canonical}' is given twice. Nothing was changed.");
        var current = Civil3DCompatibility.GetPropertyValue(wrapper, "Value")
          ?? throw new JsonRpcDispatchException("CIVIL3D.OPERATION_FAILED", $"'{canonical}' of '{style.Name}' cannot be read. Nothing was changed.");
        plan.Add((canonical, wrapper, Plain(canonical, current), Target(canonical, wrapper, current, node, database, transaction)));
      }

      var changed = new List<Dictionary<string, object?>>();
      if (dryRun)
      {
        foreach (var (key, _, before, target) in plan)
          changed.Add(new Dictionary<string, object?> { ["property"] = key, ["before"] = before, ["after"] = Plain(key, target) });
        return new Dictionary<string, object?>
        {
          ["collection"] = resolved, ["style"] = style.Name, ["dryRun"] = true, ["changed"] = changed, ["note"] = "Dry run: nothing was changed.",
        };
      }

      foreach (var (key, wrapper, _, target) in plan)
        if (!Civil3DCompatibility.TrySetProperty(wrapper, "Value", target))
          throw new JsonRpcDispatchException("CIVIL3D.OPERATION_FAILED",
            $"Civil 3D did not accept '{key}' for '{style.Name}'{(Civil3DCompatibility.GetPropertyValue(wrapper, "Locked") is true ? " (locked)" : "")}. Nothing was changed.");

      // Read back through fresh wrappers: a value Civil 3D silently does not keep (a property locked in the parent
      // style) undoes the call.
      var fresh = style.Properties
        ?? throw new JsonRpcDispatchException("CIVIL3D.OPERATION_FAILED", $"The general properties of '{style.Name}' cannot be read back. Nothing was changed.");
      var refused = new List<string>();
      foreach (var (key, _, before, target) in plan)
      {
        var after = Civil3DCompatibility.GetPropertyValue(FindWrapper(fresh, key).Wrapper, "Value");
        if (!Same(after, target)) refused.Add($"{key} (wanted {Format(Plain(key, target))}, read back {Format(Plain(key, after))})");
        changed.Add(new Dictionary<string, object?> { ["property"] = key, ["before"] = before, ["after"] = Plain(key, after) });
      }
      if (refused.Count > 0)
        throw new JsonRpcDispatchException("CIVIL3D.OPERATION_FAILED", $"Civil 3D did not keep {string.Join("; ", refused)} in '{style.Name}'. Nothing was changed.");

      var result = Describe(resolved, style, transaction);
      result["changed"] = changed;
      return result;
    }

    return dryRun ? CivilExecution.ReadAsync<object?>(Body) : CivilExecution.WriteAsync<object?>(Body);
  }

  private static Dictionary<string, object?> Describe(string collection, LabelStyle style, Transaction transaction)
  {
    var values = new Dictionary<string, object?>();
    var allowed = new Dictionary<string, object?>();
    var rawAngles = new Dictionary<string, object?>();
    var locked = new List<string>();
    var overridden = new List<string>();
    var properties = Safe(() => style.Properties);
    foreach (var (path, wrapper) in properties == null ? new List<(string, object)>() : Entries(properties))
    {
      var value = Civil3DCompatibility.GetPropertyValue(wrapper, "Value");
      values[path] = Plain(path, value);
      if (value is Enum e) allowed[path] = Enum.GetNames(e.GetType());
      if (AngleProperties.Contains(path) && value is double raw) rawAngles[path] = raw;
      if (Civil3DCompatibility.GetPropertyValue(wrapper, "Locked") is true) locked.Add(path);
      if (Civil3DCompatibility.GetPropertyValue(wrapper, "Overridden") is true) overridden.Add(path);
    }

    var parentId = Safe(() => style.ParentLabelStyleId);
    return new Dictionary<string, object?>
    {
      ["collection"] = collection,
      ["style"] = style.Name,
      ["parentStyle"] = parentId.IsNull || parentId.IsErased ? null : (transaction.GetObject(parentId, OpenMode.ForRead) as LabelStyle)?.Name,
      ["values"] = values,
      ["allowed"] = allowed,
      ["rawAngles"] = rawAngles.Count > 0 ? rawAngles : null,
      ["locked"] = locked.Count > 0 ? locked : null,
      ["overridden"] = overridden.Count > 0 ? overridden : null,
      ["note"] = "PlanReadability.PlanReadableBias in degrees; rawAngles is the stored value (radians).",
    };
  }

  /// <summary>Every property wrapper by "Group.Property" (groups in Civil's order: Label, Behavior, PlanReadability, …).</summary>
  private static List<(string Path, object Wrapper)> Entries(object properties)
  {
    var entries = new List<(string, object)>();
    foreach (var group in Civil3DCompatibility.GetCivilReferencePropertyNames(properties))
    {
      var node = Read(properties, group);
      if (node == null || IsWrapper(node)) continue;
      foreach (var name in Civil3DCompatibility.GetCivilReferencePropertyNames(node))
        if (Read(node, name) is { } wrapper && IsWrapper(wrapper)) entries.Add(($"{group}.{name}", wrapper));
    }
    return entries;
  }

  private static bool IsWrapper(object value) => value.GetType().Name.StartsWith("Property", StringComparison.Ordinal);

  private static object? Read(object target, string name)
  {
    try
    {
      var (found, value) = Civil3DCompatibility.GetPropertyIgnoreCase(target, name);
      return found ? value : null;
    }
    catch (Exception)
    {
      return null;   // a group this style cannot give
    }
  }

  /// <summary>The wrapper at "Group.Property" (case-insensitive), with the canonical path.</summary>
  private static (string Path, object Wrapper) FindWrapper(object properties, string key)
  {
    var parts = key.Split('.', StringSplitOptions.TrimEntries);
    var groups = Civil3DCompatibility.GetCivilReferencePropertyNames(properties);
    if (parts.Length != 2 || parts.Any(p => p.Length == 0))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"'{key}' is not Group.Property (groups: {string.Join(", ", groups)}). Nothing was changed.");
    var group = groups.FirstOrDefault(g => string.Equals(g, parts[0], StringComparison.OrdinalIgnoreCase));
    var node = group == null ? null : Read(properties, group);
    if (group == null || node == null)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Label styles have no group '{parts[0]}'. Groups: {string.Join(", ", groups)}. Nothing was changed.");
    var names = Civil3DCompatibility.GetCivilReferencePropertyNames(node);
    var name = names.FirstOrDefault(n => string.Equals(n, parts[1], StringComparison.OrdinalIgnoreCase));
    var wrapper = name == null ? null : Read(node, name);
    if (name == null || wrapper == null || !IsWrapper(wrapper))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"No property '{parts[1]}' in {group}. Properties: {string.Join(", ", names)}. Nothing was changed.");
    return ($"{group}.{name}", wrapper);
  }

  /// <summary>The value to write for a wrapper, checked: enum by name, bool, number (angles in degrees, 0-360),
  /// colour, and layer, linetype and text style names that exist in the drawing.</summary>
  private static object Target(string path, object wrapper, object current, JsonNode? node, Database database, Transaction transaction)
  {
    var text = node is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : node?.ToJsonString() ?? "";
    switch (current)
    {
      case Color:
        return PartStyleCommands.ParseColor(JsonValue.Create(text)!);
      case Enum e:
      {
        var type = e.GetType();
        if (Enum.TryParse(type, text, true, out var parsed) && Enum.IsDefined(type, parsed!)) return parsed!;
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"'{text}' is not valid for '{path}'. Allowed: {string.Join(", ", Enum.GetNames(type))}. Nothing was changed.");
      }
      case bool:
        if (bool.TryParse(text, out var flag)) return flag;
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"'{path}' takes true or false, not '{text}'. Nothing was changed.");
      case double:
      {
        if (!double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number))
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"'{path}' takes a number, not '{text}'. Nothing was changed.");
        if (!AngleProperties.Contains(path)) return number;
        if (number is < 0 or >= 360)
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"'{path}' is an angle in degrees, from 0 up to (not including) 360, not {number}. Nothing was changed.");
        return number * Math.PI / 180.0;
      }
      case int:
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole)) return whole;
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"'{path}' takes a whole number, not '{text}'. Nothing was changed.");
      case string:
      {
        var typeName = wrapper.GetType().Name;
        if (typeName == "PropertyLayer") return DrawingNames.Layer(database, transaction, text, path);
        if (typeName == "PropertyLinetype") return DrawingNames.Linetype(database, transaction, text, path);
        if (typeName == "PropertyTextStyle" || path.EndsWith(".TextStyle", StringComparison.OrdinalIgnoreCase))
          return DrawingNames.TextStyle(database, transaction, text, path);
        return text;
      }
      default:
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"'{path}' ({current.GetType().Name}) cannot be set here. Nothing was changed.");
    }
  }

  /// <summary>A value as label_style_properties_get shows it: enums by name, colours as text, angles in degrees.</summary>
  private static object? Plain(string path, object? value) => value switch
  {
    null => null,
    Color c => ColorText(c),
    Enum e => e.ToString(),
    double d when AngleProperties.Contains(path) => Math.Round(d * 180.0 / Math.PI, 6),
    string or bool or int or double => value,
    _ => value.ToString(),
  };

  internal static string ColorText(Color c) =>
    c.IsByLayer ? "ByLayer" : c.IsByBlock ? "ByBlock" : c.ColorMethod == ColorMethod.ByAci ? c.ColorIndex.ToString(CultureInfo.InvariantCulture) : $"{c.Red},{c.Green},{c.Blue}";

  private static bool Same(object? after, object target) => (after, target) switch
  {
    (double a, double t) => Math.Abs(a - t) <= 1e-9 * Math.Max(1.0, Math.Abs(t)),
    (Color a, Color t) => ColorText(a) == ColorText(t),
    (string a, string t) => string.Equals(a, t, StringComparison.OrdinalIgnoreCase),
    (null, _) => false,
    _ => Equals(after, target),
  };

  private static string Format(object? value) => value switch
  {
    null => "nothing",
    double d => d.ToString("0.######", CultureInfo.InvariantCulture),
    _ => value.ToString() ?? "",
  };
}
