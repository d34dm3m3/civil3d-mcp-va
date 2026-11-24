using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;

namespace Civil3DMcpPlugin;

/// <summary>
/// C118: start an interactive AutoCAD command on the command line with objects preselected, and let the user pick the
/// points. Use case: georeferencing a utility owner's scanned sheets by hand with ALIGN, the raster chosen
/// by handle. startCommand {command (ALIGN, MOVE, ROTATE, SCALE, IMAGECLIP, DRAWORDER), handles?}: the handles are
/// checked first (model space entities that exist); the command string is queued with SendStringToExecute
/// ("_.ALIGN", each object as (handent "h"), then Enter to end the selection), so it runs when the command line is idle
/// and the answer returns at once. C118b: PLINE and LINE for tracing by hand, and layer (an existing layer) is made current first.
/// C118d: REGENALL, so a script can refresh viewports after building layouts (after a restart Civil showed stale
/// viewport graphics: no profile ground line, no xrefs in new sheets). Only the allowlisted commands; nothing else reaches the command line. While the user
/// is inside the command, the plugin's own commands wait for it to end.
/// </summary>
public static class InteractiveCommandCommands
{
  private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase) { "ALIGN", "MOVE", "ROTATE", "SCALE", "IMAGECLIP", "DRAWORDER", "PLINE", "LINE", "REGENALL" };

  public static Task<object?> StartAsync(JsonObject? parameters)
  {
    var command = PluginRuntime.GetRequiredString(parameters, "command").Trim().TrimStart('_', '.').ToUpperInvariant();
    if (!Allowed.Contains(command))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"command must be one of {string.Join(", ", Allowed.OrderBy(c => c))}. Nothing was started.");
    var handles = (PluginRuntime.GetParameter(parameters, "handles") as JsonArray ?? new JsonArray())
      .Select(n => n?.GetValue<string>()?.Trim().ToUpperInvariant() ?? "").Where(h => h.Length > 0).Distinct().ToList();
    if (handles.Any(h => !h.All(Uri.IsHexDigit)))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "handles must be hexadecimal. Nothing was started.");

    var layer = PluginRuntime.GetOptionalString(parameters, "layer");
    // C118c: a write transaction; in ReadAsync the aborted transaction rolled CLAYER back (the line landed on layer 0).
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      if (layer != null)
      {
        var layers = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
        if (!layers.Has(layer))
          throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"No layer '{layer}'. Nothing was started.");
        var record = (LayerTableRecord)transaction.GetObject(layers[layer], OpenMode.ForRead);
        if (record.IsFrozen || record.IsDependent)
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Layer '{layer}' is frozen or belongs to an xref. Nothing was started.");
        database.Clayer = record.ObjectId;
      }
      var objects = new List<Dictionary<string, object?>>();
      foreach (var h in handles)
      {
        if (!database.TryGetObjectId(CivilObjectUtils.ParseHandle(h), out var id) || id.IsErased
            || transaction.GetObject(id, OpenMode.ForRead) is not Entity entity)
          throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"No drawing object with handle {h}. Nothing was started.");
        objects.Add(new() { ["handle"] = h, ["type"] = entity.GetRXClass().DxfName, ["layer"] = entity.Layer });
      }
      var text = $"_.{command}\n" + (handles.Count > 0 ? string.Concat(handles.Select(h => $"(handent \"{h}\")\n")) + "\n" : "");
      doc.SendStringToExecute(text, true, false, false);
      return new Dictionary<string, object?>
      {
        ["started"] = command,
        ["document"] = doc.Name,
        ["preselected"] = objects,
        ["currentLayer"] = ((LayerTableRecord)transaction.GetObject(database.Clayer, OpenMode.ForRead)).Name,
        ["note"] = "Queued on the command line; it runs when Civil 3D is idle. Pick the points in Civil 3D; the plugin's commands wait until the command ends.",
      };
    });
  }
  /// <summary>
  /// exportToAutocad {output}: Civil 3D's -EXPORTTOAUTOCAD on the active drawing, a copy (.dwg)
  /// where Civil objects (pipes, structures, labels, profile views) are exploded to plain AutoCAD objects and xrefs
  /// bound; the drawing itself is not changed. Queued on the command line like startCommand (answers at once; the
  /// caller waits for the file). The output must be a new .dwg on a local disk in the export roots (no overwrite, so
  /// the command never meets an overwrite prompt). Use: machine-control files with all drawing content (labels,
  /// structures) where it is on the drawing.
  /// </summary>
  public static Task<object?> ExportToAutocadAsync(JsonObject? parameters)
  {
    var output = FileBoundary.ResolveLocalExportPath(PluginRuntime.GetRequiredString(parameters, "output"), false, ".dwg");
    if (File.Exists(output))
      throw new JsonRpcDispatchException("CIVIL3D.CONFLICT", $"{output} exists. Nothing was started.");
    if (output.Contains('"'))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "output may not contain quotes. Nothing was started.");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      doc.SendStringToExecute($"_.-EXPORTTOAUTOCAD\n\n\"{output}\"\n", true, false, false);
      return new Dictionary<string, object?>
      {
        ["started"] = "-EXPORTTOAUTOCAD",
        ["document"] = doc.Name,
        ["output"] = output,
        ["note"] = "Queued; the copy is written when Civil 3D is idle. Wait for the file.",
      };
    });
  }
}

