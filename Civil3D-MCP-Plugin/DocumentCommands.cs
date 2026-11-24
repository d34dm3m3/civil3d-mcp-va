using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using App = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Civil3DMcpPlugin;

/// <summary>
/// C93 (2026-10-10): the drawings open in Civil 3D. List them, open a local .dwg/.dwt (or hand back the document when
/// the file is open already), make one active, and close one, saved or with its changes discarded. Use case: a pilot
/// project starts from the company template in a new project drawing while the template, its assembly
/// library drawing and a test drawing (T6) stay open; the plugin could create a drawing but neither switch back nor
/// close one.
/// Opening, activating and closing documents needs the AutoCAD application context, so everything here runs through
/// <see cref="CivilExecution.ExecuteInApplicationContextAsync{T}"/> (Application.Idle, no command running). Requests
/// after it run in the command context of the document that is active then. A drawing is closed only after the
/// answer has been written to the client (<see cref="CivilExecution.ExecuteThenAfterAnswerAsync{T}"/>), and never
/// while it is the only open drawing (Civil 3D would be left without a document and the plugin without a command
/// context).
/// </summary>
public static class DocumentCommands
{
  private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(100);

  // Review 2026-10-10: Database.SaveAs (saveDrawing with saveAs) writes the file but leaves a drawing never saved
  // unnamed (same document name, IsNamedDrawing false), so the file it was saved to is remembered by document name and
  // counts as the drawing's file while its Database.Filename still names it.
  private static readonly object SavedSync = new();
  private static readonly Dictionary<string, string> SavedFiles = new(StringComparer.OrdinalIgnoreCase);

  // What became of the last close after its answer (closed, already closed, not closed and why); documents_list shows it.
  private static Dictionary<string, object?>? _lastClose;

  internal static void NoteSaved(Document doc, string path)
  {
    lock (SavedSync) SavedFiles[doc.Name] = Path.GetFullPath(path);
  }

  private static void NoteClose(string name, string state, string? detail = null)
  {
    var entry = new Dictionary<string, object?> { ["name"] = name, ["state"] = state, ["detail"] = detail, ["at"] = DateTime.Now.ToString("HH:mm:ss") };
    lock (SavedSync) _lastClose = entry;
  }

  public static Task<object?> ListDocumentsAsync(JsonObject? parameters) =>
    CivilExecution.ExecuteInApplicationContextAsync<object?>(() =>
    {
      var active = App.DocumentManager.MdiActiveDocument;
      return new Dictionary<string, object?>
      {
        ["documents"] = Documents().Select(d => Describe(d, active)).ToList(),
        ["count"] = App.DocumentManager.Count,
        ["activeDocument"] = active?.Name,
        ["lastClose"] = LastClose(),
      };
    }, "documentsList", Timeout, inlineInBatch: true);

  public static Task<object?> OpenDocumentAsync(JsonObject? parameters)
  {
    var path = FileBoundary.ResolveLocalReadPath(PluginRuntime.GetRequiredString(parameters, "path"), ".dwg", ".dwt");
    var readOnly = PluginRuntime.GetOptionalBool(parameters, "readOnly") ?? false;
    var activate = PluginRuntime.GetOptionalBool(parameters, "activate") ?? true;

    return CivilExecution.ExecuteInApplicationContextAsync<object?>(() =>
    {
      var previous = App.DocumentManager.MdiActiveDocument;
      var warnings = new List<string>();
      var open = Documents().FirstOrDefault(d => PathOf(d) is { } p && string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
      if (open != null)
      {
        // Need: never a second copy of the same file (AutoCAD would open it read-only or ask).
        if (open.IsReadOnly != readOnly)
          warnings.Add($"'{open.Name}' is already open {(open.IsReadOnly ? "read-only" : "for editing")} and was not opened again; close it and open it again to change that.");
        if (activate && !Activate(open))
          warnings.Add($"Civil 3D did not report '{open.Name}' as active; check documents_list before the next call.");
        return Opened(open, previous, alreadyOpen: true, warnings);
      }

      if (!readOnly && FileBoundary.WriteAccessProblem(path) is { } problem)
        throw new JsonRpcDispatchException("CIVIL3D.CONFLICT", $"'{path}' cannot be opened for editing: {problem}. Open it with readOnly: true, or free it first. Nothing was opened.");

      Document opened;
      try
      {
        opened = App.DocumentManager.Open(path, readOnly);
      }
      catch (Autodesk.AutoCAD.Runtime.Exception ex)
      {
        throw new JsonRpcDispatchException("CIVIL3D.FILE_IO_ERROR", $"Civil 3D could not open '{path}': {ex.ErrorStatus}. Nothing was opened.");
      }

      PluginLog.Info("Documents", $"documentOpen: '{opened.Name}' opened{(readOnly ? " read-only" : "")}.");
      if (activate)
      {
        if (!Activate(opened)) warnings.Add($"Civil 3D did not report '{opened.Name}' as active; check documents_list before the next call.");
      }
      else if (previous != null && !Same(App.DocumentManager.MdiActiveDocument, previous) && !Activate(previous))
      {
        warnings.Add($"'{previous.Name}' could not be made active again; '{App.DocumentManager.MdiActiveDocument?.Name}' is active.");
      }
      return Opened(opened, previous, alreadyOpen: false, warnings);
    }, "documentOpen", Timeout);
  }

  /// <summary>
  /// C127 (2026-10-13): a DXF file to a DWG file through a side database (C132: coordinateSystem, geoX, geoY, title,
  /// comments give the DWG a georeference and drawing properties), nothing opened in the editor. documentOpen
  /// takes only .dwg/.dwt; in one project the existing VA was built as DXF from the municipality's GDB (no VA map extract for
  /// the area) and a downstream script reads a .dwg. The DXF lies in the import or export roots, the DWG in the export roots.
  /// </summary>
  public static Task<object?> DxfToDwgAsync(JsonObject? parameters)
  {
    var source = FileBoundary.ResolveLocalReadPath(PluginRuntime.GetRequiredString(parameters, "path"), ".dxf");
    var overwrite = PluginRuntime.GetOptionalBool(parameters, "overwrite") ?? false;
    var output = FileBoundary.ResolveLocalExportPath(
      PluginRuntime.GetOptionalString(parameters, "output") ?? Path.ChangeExtension(source, ".dwg"), overwrite, ".dwg");
    var geoCs = PluginRuntime.GetOptionalString(parameters, "coordinateSystem");     // C132, e.g. SWEREF-99-13-30
    var geoX = PluginRuntime.GetOptionalDouble(parameters, "geoX");
    var geoY = PluginRuntime.GetOptionalDouble(parameters, "geoY");
    var geoNote = PluginRuntime.GetOptionalString(parameters, "comments");
    var geoTitle = PluginRuntime.GetOptionalString(parameters, "title");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var log = Path.Combine(Path.GetTempPath(), "Civil3DMcp", "dxfin.log");
      Directory.CreateDirectory(Path.GetDirectoryName(log)!);
      using var side = new Database(false, true);
      side.DxfIn(source, log);
      var counts = new Dictionary<string, int>();
      using (var t = side.TransactionManager.StartTransaction())
      {
        var space = (BlockTableRecord)t.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(side), OpenMode.ForRead);
        foreach (ObjectId id in space)
          counts[id.ObjectClass.DxfName] = counts.TryGetValue(id.ObjectClass.DxfName, out var n) ? n + 1 : 1;
        t.Commit();
      }
      Dictionary<string, object?>? geo = null;
      if (geoCs != null)
      {
        // C132: georeference for a drawing handed out without the company template: GeoLocationData in grid
        // coordinates (design point = reference point, scale 1, north = +Y), INSUNITS meters, and a note in the
        // drawing properties (description: coordinate system and height system).
        side.Insunits = UnitsValue.Meters;
        var previous = HostApplicationServices.WorkingDatabase;
        HostApplicationServices.WorkingDatabase = side;
        try
        {
          // PostToDb leaves the object open for write; disposing it closes it (inside a transaction SaveAs failed
          // with eWasOpenForWrite)
          var p = new Autodesk.AutoCAD.Geometry.Point3d(geoX ?? 0, geoY ?? 0, 0);
          using var data = new GeoLocationData { BlockTableRecordId = SymbolUtilityServices.GetBlockModelSpaceId(side) };
          data.PostToDb();
          data.CoordinateSystem = geoCs;
          data.TypeOfCoordinates = TypeOfCoordinates.CoordinateTypeGrid;
          data.HorizontalUnits = UnitsValue.Meters;
          data.VerticalUnits = UnitsValue.Meters;
          data.DesignPoint = p;
          data.ReferencePoint = p;
          data.ScaleFactor = 1.0;
          data.NorthDirectionVector = Autodesk.AutoCAD.Geometry.Vector2d.YAxis;
          geo = new() { ["coordinateSystem"] = data.CoordinateSystem, ["point"] = new[] { p.X, p.Y } };
        }
        finally { HostApplicationServices.WorkingDatabase = previous; }
        if (geoNote != null || geoTitle != null)
        {
          var info = new DatabaseSummaryInfoBuilder(side.SummaryInfo);
          if (geoTitle != null) info.Title = geoTitle;
          if (geoNote != null) info.Comments = geoNote;
          side.SummaryInfo = info.ToDatabaseSummaryInfo();
        }
      }
      side.SaveAs(output, DwgVersion.Current);
      PluginLog.Info("Documents", $"dxfToDwg: '{source}' saved as '{output}'.");
      return new Dictionary<string, object?> { ["source"] = source, ["output"] = output, ["bytes"] = new FileInfo(output).Length, ["modelSpace"] = counts, ["geoLocation"] = geo };
    });
  }

  public static Task<object?> ActivateDocumentAsync(JsonObject? parameters)
  {
    var (name, path) = Target(parameters, "activated");
    return CivilExecution.ExecuteInApplicationContextAsync<object?>(() =>
    {
      var doc = Find(name, path, "activated");
      if (!App.DocumentManager.DocumentActivationEnabled && !Same(App.DocumentManager.MdiActiveDocument, doc))
        throw new JsonRpcDispatchException("CIVIL3D.CONFLICT", $"Document activation is turned off in Civil 3D (DocumentActivationEnabled is false). Nothing was activated.");
      var wasActive = Same(App.DocumentManager.MdiActiveDocument, doc);
      var warnings = new List<string>();
      if (!Activate(doc)) warnings.Add($"Civil 3D did not report '{doc.Name}' as active; check documents_list before the next call.");
      else if (!wasActive) PluginLog.Info("Documents", $"documentActivate: '{doc.Name}' is active.");
      var active = App.DocumentManager.MdiActiveDocument;
      var result = Describe(doc, active);
      result["wasActive"] = wasActive;
      result["activeDocument"] = active?.Name;
      result["warnings"] = warnings;
      return result;
    }, "documentActivate", Timeout);
  }

  public static Task<object?> CloseDocumentAsync(JsonObject? parameters)
  {
    var (name, path) = Target(parameters, "closed");
    var save = PluginRuntime.GetOptionalBool(parameters, "save") ?? false;
    var discard = PluginRuntime.GetOptionalBool(parameters, "discard") ?? false;
    if (save && discard)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Give save: true or discard: true, not both. Nothing was closed.");

    string? closing = null;                            // the document name, for a close that is not run
    return CivilExecution.ExecuteThenAfterAnswerAsync<object?>(() =>
    {
      var doc = Find(name, path, "closed");
      var documents = Documents();
      if (documents.Count <= 1)
        throw new JsonRpcDispatchException("CIVIL3D.CONFLICT", $"'{doc.Name}' is the only open drawing; Civil 3D would be left without a drawing and the plugin without a command context. Open or create another drawing first. Nothing was closed.");
      if (!string.IsNullOrEmpty(doc.CommandInProgress))
        throw new JsonRpcDispatchException("CIVIL3D.BUSY", $"'{doc.Name}' is running the command {doc.CommandInProgress}; end it first. Nothing was closed.");

      var unsaved = HasUnsavedChanges(doc);
      if (save)
      {
        if (doc.IsReadOnly)
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"'{doc.Name}' is open read-only and cannot be saved; close it with discard: true. Nothing was closed.");
        if (PathOf(doc) == null)
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"'{doc.Name}' has never been saved; save it first with civil3d_drawing save (saveAs). Nothing was closed.");
      }
      else if (unsaved != false && !discard)
      {
        throw new JsonRpcDispatchException("CIVIL3D.CONFLICT",
          $"'{doc.Name}' {(unsaved == true ? "has unsaved changes" : "may have unsaved changes (Civil 3D did not tell)")}; give save: true to save them or discard: true to drop them. Nothing was closed.");
      }

      var savedTo = save ? SaveOwnFile(doc) : null;
      var warnings = new List<string>();
      var wasActive = Same(App.DocumentManager.MdiActiveDocument, doc);
      if (wasActive)
      {
        // Need: switch away first, so the close only ever touches a document no request is running in.
        var next = documents.First(d => !Same(d, doc));
        if (!Activate(next)) warnings.Add($"'{next.Name}' could not be made active first; Civil 3D picks the next active drawing when '{doc.Name}' closes.");
      }

      var closingName = closing = doc.Name;
      var result = new Dictionary<string, object?>
      {
        ["name"] = closingName,
        ["path"] = PathOf(doc),
        ["closing"] = true,
        ["saved"] = savedTo != null,
        ["savedTo"] = savedTo,
        ["unsavedChanges"] = unsaved,
        ["discardedChanges"] = savedTo == null ? unsaved : false,   // null: Civil 3D did not tell
        ["wasActive"] = wasActive,
        ["activeDocument"] = App.DocumentManager.MdiActiveDocument?.Name,
        ["openDocuments"] = documents.Where(d => !Same(d, doc)).Select(d => d.Name).ToList(),
        ["note"] = "The drawing closes in Civil 3D right after this answer has been written; the next request runs after that (documents_list shows it gone).",
        ["warnings"] = warnings,
      };

      void CloseAfterAnswer()
      {
        var still = Documents().FirstOrDefault(d => Same(d, doc));
        if (still == null)
        {
          NoteClose(closingName, "alreadyClosed");
          PluginLog.Info("Documents", $"documentClose: '{closingName}' was already closed.");
          return;
        }
        try
        {
          still.CloseAndDiscard();                   // saved above when save was asked for
        }
        catch (System.Exception ex)
        {
          NoteClose(closingName, "notClosed", ex.Message);
          throw;
        }
        lock (SavedSync) SavedFiles.Remove(closingName);
        NoteClose(closingName, "closed");
        PluginLog.Info("Documents", $"documentClose: '{closingName}' closed.");
      }

      return ((object?)result, CloseAfterAnswer);
    }, "documentClose", Timeout,
      whenNotRun: reason => NoteClose(closing ?? name ?? path ?? "?", "notClosed", reason),
      whenScheduled: () => NoteClose(closing ?? name ?? path ?? "?", "scheduled"));
  }

  internal static List<Document> Documents()
  {
    var documents = new List<Document>();
    foreach (Document document in App.DocumentManager) documents.Add(document);
    return documents;
  }

  /// <summary>Same document (wrappers compared by their AutoCAD object); false for null or a closed document.</summary>
  internal static bool Same(Document? a, Document? b)
  {
    if (a == null || b == null) return false;
    if (ReferenceEquals(a, b)) return true;
    try
    {
      return a.UnmanagedObject != IntPtr.Zero && a.UnmanagedObject == b.UnmanagedObject;
    }
    catch (System.Exception)
    {
      return false;
    }
  }

  /// <summary>The drawing file, null while the drawing has never been saved (its Database.Filename may then name the
  /// template it was made from, which can be open beside it).</summary>
  internal static string? PathOf(Document doc)
  {
    var file = doc.Database.Filename;
    if (string.IsNullOrWhiteSpace(file)) return null;
    var full = Path.GetFullPath(file);
    if (doc.IsNamedDrawing) return full;
    lock (SavedSync) return SavedFiles.TryGetValue(doc.Name, out var saved) && string.Equals(saved, full, StringComparison.OrdinalIgnoreCase) ? full : null;
  }

  private static Dictionary<string, object?>? LastClose()
  {
    lock (SavedSync) return _lastClose == null ? null : new Dictionary<string, object?>(_lastClose);
  }

  /// <summary>Makes the document active (application context); true when Civil 3D reports it active afterwards, false
  /// also when document activation is turned off (DocumentActivationEnabled).</summary>
  internal static bool Activate(Document doc)
  {
    var manager = App.DocumentManager;
    if (Same(manager.MdiActiveDocument, doc)) return true;
    if (!manager.DocumentActivationEnabled) return false;
    manager.MdiActiveDocument = doc;
    return Same(manager.MdiActiveDocument, doc);
  }

  private static Dictionary<string, object?> Describe(Document doc, Document? active) => new()
  {
    ["name"] = doc.Name,
    ["fileName"] = Path.GetFileName(doc.Name),
    ["path"] = PathOf(doc),
    ["active"] = Same(doc, active),
    ["unsavedChanges"] = HasUnsavedChanges(doc),
    ["readOnly"] = doc.IsReadOnly,
    ["commandInProgress"] = string.IsNullOrEmpty(doc.CommandInProgress) ? null : doc.CommandInProgress,
  };

  private static Dictionary<string, object?> Opened(Document doc, Document? previous, bool alreadyOpen, List<string> warnings)
  {
    var active = App.DocumentManager.MdiActiveDocument;
    var result = Describe(doc, active);
    result["alreadyOpen"] = alreadyOpen;
    result["activeDocument"] = active?.Name;
    result["previousDocument"] = previous?.Name;
    result["warnings"] = warnings;
    return result;
  }

  /// <summary>Unsaved changes of any open document: DBMOD can be read only for the active one, so the COM document
  /// tells (AcadDocument.Saved); null when neither is available.</summary>
  internal static bool? HasUnsavedChanges(Document doc)
  {
    object? acadDocument = null;
    try
    {
      acadDocument = doc.GetAcadDocument();
    }
    catch (System.Exception)
    {
      // no COM document; DBMOD below for the active one
    }

    if (Civil3DCompatibility.GetComProperty(acadDocument, "Saved") is bool saved) return !saved;
    if (Same(doc, App.DocumentManager.MdiActiveDocument))
      return Convert.ToInt32(App.GetSystemVariable("DBMOD") ?? 0) != 0;
    return null;
  }

  /// <summary>Saves the document over its own file (as saveDrawing without saveAs). Application context: the
  /// document is locked for the save.</summary>
  private static string SaveOwnFile(Document doc)
  {
    var file = PathOf(doc)!;
    try
    {
      using (doc.LockDocument())
      {
        var database = doc.Database;
        database.CloseInput(true);                  // C71: read everything and release the source file first
        database.SaveAs(file, true, DwgVersion.Current, database.SecurityParameters);
      }
    }
    catch (Autodesk.AutoCAD.Runtime.Exception ex)
    {
      throw new JsonRpcDispatchException("CIVIL3D.FILE_IO_ERROR",
        $"Saving '{doc.Name}' failed ({ex.ErrorStatus}); nothing was closed. A drawing made from the original company template fails its first save once (C73); try again.");
    }

    PluginLog.Info("Documents", $"documentClose: '{doc.Name}' saved to {file}.");
    return file;
  }

  private static (string? Name, string? Path) Target(JsonObject? parameters, string verb)
  {
    var name = PluginRuntime.GetOptionalString(parameters, "name");
    var path = PluginRuntime.GetOptionalString(parameters, "path");
    if (string.IsNullOrWhiteSpace(name) == string.IsNullOrWhiteSpace(path))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Give the document to be {verb} by name or by path (one of them). Nothing was changed.");
    if (path == null) return (name!.Trim(), null);
    if (!System.IO.Path.IsPathFullyQualified(path))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"path must be absolute: {path}. Nothing was changed.");
    return (null, System.IO.Path.GetFullPath(path));
  }

  /// <summary>An open document by its path, or by its name: the full document name, the file name (Template.dwt) or
  /// the file name without extension.</summary>
  private static Document Find(string? name, string? path, string verb)
  {
    var documents = Documents();
    List<Document> matches;
    if (path != null)
    {
      matches = documents.Where(d => PathOf(d) is { } p && string.Equals(p, path, StringComparison.OrdinalIgnoreCase)).ToList();
    }
    else
    {
      matches = documents.Where(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
      if (matches.Count == 0)
        matches = documents.Where(d => string.Equals(Path.GetFileName(d.Name), name, StringComparison.OrdinalIgnoreCase)).ToList();
      if (matches.Count == 0)
        matches = documents.Where(d => string.Equals(Path.GetFileNameWithoutExtension(d.Name), name, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    if (matches.Count == 1) return matches[0];
    var open = string.Join(", ", documents.Select(d => $"'{d.Name}'"));
    if (matches.Count == 0)
      throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"No open drawing matches '{path ?? name}'. Open drawings: {open}. Nothing was {verb}.");
    throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
      $"'{name}' matches {matches.Count} open drawings ({string.Join(", ", matches.Select(d => $"'{d.Name}'"))}); give the full name or the path. Nothing was {verb}.");
  }
}

/// <summary>
/// C93 review: the active drawing and the number of open drawings for civil3d_health, which answers on the TCP thread
/// without the host gate. Read on the main thread when documents are created, activated or destroyed (and when a
/// call runs, CivilExecution.ActiveDrawing), so health never touches a document that may be closing.
/// </summary>
internal static class DocumentSnapshot
{
  private sealed record Value(string? ActiveDocument, int DocumentCount);

  private static Value _value = new(null, 0);
  private static DocumentCollectionEventHandler? _changed;
  private static DocumentDestroyedEventHandler? _destroyed;

  internal static (string? ActiveDocument, int DocumentCount) Current
  {
    get
    {
      var value = Volatile.Read(ref _value);
      return (value.ActiveDocument, value.DocumentCount);
    }
  }

  /// <summary>Attaches the document events and takes a first snapshot; call on the main thread.</summary>
  internal static void Start()
  {
    if (_changed != null) return;
    _changed = (_, _) => Refresh();
    _destroyed = (_, _) => Refresh();
    App.DocumentManager.DocumentCreated += _changed;
    App.DocumentManager.DocumentActivated += _changed;
    App.DocumentManager.DocumentDestroyed += _destroyed;
    Refresh();
  }

  /// <summary>Detaches the events (plugin reload, C70).</summary>
  internal static void Stop()
  {
    if (_changed != null)
    {
      App.DocumentManager.DocumentCreated -= _changed;
      App.DocumentManager.DocumentActivated -= _changed;
    }
    if (_destroyed != null) App.DocumentManager.DocumentDestroyed -= _destroyed;
    _changed = null;
    _destroyed = null;
  }

  /// <summary>Reads the active drawing and the count; main thread only.</summary>
  internal static void Refresh()
  {
    try
    {
      var manager = App.DocumentManager;
      Volatile.Write(ref _value, new Value(manager.MdiActiveDocument?.Name, manager.Count));
    }
    catch (System.Exception)
    {
      // a document is half created or destroyed; the next event refreshes
    }
  }
}
