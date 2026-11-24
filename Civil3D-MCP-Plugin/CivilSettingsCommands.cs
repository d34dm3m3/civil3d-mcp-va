using System.Collections;
using System.Globalization;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using PropertyObjectId = Autodesk.Civil.PropertyObjectId;
using SettingsAmbient = Autodesk.Civil.Settings.SettingsAmbient;

namespace Civil3DMcpPlugin;

/// <summary>
/// Civil's feature and command settings (C91): Edit Feature Settings and Edit Command Settings, reached through
/// CivilDocument.Settings.GetSettings&lt;T&gt;() for the types in Autodesk.Civil.Settings (SettingsProfileView,
/// SettingsCmdCreateProfileView, SettingsPipeNetwork, SettingsCmdCreateNetwork, SettingsCmdAddNetworkPartsToProf …).
/// Each type has groups (Styles, NameFormat, Creation, …) of Civil property wrappers (PropertyObjectId, PropertyString,
/// PropertyBoolean, PropertyInt, PropertyDouble, PropertyEnum&lt;T&gt;) read and written as "Group.Property", as the
/// ambient settings (C80); a few wrappers sit directly on the type ("DefaultLayoutCommand"). The groups every type
/// inherits from SettingsAmbient (Station, Elevation, …: per-feature overrides of the drawing's ambient settings) are
/// left out of listings unless includeAmbient. Style ids (PropertyObjectId) are shown with the style's name and set by
/// name, looked up in the style collection that holds the current style ({collection, name} names the collection
/// outright, and is needed when the setting is empty).
/// Use case: the company template's defaults for new objects: a profile view style with 1:4 vertical exaggeration
/// (1:400/1:100), a gravity pipe band set, the parts list, and pipe network name templates such as S-NB-&lt;[Next Counter(CP)]&gt;. The generic
/// method call and the type list go through Civil3DCompatibility. Every value is checked before the first is written.
/// </summary>
public static class CivilSettingsCommands
{
  private const int MaxStyleDepth = 4;
  private const int MaxTypesWithValues = 25;

  /// <summary>C91: the settings types GetSettings&lt;T&gt; takes, with their feature (base type); with contains (and at
  /// most 25 matches) also each type's values.</summary>
  public static Task<object?> ListCivilSettingsAsync(JsonObject? parameters)
  {
    var contains = PluginRuntime.GetOptionalString(parameters, "contains")?.Trim();
    var includeAmbient = PluginRuntime.GetOptionalBool(parameters, "includeAmbient") ?? false;
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var types = SettingsTypes().Where(t => string.IsNullOrEmpty(contains) || t.Name.Contains(contains, StringComparison.OrdinalIgnoreCase)).ToList();
      var withValues = !string.IsNullOrEmpty(contains) && types.Count <= MaxTypesWithValues;
      var ambient = AmbientGroups(civilDoc);
      var list = new List<Dictionary<string, object?>>();
      foreach (var type in types)
      {
        var entry = new Dictionary<string, object?> { ["settingsType"] = type.Name, ["feature"] = type.BaseType?.Name };
        if (withValues)
        {
          try
          {
            foreach (var (key, value) in Describe(Load(civilDoc, type), ambient, includeAmbient, null, transaction)) entry[key] = value;
          }
          catch (JsonRpcDispatchException ex)
          {
            entry["error"] = ex.Message;
          }
        }
        list.Add(entry);
      }

      return new Dictionary<string, object?>
      {
        ["count"] = list.Count,
        ["settingsTypes"] = list,
        ["note"] = withValues ? null : $"Give contains matching at most {MaxTypesWithValues} types (e.g. ProfileView, CreateNetwork) to see their values, or use civil_settings_get.",
      };
    });
  }

  /// <summary>C91: one settings type's values by "Group.Property" (optionally one group): style ids with the style's
  /// name, enums with their alternatives; which values are locked and which overridden.</summary>
  public static Task<object?> GetCivilSettingsAsync(JsonObject? parameters)
  {
    var typeName = PluginRuntime.GetRequiredString(parameters, "settingsType");
    var group = PluginRuntime.GetOptionalString(parameters, "group")?.Trim();
    var includeAmbient = PluginRuntime.GetOptionalBool(parameters, "includeAmbient") ?? false;
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var type = ResolveType(typeName);
      var settings = Load(civilDoc, type);
      var ambient = AmbientGroups(civilDoc);
      var result = new Dictionary<string, object?> { ["settingsType"] = type.Name, ["feature"] = type.BaseType?.Name };
      var described = Describe(settings, ambient, includeAmbient || (group != null && ambient.Contains(group)), string.IsNullOrEmpty(group) ? null : group, transaction);
      if (!string.IsNullOrEmpty(group) && described["values"] is Dictionary<string, object?> { Count: 0 })
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
          $"{type.Name} has no group '{group}'. Groups: {string.Join(", ", Groups(settings))}.");
      foreach (var (key, value) in described) result[key] = value;
      result["groups"] = Groups(settings).Where(g => includeAmbient || !ambient.Contains(g)).ToList();
      return result;
    });
  }

  /// <summary>
  /// C91: sets values of one settings type: {"Group.Property": value}. Enums by name, numbers and booleans as text,
  /// strings (name templates) as given; a style id by the style's name, or {collection, name} with the collection's
  /// path under CivilDocument.Styles (e.g. "ProfileViewStyles", "LabelStyles.StructureLabelStyles.LabelStyles").
  /// Every key, value and style is checked before the first is written; before/after per setting.
  /// </summary>
  public static Task<object?> SetCivilSettingsAsync(JsonObject? parameters)
  {
    var typeName = PluginRuntime.GetRequiredString(parameters, "settingsType");
    if (PluginRuntime.GetParameter(parameters, "values") is not JsonObject values || values.Count == 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
        "Give values: {\"Group.Property\": value}, e.g. {\"Styles.ProfileViewStyleId\": \"<style name>\"}. Nothing was changed.");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var type = ResolveType(typeName);
      var settings = Load(civilDoc, type);
      var collections = new Lazy<List<(string Path, object Collection)>>(() => StyleCollections(civilDoc));
      var plan = new List<(string Path, object Wrapper, object? Before, object? After)>();
      foreach (var (path, node) in values)
      {
        var wrapper = FindWrapper(settings, type, path);
        var before = DescribeValue(wrapper, transaction);
        if (wrapper is PropertyObjectId idWrapper)
        {
          plan.Add((path, wrapper, before, ResolveStyleId(civilDoc, transaction, path, idWrapper.Value, node, collections)));
          continue;
        }

        if (node is JsonObject)
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"'{path}' is not a style setting; give its value as text. Nothing was changed.");
        var current = Civil3DCompatibility.GetPropertyValue(wrapper, "Value")
          ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"'{path}' has no readable value. Nothing was changed.");
        plan.Add((path, wrapper, before, Convert(current, node, path)));
      }

      // Civil's settings objects may write outside the transaction, so a value that cannot be set puts the earlier ones
      // back before the error (best effort; a value that cannot be put back is named).
      var changed = new List<Dictionary<string, object?>>();
      var written = new List<(string Path, object Wrapper, object? Raw)>();
      foreach (var (path, wrapper, before, after) in plan)
      {
        var raw = Civil3DCompatibility.GetPropertyValue(wrapper, "Value");
        if (!Write(wrapper, after))
        {
          var notBack = written.AsEnumerable().Reverse().Where(w => !Write(w.Wrapper, w.Raw)).Select(w => w.Path).ToList();
          throw new JsonRpcDispatchException("CIVIL3D.OPERATION_FAILED", $"'{path}' could not be set. "
            + (written.Count == 0 ? "Nothing was changed."
              : notBack.Count == 0 ? "The settings set before it were put back; nothing was changed."
              : $"Could not put back: {string.Join(", ", notBack)}."));
        }
        written.Add((path, wrapper, raw));
        changed.Add(new Dictionary<string, object?> { ["setting"] = path, ["before"] = before, ["after"] = DescribeValue(wrapper, transaction) });
      }
      return new Dictionary<string, object?> { ["settingsType"] = type.Name, ["changed"] = changed };
    });
  }

  private static bool Write(object wrapper, object? value) => wrapper is PropertyObjectId idWrapper
    ? value is ObjectId id && Assign(() => idWrapper.Value = id)
    : Civil3DCompatibility.TrySetProperty(wrapper, "Value", value);

  private static bool Assign(Action set)
  {
    try
    {
      set();
      return true;
    }
    catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or Autodesk.AutoCAD.Runtime.Exception)
    {
      return false;
    }
  }

  // ---------------------------------------------------------------- settings types and values

  private static List<Type> SettingsTypes()
  {
    try
    {
      return Civil3DCompatibility.GetPublicSubclasses(typeof(SettingsAmbient)).Where(t => t.Namespace == "Autodesk.Civil.Settings").ToList();
    }
    catch (Exception ex) when (ex is not JsonRpcDispatchException)
    {
      throw new JsonRpcDispatchException("CIVIL3D.OPERATION_FAILED", $"The Civil settings types cannot be listed ({ex.GetType().Name}: {ex.Message}). Nothing was changed.");
    }
  }

  /// <summary>A settings type by name, with or without the "Settings" prefix, without case.</summary>
  private static Type ResolveType(string raw)
  {
    var name = raw.Trim();
    var types = SettingsTypes();
    var type = types.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))
      ?? types.FirstOrDefault(t => string.Equals(t.Name, "Settings" + name, StringComparison.OrdinalIgnoreCase));
    if (type != null) return type;
    var near = types.Where(t => t.Name.Contains(name.Replace("Settings", "", StringComparison.OrdinalIgnoreCase), StringComparison.OrdinalIgnoreCase))
      .Select(t => t.Name).Take(15).ToList();
    throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
      $"'{raw}' is not a Civil settings type{(near.Count > 0 ? $"; similar: {string.Join(", ", near)}" : "")}. civil_settings_list lists them. Nothing was changed.");
  }

  private static object Load(CivilDocument civilDoc, Type type)
  {
    try
    {
      return Civil3DCompatibility.InvokeGenericMethod(civilDoc.Settings, "GetSettings", type)
        ?? throw new JsonRpcDispatchException("CIVIL3D.OPERATION_FAILED", $"{type.Name} is not available in this drawing. Nothing was changed.");
    }
    catch (Exception ex) when (ex is not JsonRpcDispatchException)
    {
      throw new JsonRpcDispatchException("CIVIL3D.OPERATION_FAILED", $"{type.Name} cannot be read in this drawing ({ex.Message}). Nothing was changed.");
    }
  }

  /// <summary>The ambient groups (Station, Elevation, …) every settings type inherits from SettingsAmbient.</summary>
  private static HashSet<string> AmbientGroups(CivilDocument civilDoc) =>
    Civil3DCompatibility.GetCivilReferencePropertyNames(civilDoc.Settings.DrawingSettings.AmbientSettings).ToHashSet(StringComparer.OrdinalIgnoreCase);

  // Civil's property wrappers (PropertyString, PropertyEnum`1, PropertyObjectId, …) hold one value; groups hold wrappers.
  private static bool IsWrapper(object value) => value.GetType().Name.StartsWith("Property", StringComparison.Ordinal);

  private static List<string> Groups(object settings) =>
    Civil3DCompatibility.GetCivilReferencePropertyNames(settings).Where(n => Read(settings, n) is { } v && !IsWrapper(v)).ToList();

  /// <summary>Every wrapper by path: "Group.Property", and "Property" for wrappers on the type itself.</summary>
  private static List<(string Path, object Wrapper)> Entries(object settings, HashSet<string> ambient, bool includeAmbient, string? onlyGroup)
  {
    var entries = new List<(string, object)>();
    foreach (var name in Civil3DCompatibility.GetCivilReferencePropertyNames(settings))
    {
      if (!includeAmbient && ambient.Contains(name)) continue;
      if (onlyGroup != null && !string.Equals(name, onlyGroup, StringComparison.OrdinalIgnoreCase)) continue;
      var value = Read(settings, name);
      if (value == null) continue;
      if (IsWrapper(value))
      {
        entries.Add((name, value));
        continue;
      }
      foreach (var property in Civil3DCompatibility.GetCivilReferencePropertyNames(value))
        if (Read(value, property) is { } wrapper && IsWrapper(wrapper)) entries.Add(($"{name}.{property}", wrapper));
    }
    return entries;
  }

  private static Dictionary<string, object?> Describe(object settings, HashSet<string> ambient, bool includeAmbient, string? onlyGroup, Transaction transaction)
  {
    var values = new Dictionary<string, object?>();
    var locked = new List<string>();
    var overridden = new List<string>();
    foreach (var (path, wrapper) in Entries(settings, ambient, includeAmbient, onlyGroup))
    {
      values[path] = DescribeValue(wrapper, transaction);
      if (Civil3DCompatibility.GetPropertyValue(wrapper, "Locked") is true) locked.Add(path);
      if (Civil3DCompatibility.GetPropertyValue(wrapper, "Overridden") is true) overridden.Add(path);
    }
    return new Dictionary<string, object?>
    {
      ["values"] = values,
      ["locked"] = locked.Count > 0 ? locked : null,
      ["overridden"] = overridden.Count > 0 ? overridden : null,
    };
  }

  private static object? DescribeValue(object wrapper, Transaction transaction)
  {
    var value = Civil3DCompatibility.GetPropertyValue(wrapper, "Value");
    return value switch
    {
      null => null,
      ObjectId id => DescribeId(id, transaction),
      Enum e => new Dictionary<string, object?> { ["value"] = e.ToString(), ["allowed"] = Enum.GetNames(e.GetType()) },
      string or bool or int or uint or double => value,
      _ => value.ToString(),
    };
  }

  private static object? DescribeId(ObjectId id, Transaction transaction)
  {
    if (id.IsNull) return null;
    if (id.IsErased) return new Dictionary<string, object?> { ["handle"] = id.Handle.ToString(), ["erased"] = true };
    try
    {
      var target = transaction.GetObject(id, OpenMode.ForRead);
      return new Dictionary<string, object?> { ["name"] = CivilObjectUtils.GetName(target), ["type"] = target.GetType().Name, ["handle"] = id.Handle.ToString() };
    }
    catch (Autodesk.AutoCAD.Runtime.Exception ex)
    {
      return new Dictionary<string, object?> { ["handle"] = id.Handle.ToString(), ["error"] = ex.Message };
    }
  }

  private static object? Read(object target, string name)
  {
    try
    {
      var (found, value) = Civil3DCompatibility.GetPropertyIgnoreCase(target, name);
      return found ? value : null;
    }
    catch (Exception)
    {
      return null;   // a group this drawing cannot give
    }
  }

  /// <summary>The wrapper at "Group.Property" (or "Property" on the type itself), ambient groups included.</summary>
  private static object FindWrapper(object settings, Type type, string path)
  {
    var parts = path.Split('.', StringSplitOptions.TrimEntries);
    if (parts.Length is < 1 or > 2 || parts.Any(p => p.Length == 0))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"'{path}' is not Group.Property. Nothing was changed.");
    var first = Read(settings, parts[0]) ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
      $"{type.Name} has no '{parts[0]}'. Groups and settings: {string.Join(", ", Civil3DCompatibility.GetCivilReferencePropertyNames(settings))}. Nothing was changed.");
    if (parts.Length == 1)
    {
      if (!IsWrapper(first))
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
          $"'{parts[0]}' is a group; give Group.Property ({string.Join(", ", Civil3DCompatibility.GetCivilReferencePropertyNames(first).Select(p => $"{parts[0]}.{p}"))}). Nothing was changed.");
      return first;
    }

    if (IsWrapper(first))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"'{parts[0]}' is a setting, not a group; give '{parts[0]}'. Nothing was changed.");
    var wrapper = Read(first, parts[1]);
    if (wrapper == null || !IsWrapper(wrapper))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
        $"No setting '{parts[1]}' in {type.Name}.{parts[0]}. Settings: {string.Join(", ", Civil3DCompatibility.GetCivilReferencePropertyNames(first))}. Nothing was changed.");
    return wrapper;
  }

  private static object Convert(object current, JsonNode? node, string path)
  {
    var text = node?.ToString() ?? "";
    var type = current.GetType();
    try
    {
      if (type.IsEnum)
      {
        if (Enum.TryParse(type, text, true, out var parsed) && Enum.IsDefined(type, parsed!)) return parsed!;
        throw new FormatException();
      }
      return current switch
      {
        int => int.Parse(text, CultureInfo.InvariantCulture),
        uint => uint.Parse(text, CultureInfo.InvariantCulture),
        double => double.Parse(text.Replace(',', '.'), CultureInfo.InvariantCulture) is var number && double.IsFinite(number) ? number : throw new FormatException(),
        bool => bool.Parse(text),
        string => text,
        _ => throw new FormatException(),
      };
    }
    catch (Exception ex) when (ex is FormatException or OverflowException)
    {
      var allowed = type.IsEnum ? $" Allowed: {string.Join(", ", Enum.GetNames(type))}." : "";
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"'{text}' is not a valid {type.Name} for '{path}'.{allowed} Nothing was changed.");
    }
  }

  // ---------------------------------------------------------------- style ids by name

  /// <summary>A style id for a PropertyObjectId: {collection, name}; or a name looked up in the collection that holds the
  /// current style. An empty setting needs {collection, name}.</summary>
  private static ObjectId ResolveStyleId(CivilDocument civilDoc, Transaction transaction, string path, ObjectId current, JsonNode? node,
    Lazy<List<(string Path, object Collection)>> collections)
  {
    if (node is JsonObject named)
    {
      var collectionPath = named["collection"]?.GetValue<string>()?.Trim();
      var styleName = named["name"]?.GetValue<string>()?.Trim();
      if (string.IsNullOrEmpty(collectionPath) || string.IsNullOrEmpty(styleName))
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"'{path}': give {{\"collection\": \"ProfileViewStyles\", \"name\": \"…\"}}. Nothing was changed.");
      var match = collections.Value.FirstOrDefault(c => string.Equals(c.Path, collectionPath, StringComparison.OrdinalIgnoreCase));
      if (match.Collection == null)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
          $"'{path}': '{collectionPath}' is not a style collection under CivilDocument.Styles (styles_list_external lists the paths). Nothing was changed.");
      return FindInCollection(match, styleName, path, transaction);
    }

    var name = node?.ToString().Trim() ?? "";
    if (name.Length == 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"'{path}' needs a style name. Nothing was changed.");
    if (!current.IsNull && !current.IsErased)
    {
      var holder = collections.Value.FirstOrDefault(c => Ids(c.Collection).Contains(current));
      if (holder.Collection != null) return FindInCollection(holder, name, path, transaction);
    }

    // An empty setting says nothing about which collection it takes (a band set name could land in a profile view style
    // setting), so the collection must be named; the collections that have the name are listed to help.
    var having = collections.Value
      .Where(c => Ids(c.Collection).Any(i => string.Equals(CivilObjectUtils.GetName(transaction.GetObject(i, OpenMode.ForRead)), name, StringComparison.OrdinalIgnoreCase)))
      .Select(c => c.Path).ToList();
    throw new JsonRpcDispatchException(having.Count == 0 ? "CIVIL3D.OBJECT_NOT_FOUND" : "CIVIL3D.INVALID_INPUT",
      $"'{path}' is empty, so its style collection is unknown; give {{\"collection\": …, \"name\": \"{name}\"}}."
      + (having.Count == 0 ? $" No style collection has a style named '{name}'." : $" Collections with '{name}': {string.Join(", ", having)}.")
      + " Nothing was changed.");
  }

  /// <summary>A style by name in one collection: exact first, then without case.</summary>
  private static ObjectId FindInCollection((string Path, object Collection) collection, string name, string path, Transaction transaction)
  {
    var loose = ObjectId.Null;
    var names = new List<string>();
    foreach (var id in Ids(collection.Collection))
    {
      var styleName = CivilObjectUtils.GetName(transaction.GetObject(id, OpenMode.ForRead)) ?? "";
      names.Add(styleName);
      if (string.Equals(styleName, name, StringComparison.Ordinal)) return id;
      if (loose.IsNull && string.Equals(styleName, name, StringComparison.OrdinalIgnoreCase)) loose = id;
    }
    if (!loose.IsNull) return loose;
    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND",
      $"'{path}': {collection.Path} has no style '{name}'. Styles: {string.Join(", ", names)}. Nothing was changed.");
  }

  private static List<ObjectId> Ids(object collection) => CivilObjectUtils.ToObjectIds(collection).Where(id => !id.IsNull && !id.IsErased).ToList();

  /// <summary>Every style collection under CivilDocument.Styles by property path (as styles_list_external, C78).</summary>
  private static List<(string Path, object Collection)> StyleCollections(CivilDocument civilDoc)
  {
    var found = new List<(string, object)>();
    void Walk(object node, string prefix, int depth)
    {
      if (depth > MaxStyleDepth) return;
      foreach (var name in Civil3DCompatibility.GetCivilReferencePropertyNames(node))
      {
        var value = Read(node, name);
        if (value == null) continue;
        var path = prefix.Length == 0 ? name : $"{prefix}.{name}";
        if (value is IEnumerable) found.Add((path, value));
        else Walk(value, path, depth + 1);
      }
    }
    Walk(civilDoc.Styles, "", 0);
    return found;
  }
}
