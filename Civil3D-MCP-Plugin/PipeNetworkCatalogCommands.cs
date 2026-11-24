using System.Collections;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using Autodesk.Civil.DatabaseServices.Styles;
using AcDbObject = Autodesk.AutoCAD.DatabaseServices.DBObject;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;

namespace Civil3DMcpPlugin;

public static partial class PipeNetworkCommands
{
  public static Task<object?> ListPipePartsCatalogAsync(JsonObject? parameters)
  {
    var partsListName = PluginRuntime.GetOptionalString(parameters, "partsList");

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      // Typed StylesRoot.PartsListSet: the earlier reflection walk visited the same collection under two
      // names (every list twice) and looked for family collections PartsList does not have (always empty).
      var partsLists = new List<Dictionary<string, object?>>();
      foreach (ObjectId id in ((CivilDocument)civilDoc).Styles.PartsListSet)
      {
        if (id.IsNull || id.IsErased)
        {
          continue;
        }

        var partsList = CivilObjectUtils.GetRequiredObject<PartsList>(transaction, id, OpenMode.ForRead);
        var name = CivilObjectUtils.GetName(partsList);
        if (!string.IsNullOrWhiteSpace(partsListName) && !string.Equals(name, partsListName, StringComparison.OrdinalIgnoreCase))
        {
          continue;
        }

        partsLists.Add(new Dictionary<string, object?>
        {
          ["name"] = name,
          ["handle"] = CivilObjectUtils.GetHandle(partsList),
          ["pipeFamilies"] = DescribeFamilies(partsList, DomainType.Pipe, transaction, withFields: !string.IsNullOrWhiteSpace(partsListName)),
          ["structureFamilies"] = DescribeFamilies(partsList, DomainType.Structure, transaction, withFields: !string.IsNullOrWhiteSpace(partsListName)),
        });
      }

      if (!string.IsNullOrWhiteSpace(partsListName) && partsLists.Count == 0)
      {
        throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Parts list '{partsListName}' was not found.");
      }

      return new Dictionary<string, object?>
      {
        ["partsLists"] = partsLists,
      };
    });
  }

  private static List<Dictionary<string, object?>> DescribeFamilies(PartsList partsList, DomainType domain, Transaction transaction, bool withFields = false)
  {
    // Parts lists can point at families or sizes whose catalog content is missing (null object ids, live
    // 2026-10-06 "eNullObjectId"); report those entries instead of failing the whole listing.
    var families = new List<Dictionary<string, object?>>();
    ObjectIdCollection familyIds;
    try
    {
      familyIds = partsList.GetPartFamilyIdsByDomain(domain);
    }
    catch (Exception ex) when (ex is Autodesk.AutoCAD.Runtime.Exception or InvalidOperationException or ArgumentException)
    {
      families.Add(new Dictionary<string, object?> { ["error"] = $"GetPartFamilyIdsByDomain: {ex.Message}" });
      return families;
    }

    foreach (ObjectId familyId in familyIds)
    {
      if (familyId.IsNull || familyId.IsErased)
      {
        families.Add(new Dictionary<string, object?> { ["error"] = "family id is null or erased" });
        continue;
      }

      try
      {
        var family = CivilObjectUtils.GetRequiredObject<PartFamily>(transaction, familyId, OpenMode.ForRead);
        var sizes = new List<Dictionary<string, object?>>();
        for (var index = 0; index < family.PartSizeCount; index++)
        {
          try
          {
            var sizeId = family[index];
            if (sizeId.IsNull)
            {
              sizes.Add(new Dictionary<string, object?> { ["index"] = index, ["error"] = "size id is null" });
              continue;
            }

            sizes.Add(DescribeSize(CivilObjectUtils.GetRequiredObject<PartSize>(transaction, sizeId, OpenMode.ForRead), domain, withFields));
          }
          catch (Exception ex) when (ex is Autodesk.AutoCAD.Runtime.Exception or InvalidOperationException or ArgumentException)
          {
            sizes.Add(new Dictionary<string, object?> { ["index"] = index, ["error"] = ex.Message });
          }
        }

        families.Add(new Dictionary<string, object?>
        {
          ["name"] = Safe(() => family.Name),
          ["description"] = Safe(() => family.Description),
          ["partType"] = Safe(() => family.PartType.ToString()),
          ["sizes"] = sizes,
        });
      }
      catch (Exception ex) when (ex is Autodesk.AutoCAD.Runtime.Exception or InvalidOperationException or ArgumentException)
      {
        families.Add(new Dictionary<string, object?> { ["error"] = ex.Message });
      }
    }

    return families;
  }

  private static T? Safe<T>(Func<T> read)
  {
    try
    {
      return read();
    }
    catch (Exception ex) when (ex is Autodesk.AutoCAD.Runtime.Exception or InvalidOperationException)
    {
      return default;
    }
  }

  private static Dictionary<string, object?> DescribeSize(PartSize size, DomainType domain, bool withFields = false)
  {
    using var record = size.SizeDataRecord;
    var result = new Dictionary<string, object?> { ["name"] = SizeName(record) };
    // C81: the size's default style (what new parts of this size get), read in the caller's transaction.
    try
    {
      result["style"] = size.PartStyleId.IsNull ? null : (size.PartStyleId.GetObject(OpenMode.ForRead) as StyleBase)?.Name;
    }
    catch (Exception ex) when (ex is Autodesk.AutoCAD.Runtime.Exception or InvalidOperationException)
    {
      result["style"] = null;
    }
    if (domain == DomainType.Pipe)
    {
      var (inner, outer, wall) = PipeDiameters(record);
      result["innerDiameter"] = inner;
      result["wallThickness"] = wall;
      result["outerDiameter"] = outer;
      result["material"] = FieldValue(record, PartContextType.Material);
    }
    else
    {
      result["diameter"] = FieldValue(record, PartContextType.StructDiameter);
      result["innerDiameter"] = FieldValue(record, PartContextType.StructInnerDiameter);
      result["height"] = FieldValue(record, PartContextType.StructHeight);
    }

    if (withFields)
    {
      result["fields"] = record.GetAllDataFields().Select(f => new Dictionary<string, object?>
      {
        ["name"] = f.Name,
        ["description"] = f.Description,
        ["context"] = SafeText(() => f.ContextString),
        ["units"] = f.Units,
        ["value"] = f.Value is string or double or int or bool ? f.Value : f.Value?.ToString(),
      }).ToList();
    }

    return result;
  }

  private static string? SafeText(Func<string?> read)
  {
    try
    {
      return read();
    }
    catch (Exception ex) when (ex is Autodesk.AutoCAD.Runtime.Exception or InvalidOperationException)
    {
      return null;
    }
  }

  /// <summary>Inner diameter, outer diameter and wall thickness in catalog units (mm). Inner diameter and
  /// wall come from their contexts or, for SWE Part Builder families, the fields PID and WTh; outer diameter
  /// from a field such as SOD/OD/PipeOuterDiameter or inner + 2 x wall.</summary>
  private static (double? Inner, double? Outer, double? Wall) PipeDiameters(PartDataRecord record)
  {
    var fields = record.GetAllDataFields();
    double? ByName(params string[] names)
    {
      foreach (var f in fields)
      {
        if (names.Any(n => string.Equals(f.Name, n, StringComparison.OrdinalIgnoreCase)) && f.Value is double d) return d;
      }

      return null;
    }

    var inner = FieldValue(record, PartContextType.PipeInnerDiameter) as double? ?? ByName("PID");
    var wall = FieldValue(record, PartContextType.WallThickness) as double? ?? ByName("WTh", "WallThickness");
    var outer = ByName("SOD", "OD", "PipeOuterDiameter", "OuterDiameter") ?? (inner.HasValue && wall.HasValue ? inner + 2 * wall : null);
    return (inner, outer, wall);
  }

  /// <summary>Part size name as shown in Civil 3D: the catalog field PrtSN ("Part Size Name").
  /// PartSize has no Name property, so name lookups through it always came back empty.</summary>
  internal static string? SizeName(PartDataRecord record)
  {
    foreach (var field in record.GetAllDataFields())
    {
      var isSizeName = string.Equals(field.Name, "PrtSN", StringComparison.OrdinalIgnoreCase)
        || string.Equals(field.Name, "PartSizeName", StringComparison.OrdinalIgnoreCase)
        || (field.Description?.Contains("size name", StringComparison.OrdinalIgnoreCase) ?? false);
      if (isSizeName && field.Value is string text && !string.IsNullOrWhiteSpace(text))
      {
        return text;
      }
    }

    return null;
  }

  private static double? AsDouble(object? value) => value switch
  {
    double d => d,
    int i => i,
    string text when double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) => d,
    _ => null,
  };

  private static object? FieldValue(PartDataRecord record, PartContextType context)
  {
    try
    {
      var value = record.GetDataFieldBy(context)?.Value;
      return value is string or double or int or bool ? value : value?.ToString();
    }
    catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or Autodesk.AutoCAD.Runtime.Exception)
    {
      return null;
    }
  }

  /// <summary>Part families in the drawing's pipe network catalog, i.e. what can be added to a parts list.</summary>
  public static Task<object?> ListCatalogPartFamiliesAsync(JsonObject? parameters)
  {
    var domains = ParseDomains(PluginRuntime.GetOptionalString(parameters, "domain"));
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var result = new Dictionary<string, object?>();
      foreach (var domain in domains)
      {
        try
        {
          result[DomainKey(domain)] = PartsList.GetAvailablePartFamilies(domain)
            .Select(f => new Dictionary<string, object?>
            {
              ["name"] = f.Description,
              ["guid"] = f.GUID,
              ["partType"] = f.PartType.ToString(),
              ["sweptShape"] = f.SweptShape.ToString(),
              ["boundingShape"] = f.BoundingShape.ToString(),
            })
            .OrderBy(d => (string?)d["name"], StringComparer.OrdinalIgnoreCase)
            .ToList();
        }
        catch (Exception ex)
        {
          result[DomainKey(domain)] = new List<Dictionary<string, object?>> { new() { ["error"] = ex.Message } };
        }
      }

      return result;
    });
  }

  /// <summary>Adds a part family to a parts list (created first when missing and createPartsList is set).
  /// With sourcePartsList the sizes are copied from the family of the same name there; otherwise the family
  /// comes from the catalog with one size from its default size filter. A family already in the list is
  /// left unchanged.</summary>
  public static Task<object?> AddPartsListFamilyAsync(JsonObject? parameters)
  {
    var partsListName = PluginRuntime.GetRequiredString(parameters, "partsList");
    var familyName = PluginRuntime.GetRequiredString(parameters, "familyName");
    var domain = ParseDomains(PluginRuntime.GetRequiredString(parameters, "domain")).Single();
    var sourceName = PluginRuntime.GetOptionalString(parameters, "sourcePartsList");
    var create = PluginRuntime.GetOptionalBool(parameters, "createPartsList") ?? false;

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var createdList = false;
      ObjectId targetId;
      try
      {
        targetId = FindPartsListId(civilDoc, transaction, partsListName);
      }
      catch (JsonRpcDispatchException) when (create)
      {
        targetId = ((CivilDocument)civilDoc).Styles.PartsListSet.Add(partsListName);
        createdList = true;
      }

      var target = CivilObjectUtils.GetRequiredObject<PartsList>(transaction, targetId, OpenMode.ForWrite);

      PartFamily? FamilyIn(PartsList list, Func<PartFamily, bool> match, OpenMode mode) => list.GetPartFamilyIdsByDomain(domain)
        .Cast<ObjectId>()
        .Where(id => !id.IsNull)
        .Select(id => CivilObjectUtils.GetRequiredObject<PartFamily>(transaction, id, mode))
        .FirstOrDefault(match);
      bool ByName(PartFamily f) => string.Equals(f.Name, familyName, StringComparison.OrdinalIgnoreCase);
      List<Dictionary<string, object?>> Sizes(PartFamily f) => Enumerable.Range(0, f.PartSizeCount)
        .Select(i => f[i])
        .Where(id => !id.IsNull)
        .Select(id => DescribeSize(CivilObjectUtils.GetRequiredObject<PartSize>(transaction, id, OpenMode.ForRead), domain))
        .ToList();

      var existing = FamilyIn(target, ByName, OpenMode.ForRead);
      if (existing != null)
      {
        return new Dictionary<string, object?>
        {
          ["partsList"] = partsListName,
          ["family"] = existing.Name,
          ["added"] = false,
          ["reason"] = "The family is already in the parts list; nothing was changed.",
          ["sizes"] = Sizes(existing),
        };
      }

      PartFamily? source = null;
      string guid;
      if (!string.IsNullOrWhiteSpace(sourceName))
      {
        var sourceList = CivilObjectUtils.GetRequiredObject<PartsList>(transaction, FindPartsListId(civilDoc, transaction, sourceName!), OpenMode.ForRead);
        source = FamilyIn(sourceList, ByName, OpenMode.ForRead)
          ?? throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Family '{familyName}' is not in parts list '{sourceName}'.");
        guid = source.GUID;
      }
      else
      {
        guid = PartsList.GetAvailablePartFamilies(domain)
          .FirstOrDefault(f => string.Equals(f.Description, familyName, StringComparison.OrdinalIgnoreCase))?.GUID
          ?? throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Family '{familyName}' is not in the catalog ({DomainKey(domain)}); see catalog_families.");
      }

      target.AddPartFamilyByGuid(domain, guid);
      var family = FamilyIn(target, f => string.Equals(f.GUID, guid, StringComparison.OrdinalIgnoreCase), OpenMode.ForWrite)
        ?? throw new JsonRpcDispatchException("CIVIL3D.OPERATION_FAILED", $"Family '{familyName}' was not found in '{partsListName}' after adding it.");

      var notes = new List<string>();
      void AddSize(PartSize? from)
      {
        var before = Enumerable.Range(0, family.PartSizeCount).Select(i => family[i]).ToHashSet();
        try
        {
          using var filter = new SizeFilterRecord(family);
          if (from != null)
          {
            using var record = from.SizeDataRecord;
            for (var k = 0; k < filter.ParamCount; k++)
            {
              var field = filter[k];
              if (field.IsReadOnly) continue;
              try
              {
                var value = record.GetDataFieldBy(field.Name)?.Value;
                if (value != null) field.Value = value;
              }
              catch (Exception ex)
              {
                notes.Add($"filter {field.Name}: {ex.Message}");
              }
            }
          }

          family.AddPartSize(filter);
        }
        catch (Exception ex)
        {
          notes.Add($"AddPartSize: {ex.Message}");
          return;
        }

        var addedId = Enumerable.Range(0, family.PartSizeCount).Select(i => family[i]).FirstOrDefault(id => !before.Contains(id));
        if (addedId.IsNull)
        {
          notes.Add("AddPartSize added no size (it may already exist).");
          return;
        }

        if (from == null) return;
        var added = CivilObjectUtils.GetRequiredObject<PartSize>(transaction, addedId, OpenMode.ForWrite);
        try
        {
          added.SizeDataRecord = from.SizeDataRecord;
        }
        catch (Exception ex)
        {
          notes.Add($"size data: {ex.Message}");
        }

        try
        {
          added.PartStyleId = from.PartStyleId;
          added.RulesStyleId = from.RulesStyleId;
        }
        catch (Exception ex)
        {
          notes.Add($"styles: {ex.Message}");
        }
      }

      if (source == null)
      {
        AddSize(null);
      }
      else
      {
        for (var i = 0; i < source.PartSizeCount; i++)
        {
          if (!source[i].IsNull) AddSize(CivilObjectUtils.GetRequiredObject<PartSize>(transaction, source[i], OpenMode.ForRead));
        }
      }

      return new Dictionary<string, object?>
      {
        ["partsList"] = partsListName,
        ["partsListCreated"] = createdList,
        ["family"] = family.Name,
        ["guid"] = guid,
        ["added"] = true,
        ["copiedFrom"] = sourceName,
        ["sizes"] = Sizes(family),
        ["notes"] = notes,
      };
    });
  }

  /// <summary>
  /// C79: catalog sizes into a family already in a parts list. Adding a family from the catalog gives only its first
  /// size (live 2026-10-09: Uponor PE100 PN16 came with OD 110 only). The sizes are the values of one list field of the
  /// family's size filter (by default the first editable list field, e.g. the part size name): the given ones or all.
  /// Sizes already in the family are skipped. Use case: the organisation's parts lists for pressure sewer and LTA with PE100 OD 32-90.
  /// </summary>
  public static Task<object?> AddPartsListSizesAsync(JsonObject? parameters)
  {
    var partsListName = PluginRuntime.GetRequiredString(parameters, "partsList");
    var familyName = PluginRuntime.GetRequiredString(parameters, "familyName");
    var domain = ParseDomains(PluginRuntime.GetRequiredString(parameters, "domain")).Single();
    var fieldName = PluginRuntime.GetOptionalString(parameters, "field");
    var requested = (PluginRuntime.GetParameter(parameters, "sizes") as JsonArray)?.Select(n => n?.ToString()).OfType<string>().ToList();
    var all = PluginRuntime.GetOptionalBool(parameters, "all") ?? false;
    // C156: dryRun reports what would be added and changes nothing (was ignored: live 2026-10-21 a dry run added ID 1600).
    var dryRun = PluginRuntime.GetOptionalBool(parameters, "dryRun") ?? false;
    if (!all && (requested == null || requested.Count == 0))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Give sizes or all: true. Nothing was added.");

    object? Body(Autodesk.AutoCAD.ApplicationServices.Document doc, CivilDocument civilDoc, Database database, Transaction transaction)
    {
      var list = CivilObjectUtils.GetRequiredObject<PartsList>(transaction, FindPartsListId(civilDoc, transaction, partsListName), OpenMode.ForWrite);
      var family = list.GetPartFamilyIdsByDomain(domain).Cast<ObjectId>().Where(id => !id.IsNull)
        .Select(id => CivilObjectUtils.GetRequiredObject<PartFamily>(transaction, id, OpenMode.ForWrite))
        .FirstOrDefault(f => string.Equals(f.Name, familyName, StringComparison.OrdinalIgnoreCase))
        ?? throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND",
          $"Family '{familyName}' is not in parts list '{partsListName}'; add it first with parts_list_family_add. Nothing was added.");

      using var filter = new SizeFilterRecord(family);
      var fields = Enumerable.Range(0, filter.ParamCount).Select(i => filter[i]).ToList();
      var field = fieldName != null
        ? fields.FirstOrDefault(f => string.Equals(f.Name, fieldName, StringComparison.OrdinalIgnoreCase) || string.Equals(f.Description, fieldName, StringComparison.OrdinalIgnoreCase))
        : fields.FirstOrDefault(f => !f.IsReadOnly && f.IsFromList && f.ValueList != null && f.ValueList.Count > 0);
      if (field == null || field.ValueList == null)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
          $"No list field{(fieldName == null ? "" : $" '{fieldName}'")} in the size filter. Fields: {string.Join(", ", fields.Select(f => $"{f.Name} ({f.Description})"))}. Nothing was added.");

      static string Text(object? v) => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? "";
      static bool Match(object? v, string want) => string.Equals(Text(v), want, StringComparison.OrdinalIgnoreCase)
        || (double.TryParse(want.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var w)
            && v is IConvertible && double.TryParse(Text(v), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)
            && Math.Abs(d - w) < 1e-6);
      var values = Enumerable.Range(0, field.ValueList.Count).Select(i => (object?)field.ValueList[i]).ToList();
      List<object?> chosen;
      if (all) chosen = values;
      else
      {
        var missing = requested!.Where(r => !values.Any(v => Match(v, r))).ToList();
        if (missing.Count > 0)
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
            $"Not in the catalog for {family.Name} ({field.Name}): {string.Join(", ", missing)}. Available: {string.Join(", ", values.Select(Text))}. Nothing was added.");
        chosen = requested!.Select(r => values.First(v => Match(v, r))).ToList();
      }

      var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      for (var i = 0; i < family.PartSizeCount; i++)
      {
        if (family[i].IsNull) continue;
        var size = CivilObjectUtils.GetRequiredObject<PartSize>(transaction, family[i], OpenMode.ForRead);
        using var record = size.SizeDataRecord;
        try
        {
          existing.Add(Text(record.GetDataFieldBy(field.Name)?.Value));
        }
        catch (Exception)
        {
          // a size without the field: never matches
        }
      }

      var added = new List<string>();
      var skipped = new List<string>();
      var notes = new List<string>();
      foreach (var value in chosen)
      {
        if (existing.Contains(Text(value))) { skipped.Add(Text(value)); continue; }
        if (dryRun) { added.Add(Text(value)); existing.Add(Text(value)); continue; }
        try
        {
          field.Value = value;
          family.AddPartSize(filter);
          added.Add(Text(value));
          existing.Add(Text(value));
        }
        catch (Exception ex)
        {
          notes.Add($"{Text(value)}: {ex.Message}");
        }
      }

      return new Dictionary<string, object?>
      {
        ["partsList"] = partsListName,
        ["family"] = family.Name,
        ["field"] = field.Name,
        ["available"] = values.Select(Text).ToList(),
        ["dryRun"] = dryRun,
        [dryRun ? "wouldAdd" : "added"] = added,
        ["skipped"] = skipped,
        ["notes"] = notes,
        ["sizes"] = Enumerable.Range(0, family.PartSizeCount).Select(i => family[i]).Where(id => !id.IsNull)
          .Select(id => DescribeSize(CivilObjectUtils.GetRequiredObject<PartSize>(transaction, id, OpenMode.ForRead), domain)).ToList(),
      };
    }

    // A dry run reads in a transaction that is never committed: nothing it touches is kept.
    return dryRun ? CivilExecution.ReadAsync<object?>(Body) : CivilExecution.WriteAsync<object?>(Body);
  }

  private static DomainType[] ParseDomains(string? text) => text?.Trim().ToLowerInvariant() switch
  {
    null or "" => new[] { DomainType.Pipe, DomainType.Structure },
    "pipe" => new[] { DomainType.Pipe },
    "structure" => new[] { DomainType.Structure },
    _ => throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"domain must be 'pipe' or 'structure', not '{text}'."),
  };

  private static string DomainKey(DomainType domain) => domain == DomainType.Pipe ? "pipeFamilies" : "structureFamilies";
}
