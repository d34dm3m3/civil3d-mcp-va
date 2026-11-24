using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;

namespace Civil3DMcpPlugin;

/// <summary>
/// C120 (2026-10-12): labelSetStyle {handles, collection, style, dryRun?} sets the style of existing Civil labels (for
/// example structure labels) by handle; collection is a label style collection path as in labelStyleTextGet
/// ("StructureLabelStyles.LabelStyles"). Position, dragged state and the labelled object stay. Use case: the
/// two existing end manholes showed no VG because the TextForEach row lists no
/// pipe there; they get a copy of the style with VG from the sump. All handles are checked before anything changes.
/// </summary>
public static class LabelStyleAssignCommands
{
  public static Task<object?> SetLabelStyleAsync(JsonObject? parameters)
  {
    var handles = (PluginRuntime.GetParameter(parameters, "handles") as JsonArray)?.Select(n => n?.GetValue<string>())
      .OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? new List<string>();
    var path = PluginRuntime.GetRequiredString(parameters, "collection");
    var styleName = PluginRuntime.GetRequiredString(parameters, "style");
    var dryRun = PluginRuntime.GetOptionalBool(parameters, "dryRun") ?? false;
    if (handles.Count == 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Give handles. Nothing was changed.");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var (collection, resolved) = LabelStyleTextCommands.ResolveCollection(civilDoc, path);
      var styleId = LabelStyleTextCommands.FindStyleId(collection, resolved, styleName, transaction);
      var labels = new List<Autodesk.Civil.DatabaseServices.Label>();
      foreach (var h in handles)
      {
        if (!database.TryGetObjectId(CivilObjectUtils.ParseHandle(h), out var id) || id.IsErased)
          throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"No object with handle {h}. Nothing was changed.");
        if (transaction.GetObject(id, OpenMode.ForRead) is not Autodesk.Civil.DatabaseServices.Label label)
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{h} is not a Civil label. Nothing was changed.");
        labels.Add(label);
      }
      var result = new List<Dictionary<string, object?>>();
      foreach (var label in labels)
      {
        var before = label.StyleId.IsNull ? null : ((DBObject)transaction.GetObject(label.StyleId, OpenMode.ForRead)) is Autodesk.Civil.DatabaseServices.Styles.StyleBase s ? s.Name : null;
        if (!dryRun)
        {
          label.UpgradeOpen();
          label.StyleId = styleId;
        }
        result.Add(new Dictionary<string, object?> { ["handle"] = label.Handle.ToString(), ["type"] = label.GetType().Name, ["before"] = before, ["after"] = styleName });
      }
      return new Dictionary<string, object?> { ["collection"] = resolved, ["style"] = styleName, ["dryRun"] = dryRun, ["labels"] = result };
    });
  }
}
