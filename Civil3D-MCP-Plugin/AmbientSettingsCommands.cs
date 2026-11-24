using System.Globalization;
using System.Text.Json.Nodes;

namespace Civil3DMcpPlugin;

/// <summary>
/// The drawing's ambient settings (C80, E6): Civil's Edit Drawing Settings → Ambient Settings (station, elevation,
/// grade, slope, distance … each with unit, precision, rounding, format). Read all groups, or set values by
/// "Group.Property" (e.g. "Station.StationDelimiterCharacter", "Elevation.Precision", "Grade.Format"); enums by name.
/// Use case: the company template's station format 0/000, elevation and grade precision and ‰ for gravity pipes (C2).
/// Every value is converted and checked before anything is set.
/// </summary>
public static class AmbientSettingsCommands
{
  public static Task<object?> GetAmbientSettingsAsync(JsonObject? parameters)
  {
    var only = PluginRuntime.GetOptionalString(parameters, "group");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var ambient = ((Autodesk.Civil.ApplicationServices.CivilDocument)civilDoc).Settings.DrawingSettings.AmbientSettings;
      var groups = new Dictionary<string, object?>();
      foreach (var group in Civil3DCompatibility.GetCivilReferencePropertyNames(ambient))
      {
        if (only != null && !string.Equals(group, only, StringComparison.OrdinalIgnoreCase)) continue;
        var settings = Read(ambient, group);
        if (settings == null) continue;
        var values = new Dictionary<string, object?>();
        foreach (var name in Civil3DCompatibility.GetCivilReferencePropertyNames(settings))
        {
          var wrapper = Read(settings, name);
          if (wrapper == null) continue;
          var value = Civil3DCompatibility.GetPropertyValue(wrapper, "Value");
          if (value == null) continue;
          values[name] = value.GetType().IsEnum
            ? new Dictionary<string, object?> { ["value"] = value.ToString(), ["allowed"] = Enum.GetNames(value.GetType()) }
            : value is IConvertible ? value : value.ToString();
        }
        groups[group] = values;
      }
      return new Dictionary<string, object?> { ["ambientSettings"] = groups };
    });
  }

  public static Task<object?> SetAmbientSettingsAsync(JsonObject? parameters)
  {
    if (PluginRuntime.GetParameter(parameters, "values") is not JsonObject values || values.Count == 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Give values: {\"Group.Property\": value}. Nothing was changed.");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var ambient = ((Autodesk.Civil.ApplicationServices.CivilDocument)civilDoc).Settings.DrawingSettings.AmbientSettings;
      var plan = new List<(string Path, object Wrapper, object Before, object After)>();
      foreach (var (path, node) in values)
      {
        var parts = path.Split('.', 2);
        if (parts.Length != 2)
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"'{path}' is not Group.Property. Nothing was changed.");
        var settings = Read(ambient, parts[0]) ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
          $"No ambient settings group '{parts[0]}'. Groups: {string.Join(", ", Civil3DCompatibility.GetCivilReferencePropertyNames(ambient))}. Nothing was changed.");
        var wrapper = Read(settings, parts[1]) ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
          $"No setting '{parts[1]}' in {parts[0]}. Settings: {string.Join(", ", Civil3DCompatibility.GetCivilReferencePropertyNames(settings))}. Nothing was changed.");
        var before = Civil3DCompatibility.GetPropertyValue(wrapper, "Value")
          ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"'{path}' has no readable value. Nothing was changed.");
        plan.Add((path, wrapper, before, Convert(before, node, path)));
      }

      var changed = new List<Dictionary<string, object?>>();
      foreach (var (path, wrapper, before, after) in plan)
      {
        if (!Civil3DCompatibility.TrySetProperty(wrapper, "Value", after))
          throw new JsonRpcDispatchException("CIVIL3D.OPERATION_FAILED", $"'{path}' could not be set (earlier settings in this call were set).");
        changed.Add(new Dictionary<string, object?>
        {
          ["setting"] = path, ["before"] = before.ToString(), ["after"] = Civil3DCompatibility.GetPropertyValue(wrapper, "Value")?.ToString(),
        });
      }
      return new Dictionary<string, object?> { ["changed"] = changed };
    });
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
      return null;
    }
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
        double => double.Parse(text.Replace(',', '.'), CultureInfo.InvariantCulture),
        bool => bool.Parse(text),
        string => text,
        _ => throw new FormatException(),
      };
    }
    catch (FormatException)
    {
      var allowed = type.IsEnum ? $" Allowed: {string.Join(", ", Enum.GetNames(type))}." : "";
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"'{text}' is not a valid {type.Name} for '{path}'.{allowed} Nothing was changed.");
    }
  }
}
