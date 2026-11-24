using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace Civil3DMcpPlugin;

/// <summary>
/// Read and set a few system variables that matter for sheets and plotting. Only the allowlisted names are
/// accepted; the new value is converted to the type of the current value (short, int, double or string).
/// </summary>
public static class SystemVariableCommands
{
  private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
  {
    "SAVETIME", "LTSCALE", "PSLTSCALE", "MSLTSCALE", "CANNOSCALE", "ANNOALLVISIBLE", "BACKGROUNDPLOT", "LWDISPLAY",
    "CTAB",         // C180 (2026-10-23): current layout, so that REGENALL reaches the sheets' viewports (empty viewports after a script run)
    "XCLIPFRAME",   // C179 (2026-10-23): the xref/block clip frame is shown but not plotted (found in a sheet review)
  };

  private static string AllowedName(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name").Trim().ToUpperInvariant();
    if (!Allowed.Contains(name))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"System variable '{name}' is not allowed. Use {string.Join(", ", Allowed)}.");
    return name;
  }

  private static object? Plain(object? value) => value switch
  {
    short s => (int)s,
    _ => value,
  };

  public static Task<object?> GetSystemVariableAsync(JsonObject? parameters)
  {
    var name = AllowedName(parameters);
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var value = AcApp.GetSystemVariable(name);
      return new Dictionary<string, object?> { ["name"] = name, ["value"] = Plain(value), ["type"] = value?.GetType().Name };
    });
  }

  public static Task<object?> SetSystemVariableAsync(JsonObject? parameters)
  {
    var name = AllowedName(parameters);
    if (PluginRuntime.GetParameter(parameters, "value") is not JsonValue raw)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "value is required (number, string or boolean). Nothing was changed.");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var old = AcApp.GetSystemVariable(name);
      object converted;
      try
      {
        converted = Convert(raw, old);
      }
      catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException or InvalidOperationException)
      {
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{name} takes a {old?.GetType().Name ?? "value"}; '{raw.ToJsonString()}' does not convert. Nothing was changed.");
      }

      try
      {
        AcApp.SetSystemVariable(name, converted);
      }
      catch (Autodesk.AutoCAD.Runtime.Exception ex)
      {
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"AutoCAD rejected {name} = {converted}: {ex.ErrorStatus}. Nothing was changed.");
      }

      return new Dictionary<string, object?>
      {
        ["name"] = name,
        ["oldValue"] = Plain(old),
        ["newValue"] = Plain(AcApp.GetSystemVariable(name)),
        ["type"] = old?.GetType().Name,
      };
    });
  }

  private static object Convert(JsonValue raw, object? current)
  {
    var element = raw.GetValue<JsonElement>();
    object? source = element.ValueKind switch
    {
      JsonValueKind.True => 1,
      JsonValueKind.False => 0,
      JsonValueKind.Number => element.GetDouble(),
      JsonValueKind.String => element.GetString(),
      _ => throw new InvalidCastException(),
    };

    return current switch
    {
      string => source is double d ? d.ToString(CultureInfo.InvariantCulture) : System.Convert.ToString(source, CultureInfo.InvariantCulture)!,
      short => System.Convert.ToInt16(Whole(source), CultureInfo.InvariantCulture),
      int => System.Convert.ToInt32(Whole(source), CultureInfo.InvariantCulture),
      double => System.Convert.ToDouble(source, CultureInfo.InvariantCulture),
      _ => throw new InvalidOperationException(),
    };
  }

  private static object? Whole(object? value)
  {
    if (value is double d && Math.Abs(d - Math.Round(d)) > 1e-9) throw new FormatException();
    return value is double w ? Math.Round(w) : value;
  }
}
