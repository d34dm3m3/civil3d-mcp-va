using System.Text.Json.Nodes;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;

namespace Civil3DMcpPlugin;

/// <summary>
/// C117: a background mask on existing multileaders. Use case (the template's open question 5): AutoCAD
/// does not keep a mask in a multileader style (setMLeaderStyle wrote DefaultMText with BackgroundFill, the style reads
/// back background null in the pilot), so a flag the user draws by hand in Civil has no mask and the base map's lines run
/// through its text in the plot. The build chain therefore masks every flag on the flag layers. Only the MText's
/// background changes: position, text, leaders and style stay (the text location is compared before and after).
/// setMLeaderMask {handles? | layers?, backgroundColor? (0 = mask in the background colour, default; 1-255 a fill),
/// maskScale? (default 1.25), dryRun?}: model space multileaders with MText content; those on locked layers are skipped.
/// </summary>
public static class MLeaderMaskCommands
{
  public static Task<object?> SetMaskAsync(JsonObject? parameters)
  {
    var handles = (PluginRuntime.GetParameter(parameters, "handles") as JsonArray ?? new JsonArray())
      .Select(n => n?.GetValue<string>()?.Trim().ToUpperInvariant() ?? "").Where(h => h.Length > 0).ToHashSet();
    var layers = (PluginRuntime.GetParameter(parameters, "layers") as JsonArray ?? new JsonArray())
      .Select(n => n?.GetValue<string>()?.Trim() ?? "").Where(h => h.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
    if (handles.Count == 0 && layers.Count == 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Give handles or layers. Nothing was changed.");
    var color = PluginRuntime.GetOptionalInt(parameters, "backgroundColor") ?? 0;
    if (color is < 0 or > 255)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "backgroundColor must be 0 (mask in the background colour) or an ACI colour 1-255. Nothing was changed.");
    var scale = PluginRuntime.GetOptionalDouble(parameters, "maskScale") ?? 1.25;
    if (scale is < 1.0 or > 5.0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "maskScale must be 1-5. Nothing was changed.");
    var dryRun = PluginRuntime.GetOptionalBool(parameters, "dryRun") ?? false;

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var changed = new List<string>();
      var already = new List<string>();
      var skipped = new List<Dictionary<string, object?>>();
      var moved = new List<Dictionary<string, object?>>();
      var found = new HashSet<string>();
      var layerTable = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
      var space = (BlockTableRecord)transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(database), OpenMode.ForRead);
      foreach (ObjectId id in space)
      {
        if (id.ObjectClass.DxfName != "MULTILEADER") continue;
        var ml = (MLeader)transaction.GetObject(id, OpenMode.ForRead);
        var handle = ml.Handle.ToString().ToUpperInvariant();
        if (!handles.Contains(handle) && !layers.Contains(ml.Layer)) continue;
        found.Add(handle);
        if (ml.ContentType != ContentType.MTextContent)
        {
          skipped.Add(new() { ["handle"] = handle, ["reason"] = "not MText content" });
          continue;
        }
        using (var current = ml.MText)
        {
          var same = current.BackgroundFill && current.UseBackgroundColor == (color == 0)
            && (color == 0 || current.BackgroundFillColor.ColorIndex == color)
            && Math.Abs(current.BackgroundScaleFactor - scale) < 1e-9;
          if (same) { already.Add(handle); continue; }
        }
        var layerRecord = (LayerTableRecord)transaction.GetObject(layerTable[ml.Layer], OpenMode.ForRead);
        if (layerRecord.IsLocked)
        {
          skipped.Add(new() { ["handle"] = handle, ["reason"] = $"layer {ml.Layer} is locked" });
          continue;
        }
        if (dryRun) { changed.Add(handle); continue; }
        var before = ml.TextLocation;
        ml.UpgradeOpen();
        var mtext = ml.MText;
        mtext.BackgroundFill = true;
        mtext.UseBackgroundColor = color == 0;
        if (color > 0) mtext.BackgroundFillColor = Color.FromColorIndex(ColorMethod.ByAci, (short)color);
        mtext.BackgroundScaleFactor = scale;
        ml.MText = mtext;
        mtext.Dispose();
        var after = ml.TextLocation;
        if (before.DistanceTo(after) > 1e-6)
          moved.Add(new() { ["handle"] = handle, ["before"] = new[] { before.X, before.Y }, ["after"] = new[] { after.X, after.Y } });
        changed.Add(handle);
      }
      var missing = handles.Where(h => !found.Contains(h)).ToList();
      if (moved.Count > 0)
        throw new JsonRpcDispatchException("CIVIL3D.OPERATION_FAILED",
          $"Setting the mask moved {moved.Count} text(s) (first {moved[0]["handle"]}); the transaction is rolled back. Nothing was changed.");
      return new Dictionary<string, object?>
      {
        ["dryRun"] = dryRun,
        [dryRun ? "wouldChange" : "changed"] = changed,
        ["alreadyMasked"] = already,
        ["skipped"] = skipped,
        ["handlesNotFound"] = missing,
        ["background"] = color == 0 ? "mask" : color.ToString(),
        ["maskScale"] = scale,
      };
    });
  }
}
