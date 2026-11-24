using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using Autodesk.Civil.Settings;
using System.Text.Json.Nodes;
using App = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Civil3DMcpPlugin;

public static class DrawingCommands
{
  public static Task<object?> GetCivil3DHealthAsync()
  {
    var status = PluginRuntime.GetStatus();
    var doc = App.DocumentManager.MdiActiveDocument;
    var process = System.Diagnostics.Process.GetCurrentProcess();
    var jobs = JobRegistry.GetStats();
    var documents = DocumentSnapshot.Current;

    object response = new Dictionary<string, object?>
    {
      ["connected"] = true,
      ["civil3dVersion"] = App.GetSystemVariable("ACADVER")?.ToString(),
      ["pluginVersion"] = typeof(PluginEntry).Assembly.GetName().Version?.ToString(),
      ["drawingLoaded"] = doc != null,
      // C93c: which drawing the next call runs in (the active one), and how many are open; from the snapshot taken on
      // the main thread (health answers on the TCP thread).
      ["activeDocument"] = documents.ActiveDocument,
      ["documentCount"] = documents.DocumentCount,
      ["operationInProgress"] = status.OperationInProgress,
      ["currentOperation"] = status.CurrentOperation,
      ["queueDepth"] = status.QueueDepth,
      ["queueCapacity"] = status.QueueCapacity,
      ["currentOperationStartedAtUnixMs"] = status.CurrentOperationStartedAtUnixMs,
      ["currentRequestId"] = status.CurrentRequestId,
      ["currentOperationDurationMs"] = status.CurrentOperationStartedAtUnixMs is long startedAt
        ? Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - startedAt)
        : null,
      ["memoryUsageMb"] = Math.Round(process.PrivateMemorySize64 / 1024d / 1024d, 2),
      ["logFilePath"] = PluginLog.LogFilePath,
      ["fileLoggingHealthy"] = PluginLog.IsFileLoggingHealthy,
      ["fileLoggingError"] = PluginLog.LastFileError,
      ["jobs"] = new Dictionary<string, object?>
      {
        ["total"] = jobs.Total,
        ["running"] = jobs.Running,
        ["completed"] = jobs.Completed,
        ["failed"] = jobs.Failed,
        ["cancelled"] = jobs.Cancelled,
        ["capacity"] = jobs.Capacity,
        ["terminalRetentionMinutes"] = jobs.TerminalRetentionMinutes,
      },
    };

    return Task.FromResult<object?>(response);
  }

  public static Task<object?> GetDrawingInfoAsync()
  {
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
      BuildDrawingInfo(doc, civilDoc, database, transaction));
  }

  public static Task<object?> GetProjectContextAsync(JsonObject? parameters)
  {
    var requestedLimit = PluginRuntime.GetOptionalInt(parameters, "selectedObjectLimit") ?? 25;
    var selectedObjectLimit = Math.Clamp(requestedLimit, 1, 100);

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
      new Dictionary<string, object?>
      {
        ["drawingInfo"] = BuildDrawingInfo(doc, civilDoc, database, transaction),
        ["objectTypes"] = BuildCivilObjectTypes(),
        ["selectedObjects"] = BuildSelectedCivilObjects(doc, transaction, selectedObjectLimit),
      });
  }

  public static Task<object?> GetDrawingSettingsAsync()
  {
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var currentLayer = CivilObjectUtils.GetRequiredObject<LayerTableRecord>(transaction, database.Clayer, OpenMode.ForRead);
      var coords = ReadCoordinateSystemFields(civilDoc);

      return new Dictionary<string, object?>
      {
        ["coordinateSystem"] = coords.code,
        ["coordinateZone"] = coords.zone,
        ["datum"] = coords.datum,
        ["scaleFactor"] = database.Dimscale,               // C93c: this drawing, not the current document's system variable
        ["elevationReference"] = coords.verticalDatum,
        ["defaultLayer"] = currentLayer.Name,
        ["defaultStyles"] = new Dictionary<string, object?>
        {
          ["surface"] = LookupUtils.GetFirstStyleName(civilDoc.Styles.SurfaceStyles, transaction),
          ["alignment"] = LookupUtils.GetFirstStyleName(civilDoc.Styles.AlignmentStyles, transaction),
          ["profile"] = LookupUtils.GetFirstStyleName(civilDoc.Styles.ProfileStyles, transaction),
          ["corridor"] = null,
          ["pipeNetwork"] = PipeNetworkCommands.GetFirstPipeNetworkStyleName(civilDoc, transaction),
        },
      };
    });
  }

  /// <summary>
  /// Saves the active drawing over its own file, or with saveAs to a new file in the export roots. C97 (2026-10-10):
  /// through the document (COM AcadDocument.Save, as QSAVE, or AcadDocument.SaveAs) in the application context, so a
  /// successful save leaves the document saved: Database.SaveAs wrote the file but left DBMOD set (documents_list showed
  /// unsavedChanges true right after saving, the template since M24) and a new drawing unnamed. The answer reports
  /// unsavedChanges after the save. Inside a batch (a document's command context) and when the document has no COM
  /// object, Database.SaveAs as before, with a warning.
  /// </summary>
  public static Task<object?> SaveDrawingAsync(JsonObject? parameters)
  {
    var overwrite = PluginRuntime.GetOptionalBool(parameters, "overwrite") ?? false;
    var saveAs = PluginRuntime.GetOptionalString(parameters, "saveAs");
    // Saving the active drawing over its own file is not an export: its file necessarily exists. Boundary, local disk
    // and overwrite checks apply to Save As, before Civil 3D is asked.
    var saveAsPath = string.IsNullOrWhiteSpace(saveAs) ? null : FileBoundary.ResolveLocalExportPath(saveAs, overwrite, ".dwg");

    if (CivilExecution.InBatch)
    {
      return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
      {
        var target = Target(doc, saveAsPath);
        SaveDatabase(database, target);
        DocumentCommands.NoteSaved(doc, target);   // C93: the document keeps its name; its file is remembered
        return Saved(doc, target, "Database.SaveAs (in a batch)",
          ["Saved through the database inside a batch: Civil 3D still reports unsaved changes and a drawing saved for the first time keeps its name; save on its own for that."]);
      });
    }

    var requestedDrawing = PluginRuntime.GetRequestedDrawing();
    return CivilExecution.ExecuteInApplicationContextAsync<object?>(() =>
    {
      var doc = App.DocumentManager.MdiActiveDocument ?? throw new JsonRpcDispatchException("CIVIL3D.NO_DRAWING", "No active drawing is open in Civil 3D.");
      PluginRuntime.CheckRequestedDrawing(doc, requestedDrawing);   // C150: never save another drawing than the caller named
      var target = Target(doc, saveAsPath);
      if (saveAsPath == null && doc.IsReadOnly)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"'{doc.Name}' is open read-only; give saveAs to save a copy. Nothing was saved.");
      // Review: checked again right before writing (the request may have waited for an idle Civil 3D); COM SaveAs
      // overwrites without asking.
      if (saveAsPath != null && !overwrite && File.Exists(saveAsPath))
        throw new JsonRpcDispatchException("CIVIL3D.CONFLICT", $"Output file already exists: {saveAsPath}. Set overwrite=true to replace it explicitly.");
      object? acadDocument = null;
      try
      {
        acadDocument = doc.GetAcadDocument();
      }
      catch (System.Exception)
      {
        // no COM document; the database path below
      }

      string method;
      var warnings = new List<string>();
      try
      {
        if (acadDocument == null)
        {
          using (doc.LockDocument()) SaveDatabase(doc.Database, target);
          method = "Database.SaveAs";
          warnings.Add("The document has no COM object, so it was saved through the database: Civil 3D still reports unsaved changes.");
        }
        else if (saveAsPath == null && doc.IsNamedDrawing && SameFileName(doc.Name, target))
        {
          // QSAVE writes the document's own file in its own format (a template stays a template).
          Civil3DCompatibility.InvokeComMethod(acadDocument, "Save");
          method = "AcadDocument.Save";
        }
        else
        {
          // A new file, or a drawing whose database was saved elsewhere (batch, no COM) without the document following:
          // SaveAs writes the target and gives the document its name. acNative (64): the current DWG format, as
          // Database.SaveAs wrote, not the user's default save format.
          Civil3DCompatibility.InvokeComMethod(acadDocument, "SaveAs", target, AcNative);
          method = "AcadDocument.SaveAs";
        }
      }
      catch (Exception ex) when (ex is not JsonRpcDispatchException)
      {
        throw new JsonRpcDispatchException("CIVIL3D.FILE_IO_ERROR",
          $"Saving '{doc.Name}' to '{target}' failed: {ex.Message} (If this is the first save of a drawing made from the original company template, try again: C73.)");
      }

      DocumentCommands.NoteSaved(doc, target);
      PluginLog.Info("Documents", $"saveDrawing: '{doc.Name}' saved to {target} ({method}).");
      return Saved(doc, target, method, warnings);
    }, "saveDrawing", TimeSpan.FromSeconds(100));
  }

  // AcSaveAsType.acNative: the current DWG format.
  private const int AcNative = 64;

  /// <summary>Whether a document name (the full path of a saved drawing) is the target file.</summary>
  private static bool SameFileName(string documentName, string target)
  {
    try
    {
      return Path.IsPathFullyQualified(documentName) && string.Equals(Path.GetFullPath(documentName), target, StringComparison.OrdinalIgnoreCase);
    }
    catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
    {
      return false;
    }
  }

  /// <summary>The file to save to: saveAs, or the drawing's own file. C93 review: a drawing never saved has no file of
  /// its own; its Database.Filename names the template it was made from, which a save without saveAs would overwrite.</summary>
  private static string Target(Document doc, string? saveAsPath) =>
    saveAsPath ?? DocumentCommands.PathOf(doc)
      ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"saveDrawing requires 'saveAs': '{doc.Name}' has not been saved yet. Nothing was saved.");

  private static void SaveDatabase(Database database, string target)
  {
    // C71 (live 2026-10-09): AutoCAD reads drawings lazily and keeps the source file open; read everything in and
    // release the source file first when saving over it, as QSAVE does.
    if (string.Equals(Path.GetFullPath(database.Filename ?? ""), target, StringComparison.OrdinalIgnoreCase))
      database.CloseInput(true);
    database.SaveAs(target, true, DwgVersion.Current, database.SecurityParameters);
  }

  private static Dictionary<string, object?> Saved(Document doc, string target, string method, List<string> warnings) => new()
  {
    ["saved"] = true,
    ["filePath"] = DocumentCommands.PathOf(doc) ?? target,
    ["drawingName"] = doc.Name,
    ["unsavedChanges"] = DocumentCommands.HasUnsavedChanges(doc),
    ["method"] = method,
    ["warnings"] = warnings,
  };

  public static async Task<object?> NewDrawingAsync(JsonObject? parameters)
  {
    var templatePath = PluginRuntime.GetOptionalString(parameters, "templatePath");
    if (!string.IsNullOrWhiteSpace(templatePath))
    {
      templatePath = FileBoundary.ResolveImportPath(templatePath, ".dwt", ".dwg");
      FileBoundary.RequireLocalDisk(templatePath);   // C93: local disks only
    }

    // C93: created and activated in the application context (it ran in a document's command context, where AutoCAD
    // defers a document switch until the command ends), so the next request already runs in the new drawing.
    return await CivilExecution.ExecuteInApplicationContextAsync<object?>(() =>
    {
      var previous = App.DocumentManager.MdiActiveDocument;
      var createdDocument = string.IsNullOrWhiteSpace(templatePath)
        ? App.DocumentManager.Add(string.Empty)
        : App.DocumentManager.Add(templatePath);

      var active = DocumentCommands.Activate(createdDocument);
      PluginLog.Info("Documents", $"newDrawing: '{createdDocument.Name}' created{(string.IsNullOrWhiteSpace(templatePath) ? "" : $" from {templatePath}")}, active {active}.");
      return new Dictionary<string, object?>
      {
        ["drawingName"] = createdDocument.Name,
        // C93: null until the drawing is saved (Database.Filename of a new drawing may name its template instead).
        ["filePath"] = DocumentCommands.PathOf(createdDocument),
        ["templatePath"] = templatePath,
        ["active"] = active,
        ["activeDocument"] = App.DocumentManager.MdiActiveDocument?.Name,
        ["previousDocument"] = previous?.Name,
        ["note"] = "Not saved yet: give it a file with civil3d_drawing save (saveAs) before closing it with save: true.",
      };
    }, "newDrawing", TimeSpan.FromSeconds(100));
  }

  public static Task<object?> UndoDrawingAsync(JsonObject? parameters)
  {
    var steps = PluginRuntime.GetOptionalInt(parameters, "steps") ?? 1;
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      doc.SendStringToExecute($"_.UNDO {steps} ", true, false, false);
      return new Dictionary<string, object?>
      {
        ["steps"] = steps,
        ["action"] = "undo",
      };
    });
  }

  public static Task<object?> RedoDrawingAsync(JsonObject? parameters)
  {
    var steps = PluginRuntime.GetOptionalInt(parameters, "steps") ?? 1;
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      doc.SendStringToExecute($"_.REDO {steps} ", true, false, false);
      return new Dictionary<string, object?>
      {
        ["steps"] = steps,
        ["action"] = "redo",
      };
    });
  }

  public static Task<object?> ListCivilObjectTypesAsync()
  {
    return Task.FromResult<object?>(BuildCivilObjectTypes());
  }

  public static Task<object?> GetSelectedCivilObjectsInfoAsync(JsonObject? parameters)
  {
    var limit = PluginRuntime.GetOptionalInt(parameters, "limit") ?? 100;

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
      BuildSelectedCivilObjects(doc, transaction, limit));
  }

  private static Dictionary<string, object?> BuildDrawingInfo(
    Document doc,
    CivilDocument civilDoc,
    Database database,
    Transaction transaction)
  {
    // C93c (live 2026-10-10): read from this drawing, not through system variables of whichever document AutoCAD
    // treats as current, and a drawing never saved has no file: its Database.Filename is the template it was made
    // from (Drawing2.dwg reported the company template as fileName), so fileName is the document name and filePath null.
    var unsavedChanges = DocumentCommands.HasUnsavedChanges(doc) ?? Convert.ToInt32(App.GetSystemVariable("DBMOD") ?? 0) != 0;
    var angularUnits = CivilObjectUtils.AngularUnits((short)database.Aunits);
    var coordinateSystemCode = ReadCoordinateSystemCode(civilDoc);
    var filePath = DocumentCommands.PathOf(doc);

    return new Dictionary<string, object?>
    {
      ["fileName"] = filePath != null ? Path.GetFileName(filePath) : Path.GetFileName(doc.Name),
      ["filePath"] = filePath,
      ["templatePath"] = filePath == null && !string.IsNullOrWhiteSpace(database.Filename) ? database.Filename : null,
      ["activeDocument"] = App.DocumentManager.MdiActiveDocument?.Name,
      ["contextDocument"] = App.DocumentManager.CurrentDocument?.Name,
      ["coordinateSystem"] = coordinateSystemCode,
      ["linearUnits"] = CivilObjectUtils.LinearUnits(database),
      ["angularUnits"] = angularUnits,
      ["unsavedChanges"] = unsavedChanges,
      ["objectCounts"] = new Dictionary<string, object?>
      {
        ["surfaces"] = civilDoc.GetSurfaceIds().Count,
        ["alignments"] = civilDoc.GetAlignmentIds().Count,
        ["profiles"] = CountProfiles(civilDoc, transaction),
        ["corridors"] = civilDoc.CorridorCollection.Count,
        ["pipeNetworks"] = PipeNetworkCommands.CountPipeNetworks(civilDoc),
        ["points"] = civilDoc.CogoPoints.Count,
        ["parcels"] = CountParcels(civilDoc, transaction),
      },
      ["drawingName"] = doc.Name,
      ["projectName"] = null,
      ["units"] = CivilObjectUtils.LinearUnits(database),
    };
  }

  private static object[] BuildCivilObjectTypes() =>
  [
    "Alignment",
    "Surface",
    "Profile",
    "Corridor",
    "CogoPoint",
    "SampleLineGroup",
    "FeatureLine",
    "Parcel",
    "PipeNetwork",
  ];

  private static List<Dictionary<string, object?>> BuildSelectedCivilObjects(
    Document doc,
    Transaction transaction,
    int limit)
  {
    var result = new List<Dictionary<string, object?>>();
    // The pick-first set is gone by the time the plugin's command runs; use the selection the tracker
    // captured when the user made it, and the live one only if the tracker has nothing.
    var ids = SelectionTracker.For(doc);
    if (ids.Length == 0)
    {
      var selection = doc.Editor.SelectImplied();
      if (selection.Status == PromptStatus.OK && selection.Value != null) ids = selection.Value.GetObjectIds();
    }

    foreach (var objectId in ids.Take(limit))
    {
      var dbObject = transaction.GetObject(objectId, OpenMode.ForRead);
      if (dbObject == null)
      {
        continue;
      }

      result.Add(new Dictionary<string, object?>
      {
        ["handle"] = dbObject.Handle.ToString(),
        ["objectType"] = dbObject.GetType().Name,
        ["name"] = CivilObjectUtils.GetName(dbObject),
        ["layer"] = (dbObject as Autodesk.AutoCAD.DatabaseServices.Entity)?.Layer,
        ["description"] = CivilObjectUtils.GetStringProperty(dbObject, "Description"),
      });
    }

    return result;
  }

  private static int CountProfiles(CivilDocument civilDoc, Transaction transaction)
  {
    var count = 0;
    foreach (ObjectId objectId in civilDoc.GetAlignmentIds())
    {
      var alignment = CivilObjectUtils.GetRequiredObject<Alignment>(transaction, objectId, OpenMode.ForRead);
      count += alignment.GetProfileIds().Count;
    }

    return count;
  }

  private static int CountParcels(CivilDocument civilDoc, Transaction transaction)
  {
    var count = 0;

    foreach (ObjectId siteId in civilDoc.GetSiteIds())
    {
      var site = CivilObjectUtils.GetRequiredObject<Site>(transaction, siteId, OpenMode.ForRead);
      count += site.GetParcelIds().Count;
    }

    return count;
  }

  // Single-field coordinate system code (for getDrawingInfo).
  private static string? ReadCoordinateSystemCode(CivilDocument civilDoc)
  {
    return ReadCoordinateSystemFields(civilDoc).code;
  }

  private static (string? code, string? zone, string? datum, string? verticalDatum) ReadCoordinateSystemFields(CivilDocument civilDoc)
  {
    var unitZone = civilDoc.Settings.DrawingSettings.UnitZoneSettings;
    var code = unitZone.CoordinateSystemCode;
    if (string.IsNullOrWhiteSpace(code))
    {
      return (null, null, null, null);
    }

    var coordinateSystem = SettingsUnitZone.GetCoordinateSystemByCode(code);
    return (code, coordinateSystem.Category, coordinateSystem.Datum, null);
  }
}
