using System.Collections;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices.Styles;

namespace Civil3DMcpPlugin;

/// <summary>
/// Styles between drawings (C78, E1): list the style collections and style names of another drawing (or the active
/// one) and import named styles from it with StyleBase.ExportTo, the API behind Civil's Import Styles dialog.
/// A collection is named by its property path under CivilDocument.Styles, e.g. "PipeStyles", "PartsListSet",
/// "ProfileViewBandSetStyles", "LabelStyles.PipeLabelStyles.PlanProfileLabelStyles". Use case: one project's styles into
/// the company template and the template's styles out into project drawings without the dialog (2026-10-09).
/// Existing names are left alone by default (conflict "ignore"); "rename" imports a renamed copy, "override" replaces.
/// </summary>
public static class StyleTransferCommands
{
  private const int MaxDepth = 4;

  public static Task<object?> ListExternalStylesAsync(JsonObject? parameters)
  {
    var rawPath = PluginRuntime.GetOptionalString(parameters, "sourcePath");
    var collection = PluginRuntime.GetOptionalString(parameters, "collection");
    var contains = PluginRuntime.GetOptionalString(parameters, "contains");
    var path = rawPath == null ? null : FileBoundary.ResolveImportPath(rawPath, ".dwg", ".dwt");

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      using var source = path == null ? null : BlockCommands.ReadSource(path);
      var db = source ?? database;
      var styles = (source == null ? civilDoc : CivilDocument.GetCivilDocument(source)).Styles;
      using var tr = db.TransactionManager.StartOpenCloseTransaction();
      object? result;
      if (collection == null)
      {
        var found = new List<Dictionary<string, object?>>();
        Walk(styles, "", 0, found, tr);
        result = new Dictionary<string, object?> { ["sourcePath"] = path, ["collections"] = found };
      }
      else
      {
        var names = Names(Resolve(styles, collection), tr)
          .Where(n => contains == null || n.Contains(contains, StringComparison.OrdinalIgnoreCase)).ToList();
        result = new Dictionary<string, object?> { ["sourcePath"] = path, ["collection"] = collection, ["count"] = names.Count, ["names"] = names };
      }
      tr.Commit();
      return result;
    });
  }

  public static Task<object?> ImportStylesAsync(JsonObject? parameters)
  {
    var path = FileBoundary.ResolveImportPath(PluginRuntime.GetRequiredString(parameters, "sourcePath"), ".dwg", ".dwt");
    var conflictText = PluginRuntime.GetOptionalString(parameters, "conflict") ?? "ignore";
    var conflict = conflictText.ToLowerInvariant() switch
    {
      "ignore" => Autodesk.Civil.StyleConflictResolverType.Ignore,
      "rename" => Autodesk.Civil.StyleConflictResolverType.Rename,
      "override" => Autodesk.Civil.StyleConflictResolverType.Override,
      _ => throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "conflict must be ignore, rename or override. Nothing was imported."),
    };
    var items = (PluginRuntime.GetParameter(parameters, "items") as JsonArray ?? new JsonArray())
      .Select(n => n as JsonObject ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "items are {collection, names}. Nothing was imported."))
      .Select(o => (Collection: o["collection"]?.GetValue<string>() ?? "",
        Names: (o["names"] as JsonArray ?? new JsonArray()).Select(x => x?.GetValue<string>() ?? "").Where(x => x.Length > 0).ToList()))
      .ToList();
    if (items.Count == 0 || items.Any(i => i.Collection.Length == 0 || i.Names.Count == 0))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Give items: [{collection, names: [...]}]. Nothing was imported.");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      using var source = BlockCommands.ReadSource(path);
      var sourceStyles = CivilDocument.GetCivilDocument(source).Styles;

      // Everything is looked up before anything is exported: a missing collection or name changes nothing.
      var ids = new ObjectIdCollection();
      var report = new List<Dictionary<string, object?>>();
      using (var str = source.TransactionManager.StartOpenCloseTransaction())
      {
        var missing = new List<string>();
        foreach (var (collectionPath, names) in items)
        {
          var byName = ById(Resolve(sourceStyles, collectionPath), str);
          var existing = new HashSet<string>(Names(Resolve(civilDoc.Styles, collectionPath), transaction), StringComparer.OrdinalIgnoreCase);
          foreach (var name in names)
          {
            if (!byName.TryGetValue(name, out var id)) { missing.Add($"{collectionPath}: {name}"); continue; }
            ids.Add(id);
            report.Add(new Dictionary<string, object?> { ["collection"] = collectionPath, ["name"] = name, ["existed"] = existing.Contains(name) });
          }
        }
        str.Commit();
        if (missing.Count > 0)
          throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Not in the source drawing: {string.Join("; ", missing)}. Nothing was imported.");
      }

      StyleBase.ExportTo(ids, database, conflict);

      foreach (var entry in report)
      {
        var now = new HashSet<string>(Names(Resolve(civilDoc.Styles, (string)entry["collection"]!), transaction), StringComparer.OrdinalIgnoreCase);
        var existed = (bool)entry["existed"]!;
        entry["result"] = !now.Contains((string)entry["name"]!) ? "not imported"
          : !existed ? "imported"
          : conflict == Autodesk.Civil.StyleConflictResolverType.Override ? "replaced"
          : conflict == Autodesk.Civil.StyleConflictResolverType.Rename ? "imported as a renamed copy"
          : "kept the drawing's own";
      }
      return new Dictionary<string, object?> { ["sourcePath"] = path, ["conflict"] = conflictText.ToLowerInvariant(), ["styles"] = report };
    });
  }

  /// <summary>The object at a dotted property path under CivilDocument.Styles.</summary>
  private static object Resolve(StylesRoot styles, string path)
  {
    object current = styles;
    foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
      var (found, value) = Civil3DCompatibility.GetPropertyIgnoreCase(current, part);
      if (!found)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
          $"'{part}' is not a style collection under {current.GetType().Name} ({path}). Use styles_list_external without collection to see the paths. Nothing was changed.");
      current = value ?? throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"'{path}' is empty in this drawing. Nothing was changed.");
    }
    if (current is not IEnumerable)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"'{path}' is a group of collections, not a collection. Nothing was changed.");
    return current;
  }

  private static IEnumerable<ObjectId> Ids(object collection) =>
    CivilObjectUtils.ToObjectIds(collection).Where(id => !id.IsNull && !id.IsErased);

  private static List<string> Names(object collection, Transaction transaction) =>
    Ids(collection).Select(id => transaction.GetObject(id, OpenMode.ForRead) as StyleBase).OfType<StyleBase>().Select(s => s.Name).ToList();

  private static Dictionary<string, ObjectId> ById(object collection, Transaction transaction)
  {
    var map = new Dictionary<string, ObjectId>(StringComparer.OrdinalIgnoreCase);
    foreach (var id in Ids(collection))
      if (transaction.GetObject(id, OpenMode.ForRead) is StyleBase style) map.TryAdd(style.Name, id);
    return map;
  }

  /// <summary>Every style collection reachable from Styles (property paths with style counts).</summary>
  private static void Walk(object node, string prefix, int depth, List<Dictionary<string, object?>> found, Transaction transaction)
  {
    if (depth > MaxDepth) return;
    foreach (var name in Civil3DCompatibility.GetCivilReferencePropertyNames(node))
    {
      object? value;
      try
      {
        value = Civil3DCompatibility.GetPropertyIgnoreCase(node, name).Value;
      }
      catch (Exception)
      {
        continue;   // some collections throw in drawings without that feature
      }
      if (value == null) continue;
      var path = prefix.Length == 0 ? name : $"{prefix}.{name}";
      if (value is IEnumerable)
      {
        int count;
        try
        {
          count = Ids(value).Count(id => transaction.GetObject(id, OpenMode.ForRead) is StyleBase);
        }
        catch (Exception)
        {
          continue;
        }
        found.Add(new Dictionary<string, object?> { ["path"] = path, ["count"] = count });
      }
      else
      {
        Walk(value, path, depth + 1, found, transaction);
      }
    }
  }
}
