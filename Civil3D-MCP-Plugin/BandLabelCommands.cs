using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.DatabaseServices;

namespace Civil3DMcpPlugin;

/// <summary>
/// C143: single band labels in a profile view. A band labels every pipe and structure (or every profile data point),
/// and where parts lie closer than the label width the texts print on top of each other (a drawing review: levels
/// "41,06/41,09" and "PE 110 SDR 17" three times at the tee and valves on two plan/profile sheets). The API has no
/// per-label filter in the band style, but each label of a band label group is a LabelGroupSubEntity with its own
/// visibility and dragged offset. listProfileViewBandLabels reads them; setProfileViewBandLabels hides, shows or moves
/// them. Label text is not exposed by the API, so callers decide from location and band style.
/// </summary>
public static class BandLabelCommands
{
  private static IEnumerable<(string Kind, ObjectId Id)> Groups(ObjectId profileViewId)
  {
    foreach (ObjectId id in PipeNetworkBandLabelGroup.GetAvailableLabelGroupIds(profileViewId)) yield return ("pipeNetwork", id);
    foreach (ObjectId id in ProfileDataBandLabelGroup.GetAvailableLabelGroupIds(profileViewId)) yield return ("profileData", id);
  }

  // ProfileBandLabelGroup.StyleName throws "Label style is invalid for a band label group" (live 2026-10-19);
  // the style id resolves through the transaction when it is set.
  private static string? StyleName(LabelGroup group, Transaction transaction)
  {
    try
    {
      var sid = group.StyleId;
      if (!sid.IsNull && transaction.GetObject(sid, OpenMode.ForRead) is Autodesk.Civil.DatabaseServices.Styles.StyleBase s) return s.Name;
    }
    catch (System.Exception) { }
    return null;
  }

  private static int SafeCount(LabelGroupSubEntity sub)
  {
    try { return sub.GetTextComponentIds().Count; } catch (System.Exception) { return -1; }
  }

  public static Task<object?> ListProfileViewBandLabelsAsync(JsonObject? parameters)
  {
    var profileViewName = PluginRuntime.GetRequiredString(parameters, "profileViewName");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var view = ProfileEditCommands.FindProfileViewByName(civilDoc, transaction, profileViewName);
      var groups = new List<Dictionary<string, object?>>();
      foreach (var (kind, gid) in Groups(view.ObjectId))
      {
        if (transaction.GetObject(gid, OpenMode.ForRead) is not LabelGroup group) continue;
        var labels = new List<Dictionary<string, object?>>();
        for (uint i = 0; i < group.SubEntityCount; i++)
        {
          var sub = group.GetAt(i);
          var p = sub.LabelLocation;
          labels.Add(new Dictionary<string, object?>
          {
            ["index"] = i,
            ["x"] = Math.Round(p.X, 4),
            ["y"] = Math.Round(p.Y, 4),
            ["visible"] = sub.Visibility,
            ["dragged"] = sub.Dragged,
            ["draggedOffset"] = new[] { Math.Round(sub.DraggedOffset.X, 4), Math.Round(sub.DraggedOffset.Y, 4) },
            ["allowsDragging"] = sub.AllowsDragging,
            // labels whose style part has no text component (e.g. structures in a pipe material band) print nothing
            ["textComponents"] = SafeCount(sub),
          });
        }
        groups.Add(new Dictionary<string, object?>
        {
          ["kind"] = kind,
          ["handle"] = group.Handle.ToString(),
          ["style"] = StyleName(group, transaction),
          ["count"] = labels.Count,
          ["labels"] = labels,
        });
      }
      return new Dictionary<string, object?>
      {
        ["profileView"] = view.Name,
        ["insertion"] = new[] { Math.Round(view.Location.X, 3), Math.Round(view.Location.Y, 3) },
        ["groups"] = groups,
      };
    });
  }

  /// <summary>updates: [{handle, index, visible?, draggedOffset?: [dx, dy], reset?}] (handle = label group from
  /// listProfileViewBandLabels). reset clears location and properties first; resetAll: true resets every label of
  /// every band group of the view (undo of earlier calls).</summary>
  public static Task<object?> SetProfileViewBandLabelsAsync(JsonObject? parameters)
  {
    var profileViewName = PluginRuntime.GetRequiredString(parameters, "profileViewName");
    var resetAll = PluginRuntime.GetOptionalBool(parameters, "resetAll") ?? false;
    var updates = parameters?["updates"] as JsonArray ?? new JsonArray();
    if (!resetAll && updates.Count == 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "updates must list at least one {handle, index, ...}, or resetAll must be true. Nothing was changed.");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var view = ProfileEditCommands.FindProfileViewByName(civilDoc, transaction, profileViewName);
      var own = Groups(view.ObjectId).Select(g => g.Id).ToList();
      int reset = 0, changed = 0;
      if (resetAll)
      {
        foreach (var gid in own)
        {
          var group = (LabelGroup)transaction.GetObject(gid, OpenMode.ForWrite);
          for (uint i = 0; i < group.SubEntityCount; i++)
          {
            var sub = group.GetAt(i);
            if (!sub.Visibility) sub.Visibility = true;
            if (sub.Dragged) sub.ResetLocation();
            reset++;
          }
        }
      }
      for (var k = 0; k < updates.Count; k++)
      {
        var u = updates[k] as JsonObject
          ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"updates[{k}] must be an object. Nothing after it was changed.");
        var handleText = u["handle"]?.GetValue<string>()
          ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"updates[{k}].handle is missing.");
        var id = database.GetObjectId(false, CivilObjectUtils.ParseHandle(handleText), 0);
        if (!own.Contains(id))
          throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"updates[{k}]: {handleText} is not a band label group of {profileViewName}.");
        var group = (LabelGroup)transaction.GetObject(id, OpenMode.ForWrite);
        var index = u["index"]?.GetValue<uint>() ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"updates[{k}].index is missing.");
        if (index >= group.SubEntityCount)
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"updates[{k}].index {index} is outside 0..{group.SubEntityCount - 1}.");
        var sub = group.GetAt(index);
        if (u["reset"]?.GetValue<bool>() == true) sub.Reset();
        if (u["visible"] is JsonNode vis) sub.Visibility = vis.GetValue<bool>();
        if (u["draggedOffset"] is JsonArray off && off.Count >= 2)
        {
          if (!sub.AllowsDragging)
            throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"updates[{k}]: the label cannot be dragged.");
          sub.DraggedOffset = new Vector3d(off[0]!.GetValue<double>(), off[1]!.GetValue<double>(), 0);
        }
        changed++;
      }
      return new Dictionary<string, object?> { ["profileView"] = view.Name, ["reset"] = reset, ["changed"] = changed };
    });
  }

  /// <summary>C204: weeds overlapping band labels of a profile view: {profileViewName, minSpacing? (drawing units) |
  /// minSpacingMm=3.5 with horizontalScale=400, rowTolerance=0.2, dryRun?}. Labels are grouped in rows by their y
  /// (all band groups together); within a row a label closer than minSpacing (x) to a label that stays is hidden. Labels
  /// with more text components stay first (a structure's level labels before the pipe ends' cell borders), then the
  /// lower station. Dragged labels (moved by hand) always stay and are never hidden; labels that print nothing (no text
  /// component) and labels already hidden are ignored, so a rerun changes nothing. Need: gap analysis point 12, band
  /// weeding (BandSetItem.Weeding) does not work on pipe network bands (C55/C125), so stacked levels at short pipe ends
  /// were hidden station by station in project scripts.</summary>
  public static Task<object?> WeedProfileViewBandLabelsAsync(JsonObject? parameters)
  {
    var profileViewName = PluginRuntime.GetRequiredString(parameters, "profileViewName");
    var scale = PluginRuntime.GetOptionalDouble(parameters, "horizontalScale") ?? 400;
    var minSpacing = PluginRuntime.GetOptionalDouble(parameters, "minSpacing")
      ?? (PluginRuntime.GetOptionalDouble(parameters, "minSpacingMm") ?? 3.5) / 1000 * scale;
    var rowTol = PluginRuntime.GetOptionalDouble(parameters, "rowTolerance") ?? 0.2;
    var dryRun = PluginRuntime.GetOptionalBool(parameters, "dryRun") ?? false;
    if (!(minSpacing > 0))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "minSpacing must be > 0. Nothing was changed.");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var view = ProfileEditCommands.FindProfileViewByName(civilDoc, transaction, profileViewName);
      var labels = new List<(ObjectId G, string Handle, uint I, double X, double Y, int Texts, bool Dragged)>();
      foreach (var (kind, gid) in Groups(view.ObjectId))
      {
        if (transaction.GetObject(gid, OpenMode.ForRead) is not LabelGroup group) continue;
        for (uint i = 0; i < group.SubEntityCount; i++)
        {
          var sub = group.GetAt(i);
          if (!sub.Visibility) continue;
          var texts = SafeCount(sub);
          if (texts == 0) continue;
          labels.Add((gid, group.Handle.ToString(), i, sub.LabelLocation.X, sub.LabelLocation.Y, texts < 0 ? 1 : texts, sub.Dragged));
        }
      }
      // rows by y
      var rows = new List<List<int>>();
      foreach (var k in Enumerable.Range(0, labels.Count).OrderBy(k => labels[k].Y))
      {
        if (rows.Count > 0 && Math.Abs(labels[rows[^1][0]].Y - labels[k].Y) <= rowTol) rows[^1].Add(k); else rows.Add(new List<int> { k });
      }
      var hide = new List<int>();
      foreach (var row in rows)
      {
        var kept = new List<double>();
        foreach (var k in row.Where(k => labels[k].Dragged)) kept.Add(labels[k].X);
        foreach (var k in row.Where(k => !labels[k].Dragged).OrderByDescending(k => labels[k].Texts).ThenBy(k => labels[k].X))
        {
          if (kept.Any(x => Math.Abs(x - labels[k].X) < minSpacing)) hide.Add(k); else kept.Add(labels[k].X);
        }
      }
      if (!dryRun)
        foreach (var k in hide)
        {
          var group = (LabelGroup)transaction.GetObject(labels[k].G, OpenMode.ForWrite);
          group.GetAt(labels[k].I).Visibility = false;
        }
      var x0 = view.Location.X;
      return new Dictionary<string, object?>
      {
        ["profileView"] = view.Name, ["dryRun"] = dryRun, ["minSpacing"] = Math.Round(minSpacing, 3), ["rows"] = rows.Count,
        ["labels"] = labels.Count, ["hidden"] = hide.Count,
        ["hiddenLabels"] = hide.Select(k => new Dictionary<string, object?> { ["handle"] = labels[k].Handle, ["index"] = labels[k].I,
          ["x"] = Math.Round(labels[k].X - x0, 3), ["y"] = Math.Round(labels[k].Y - view.Location.Y, 3), ["texts"] = labels[k].Texts }).ToList(),
      };
    });
  }
}
