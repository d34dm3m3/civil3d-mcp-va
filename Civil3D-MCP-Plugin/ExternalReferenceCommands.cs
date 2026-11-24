using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace Civil3DMcpPlugin;

/// <summary>
/// External references of the active drawing (C62, 2026-10-08): xrefs, raster images, point clouds (ReCap) and
/// PDF/DWF/DGN underlays with their saved and resolved paths, and re-pathing one of them, typically to a path
/// relative to the drawing's folder (".\Grunddata\Ortofoto\x.jpg") so a project folder can be moved as a whole.
/// </summary>
public static class ExternalReferenceCommands
{
  private static readonly string[] UnderlayDictionaries = { "ACAD_PDFDEFINITIONS", "ACAD_DWFDEFINITIONS", "ACAD_DGNDEFINITIONS" };
  private const string PointCloudDictionary = "ACAD_POINTCLOUD_EX_DICT";

  public static Task<object?> ListExternalReferencesAsync(JsonObject? parameters) =>
    CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      // C93 review: a drawing never saved has no folder; only absolute paths resolve then.
      var folder = DocumentCommands.PathOf(doc) is { } file ? Path.GetDirectoryName(file) : null;
      var items = new List<Dictionary<string, object?>>();
      foreach (var (id, kind, name) in Enumerate(database, transaction))
      {
        var obj = transaction.GetObject(id, OpenMode.ForRead);
        var source = SourcePath(obj);
        items.Add(Describe(obj, kind, name, source, folder));
      }

      return new Dictionary<string, object?> { ["drawingFolder"] = folder, ["references"] = items };
    });

  /// <summary>Sets the saved path of one reference (by handle) to path (relative paths are relative to the
  /// drawing's folder), or with makeRelative to the relative form of its current resolved file. Reloads it.</summary>
  public static Task<object?> SetExternalReferencePathAsync(JsonObject? parameters)
  {
    var handleText = PluginRuntime.GetRequiredString(parameters, "handle");
    var newPath = PluginRuntime.GetOptionalString(parameters, "path");
    var makeRelative = PluginRuntime.GetOptionalBool(parameters, "makeRelative") ?? false;
    var requireExisting = PluginRuntime.GetOptionalBool(parameters, "requireExisting") ?? true;
    if ((newPath == null) == !makeRelative)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Give either path or makeRelative=true. Nothing was changed.");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var folder = DrawingFolder(doc);
      var found = Enumerate(database, transaction).FirstOrDefault(e =>
        string.Equals(e.Id.Handle.ToString(), handleText, StringComparison.OrdinalIgnoreCase));
      if (found.Id.IsNull)
        throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"No external reference with handle {handleText}. Nothing was changed.");
      var obj = transaction.GetObject(found.Id, OpenMode.ForWrite);
      var before = SourcePath(obj);
      string target;
      if (makeRelative)
      {
        var absolute = Resolve(ActivePath(obj) ?? before, folder);
        if (absolute == null || !File.Exists(absolute))
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"The current file '{before}' is not found; give path instead. Nothing was changed.");
        target = Relative(folder, absolute);
      }
      else
      {
        target = newPath!;
      }

      var resolved = Resolve(target, folder);
      if (requireExisting && (resolved == null || !File.Exists(resolved)))
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"'{target}' does not resolve to an existing file from '{folder}'. Nothing was changed.");

      switch (obj)
      {
        case RasterImageDef image:
          image.SourceFileName = target;
          if (resolved != null && File.Exists(resolved)) image.Load();
          break;
        case PointCloudDefEx cloud:
          cloud.unload();
          cloud.SourceFileName = target;
          if (resolved != null && File.Exists(resolved)) cloud.load();
          break;
        case UnderlayDefinition underlay:
          underlay.SourceFileName = target;
          break;
        case BlockTableRecord xref:
          xref.PathName = target;
          break;
        default:
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{obj.GetType().Name} is not a supported reference. Nothing was changed.");
      }

      if (obj is BlockTableRecord)
        database.ReloadXrefs(new ObjectIdCollection { found.Id });
      return new Dictionary<string, object?>
      {
        ["handle"] = found.Id.Handle.ToString(),
        ["kind"] = found.Kind,
        ["name"] = found.Name,
        ["before"] = before,
        ["after"] = target,
        ["resolved"] = resolved,
        ["exists"] = resolved != null && File.Exists(resolved),
      };
    });
  }

  /// <summary>
  /// C95 (2026-10-10): the drawing references (xrefs) of the active drawing: name, saved path, the file AutoCAD found,
  /// status (loaded, unloaded, unresolved, unreferenced), overlay or attach, and how many references the drawing has.
  /// </summary>
  public static Task<object?> ListXrefsAsync(JsonObject? parameters) =>
    CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var host = DocumentCommands.PathOf(doc);
      var folder = host == null ? null : Path.GetDirectoryName(host);
      var xrefs = new List<Dictionary<string, object?>>();
      var table = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
      foreach (ObjectId id in table)
      {
        var record = (BlockTableRecord)transaction.GetObject(id, OpenMode.ForRead);
        if (!record.IsFromExternalReference && !record.IsFromOverlayReference) continue;
        string? found = null;
        if (record.XrefStatus == XrefStatus.Resolved)
        {
          try
          {
            found = record.GetXrefDatabase(false)?.Filename;
          }
          catch (Autodesk.AutoCAD.Runtime.Exception)
          {
            // not available; the saved path is resolved below
          }
        }

        var resolved = string.IsNullOrWhiteSpace(found) ? ResolveFrom(record.PathName, folder) : Path.GetFullPath(found);
        xrefs.Add(new Dictionary<string, object?>
        {
          ["name"] = record.Name,
          ["handle"] = record.Handle.ToString(),
          ["path"] = record.PathName,
          ["relative"] = !string.IsNullOrWhiteSpace(record.PathName) && !Path.IsPathRooted(record.PathName),
          ["resolvedPath"] = resolved,
          ["exists"] = resolved != null && File.Exists(resolved),
          ["status"] = StatusName(record.XrefStatus),
          ["xrefStatus"] = record.XrefStatus.ToString(),
          ["overlay"] = record.IsFromOverlayReference,
          ["references"] = record.GetBlockReferenceIds(true, false).Count,
        });
      }

      return new Dictionary<string, object?>
      {
        ["drawing"] = doc.Name,
        ["drawingFolder"] = folder,
        ["count"] = xrefs.Count,
        ["xrefs"] = xrefs,
      };
    });

  /// <summary>
  /// C95 (2026-10-10): attaches a local drawing as an external reference in model space of the active drawing
  /// (Database.OverlayXref, or AttachXref with overlay false, and a block reference). Use case: a pilot project gets
  /// the transformed base map (C94) and a drawing of the existing VA as xrefs in the project drawing. The path is
  /// stored relative to the drawing's folder when it can be (relativePath, default true): not for a drawing never saved
  /// or a file on another drive, then absolute with a warning. The block name, the layer name and the file are checked,
  /// and a name already in the drawing or a file already referenced is refused, before anything is attached.
  /// </summary>
  public static Task<object?> AttachXrefAsync(JsonObject? parameters)
  {
    var path = FileBoundary.ResolveLocalReadPath(PluginRuntime.GetRequiredString(parameters, "path"), ".dwg");
    var name = (PluginRuntime.GetOptionalString(parameters, "name") ?? Path.GetFileNameWithoutExtension(path)).Trim();
    var overlay = PluginRuntime.GetOptionalBool(parameters, "overlay") ?? true;
    var x = PluginRuntime.GetOptionalDouble(parameters, "x") ?? 0;
    var y = PluginRuntime.GetOptionalDouble(parameters, "y") ?? 0;
    var z = PluginRuntime.GetOptionalDouble(parameters, "z") ?? 0;
    var scale = PluginRuntime.GetOptionalDouble(parameters, "scale") ?? 1;
    var rotationDegrees = PluginRuntime.GetOptionalDouble(parameters, "rotationDegrees") ?? 0;
    var layer = (PluginRuntime.GetOptionalString(parameters, "layer") ?? "XREF").Trim();
    var relativePath = PluginRuntime.GetOptionalBool(parameters, "relativePath") ?? true;
    if (!new[] { x, y, z, rotationDegrees }.All(double.IsFinite))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "x, y, z and rotationDegrees must be finite numbers. Nothing was attached.");
    if (!double.IsFinite(scale) || scale <= 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "scale must be a positive number. Nothing was attached.");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      ValidateName(name, "name");                     // AutoCAD API: on the main thread
      ValidateName(layer, "layer");
      var host = DocumentCommands.PathOf(doc);
      if (host != null && SamePath(host, path))
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "A drawing cannot reference itself. Nothing was attached.");
      var folder = host == null ? null : Path.GetDirectoryName(host);

      // Need: names and existing references read through an open/close transaction, so the block table is closed
      // again before AttachXref/OverlayXref change it. The same file is looked for before the name, so attaching it
      // again under its default name says it is referenced already.
      using (var check = database.TransactionManager.StartOpenCloseTransaction())
      {
        var table = (BlockTable)check.GetObject(database.BlockTableId, OpenMode.ForRead);
        foreach (ObjectId id in table)
        {
          var record = (BlockTableRecord)check.GetObject(id, OpenMode.ForRead);
          if ((record.IsFromExternalReference || record.IsFromOverlayReference) && ResolveFrom(record.PathName, folder) is { } existing && SamePath(existing, path))
            throw new JsonRpcDispatchException("CIVIL3D.CONFLICT", $"'{path}' is already referenced as '{record.Name}'. Nothing was attached.");
        }
        if (table.Has(name))
          throw new JsonRpcDispatchException("CIVIL3D.CONFLICT", $"'{name}' is already a block or an xref in the drawing (xrefs_list); give another name. Nothing was attached.");
        check.Commit();
      }

      var warnings = new List<string>();
      var stored = path;
      if (relativePath)
      {
        if (folder == null)
          warnings.Add("The drawing has never been saved, so the path is stored absolute; after saving, external_reference_set_path with makeRelative makes it relative.");
        else if (!string.Equals(Path.GetPathRoot(folder), Path.GetPathRoot(path), StringComparison.OrdinalIgnoreCase))
          warnings.Add("The file is on another drive than the drawing, so the path is stored absolute.");
        else
          stored = Relative(folder, path);
      }

      ObjectId xrefId;
      try
      {
        xrefId = overlay ? database.OverlayXref(path, name) : database.AttachXref(path, name);
      }
      catch (Autodesk.AutoCAD.Runtime.Exception ex)
      {
        throw new JsonRpcDispatchException("CIVIL3D.FILE_IO_ERROR", $"Civil 3D could not reference '{path}': {ex.ErrorStatus}. Nothing was attached.");
      }

      BlockTableRecord definition;
      BlockReference reference;
      try
      {
        // The layer table is opened only now: attaching adds the reference's own layers (name|layer) to it.
        var layerId = LookupUtils.GetOrCreateLayerId(database, transaction, layer, true);
        definition = (BlockTableRecord)transaction.GetObject(xrefId, OpenMode.ForWrite);
        if (!string.Equals(stored, path, StringComparison.Ordinal)) definition.PathName = stored;
        var space = (BlockTableRecord)transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(database), OpenMode.ForWrite);
        reference = new BlockReference(new Point3d(x, y, z), xrefId)
        {
          ScaleFactors = new Scale3d(scale),
          Rotation = rotationDegrees * Math.PI / 180.0,
        };
        reference.SetDatabaseDefaults(database);
        reference.LayerId = layerId;
        space.AppendEntity(reference);
        transaction.AddNewlyCreatedDBObject(reference, true);
      }
      catch (System.Exception)
      {
        // Review: attaching may not be undone by the aborted transaction; the reference is detached, best effort.
        try
        {
          database.DetachXref(xrefId);
        }
        catch (System.Exception detach)
        {
          PluginLog.Warn("Xref", $"xrefAttach: '{name}' could not be detached after a failure: {detach.Message}");
        }
        throw;
      }

      if (definition.XrefStatus != XrefStatus.Resolved)
        warnings.Add($"The xref status is {definition.XrefStatus}; check it with xrefs_list.");
      // Review: the API attaches at scale 1 whatever the units; XATTACH would scale by INSUNITS.
      try
      {
        var units = definition.GetXrefDatabase(false)?.Insunits;
        if (units is { } xrefUnits && xrefUnits != UnitsValue.Undefined && database.Insunits != UnitsValue.Undefined && xrefUnits != database.Insunits)
          warnings.Add($"The referenced drawing is in {xrefUnits} and this drawing in {database.Insunits}; it was attached at scale {scale} without unit scaling.");
      }
      catch (Autodesk.AutoCAD.Runtime.Exception)
      {
        // units not available
      }

      return new Dictionary<string, object?>
      {
        ["name"] = definition.Name,
        ["handle"] = reference.Handle.ToString(),
        ["definitionHandle"] = definition.Handle.ToString(),
        ["overlay"] = overlay,
        ["path"] = stored,
        ["relative"] = !string.Equals(stored, path, StringComparison.Ordinal),
        ["resolvedPath"] = path,
        ["status"] = StatusName(definition.XrefStatus),
        ["layer"] = reference.Layer,
        ["position"] = new[] { x, y, z },
        ["scale"] = scale,
        ["rotationDegrees"] = rotationDegrees,
        ["warnings"] = warnings,
      };
    });
  }

  /// <summary>The same file by path or, when both exist, by file identity (a short 8.3 name, a hard link).</summary>
  private static bool SamePath(string first, string second) =>
    string.Equals(first, second, StringComparison.OrdinalIgnoreCase)
    || (File.Exists(first) && File.Exists(second) && FileBoundary.SameFile(first, second));

  private static void ValidateName(string name, string parameter)
  {
    try
    {
      SymbolUtilityServices.ValidateSymbolName(name, false);
    }
    catch (Autodesk.AutoCAD.Runtime.Exception)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{parameter} '{name}' is not a valid name (none of < > / \\ \" : ; ? * | , = `). Nothing was attached.");
    }
  }

  /// <summary>
  /// C97 (2026-10-10): detaches a drawing reference of the active drawing (Database.DetachXref, which erases its
  /// insertions, its definition and its dependent layers). Refused before anything changes when it is nested in another
  /// reference (detach that one), inserted inside a block definition (remove those insertions first) or inserted on a
  /// locked layer (unlock it first; a frozen layer is fine). Need: the pilot's overlay Bef_VA has been replaced by drawn
  /// BK_Bef_* objects and is to go.
  /// </summary>
  public static Task<object?> DetachXrefAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name").Trim();
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      // Need: everything read through an open/close transaction, so nothing of the reference is held open while
      // DetachXref erases it.
      ObjectId xrefId;
      string xrefName, path;
      List<ObjectId> inserts;
      var dependentLayers = 0;
      using (var check = database.TransactionManager.StartOpenCloseTransaction())
      {
        var record = FindXref(database, check, name, "detached");
        (xrefId, xrefName, path) = (record.ObjectId, record.Name, record.PathName);
        inserts = record.GetBlockReferenceIds(true, false).Cast<ObjectId>().Where(id => !id.IsErased).ToList();
        var inBlocks = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var locked = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in inserts)
        {
          var insert = (BlockReference)check.GetObject(id, OpenMode.ForRead);
          var owner = (BlockTableRecord)check.GetObject(insert.OwnerId, OpenMode.ForRead);
          if (!owner.IsLayout) inBlocks.Add(owner.Name);
          if (((LayerTableRecord)check.GetObject(insert.LayerId, OpenMode.ForRead)).IsLocked) locked.Add(insert.Layer);
        }
        if (inBlocks.Count > 0)
          throw new JsonRpcDispatchException("CIVIL3D.CONFLICT", $"'{xrefName}' is also inserted in block definitions ({string.Join(", ", inBlocks)}); remove those insertions first. Nothing was changed.");
        if (locked.Count > 0)
          throw new JsonRpcDispatchException("CIVIL3D.CONFLICT", $"'{xrefName}' is inserted on locked layer(s) {string.Join(", ", locked)}; unlock them first. Nothing was changed.");

        var prefix = xrefName + "|";
        foreach (ObjectId id in (LayerTable)check.GetObject(database.LayerTableId, OpenMode.ForRead))
          if (((LayerTableRecord)check.GetObject(id, OpenMode.ForRead)).Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) dependentLayers++;
        check.Commit();
      }

      try
      {
        database.DetachXref(xrefId);
      }
      catch (Autodesk.AutoCAD.Runtime.Exception ex)
      {
        // The transaction is aborted.
        throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"Civil 3D could not detach '{xrefName}': {ex.ErrorStatus}. Nothing was changed.");
      }

      // Should an insertion have outlived the detach, it is erased here (it would refer to an erased definition).
      var leftOver = inserts.Where(id => !id.IsErased).ToList();
      foreach (var id in leftOver)
        transaction.GetObject(id, OpenMode.ForWrite).Erase();

      PluginLog.Info("Xref", $"xrefDetach: '{xrefName}' detached, {inserts.Count} insertion(s) erased.");
      return new Dictionary<string, object?>
      {
        ["name"] = xrefName,
        ["path"] = path,
        ["detached"] = true,
        ["insertionsErased"] = inserts.Count,
        ["insertionHandles"] = inserts.Select(id => id.Handle.ToString()).ToList(),
        ["erasedAfterDetach"] = leftOver.Count,
        ["dependentLayers"] = dependentLayers,
      };
    });
  }

  /// <summary>C97: unloads a drawing reference (kept in the drawing, not shown or loaded until reloaded).</summary>
  public static Task<object?> UnloadXrefAsync(JsonObject? parameters) => LoadXref(parameters, unload: true);

  /// <summary>C97: reloads a drawing reference from its file (after an unload or a change of the referenced drawing).</summary>
  public static Task<object?> ReloadXrefAsync(JsonObject? parameters) => LoadXref(parameters, unload: false);

  private static Task<object?> LoadXref(JsonObject? parameters, bool unload)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name").Trim();
    var verb = unload ? "unloaded" : "reloaded";
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var record = FindXref(database, transaction, name, verb);
      var before = record.XrefStatus;
      var overrides = unload ? null : SnapshotXrefLayers(database, transaction, record.Name);
      try
      {
        var ids = new ObjectIdCollection { record.ObjectId };
        if (unload) database.UnloadXrefs(ids);
        else database.ReloadXrefs(ids);
      }
      catch (Autodesk.AutoCAD.Runtime.Exception ex)
      {
        throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"Civil 3D could not {(unload ? "unload" : "reload")} '{record.Name}': {ex.ErrorStatus}. Nothing was changed.");
      }

      var after = ((BlockTableRecord)transaction.GetObject(record.ObjectId, OpenMode.ForRead)).XrefStatus;
      var restored = overrides == null ? new List<string>() : RestoreXrefLayers(database, transaction, overrides);
      return new Dictionary<string, object?>
      {
        ["name"] = record.Name,
        ["path"] = record.PathName,
        ["statusBefore"] = StatusName(before),
        ["status"] = StatusName(after),
        [verb] = true,
        ["layerOverridesRestored"] = restored,
      };
    });
  }

  private sealed record XrefLayerState(string Name, ObjectId Linetype, Autodesk.AutoCAD.Colors.Color Color, LineWeight LineWeight, bool IsPlottable);

  /// <summary>C161 (2026-10-21): the host drawing's settings of a reference's layers ("Name|layer") before a reload.
  /// Reloading put back the referenced drawing's linetype (in one project the linetype override of the xref layers
  /// disappeared at every xrefReload and a script had to set it again).</summary>
  private static List<XrefLayerState> SnapshotXrefLayers(Database database, Transaction transaction, string xrefName)
  {
    var prefix = xrefName + "|";
    var layers = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
    var list = new List<XrefLayerState>();
    foreach (ObjectId id in layers)
    {
      var layer = (LayerTableRecord)transaction.GetObject(id, OpenMode.ForRead);
      if (!layer.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
      list.Add(new XrefLayerState(layer.Name, layer.LinetypeObjectId, layer.Color, layer.LineWeight, layer.IsPlottable));
    }
    return list;
  }

  /// <summary>C161: sets back what the reload changed on the reference's layers (linetype, colour, lineweight, plot);
  /// returns the layers changed back. A linetype that no longer exists is left as the reload set it.</summary>
  private static List<string> RestoreXrefLayers(Database database, Transaction transaction, List<XrefLayerState> states)
  {
    var layers = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
    var restored = new List<string>();
    foreach (var state in states)
    {
      if (!layers.Has(state.Name)) continue;
      var layer = (LayerTableRecord)transaction.GetObject(layers[state.Name], OpenMode.ForRead);
      var linetype = layer.LinetypeObjectId != state.Linetype && !state.Linetype.IsNull && !state.Linetype.IsErased && state.Linetype.IsValid;
      var color = !layer.Color.Equals(state.Color);
      var weight = layer.LineWeight != state.LineWeight;
      var plot = layer.IsPlottable != state.IsPlottable;
      if (!(linetype || color || weight || plot)) continue;
      layer.UpgradeOpen();
      if (linetype) layer.LinetypeObjectId = state.Linetype;
      if (color) layer.Color = state.Color;
      if (weight) layer.LineWeight = state.LineWeight;
      if (plot) layer.IsPlottable = state.IsPlottable;
      restored.Add(state.Name);
    }
    return restored;
  }

  /// <summary>
  /// C128 (2026-10-13): clips the references of drawing references (XCLIP) to a polygon: boundary [[x, y], ...] in
  /// world coordinates or boundaryHandle (a closed polyline); remove:true takes the clip away. All references of each
  /// named xref in model space get the same clip; the boundary is taken into the reference's own coordinates (inverse
  /// block transform). Use case: the base map and the administrative boundaries cover the whole municipality
  /// and a user-drawn rectangle is the project's boundary.
  /// </summary>
  public static Task<object?> ClipXrefAsync(JsonObject? parameters)
  {
    var names = PluginRuntime.GetParameter(parameters, "names") is JsonArray array
      ? array.Select(n => n?.GetValue<string>()?.Trim() ?? "").Where(n => n.Length > 0).ToList()
      : new List<string> { PluginRuntime.GetRequiredString(parameters, "name").Trim() };
    var remove = PluginRuntime.GetOptionalBool(parameters, "remove") ?? false;
    var handleText = PluginRuntime.GetOptionalString(parameters, "boundaryHandle");
    var given = new List<Point2d>();
    if (PluginRuntime.GetParameter(parameters, "boundary") is JsonArray points)
      foreach (var p in points)
        if (p is JsonArray xy && xy.Count >= 2) given.Add(new Point2d(xy[0]!.GetValue<double>(), xy[1]!.GetValue<double>()));
    if (!remove && given.Count == 0 && string.IsNullOrWhiteSpace(handleText))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Give boundary [[x, y], ...], boundaryHandle or remove:true. Nothing was clipped.");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var boundary = given;
      if (!remove && boundary.Count == 0)
      {
        var id = database.GetObjectId(false, CivilObjectUtils.ParseHandle(handleText), 0);
        if (transaction.GetObject(id, OpenMode.ForRead) is not Polyline pl || !pl.Closed)
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"boundaryHandle {handleText} is not a closed polyline. Nothing was clipped.");
        boundary = Enumerable.Range(0, pl.NumberOfVertices).Select(i => pl.GetPoint2dAt(i)).ToList();
      }
      if (!remove && boundary.Count < 3)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "The boundary needs at least three points. Nothing was clipped.");

      var records = names.Select(n => FindXref(database, transaction, n, "clipped")).ToList();
      var done = new List<Dictionary<string, object?>>();
      foreach (var record in records)
      {
        var references = 0;
        foreach (ObjectId refId in record.GetBlockReferenceIds(true, false))
        {
          var reference = (BlockReference)transaction.GetObject(refId, OpenMode.ForWrite);
          if (reference.OwnerId != SymbolUtilityServices.GetBlockModelSpaceId(database)) continue;
          if (reference.ExtensionDictionary.IsNull)
          {
            if (remove) continue;
            reference.CreateExtensionDictionary();
          }
          var extension = (DBDictionary)transaction.GetObject(reference.ExtensionDictionary, OpenMode.ForWrite);
          DBDictionary filters;
          if (extension.Contains("ACAD_FILTER")) filters = (DBDictionary)transaction.GetObject(extension.GetAt("ACAD_FILTER"), OpenMode.ForWrite);
          else if (remove) continue;
          else
          {
            filters = new DBDictionary();
            extension.SetAt("ACAD_FILTER", filters);
            transaction.AddNewlyCreatedDBObject(filters, true);
          }
          if (filters.Contains("SPATIAL"))
          {
            transaction.GetObject(filters.GetAt("SPATIAL"), OpenMode.ForWrite).Erase();
            filters.Remove("SPATIAL");
          }
          if (!remove)
          {
            var inverse = reference.BlockTransform.Inverse();
            var local = new Point2dCollection();
            foreach (var p in boundary)
            {
              var q = new Point3d(p.X, p.Y, 0).TransformBy(inverse);
              local.Add(new Point2d(q.X, q.Y));
            }
            var filter = new Autodesk.AutoCAD.DatabaseServices.Filters.SpatialFilter
            {
              Definition = new Autodesk.AutoCAD.DatabaseServices.Filters.SpatialFilterDefinition(local, Vector3d.ZAxis, 0.0, 0.0, 0.0, true),
            };
            filters.SetAt("SPATIAL", filter);
            transaction.AddNewlyCreatedDBObject(filter, true);
          }
          reference.RecordGraphicsModified(true);
          references++;
        }
        done.Add(new Dictionary<string, object?> { ["name"] = record.Name, ["references"] = references });
      }
      return new Dictionary<string, object?> { ["clipped"] = !remove, ["removed"] = remove, ["xrefs"] = done, ["boundaryPoints"] = remove ? 0 : boundary.Count };
    });
  }

  /// <summary>A drawing reference of the drawing by its block name; refused when it is nested in another reference
  /// (only the reference that holds it can be detached or unloaded).</summary>
  private static BlockTableRecord FindXref(Database database, Transaction transaction, string name, string verb)
  {
    var table = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
    var names = new List<string>();
    BlockTableRecord? found = null;
    foreach (ObjectId id in table)
    {
      var record = (BlockTableRecord)transaction.GetObject(id, OpenMode.ForRead);
      if (!record.IsFromExternalReference && !record.IsFromOverlayReference) continue;
      names.Add(record.Name);
      if (string.Equals(record.Name, name, StringComparison.OrdinalIgnoreCase)) found = record;
    }
    if (found == null)
      throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"No drawing reference named '{name}'. References: {(names.Count == 0 ? "none" : string.Join(", ", names))}. Nothing was {verb}.");

    using var graph = database.GetHostDwgXrefGraph(true);
    if (graph.GetXrefNode(found.ObjectId) is { IsNested: true })
      throw new JsonRpcDispatchException("CIVIL3D.CONFLICT", $"'{found.Name}' is nested in another reference; that reference is the one to change. Nothing was {verb}.");
    return found;
  }

  private static string StatusName(XrefStatus status) => status switch
  {
    XrefStatus.Resolved => "loaded",
    XrefStatus.Unloaded => "unloaded",
    XrefStatus.Unreferenced => "unreferenced",
    XrefStatus.FileNotFound or XrefStatus.Unresolved => "unresolved",
    _ => status.ToString(),
  };

  /// <summary>A saved path as a full path: relative paths from the drawing's folder (null when the drawing has none).</summary>
  private static string? ResolveFrom(string? path, string? folder) =>
    string.IsNullOrWhiteSpace(path) ? null
    : Path.IsPathRooted(path) ? Resolve(path, Path.GetPathRoot(path)!)
    : folder == null ? null
    : Resolve(path, folder);

  private static IEnumerable<(ObjectId Id, string Kind, string Name)> Enumerate(Database database, Transaction transaction)
  {
    var table = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
    foreach (ObjectId id in table)
    {
      var record = (BlockTableRecord)transaction.GetObject(id, OpenMode.ForRead);
      if (record.IsFromExternalReference || record.IsFromOverlayReference)
        yield return (id, record.IsFromOverlayReference ? "overlay" : "xref", record.Name);
    }

    var named = (DBDictionary)transaction.GetObject(database.NamedObjectsDictionaryId, OpenMode.ForRead);
    var imageDictId = RasterImageDef.GetImageDictionary(database);
    if (!imageDictId.IsNull)
      foreach (var entry in (DBDictionary)transaction.GetObject(imageDictId, OpenMode.ForRead))
        yield return (entry.Value, "image", entry.Key);
    if (named.Contains(PointCloudDictionary))
      foreach (var entry in (DBDictionary)transaction.GetObject(named.GetAt(PointCloudDictionary), OpenMode.ForRead))
        yield return (entry.Value, "pointcloud", entry.Key);
    foreach (var key in UnderlayDictionaries)
      if (named.Contains(key))
        foreach (var entry in (DBDictionary)transaction.GetObject(named.GetAt(key), OpenMode.ForRead))
          yield return (entry.Value, "underlay", entry.Key);
  }

  private static string? SourcePath(DBObject obj) => obj switch
  {
    RasterImageDef image => image.SourceFileName,
    PointCloudDefEx cloud => cloud.SourceFileName,
    UnderlayDefinition underlay => underlay.SourceFileName,
    BlockTableRecord xref => xref.PathName,
    _ => null,
  };

  private static string? ActivePath(DBObject obj) => obj switch
  {
    RasterImageDef image => string.IsNullOrWhiteSpace(image.ActiveFileName) ? null : image.ActiveFileName,
    PointCloudDefEx cloud => string.IsNullOrWhiteSpace(cloud.ActiveFileName) ? null : cloud.ActiveFileName,
    _ => null,
  };

  private static Dictionary<string, object?> Describe(DBObject obj, string kind, string name, string? source, string? folder)
  {
    var resolved = ResolveFrom(ActivePath(obj) ?? source, folder);
    return new Dictionary<string, object?>
    {
      ["handle"] = obj.Handle.ToString(),
      ["kind"] = kind,
      ["name"] = name,
      ["savedPath"] = source,
      ["activePath"] = ActivePath(obj),
      ["relative"] = source != null && !Path.IsPathRooted(source),
      ["resolved"] = resolved,
      ["exists"] = resolved != null && File.Exists(resolved),
      ["loaded"] = obj switch { RasterImageDef i => i.IsLoaded, _ => (bool?)null },
    };
  }

  // C93 review: the folder of the drawing's own file; a drawing never saved has none (its Database.Filename names the
  // template it was made from, whose folder relative paths would silently have been taken from).
  private static string DrawingFolder(Autodesk.AutoCAD.ApplicationServices.Document doc) =>
    (DocumentCommands.PathOf(doc) is { } file ? Path.GetDirectoryName(file) : null)
      ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"'{doc.Name}' has no folder of its own yet (save it first). Nothing was changed.");

  private static string? Resolve(string? path, string folder)
  {
    if (string.IsNullOrWhiteSpace(path)) return null;
    try
    {
      return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(folder, path));
    }
    catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
    {
      return null;
    }
  }

  /// <summary>AutoCAD's relative form: ".\sub\file" below the folder, "..\x\file" beside it.</summary>
  public static string Relative(string folder, string absolute)
  {
    var relative = Path.GetRelativePath(folder, absolute);
    if (Path.IsPathRooted(relative))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"'{absolute}' is on another drive than '{folder}'; it cannot be relative.");
    return relative.StartsWith("..", StringComparison.Ordinal) ? relative : "." + Path.DirectorySeparatorChar + relative;
  }
}
