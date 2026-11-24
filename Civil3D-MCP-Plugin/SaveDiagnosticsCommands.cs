using System.Diagnostics;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;

namespace Civil3DMcpPlugin;

/// <summary>
/// Save diagnostics (C73, read only for the open drawing). Use case: the first save after opening the company template
/// always fails ("One or more objects in this drawing cannot be saved to the specified format", reported 2026-10-09
/// as happening countless times), the next one works; a blank drawing saves fine. These tools look for the cause without
/// dialogs: an inventory of object classes (with the module that implements them and proxy objects), the loaded
/// AutoCAD modules, and save tests on a side database read from a file (saved several times to a temp folder,
/// optionally after erasing named-dictionary entries or object classes), where a failing save throws instead of
/// showing a message box. The open drawing and the source file are never written.
/// </summary>
public static class SaveDiagnosticsCommands
{
  private static readonly Dictionary<string, DwgVersion> Versions = new(StringComparer.OrdinalIgnoreCase)
  {
    ["current"] = DwgVersion.Current,
    ["2018"] = DwgVersion.AC1032,
    ["2013"] = DwgVersion.AC1027,
    ["2010"] = DwgVersion.AC1024,
    ["2007"] = DwgVersion.AC1021,
  };

  public static Task<object?> ListObjectClassesAsync(JsonObject? parameters)
  {
    var sourcePath = PluginRuntime.GetOptionalString(parameters, "sourcePath");
    if (sourcePath != null)
    {
      var full = FileBoundary.ResolveImportPath(sourcePath, ".dwg", ".dwt");
      return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
      {
        using var side = ReadSide(full);
        using var sideTransaction = side.TransactionManager.StartTransaction();
        var census = Census(side, sideTransaction);
        census["source"] = full;
        return census;
      });
    }

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var census = Census(database, transaction);
      census["source"] = database.Filename;
      return census;
    });
  }

  public static Task<object?> ListLoadedModulesAsync(JsonObject? parameters)
  {
    var contains = PluginRuntime.GetOptionalString(parameters, "contains");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var modules = SystemObjects.DynamicLinker.GetLoadedModules().Cast<string>()
        .Where(m => contains == null || m.Contains(contains, StringComparison.OrdinalIgnoreCase))
        .OrderBy(m => m, StringComparer.OrdinalIgnoreCase).ToList();
      return new Dictionary<string, object?> { ["count"] = modules.Count, ["modules"] = modules };
    });
  }

  /// <summary>Writes to the open drawing: 3D solids with history in the given blocks (all solids with history when
  /// none are given) are replaced with history-free copies. Fixes the first-save failure (eExcessiveItemCount).</summary>
  public static Task<object?> ReplaceSolidsWithoutHistoryAsync(JsonObject? parameters)
  {
    var blocks = Strings(parameters, "blocks");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var replaced = ReplaceSolidsWithoutHistory(database, transaction, blocks);
      return new Dictionary<string, object?> { ["replaced"] = replaced, ["count"] = replaced.Count };
    });
  }

  public static Task<object?> SaveTestAsync(JsonObject? parameters)
  {
    var full = FileBoundary.ResolveImportPath(PluginRuntime.GetRequiredString(parameters, "sourcePath"), ".dwg", ".dwt");
    var attempts = PluginRuntime.GetOptionalInt(parameters, "attempts") ?? 2;
    if (attempts is < 1 or > 5) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "attempts must lie between 1 and 5.");
    var versionName = PluginRuntime.GetOptionalString(parameters, "version") ?? "current";
    if (!Versions.TryGetValue(versionName, out var version))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"version must be one of {string.Join(", ", Versions.Keys)}.");
    var extension = (PluginRuntime.GetOptionalString(parameters, "format") ?? Path.GetExtension(full).TrimStart('.')).ToLowerInvariant();
    if (extension is not ("dwg" or "dwt" or "dxf")) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "format must be dwg, dwt or dxf.");
    var eraseKeys = Strings(parameters, "eraseDictionaryEntries");
    var eraseClasses = new HashSet<string>(Strings(parameters, "eraseClasses"), StringComparer.OrdinalIgnoreCase);
    var clearHistory = PluginRuntime.GetOptionalBool(parameters, "clearSolidHistory") ?? false;
    var eraseBlocks = Strings(parameters, "eraseBlocks");
    var replaceSolidBlocks = PluginRuntime.GetParameter(parameters, "replaceSolidsInBlocks") is JsonArray ? Strings(parameters, "replaceSolidsInBlocks") : null;

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      using var side = ReadSide(full);
      var erased = new Dictionary<string, object?>();
      if (clearHistory)
      {
        using var t = side.TransactionManager.StartTransaction();
        erased["solidHistoryCleared"] = ClearSolidHistory(side, t);
        t.Commit();
      }
      if (replaceSolidBlocks != null)
      {
        using var t = side.TransactionManager.StartTransaction();
        erased["solidsReplaced"] = ReplaceSolidsWithoutHistory(side, t, replaceSolidBlocks);
        t.Commit();
      }
      if (eraseBlocks.Count > 0)
      {
        using var t = side.TransactionManager.StartTransaction();
        var table = (BlockTable)t.GetObject(side.BlockTableId, OpenMode.ForRead);
        var done = new List<string>();
        // Database.Purge keeps only ids nothing refers to (block references, but also styles holding the block).
        var requested = new ObjectIdCollection();
        foreach (var name in eraseBlocks)
          if (table.Has(name)) requested.Add(table[name]);
        var purgeable = new ObjectIdCollection();
        foreach (ObjectId id in requested) purgeable.Add(id);
        side.Purge(purgeable);
        var notPurgeable = new List<string>();
        foreach (var name in eraseBlocks)
        {
          if (!table.Has(name)) { done.Add(name + " (missing)"); continue; }
          if (!purgeable.Contains(table[name])) notPurgeable.Add(name);
          var record = (BlockTableRecord)t.GetObject(table[name], OpenMode.ForWrite);
          if (record.GetBlockReferenceIds(true, false).Count > 0) { done.Add(name + " (in use)"); continue; }
          record.Erase();
          done.Add(name);
        }
        erased["blocks"] = done;
        erased["notPurgeable"] = notPurgeable;
        t.Commit();
      }
      if (eraseKeys.Count + eraseClasses.Count > 0)
      {
        using var t = side.TransactionManager.StartTransaction();
        var named = (DBDictionary)t.GetObject(side.NamedObjectsDictionaryId, OpenMode.ForWrite);
        var missing = new List<string>();
        foreach (var key in eraseKeys)
        {
          if (!named.Contains(key)) { missing.Add(key); continue; }
          t.GetObject(named.GetAt(key), OpenMode.ForWrite).Erase();
          named.Remove(key);
        }
        erased["dictionaryEntries"] = eraseKeys.Except(missing).ToList();
        erased["missingDictionaryEntries"] = missing;
        if (eraseClasses.Count > 0)
        {
          var count = 0;
          foreach (var id in AllObjects(side))
          {
            if (!eraseClasses.Contains(id.ObjectClass.DxfName) && !eraseClasses.Contains(id.ObjectClass.Name)) continue;
            try
            {
              t.GetObject(id, OpenMode.ForWrite).Erase();
              count++;
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
              // not erasable (e.g. a symbol table record in use)
            }
          }
          erased["objectsOfClasses"] = count;
        }
        t.Commit();
      }

      var folder = Path.Combine(Path.GetTempPath(), "Civil3DMcp", "savetest");
      Directory.CreateDirectory(folder);
      var stamp = DateTime.Now.ToString("HHmmss");
      var results = new List<Dictionary<string, object?>>();
      for (var i = 1; i <= attempts; i++)
      {
        var output = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(full)}_{stamp}_{i}.{extension}");
        var watch = Stopwatch.StartNew();
        // Objects changed, added or erased while saving: if a failing first save "repairs" something so that the
        // second one works, these events show what.
        var touched = new List<string>();
        void Note(string kind, DBObject o)
        {
          if (touched.Count < 300) touched.Add($"{kind} {o.Handle} {o.GetRXClass().Name}");
        }
        ObjectEventHandler modified = (_, e) => Note("modified", e.DBObject);
        ObjectEventHandler appended = (_, e) => Note("appended", e.DBObject);
        ObjectErasedEventHandler erasedHandler = (_, e) => Note(e.Erased ? "erased" : "unerased", e.DBObject);
        side.ObjectModified += modified;
        side.ObjectAppended += appended;
        side.ObjectErased += erasedHandler;
        try
        {
          // DXF is written object by object as text: when it fails, the tail of the partial file shows the object.
          if (extension == "dxf") side.DxfOut(output, 16, version);
          else side.SaveAs(output, version);
          results.Add(new Dictionary<string, object?> { ["attempt"] = i, ["ok"] = true, ["bytes"] = new FileInfo(output).Length, ["ms"] = watch.ElapsedMilliseconds, ["output"] = output, ["touched"] = touched });
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
          results.Add(new Dictionary<string, object?>
          {
            ["attempt"] = i, ["ok"] = false, ["errorStatus"] = ex.ErrorStatus.ToString(), ["message"] = ex.Message, ["ms"] = watch.ElapsedMilliseconds,
            ["partialBytes"] = File.Exists(output) ? new FileInfo(output).Length : null,
            ["partialTail"] = extension == "dxf" && File.Exists(output) ? Tail(output, 4000) : null,
            ["output"] = File.Exists(output) ? output : null,
            ["touched"] = touched,
          });
        }
        catch (System.Exception ex)
        {
          results.Add(new Dictionary<string, object?> { ["attempt"] = i, ["ok"] = false, ["exception"] = ex.GetType().Name, ["message"] = ex.Message, ["ms"] = watch.ElapsedMilliseconds, ["touched"] = touched });
        }
        finally
        {
          side.ObjectModified -= modified;
          side.ObjectAppended -= appended;
          side.ObjectErased -= erasedHandler;
        }
      }

      return new Dictionary<string, object?>
      {
        ["source"] = full,
        ["version"] = versionName,
        ["format"] = extension,
        ["erased"] = erased.Count == 0 ? null : erased,
        ["attempts"] = results,
      };
    });
  }

  /// <summary>Stops every 3D solid with history from recording it, which deletes the history (the shape stays);
  /// returns the block and handle of each solid changed. Use case: the solid history in four unused blocks of the company
  /// template made the first DWG save fail with eExcessiveItemCount (C73, 2026-10-09).</summary>
  internal static List<string> ClearSolidHistory(Database database, Transaction transaction)
  {
    var changed = new List<string>();
    var table = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
    foreach (ObjectId recordId in table)
    {
      var record = (BlockTableRecord)transaction.GetObject(recordId, OpenMode.ForRead);
      foreach (ObjectId id in record)
      {
        if (id.ObjectClass.Name != "AcDb3dSolid") continue;
        var solid = (Solid3d)transaction.GetObject(id, OpenMode.ForRead);
        if (!solid.RecordHistory && !solid.ShowHistory) continue;
        solid.UpgradeOpen();
        solid.ShowHistory = false;
        solid.RecordHistory = false;
        changed.Add($"{record.Name} {solid.Handle}");
      }
    }

    return changed;
  }

  /// <summary>Replaces every 3D solid that carries history with a history-free copy (ACIS round trip, as ACISOUT +
  /// ACISIN): same block, layer, colour and other common properties; the original is erased. Blocks restrict the
  /// search to those block definitions (all when empty). Use case: the extruded solids with history in the company
  /// template's 3D-Top-Block1 and 3D-Bottom-block1 (blocks Civil 3D styles refer to, so they cannot be purged) made
  /// the first DWG save fail with eExcessiveItemCount; clearing RecordHistory did not remove the history.</summary>
  internal static List<string> ReplaceSolidsWithoutHistory(Database database, Transaction transaction, ICollection<string> blocks)
  {
    var replaced = new List<string>();
    var folder = Path.Combine(Path.GetTempPath(), "Civil3DMcp", "sat");
    Directory.CreateDirectory(folder);
    var table = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
    foreach (ObjectId recordId in table)
    {
      var record = (BlockTableRecord)transaction.GetObject(recordId, OpenMode.ForRead);
      if (blocks.Count > 0 && !blocks.Contains(record.Name, StringComparer.OrdinalIgnoreCase)) continue;
      var solids = new List<ObjectId>();
      foreach (ObjectId id in record)
        if (id.ObjectClass.Name == "AcDb3dSolid") solids.Add(id);
      foreach (var id in solids)
      {
        var solid = (Solid3d)transaction.GetObject(id, OpenMode.ForRead);
        if (!solid.RecordHistory && !solid.ShowHistory && blocks.Count == 0) continue;
        var sat = Path.Combine(folder, $"{solid.Handle}_{Guid.NewGuid():N}.sat");
        Body.AcisOut(sat, new DBObjectCollection { solid });
        var copies = Body.AcisIn(sat);
        File.Delete(sat);
        record.UpgradeOpen();
        foreach (DBObject copy in copies)
        {
          if (copy is not Entity entity) { copy.Dispose(); continue; }
          entity.SetPropertiesFrom(solid);
          record.AppendEntity(entity);
          transaction.AddNewlyCreatedDBObject(entity, true);
          replaced.Add($"{record.Name} {solid.Handle} → {entity.Handle} {entity.GetRXClass().Name}");
        }
        solid.UpgradeOpen();
        solid.Erase();
      }
    }

    return replaced;
  }

  private static Database ReadSide(string path)
  {
    var side = new Database(false, true);
    try
    {
      side.ReadDwgFile(path, FileOpenMode.OpenForReadAndAllShare, true, "");
      side.CloseInput(true);
      return side;
    }
    catch
    {
      side.Dispose();
      throw;
    }
  }

  /// <summary>Every live object by handle (handles below the handle seed); also finds objects no table refers to.</summary>
  private static IEnumerable<ObjectId> AllObjects(Database database)
  {
    var seed = database.Handseed.Value;
    for (long value = 1; value < seed; value++)
    {
      if (!database.TryGetObjectId(new Handle(value), out var id) || id.IsNull || id.IsErased) continue;
      yield return id;
    }
  }

  private static Dictionary<string, object?> Census(Database database, Transaction transaction)
  {
    var watch = Stopwatch.StartNew();
    var classes = new Dictionary<(string Name, string Dxf, string App), int>();
    var proxies = new Dictionary<string, int>(StringComparer.Ordinal);
    var total = 0;
    foreach (var id in AllObjects(database))
    {
      total++;
      var cls = id.ObjectClass;
      var key = (cls.Name, cls.DxfName ?? "", cls.AppName ?? "");
      classes[key] = classes.TryGetValue(key, out var n) ? n + 1 : 1;
      if (cls.Name is "AcDbProxyObject" or "AcDbProxyEntity")
      {
        var original = transaction.GetObject(id, OpenMode.ForRead) switch
        {
          ProxyObject p => p.OriginalClassName,
          ProxyEntity e => e.OriginalClassName,
          _ => "?",
        };
        proxies[original] = proxies.TryGetValue(original, out var m) ? m + 1 : 1;
      }
    }

    // Summary info custom properties (a DWG-only section; the DXF export of the template did not fail).
    var custom = new List<string>();
    var walker = database.SummaryInfo.CustomProperties;
    while (walker.MoveNext()) custom.Add($"{walker.Key}={walker.Value}");

    var named = (DBDictionary)transaction.GetObject(database.NamedObjectsDictionaryId, OpenMode.ForRead);
    var namedEntries = new List<Dictionary<string, object?>>();
    foreach (DBDictionaryEntry entry in named)
      namedEntries.Add(new Dictionary<string, object?> { ["key"] = entry.Key, ["class"] = entry.Value.ObjectClass.Name, ["app"] = entry.Value.ObjectClass.AppName });

    return new Dictionary<string, object?>
    {
      ["objects"] = total,
      ["handseed"] = database.Handseed.Value,
      ["ms"] = watch.ElapsedMilliseconds,
      ["classes"] = classes.OrderByDescending(c => c.Value)
        .Select(c => new Dictionary<string, object?> { ["class"] = c.Key.Name, ["dxf"] = c.Key.Dxf, ["app"] = c.Key.App, ["count"] = c.Value }).ToList(),
      ["proxies"] = proxies.Select(p => new Dictionary<string, object?> { ["originalClass"] = p.Key, ["count"] = p.Value }).ToList(),
      ["summaryCustomProperties"] = new Dictionary<string, object?> { ["count"] = custom.Count, ["items"] = custom.Take(30).ToList() },
      ["namedObjectsDictionary"] = namedEntries,
    };
  }

  private static string Tail(string path, int characters)
  {
    using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);   // read only
    var start = Math.Max(0, stream.Length - characters);
    stream.Seek(start, SeekOrigin.Begin);
    using var reader = new StreamReader(stream);
    return reader.ReadToEnd();
  }

  private static List<string> Strings(JsonObject? parameters, string name) =>
    (PluginRuntime.GetParameter(parameters, name) as JsonArray ?? new JsonArray())
      .Select(n => n?.GetValue<string>()?.Trim() ?? "").Where(s => s.Length > 0).ToList();
}
