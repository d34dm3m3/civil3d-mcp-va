using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using Autodesk.Civil.DatabaseServices.Styles;

namespace Civil3DMcpPlugin;

/// <summary>
/// Template clean-up (C82): purge block definitions nothing refers to (AutoCAD's Database.Purge decides, so blocks
/// used by Civil styles, layouts or other blocks stay), by name or name pattern, with a dry run; and remove a family
/// from a parts list. Use case: the company template carries US example blocks, GUID-named blocks and duplicate or
/// test families in the Standard parts list (requested 2026-10-09: clean both).
/// </summary>
public static class PurgeCommands
{
  public static Task<object?> PurgeBlocksAsync(JsonObject? parameters)
  {
    var names = (PluginRuntime.GetParameter(parameters, "names") as JsonArray)?.Select(n => n?.GetValue<string>()).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
    var patternText = PluginRuntime.GetOptionalString(parameters, "pattern");
    var dryRun = PluginRuntime.GetOptionalBool(parameters, "dryRun") ?? true;
    if ((names == null || names.Count == 0) && string.IsNullOrWhiteSpace(patternText))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Give names or pattern. Nothing was purged.");
    Regex? pattern;
    try
    {
      pattern = string.IsNullOrWhiteSpace(patternText) ? null : new Regex(patternText, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
    }
    catch (ArgumentException ex)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"pattern: {ex.Message}. Nothing was purged.");
    }

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var table = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
      var candidates = new ObjectIdCollection();
      var byId = new Dictionary<ObjectId, string>();
      foreach (ObjectId id in table)
      {
        var record = (BlockTableRecord)transaction.GetObject(id, OpenMode.ForRead);
        if (record.IsLayout || record.IsAnonymous || record.IsFromExternalReference || record.IsDependent) continue;
        var match = (names != null && names.Contains(record.Name)) || (pattern != null && pattern.IsMatch(record.Name));
        if (!match) continue;
        candidates.Add(id);
        byId[id] = record.Name;
      }

      var requestedMissing = names?.Where(n => !byId.Values.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList() ?? new List<string>();
      var purgeable = new ObjectIdCollection();
      foreach (ObjectId id in candidates) purgeable.Add(id);
      database.Purge(purgeable);
      var purgeableIds = purgeable.Cast<ObjectId>().ToHashSet();
      var kept = byId.Where(kv => !purgeableIds.Contains(kv.Key)).Select(kv => kv.Value).OrderBy(n => n).ToList();
      var purged = purgeableIds.Select(id => byId[id]).OrderBy(n => n).ToList();
      if (!dryRun)
      {
        foreach (var id in purgeableIds) transaction.GetObject(id, OpenMode.ForWrite).Erase();
      }

      return new Dictionary<string, object?>
      {
        ["dryRun"] = dryRun,
        [dryRun ? "wouldPurge" : "purged"] = purged,
        ["inUse"] = kept,
        ["notFound"] = requestedMissing,
      };
    });
  }

  public static Task<object?> RemovePartsListFamilyAsync(JsonObject? parameters)
  {
    var partsListName = PluginRuntime.GetRequiredString(parameters, "partsList");
    var familyName = PluginRuntime.GetRequiredString(parameters, "familyName");
    var domainText = PluginRuntime.GetRequiredString(parameters, "domain").Trim().ToLowerInvariant();
    var domain = domainText switch
    {
      "pipe" => DomainType.Pipe,
      "structure" => DomainType.Structure,
      _ => throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"domain must be 'pipe' or 'structure', not '{domainText}'."),
    };

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      PartsList? list = null;
      foreach (ObjectId id in ((CivilDocument)civilDoc).Styles.PartsListSet)
      {
        var candidate = CivilObjectUtils.GetRequiredObject<PartsList>(transaction, id, OpenMode.ForRead);
        if (string.Equals(candidate.Name, partsListName, StringComparison.OrdinalIgnoreCase)) list = candidate;
      }
      if (list == null) throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Parts list '{partsListName}' was not found. Nothing was removed.");
      var familyId = list.GetPartFamilyIdsByDomain(domain).Cast<ObjectId>().Where(id => !id.IsNull)
        .FirstOrDefault(id => string.Equals(CivilObjectUtils.GetRequiredObject<PartFamily>(transaction, id, OpenMode.ForRead).Name, familyName, StringComparison.OrdinalIgnoreCase));
      if (familyId.IsNull)
        throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Family '{familyName}' is not in parts list '{partsListName}'. Nothing was removed.");
      list.UpgradeOpen();
      list.RemovePartFamily(familyId);
      return new Dictionary<string, object?> { ["partsList"] = list.Name, ["removed"] = familyName, ["domain"] = domainText };
    });
  }
}
