using System.Text.Json.Nodes;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices.Styles;

namespace Civil3DMcpPlugin;

/// <summary>
/// Read, change and copy pipe and structure styles: per view (plan, profile, section, model) the options
/// (sizes, size type, centerline and wall options, symbol block...) and the display components (centerline,
/// walls, structure, hatch...) with visibility, layer, color, linetype, linetype scale and lineweight.
/// Options are read and written generically over the option objects' simple properties, so every option
/// Civil 3D exposes is reachable without a list here.
/// </summary>
public static class PartStyleCommands
{
  private static readonly string[] Views = { "plan", "profile", "section", "model" };

  private static StyleBase FindStyle(CivilDocument cdoc, Transaction transaction, string kind, string name, OpenMode mode)
  {
    object collection = kind switch
    {
      "pipe" => cdoc.Styles.PipeStyles,
      "structure" => cdoc.Styles.StructureStyles,
      _ => throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"kind must be 'pipe' or 'structure', not '{kind}'."),
    };
    var id = LookupUtils.GetStyleIdStrict(collection, transaction, name, kind == "pipe" ? "Pipe style" : "Structure style");
    return (StyleBase)transaction.GetObject(id, mode);
  }

  private static object? Option(StyleBase style, string view) => (style, view) switch
  {
    (PipeStyle p, "plan") => p.PlanOption,
    (PipeStyle p, "profile") => p.ProfileOption,
    (StructureStyle s, "plan") => s.PlanOption,
    (StructureStyle s, "profile") => s.ProfileOption,
    (StructureStyle s, "section") => s.SectionOption,
    (StructureStyle s, "model") => s.ModelOption,
    _ => null,
  };

  private static Type? ComponentEnum(StyleBase style, string view) => (style, view) switch
  {
    (PipeStyle, "plan") => typeof(PipeDisplayStylePlanType),
    (PipeStyle, "profile") => typeof(PipeDisplayStyleProfileType),
    (PipeStyle, "section") => typeof(PipeDisplayStyleSectionType),
    (StructureStyle, "plan") => typeof(StructureDisplayStylePlanType),
    (StructureStyle, "profile") => typeof(StructureDisplayStyleProfileType),
    (StructureStyle, "section") => typeof(StructureDisplayStyleSectionType),
    _ => null,
  };

  private static DisplayStyle? Component(StyleBase style, string view, string? component)
  {
    if (view == "model")
      return style switch { PipeStyle p => p.GetDisplayStyleModel(), StructureStyle s => s.GetDisplayStyleModel(), _ => null };
    var type = ComponentEnum(style, view);
    if (type == null || component == null || !Enum.TryParse(type, component, true, out var value)) return null;
    return (style, view) switch
    {
      (PipeStyle p, "plan") => p.GetDisplayStylePlan((PipeDisplayStylePlanType)value!),
      (PipeStyle p, "profile") => p.GetDisplayStyleProfile((PipeDisplayStyleProfileType)value!),
      (PipeStyle p, "section") => p.GetDisplayStyleSection((PipeDisplayStyleSectionType)value!),
      (StructureStyle s, "plan") => s.GetDisplayStylePlan((StructureDisplayStylePlanType)value!),
      (StructureStyle s, "profile") => s.GetDisplayStyleProfile((StructureDisplayStyleProfileType)value!),
      (StructureStyle s, "section") => s.GetDisplayStyleSection((StructureDisplayStyleSectionType)value!),
      _ => null,
    };
  }

  private static IEnumerable<string> ComponentNames(StyleBase style, string view) =>
    view == "model" ? new[] { "model" } : ComponentEnum(style, view) is Type t ? Enum.GetNames(t) : Array.Empty<string>();

  private static IReadOnlyList<(string Name, Type Type, bool CanWrite)> SimpleProperties(object option) =>
    Civil3DCompatibility.GetSimplePropertyInfo(option);

  private static Dictionary<string, object?> DescribeDisplay(DisplayStyle d) => new()
  {
    ["visible"] = d.Visible,
    ["layer"] = d.Layer,
    ["color"] = d.Color.IsByLayer ? "ByLayer" : d.Color.IsByBlock ? "ByBlock" : d.Color.ColorMethod == ColorMethod.ByAci ? d.Color.ColorIndex.ToString() : $"{d.Color.Red},{d.Color.Green},{d.Color.Blue}",
    ["linetype"] = d.Linetype,
    ["linetypeScale"] = d.LinetypeScale,
    ["lineweight"] = d.Lineweight.ToString(),
  };

  private static Dictionary<string, object?> Describe(string kind, StyleBase style)
  {
    var options = new Dictionary<string, object?>();
    var display = new Dictionary<string, object?>();
    foreach (var view in Views)
    {
      if (Option(style, view) is object option)
      {
        var values = new Dictionary<string, object?>();
        foreach (var p in SimpleProperties(option))
        {
          try { var value = Civil3DCompatibility.GetPropertyValue(option, p.Name); values[p.Name] = p.Type.IsEnum ? value?.ToString() : value; }
          catch (Exception ex) { values[p.Name] = $"<{ex.GetType().Name}>"; }
        }

        options[view] = values;
      }

      var components = new Dictionary<string, object?>();
      foreach (var name in ComponentNames(style, view))
      {
        try
        {
          if (Component(style, view, name) is DisplayStyle d) components[name] = DescribeDisplay(d);
        }
        catch (Exception ex)
        {
          components[name] = new Dictionary<string, object?> { ["error"] = ex.Message };
        }
      }

      if (components.Count > 0) display[view] = components;
    }

    return new Dictionary<string, object?> { ["kind"] = kind, ["name"] = style.Name, ["options"] = options, ["display"] = display };
  }

  public static Task<object?> GetPartStyleAsync(JsonObject? parameters)
  {
    var kind = PluginRuntime.GetRequiredString(parameters, "kind").Trim().ToLowerInvariant();
    var name = PluginRuntime.GetRequiredString(parameters, "style");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
      Describe(kind, FindStyle((CivilDocument)civilDoc, transaction, kind, name, OpenMode.ForRead)));
  }

  public static Task<object?> CopyPartStyleAsync(JsonObject? parameters)
  {
    var kind = PluginRuntime.GetRequiredString(parameters, "kind").Trim().ToLowerInvariant();
    var name = PluginRuntime.GetRequiredString(parameters, "style");
    var newName = PluginRuntime.GetRequiredString(parameters, "newName");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var cdoc = (CivilDocument)civilDoc;
      var source = FindStyle(cdoc, transaction, kind, name, OpenMode.ForRead);
      try
      {
        FindStyle(cdoc, transaction, kind, newName, OpenMode.ForRead);
        throw new JsonRpcDispatchException("CIVIL3D.CONFLICT", $"A {kind} style named '{newName}' already exists. Nothing was copied.");
      }
      catch (JsonRpcDispatchException ex) when (ex.Code != "CIVIL3D.CONFLICT")
      {
        // not found: fine
      }

      var copy = (StyleBase)transaction.GetObject(source.CopyAsSibling(newName), OpenMode.ForRead);
      return Describe(kind, copy);
    });
  }

  private static object Convert(Type type, JsonNode node, string what)
  {
    try
    {
      if (type.IsEnum)
        return Enum.Parse(type, node.GetValue<string>(), true);
      if (type == typeof(double)) return node.GetValue<double>();
      if (type == typeof(int)) return node.GetValue<int>();
      if (type == typeof(bool)) return node.GetValue<bool>();
      return node.GetValue<string>();
    }
    catch (Exception ex) when (ex is not JsonRpcDispatchException)
    {
      var hint = type.IsEnum ? $" Use {string.Join(", ", Enum.GetNames(type))}." : $" Expected {type.Name}.";
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{what}: '{node.ToJsonString()}' is not valid.{hint} Nothing was changed.");
    }
  }

  internal static Color ParseColor(JsonNode node)
  {
    var text = node.ToString().Trim().Trim('"');
    if (string.Equals(text, "ByLayer", StringComparison.OrdinalIgnoreCase)) return Color.FromColorIndex(ColorMethod.ByLayer, 256);
    if (string.Equals(text, "ByBlock", StringComparison.OrdinalIgnoreCase)) return Color.FromColorIndex(ColorMethod.ByBlock, 0);
    var parts = text.Split(',');
    if (parts.Length == 3 && parts.All(p => byte.TryParse(p.Trim(), out _)))
      return Color.FromRgb(byte.Parse(parts[0].Trim()), byte.Parse(parts[1].Trim()), byte.Parse(parts[2].Trim()));
    if (short.TryParse(text, out var aci) && aci is >= 0 and <= 256) return Color.FromColorIndex(ColorMethod.ByAci, aci);
    throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"color '{text}' must be ByLayer, ByBlock, an ACI number 0-256 or 'r,g,b'. Nothing was changed.");
  }

  /// <summary>Changes options (options: {view: {property: value}}) and display components (display: {view:
  /// {component: {visible, layer, color, linetype, linetypeScale, lineweight}}}). All in one transaction;
  /// an unknown view, property or component fails the whole call.</summary>
  public static Task<object?> SetPartStyleAsync(JsonObject? parameters)
  {
    var kind = PluginRuntime.GetRequiredString(parameters, "kind").Trim().ToLowerInvariant();
    var name = PluginRuntime.GetRequiredString(parameters, "style");
    var options = PluginRuntime.GetParameter(parameters, "options") as JsonObject;
    var display = PluginRuntime.GetParameter(parameters, "display") as JsonObject;
    if ((options == null || options.Count == 0) && (display == null || display.Count == 0))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Give options and/or display.");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var style = FindStyle((CivilDocument)civilDoc, transaction, kind, name, OpenMode.ForWrite);
      var changed = new List<string>();
      foreach (var (view, node) in options ?? new JsonObject())
      {
        var option = Option(style, view.ToLowerInvariant())
          ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{kind} styles have no '{view}' options. Nothing was changed.");
        var properties = SimpleProperties(option).ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var (prop, valueNode) in node as JsonObject ?? new JsonObject())
        {
          if (!properties.TryGetValue(prop, out var property) || !property.CanWrite)
            throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
              $"'{prop}' is not a settable {view} option. Options: {string.Join(", ", properties.Values.Where(p => p.CanWrite).Select(p => p.Name))}. Nothing was changed.");
          if (valueNode == null) continue;
          if (!Civil3DCompatibility.TrySetProperty(option, property.Name, Convert(property.Type, valueNode, $"{view}.{prop}")))
            throw new JsonRpcDispatchException("CIVIL3D.OPERATION_FAILED", $"Civil 3D did not accept {view}.{property.Name}. Nothing was changed.");
          changed.Add($"{view}.{property.Name}");
        }
      }

      foreach (var (view, node) in display ?? new JsonObject())
      {
        var v = view.ToLowerInvariant();
        foreach (var (component, propsNode) in node as JsonObject ?? new JsonObject())
        {
          var d = Component(style, v, v == "model" ? "model" : component)
            ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
              $"'{component}' is not a {view} component of {kind} styles. Components: {string.Join(", ", ComponentNames(style, v))}. Nothing was changed.");
          foreach (var (prop, valueNode) in propsNode as JsonObject ?? new JsonObject())
          {
            if (valueNode == null) continue;
            switch (prop.ToLowerInvariant())
            {
              case "visible": d.Visible = valueNode.GetValue<bool>(); break;
              case "layer": d.Layer = valueNode.GetValue<string>(); break;
              case "color": d.Color = ParseColor(valueNode); break;
              case "linetype": d.Linetype = valueNode.GetValue<string>(); break;
              case "linetypescale": d.LinetypeScale = valueNode.GetValue<double>(); break;
              case "lineweight": d.Lineweight = (LineWeight)valueNode.GetValue<int>(); break;
              default:
                throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
                  $"'{prop}' is not a display property; use visible, layer, color, linetype, linetypeScale, lineweight. Nothing was changed.");
            }

            changed.Add($"{view}.{component}.{prop}");
          }
        }
      }

      var result = Describe(kind, style);
      result["changed"] = changed;
      return result;
    });
  }
}
