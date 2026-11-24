using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.DataShortcuts;

namespace Civil3DMcpPlugin;

/// <summary>
/// Data shortcut projects without dialogs (C61, 2026-10-08), through Autodesk.Civil.DataShortcuts.DataShortcuts:
/// read and set the working folder and current shortcut project (create it, optionally from a project template),
/// associate the active drawing with the project, and publish data shortcuts from the active drawing by type and
/// name. The data shortcut XML stores paths relative to the project folder, so the project can be moved.
/// The older dataShortcut* commands open the Civil 3D dialogs instead.
/// </summary>
public static class DataShortcutProjectCommands
{
  public static Task<object?> GetDataShortcutProjectAsync(JsonObject? parameters) =>
    CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) => State(database));

  public static Task<object?> SetDataShortcutProjectAsync(JsonObject? parameters)
  {
    var workingFolder = PluginRuntime.GetOptionalString(parameters, "workingFolder");
    var projectFolder = PluginRuntime.GetOptionalString(parameters, "projectFolder");
    var create = PluginRuntime.GetOptionalBool(parameters, "create") ?? false;
    var description = PluginRuntime.GetOptionalString(parameters, "description") ?? "";
    var templatePath = PluginRuntime.GetOptionalString(parameters, "templatePath");
    var associate = PluginRuntime.GetOptionalBool(parameters, "associateDrawing") ?? false;
    if (workingFolder != null && !Directory.Exists(workingFolder))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Working folder '{workingFolder}' does not exist. Nothing was changed.");
    if (templatePath != null && !Directory.Exists(templatePath))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Project template '{templatePath}' does not exist. Nothing was changed.");
    if (create && string.IsNullOrWhiteSpace(projectFolder))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "create needs projectFolder (the project's folder name in the working folder). Nothing was changed.");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var steps = new List<string>();
      if (workingFolder != null)
      {
        DataShortcuts.SetWorkingFolder(workingFolder);
        steps.Add($"working folder {workingFolder}");
      }

      if (!string.IsNullOrWhiteSpace(projectFolder))
      {
        var path = Path.Combine(DataShortcuts.GetWorkingFolder(), projectFolder!);
        var isProject = Directory.Exists(Path.Combine(path, "_Shortcuts"));
        if (create && !isProject)
        {
          // an existing folder without _Shortcuts is made a shortcut project in place (its files stay)
          if (templatePath != null)
            DataShortcuts.CreateProjectFolder(projectFolder!, description, templatePath, true);
          else
            DataShortcuts.CreateProjectFolder(projectFolder!, description, true);
          steps.Add($"created shortcut project {projectFolder}");
        }
        else
        {
          if (!isProject)
            throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND",
              $"'{path}' is not a data shortcut project (no _Shortcuts folder); give create=true. {string.Join("; ", steps)}");
          DataShortcuts.SetCurrentProjectFolder(projectFolder!);
          steps.Add($"current project {projectFolder}");
        }
      }

      if (associate)
      {
        var current = DataShortcuts.GetCurrentProjectFolder();
        if (string.IsNullOrWhiteSpace(current))
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"No current shortcut project to associate with. {string.Join("; ", steps)}");
        var id = DataShortcuts.GetDSProjectId(ProjectPath(current));
        DataShortcuts.AssociateDSProject(id, database, false);
        steps.Add($"drawing associated with {id}");
      }

      var state = State(database);
      state["steps"] = steps;
      return state;
    });
  }

  /// <summary>Lists what the active drawing can publish; selects items by type and name (or all) and saves the
  /// shortcuts. Dependent items (a profile's alignment) are selected by Civil 3D itself.</summary>
  public static Task<object?> PublishDataShortcutsAsync(JsonObject? parameters)
  {
    var all = PluginRuntime.GetOptionalBool(parameters, "all") ?? false;
    var listOnly = PluginRuntime.GetOptionalBool(parameters, "listOnly") ?? false;
    var wanted = new List<(string? Type, string Name)>();
    if (PluginRuntime.GetParameter(parameters, "items") is JsonArray items)
    {
      foreach (var node in items)
      {
        if (node is JsonObject o && o["name"]?.GetValue<string>() is { Length: > 0 } name)
          wanted.Add((o["type"]?.GetValue<string>(), name));
        else if (node is JsonValue v && v.TryGetValue<string>(out var plain))
          wanted.Add((null, plain));
        else
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "items are names or {type, name}. Nothing was published.");
      }
    }

    if (!listOnly && !all && wanted.Count == 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Give items, all=true or listOnly=true. Nothing was published.");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      if (string.IsNullOrWhiteSpace(DataShortcuts.GetCurrentProjectFolder()))
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "No current data shortcut project; set it with setDataShortcutProject. Nothing was published.");
      var ok = false;
      var manager = DataShortcuts.CreateDataShortcutManager(ref ok);
      if (!ok || manager == null)
        throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", "Civil 3D could not create a data shortcut manager for the active drawing (is it saved?). Nothing was published.");
      try
      {
        var rows = new List<Dictionary<string, object?>>();
        var matched = new HashSet<int>();
        for (var i = 0; i < manager.GetExportableItemsCount(); i++)
        {
          var item = manager.GetExportableItemAt(i);
          var type = item.DSEntityType.ToString();
          var select = all || wanted.Any(w => string.Equals(w.Name, item.Name, StringComparison.OrdinalIgnoreCase)
            && (w.Type == null || string.Equals(w.Type, type, StringComparison.OrdinalIgnoreCase)));
          if (select)
            matched.Add(i);
          rows.Add(new Dictionary<string, object?>
          {
            ["index"] = i, ["type"] = type, ["name"] = item.Name, ["alreadyPublished"] = manager.IsItemAtIndexAlreadyPublished(i),
            ["selected"] = select,
          });
        }

        var missing = wanted.Where(w => !rows.Any(r => string.Equals((string)r["name"]!, w.Name, StringComparison.OrdinalIgnoreCase)
          && (w.Type == null || string.Equals((string)r["type"]!, w.Type, StringComparison.OrdinalIgnoreCase)))).ToList();
        if (listOnly)
          return new Dictionary<string, object?> { ["project"] = DataShortcuts.GetCurrentProjectFolder(), ["exportable"] = rows };
        if (missing.Count > 0)
          throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND",
            "Not publishable from this drawing: " + string.Join(", ", missing.Select(m => (m.Type != null ? m.Type + " " : "") + m.Name)) + ". Nothing was published.");
        foreach (var i in matched)
          manager.SetSelectItemAtIndex(i, true);
        var saved = DataShortcuts.SaveDataShortcutManager(ref manager);
        if (!saved)
          throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", "Civil 3D did not save the data shortcuts.");
        return new Dictionary<string, object?>
        {
          ["project"] = DataShortcuts.GetCurrentProjectFolder(),
          ["published"] = rows.Where(r => (bool)r["selected"]!).Select(r => new { type = r["type"], name = r["name"] }).ToList(),
          ["exportable"] = rows,
        };
      }
      finally
      {
        manager.Dispose();
      }
    });
  }

  /// <summary>The current project as a path (the API may give the folder name).</summary>
  private static string ProjectPath(string current) =>
    Path.IsPathRooted(current) ? current : Path.Combine(DataShortcuts.GetWorkingFolder(), current);

  private static Dictionary<string, object?> State(Database database)
  {
    var current = DataShortcuts.GetCurrentProjectFolder();
    return new Dictionary<string, object?>
    {
      ["workingFolder"] = DataShortcuts.GetWorkingFolder(),
      ["currentProject"] = current,
      ["currentProjectId"] = string.IsNullOrWhiteSpace(current) ? null : DataShortcuts.GetDSProjectId(ProjectPath(current)),
      ["otherProjects"] = DataShortcuts.GetOtherProjectFolders()?.ToList(),
      ["drawingProjectId"] = DataShortcuts.GetAssociateShortcutProjectIdFromDrawing(database),
      ["drawing"] = database.Filename,
    };
  }
}
