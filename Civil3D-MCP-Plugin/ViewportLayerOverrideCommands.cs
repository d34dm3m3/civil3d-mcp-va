using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;

namespace Civil3DMcpPlugin;

/// <summary>
/// C99i: layer property overrides in one viewport (VP Lineweight in the Layer Properties Manager): set or remove the
/// lineweight override of the named layers, or of all layers including xref-dependent ones, in a layout viewport. Use case
/// (pilot project, 2026-10-11): the small key map (1:6 000) on every sheet plots clearest with the thinnest
/// lineweight on every layer, while the main plan keeps the layer standard. Parameters: layout, handle (the viewport),
/// layers (names; omitted = all layers), lineweight (hundredths of a mm, a standard lineweight) or remove: true.
/// </summary>
public static class ViewportLayerOverrideCommands
{
  public static Task<object?> SetViewportLayerOverridesAsync(JsonObject? parameters)
  {
    var layoutName = PluginRuntime.GetRequiredString(parameters, "layout");
    var handle = PluginRuntime.GetRequiredString(parameters, "handle");
    var remove = PluginRuntime.GetOptionalBool(parameters, "remove") ?? false;
    var hundredths = PluginRuntime.GetOptionalInt(parameters, "lineweight");
    var names = (PluginRuntime.GetParameter(parameters, "layers") as JsonArray)?.Select(n => n?.GetValue<string>()).OfType<string>()
      .ToHashSet(StringComparer.OrdinalIgnoreCase);
    if (!remove && hundredths == null)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Give lineweight (hundredths of a mm) or remove: true. Nothing was changed.");
    var weight = remove ? LineWeight.ByLayer : AcadCommands.StandardLineWeight(hundredths!.Value);

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var (_, viewport) = ViewportCommands.FindViewport(database, transaction, layoutName, handle, OpenMode.ForRead);
      var table = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
      if (names != null)
      {
        var missing = names.Where(n => !table.Has(n)).ToList();
        if (missing.Count > 0)
          throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Layers not found: {string.Join(", ", missing)}. Nothing was changed.");
      }
      var changed = 0;
      foreach (ObjectId id in table)
      {
        var layer = (LayerTableRecord)transaction.GetObject(id, OpenMode.ForRead);
        if (names != null && !names.Contains(layer.Name))
          continue;
        layer.UpgradeOpen();
        var overrides = layer.GetViewportOverrides(viewport.ObjectId);
        if (remove)
        {
          // removal: API name not confirmed for Civil 3D 2026 (LayerViewportProperties has no RemoveLineWeightOverride);
          // set ByLayer as the neutral value until it is
          if (overrides.IsLineWeightOverridden && overrides.LineWeight != LineWeight.ByLayer)
          {
            overrides.LineWeight = LineWeight.ByLayer;
            changed++;
          }
        }
        else if (!overrides.IsLineWeightOverridden || overrides.LineWeight != weight)
        {
          overrides.LineWeight = weight;
          changed++;
        }
      }
      return new Dictionary<string, object?>
      {
        ["layout"] = layoutName,
        ["handle"] = handle,
        ["lineweight"] = remove ? null : hundredths,
        ["removed"] = remove,
        ["changed"] = changed,
        ["note"] = "Entities with their own lineweight (not ByLayer) keep it; the override applies to ByLayer lineweights.",
      };
    });
  }
}
