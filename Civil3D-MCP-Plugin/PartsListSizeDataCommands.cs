using System.Globalization;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using Autodesk.Civil.DatabaseServices.Styles;

namespace Civil3DMcpPlugin;

/// <summary>
/// C98d: the data of the sizes in a parts list family. Use case (pilot project, 2026-10-10): in the pressure-sewer
/// parts list the sizes of the Uponor PE100 PN10 and PN16 pressure pipe families show the material "Undefined
/// Material", in the water parts list the same families show "PE" (the pressure-sewer sizes were added by C79, which sets only
/// the size filter's list field and leaves the rest at the catalog's default).
/// A size's data is PartSize.SizeDataRecord (PartDataRecord of PartDataField: Name, Description, Context, Units,
/// DataType, Value, IsReadOnly, ValueList/ValueRange); how the catalog fills a parameter is told by the family's size
/// filter (PartFamily.PartSizeFilter, SizeFilterField.DataSource: Constant, FixedList, RangeList, BasicTable, Calculation,
/// Optional). A field is written only where the catalog lets the parts list choose it: not read-only in the size, a size
/// filter parameter whose data source is a list, a range or optional (never a constant, a table column, which picks the
/// catalog row the size belongs to, or a calculation), and a value its list or range allows. Each size is opened for
/// write in the transaction, its record changed and assigned back (the PartSize.SizeDataRecord setter, as
/// parts_list_family_add copies sizes) and read back; a value Civil 3D does not keep aborts the transaction, so nothing
/// stays. Parts already drawn keep their own copy of the data (Part.PartData).
/// </summary>
public static class PartsListSizeDataCommands
{
  private const int MaxListValues = 60;
  private static readonly PartDataSourceType[] WritableSources = { PartDataSourceType.FixedList, PartDataSourceType.RangeList, PartDataSourceType.Optional };

  private static T? Safe<T>(Func<T> read)
  {
    try
    {
      return read();
    }
    catch (Exception ex) when (ex is not JsonRpcDispatchException)
    {
      return default;   // Civil's wrappers throw several types (CivilException derives from Exception itself)
    }
  }

  /// <summary>C98d: without familyName the families of the parts list with their sizes; with it every size's data fields
  /// (optionally sizes and one field) with value, unit, type, whether writable and why not, and the family's size filter
  /// (data source, allowed values) that decides it.</summary>
  public static Task<object?> GetSizeDataAsync(JsonObject? parameters)
  {
    var partsListName = PluginRuntime.GetRequiredString(parameters, "partsList").Trim();
    var familyName = PluginRuntime.GetOptionalString(parameters, "familyName")?.Trim();
    var domain = Domain(PluginRuntime.GetOptionalString(parameters, "domain"));
    var selectors = Selectors(parameters);
    var fieldKey = PluginRuntime.GetOptionalString(parameters, "field")?.Trim();
    if (familyName is "") familyName = null;
    if (fieldKey is "") fieldKey = null;

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var partsList = FindPartsList(civilDoc, transaction, partsListName);
      if (familyName == null)
      {
        return new Dictionary<string, object?>
        {
          ["partsList"] = partsList.Name,
          ["domain"] = DomainName(domain),
          ["families"] = Families(partsList, domain, transaction).Select(f => new Dictionary<string, object?>
          {
            ["name"] = f.Name,
            ["partType"] = Safe(() => f.PartType.ToString()),
            ["sizes"] = SizeIds(f).Select((id, i) => SizeLabel(transaction, id, i)).ToList(),
          }).ToList(),
          ["note"] = "Give familyName for the data of its sizes.",
        };
      }

      var family = FindFamily(partsList, domain, familyName, transaction, OpenMode.ForRead);
      using var filter = NewFilter(family);
      var filterFields = FilterFields(filter);
      var sizes = new List<Dictionary<string, object?>>();
      foreach (var (id, index) in Select(family, selectors, transaction))
      {
        var size = (PartSize)transaction.GetObject(id, OpenMode.ForRead);
        using var record = size.SizeDataRecord;
        var fields = record.GetAllDataFields().Where(f => fieldKey == null || Matches(f, fieldKey)).Select(f =>
        {
          var source = filterFields.FirstOrDefault(p => string.Equals(Safe(() => p.Name), Safe(() => f.Name), StringComparison.OrdinalIgnoreCase));
          var why = WhyNotWritable(f, source, filter != null);
          return new Dictionary<string, object?>
          {
            ["name"] = Safe(() => f.Name),
            ["description"] = Safe(() => f.Description),
            ["context"] = Safe(() => f.Context.ToString()),
            ["units"] = Safe(() => f.Units),
            ["dataType"] = Safe(() => f.DataType.ToString()),
            ["value"] = Plain(Safe(() => f.Value)),
            ["readOnly"] = Safe<bool?>(() => f.IsReadOnly),
            ["dataSource"] = source == null ? null : Safe(() => source.DataSource.ToString()),
            ["writable"] = why == null,
            ["reason"] = why,
          };
        }).ToList();
        if (fieldKey != null && fields.Count == 0)
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
            $"The sizes of '{family.Name}' have no field '{fieldKey}'. Fields: {string.Join(", ", record.GetAllDataFields().Select(Label))}.");
        sizes.Add(new Dictionary<string, object?> { ["index"] = index, ["name"] = PipeNetworkCommands.SizeName(record), ["description"] = PartDescription(record), ["fields"] = fields });
      }

      return new Dictionary<string, object?>
      {
        ["partsList"] = partsList.Name,
        ["domain"] = DomainName(domain),
        ["family"] = family.Name,
        ["partType"] = Safe(() => family.PartType.ToString()),
        ["guid"] = Safe(() => family.GUID),
        ["sizeFilter"] = filterFields.Where(p => fieldKey == null || Matches(p, fieldKey)).Select(DescribeFilterField).ToList(),
        ["sizes"] = sizes,
        ["note"] = "A field can be written when it is not read-only and the size filter gives it from a list, a range or as optional "
          + "(dataSource FixedList, RangeList, Optional), with a value the list or range allows.",
      };
    });
  }

  /// <summary>
  /// C98d: writes data fields of sizes in a parts list family: values {field: value} (field by name, context such as
  /// Material, or description), for the sizes given (names or indexes) or all. Everything is checked for every size
  /// before the first write (see the class summary); fields that already hold the value are left alone. dryRun reports
  /// before/after without writing.
  /// </summary>
  public static Task<object?> SetSizeDataAsync(JsonObject? parameters)
  {
    var partsListName = PluginRuntime.GetRequiredString(parameters, "partsList").Trim();
    var familyName = PluginRuntime.GetRequiredString(parameters, "familyName").Trim();
    var domain = Domain(PluginRuntime.GetOptionalString(parameters, "domain"));
    var selectors = Selectors(parameters);
    var dryRun = PluginRuntime.GetOptionalBool(parameters, "dryRun") ?? false;
    if (PluginRuntime.GetParameter(parameters, "values") is not JsonObject values || values.Count == 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Give values: {field: value}, e.g. {\"Material\": \"PE\"}. Nothing was changed.");

    object? Body(Autodesk.AutoCAD.ApplicationServices.Document doc, CivilDocument civilDoc, Database database, Transaction transaction)
    {
      var partsList = FindPartsList(civilDoc, transaction, partsListName);
      var family = FindFamily(partsList, domain, familyName, transaction, OpenMode.ForRead);
      using var filter = NewFilter(family);
      var filterFields = FilterFields(filter);
      var targets = Select(family, selectors, transaction);
      if (targets.Count == 0)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"'{family.Name}' in '{partsList.Name}' has no sizes. Nothing was changed.");

      // Check everything for every size first.
      var plan = new List<(ObjectId Id, int Index, string? Name, List<(string Field, object? Before, object After)> Changes)>();
      var unchanged = 0;
      foreach (var (id, index) in targets)
      {
        var size = (PartSize)transaction.GetObject(id, OpenMode.ForRead);
        using var record = size.SizeDataRecord;
        var all = record.GetAllDataFields();
        var changes = new List<(string Field, object? Before, object After)>();
        var given = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, node) in values)
        {
          var field = all.FirstOrDefault(f => string.Equals(Safe(() => f.Name), key.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(f => Matches(f, key))
            ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
              $"Size {SizeText(record, index)} of '{family.Name}' has no field '{key}'. Fields: {string.Join(", ", all.Select(Label))}. Nothing was changed.");
          var name = field.Name;
          if (!given.Add(name))
            throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Field '{Label(field)}' is given twice (by name, context or description). Nothing was changed.");
          var source = filterFields.FirstOrDefault(p => string.Equals(Safe(() => p.Name), name, StringComparison.OrdinalIgnoreCase));
          var why = WhyNotWritable(field, source, filter != null);
          if (why != null)
            throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
              $"Field '{Label(field)}' of '{family.Name}' cannot be written in the parts list: {why}. Nothing was changed.");
          var target = Allowed(field, source, Convert(node, field.DataType, name), family.Name);
          var before = Safe(() => field.Value);
          if (Same(before, target)) { unchanged++; continue; }
          changes.Add((name, Plain(before), target));
        }
        if (changes.Count > 0) plan.Add((id, index, PipeNetworkCommands.SizeName(record), changes));
      }

      var changed = new List<Dictionary<string, object?>>();
      foreach (var (id, index, sizeName, changes) in plan)
      {
        if (!dryRun)
        {
          var size = (PartSize)transaction.GetObject(id, OpenMode.ForWrite);
          using (var record = size.SizeDataRecord)
          {
            foreach (var (field, _, target) in changes)
            {
              var data = record.GetDataFieldBy(field)
                ?? throw new JsonRpcDispatchException("CIVIL3D.OPERATION_FAILED", $"Field '{field}' of size {sizeName ?? $"#{index}"} of '{family.Name}' cannot be opened. Nothing was changed.");
              try
              {
                data.Value = target;
              }
              catch (Exception ex) when (ex is not JsonRpcDispatchException)
              {
                throw new JsonRpcDispatchException("CIVIL3D.OPERATION_FAILED",
                  $"Civil 3D refused {field} = {Plain(target)} for size {sizeName ?? $"#{index}"} of '{family.Name}': {ex.Message} Nothing was changed.");
              }
            }
            size.SizeDataRecord = record;
          }

          using var stored = size.SizeDataRecord;
          var refused = changes.Where(c => !Same(Safe(() => stored.GetDataFieldBy(c.Field)?.Value), c.After))
            .Select(c => $"{c.Field} = {Plain(Safe(() => stored.GetDataFieldBy(c.Field)?.Value)) ?? "nothing"}").ToList();
          if (refused.Count > 0)
            throw new JsonRpcDispatchException("CIVIL3D.OPERATION_FAILED",
              $"Civil 3D did not keep the new data of size {sizeName ?? $"#{index}"} of '{family.Name}' (read back {string.Join(", ", refused)}). Nothing was changed.");
        }
        changed.Add(new Dictionary<string, object?>
        {
          ["index"] = index,
          ["name"] = sizeName,
          ["changes"] = changes.Select(c => new Dictionary<string, object?> { ["field"] = c.Field, ["before"] = c.Before, ["after"] = Plain(c.After) }).ToList(),
        });
      }

      return new Dictionary<string, object?>
      {
        ["partsList"] = partsList.Name,
        ["family"] = family.Name,
        ["dryRun"] = dryRun,
        ["sizes"] = targets.Count,
        ["changed"] = changed,
        ["unchanged"] = unchanged,
        ["note"] = dryRun ? "Dry run: nothing was changed."
          : changed.Count == 0 ? "Every size already had the values; nothing was changed."
          : "Parts already drawn keep their own data (Part.PartData); new parts of these sizes get the new values.",
      };
    }

    return dryRun ? CivilExecution.ReadAsync<object?>(Body) : CivilExecution.WriteAsync<object?>(Body);
  }

  // ---------------------------------------------------------------- parts list, family, sizes

  private static DomainType Domain(string? text) => text?.Trim().ToLowerInvariant() switch
  {
    null or "" or "pipe" => DomainType.Pipe,
    "structure" => DomainType.Structure,
    _ => throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"domain must be 'pipe' or 'structure', not '{text}'."),
  };

  private static string DomainName(DomainType domain) => domain == DomainType.Pipe ? "pipe" : "structure";

  private static PartsList FindPartsList(CivilDocument civilDoc, Transaction transaction, string name)
  {
    PartsList? loose = null;
    var names = new List<string>();
    foreach (ObjectId id in civilDoc.Styles.PartsListSet)
    {
      if (id.IsNull || id.IsErased || transaction.GetObject(id, OpenMode.ForRead) is not PartsList list) continue;
      names.Add(list.Name);
      if (string.Equals(list.Name, name, StringComparison.Ordinal)) return list;
      if (loose == null && string.Equals(list.Name, name, StringComparison.OrdinalIgnoreCase)) loose = list;
    }
    return loose ?? throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Parts list '{name}' was not found. Parts lists: {string.Join(", ", names)}.");
  }

  private static List<PartFamily> Families(PartsList partsList, DomainType domain, Transaction transaction)
  {
    var families = new List<PartFamily>();
    foreach (ObjectId id in partsList.GetPartFamilyIdsByDomain(domain))
      if (!id.IsNull && !id.IsErased && transaction.GetObject(id, OpenMode.ForRead) is PartFamily family) families.Add(family);
    return families;
  }

  private static PartFamily FindFamily(PartsList partsList, DomainType domain, string name, Transaction transaction, OpenMode mode)
  {
    var families = Families(partsList, domain, transaction);
    var family = families.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.Ordinal))
      ?? families.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))
      ?? throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND",
        $"Parts list '{partsList.Name}' has no {DomainName(domain)} family '{name}'. Families: {string.Join(", ", families.Select(f => f.Name))}. Nothing was changed.");
    if (mode == OpenMode.ForWrite) family.UpgradeOpen();
    return family;
  }

  private static List<ObjectId> SizeIds(PartFamily family)
  {
    var ids = new List<ObjectId>();
    for (var i = 0; i < family.PartSizeCount; i++) ids.Add(family[i]);
    return ids;
  }

  private static string SizeLabel(Transaction transaction, ObjectId id, int index)
  {
    if (id.IsNull || id.IsErased) return $"#{index} (missing)";
    using var record = ((PartSize)transaction.GetObject(id, OpenMode.ForRead)).SizeDataRecord;
    return SizeText(record, index);
  }

  /// <summary>"#index name" (or the part description when the size has no name, as the Uponor sizes).</summary>
  private static string SizeText(PartDataRecord record, int index)
  {
    var name = PipeNetworkCommands.SizeName(record) ?? PartDescription(record);
    return string.IsNullOrWhiteSpace(name) ? $"#{index}" : $"#{index} {name}";
  }

  private static string? PartDescription(PartDataRecord record)
  {
    foreach (var field in record.GetAllDataFields())
    {
      var isDescription = string.Equals(Safe(() => field.Name), "PrtD", StringComparison.OrdinalIgnoreCase)
        || (Safe(() => field.Description)?.Contains("part description", StringComparison.OrdinalIgnoreCase) ?? false);
      if (isDescription && Safe(() => field.Value) is string text && !string.IsNullOrWhiteSpace(text)) return text;
    }
    return null;
  }

  /// <summary>sizes: names (size name or part description) and/or indexes (0-based, in the family's order).</summary>
  private static List<(string? Name, int? Index)>? Selectors(JsonObject? parameters)
  {
    if (PluginRuntime.GetParameter(parameters, "sizes") is not JsonArray array || array.Count == 0) return null;
    var selectors = new List<(string?, int?)>();
    foreach (var node in array)
    {
      if (node is JsonValue v && v.TryGetValue<int>(out var index) && index >= 0) selectors.Add((null, index));
      else if (node is JsonValue s && s.TryGetValue<string>(out var name) && !string.IsNullOrWhiteSpace(name)) selectors.Add((name.Trim(), null));
      else throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"sizes takes size names or indexes (0, 1, …), not {node?.ToJsonString() ?? "null"}. Nothing was changed.");
    }
    return selectors;
  }

  /// <summary>The sizes chosen by the selectors (all without), each once, in the family's order; an unknown name or
  /// index is refused with the sizes there are.</summary>
  private static List<(ObjectId Id, int Index)> Select(PartFamily family, List<(string? Name, int? Index)>? selectors, Transaction transaction)
  {
    var ids = SizeIds(family);
    var labels = ids.Select((id, i) => SizeLabel(transaction, id, i)).ToList();
    var chosen = new SortedSet<int>();
    if (selectors == null)
    {
      for (var i = 0; i < ids.Count; i++) chosen.Add(i);
    }
    else
    {
      foreach (var (name, index) in selectors)
      {
        if (index != null)
        {
          if (index >= ids.Count)
            throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"'{family.Name}' has no size #{index}. Sizes: {string.Join(", ", labels)}. Nothing was changed.");
          chosen.Add(index.Value);
          continue;
        }

        var found = false;
        for (var i = 0; i < ids.Count; i++)
        {
          if (ids[i].IsNull || ids[i].IsErased) continue;
          using var record = ((PartSize)transaction.GetObject(ids[i], OpenMode.ForRead)).SizeDataRecord;
          if (string.Equals(PipeNetworkCommands.SizeName(record), name, StringComparison.OrdinalIgnoreCase)
              || string.Equals(PartDescription(record), name, StringComparison.OrdinalIgnoreCase))
          {
            chosen.Add(i);
            found = true;
          }
        }
        if (!found)
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"'{family.Name}' has no size '{name}'. Sizes: {string.Join(", ", labels)}. Nothing was changed.");
      }
    }

    var result = new List<(ObjectId, int)>();
    foreach (var i in chosen)
    {
      if (ids[i].IsNull || ids[i].IsErased)
        throw new JsonRpcDispatchException("CIVIL3D.OPERATION_FAILED", $"Size #{i} of '{family.Name}' is missing from the drawing (null or erased id). Nothing was changed.");
      result.Add((ids[i], i));
    }
    return result;
  }

  // ---------------------------------------------------------------- size filter and fields

  /// <summary>The family's size filter (a new SizeFilterRecord, disposed by the caller; PartFamily.PartSizeFilter is
  /// obsolete). parts_list_sizes_add makes it with the family open for write; should that be needed, the family is
  /// upgraded (a read call is never committed; a write call leaves the family's content as it was).</summary>
  private static SizeFilterRecord? NewFilter(PartFamily family)
  {
    var filter = Safe(() => new SizeFilterRecord(family));
    if (filter != null || family.IsWriteEnabled) return filter;
    family.UpgradeOpen();
    return Safe(() => new SizeFilterRecord(family));
  }

  private static List<SizeFilterField> FilterFields(SizeFilterRecord? filter)
  {
    var fields = new List<SizeFilterField>();
    if (filter == null) return fields;
    var count = Safe(() => filter.ParamCount);
    for (var i = 0; i < count; i++)
    {
      var index = i;
      if (Safe(() => filter[index]) is SizeFilterField field) fields.Add(field);
    }
    return fields;
  }

  private static Dictionary<string, object?> DescribeFilterField(SizeFilterField field) => new()
  {
    ["name"] = Safe(() => field.Name),
    ["description"] = Safe(() => field.Description),
    ["context"] = Safe(() => field.Context.ToString()),
    ["dataSource"] = Safe(() => field.DataSource.ToString()),
    ["readOnly"] = Safe<bool?>(() => field.IsReadOnly),
    ["list"] = ListValues(field)?.Take(MaxListValues).Select(Plain).ToList(),
    ["range"] = Safe(() => field.IsFromRange) ? Safe(() => new Dictionary<string, object?>
    {
      ["min"] = Plain(field.ValueRange.RangeMin), ["max"] = Plain(field.ValueRange.RangeMax), ["default"] = Plain(field.ValueRange.RangeDefault),
    }) : null,
  };

  private static List<object?>? ListValues(PartDataField field)
  {
    if (!Safe(() => field.IsFromList)) return null;
    var list = Safe(() => field.ValueList);
    if (list == null) return null;
    var values = new List<object?>();
    var count = Safe(() => list.Count);
    for (var i = 0; i < count; i++)
    {
      var index = i;
      values.Add(Safe(() => list[index]));
    }
    return values;
  }

  /// <summary>Why a field of a size cannot be written in the parts list, or null when it can.</summary>
  private static string? WhyNotWritable(PartDataField field, SizeFilterField? source, bool filterRead)
  {
    if (Safe<bool?>(() => field.IsReadOnly) ?? true) return "it is read-only in the size data";
    if (!filterRead) return "the family's size filter cannot be read, so how the catalog fills the field is unknown";
    if (source == null) return "it is not a parameter of the family's size filter (Civil 3D calculates it or takes it from the catalog)";
    var dataSource = Safe(() => source.DataSource);
    return dataSource switch
    {
      _ when WritableSources.Contains(dataSource) => null,
      PartDataSourceType.Constant => "the catalog gives it one value for every size (data source Constant)",
      PartDataSourceType.BasicTable => "it is a column of the catalog table and picks the catalog row the size belongs to (data source BasicTable); add the size with the wanted value instead",
      PartDataSourceType.Calculation => "Civil 3D calculates it from other parameters (data source Calculation)",
      _ => $"its data source is {dataSource}",
    };
  }

  /// <summary>The value to write, checked against the list or range the catalog allows (the list's own spelling is used).</summary>
  private static object Allowed(PartDataField field, SizeFilterField? source, object value, string family)
  {
    foreach (var candidate in new PartDataField?[] { source, field })
    {
      if (candidate == null) continue;
      var list = ListValues(candidate);
      if (list is { Count: > 0 })
      {
        var match = list.FirstOrDefault(v => Same(v, value));
        if (match == null)
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
            $"'{Plain(value)}' is not allowed for '{Label(field)}' of '{family}'. Allowed: {string.Join(", ", list.Take(MaxListValues).Select(v => Plain(v)))}. Nothing was changed.");
        return match;
      }

      if (Safe(() => candidate.IsFromRange) && Safe(() => candidate.ValueRange) is PartDataRange range)
      {
        bool valid;
        try
        {
          valid = range.IsValidValue(value);
        }
        catch (Exception ex) when (ex is not JsonRpcDispatchException)
        {
          valid = false;
        }
        if (!valid)
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
            $"{Plain(value)} is outside the range of '{Label(field)}' of '{family}' ({Plain(Safe(() => range.RangeMin))} to {Plain(Safe(() => range.RangeMax))}). Nothing was changed.");
        return value;
      }
    }
    return value;
  }

  /// <summary>
  /// C148: the data of drawn parts (Part.PartData) by network and part name. Use case (pilot project, 2026-10-20/21;
  /// requested: extend the plugin): a pigging chamber is a manhole (family Nedstigningsbrunn) DN 1600, but the catalog family's inner diameter (SID) is a
  /// list (1000, 1350, 1500, 1800, ...) without 1600, so neither network_build nor parts_list_size_data_set can give it. A drawn
  /// part keeps its own copy of the size data; this writes fields of that copy (the record read, changed and assigned back
  /// with the Part.PartData setter) and reads them back. Values outside the catalog's list are allowed here on purpose (the
  /// part, not the parts list, is changed); read-only fields are refused. A value Civil 3D does not keep aborts the
  /// transaction, so nothing stays. Params: networkName, parts [names], values {field: value} (name, context or description),
  /// dryRun.
  /// </summary>
  public static Task<object?> SetPartDataAsync(JsonObject? parameters)
  {
    var networkName = PluginRuntime.GetRequiredString(parameters, "networkName").Trim();
    var names = (PluginRuntime.GetParameter(parameters, "parts") as JsonArray)?.Select(n => n?.ToString()?.Trim()).OfType<string>().Where(n => n.Length > 0).ToList();
    if (names == null || names.Count == 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Give parts: [part names]. Nothing was changed.");
    if (PluginRuntime.GetParameter(parameters, "values") is not JsonObject values || values.Count == 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Give values: {field: value}, e.g. {\"SID\": 1600}. Nothing was changed.");
    var dryRun = PluginRuntime.GetOptionalBool(parameters, "dryRun") ?? false;

    object? Body(Autodesk.AutoCAD.ApplicationServices.Document doc, CivilDocument civilDoc, Database database, Transaction transaction)
    {
      var network = PipeNetworkCommands.FindPipeNetworkByName(civilDoc, transaction, networkName, OpenMode.ForRead);
      var byName = new Dictionary<string, ObjectId>(StringComparer.OrdinalIgnoreCase);
      foreach (ObjectId id in network.GetStructureIds()) byName[CivilObjectUtils.GetRequiredObject<Part>(transaction, id, OpenMode.ForRead).Name] = id;
      foreach (ObjectId id in network.GetPipeIds()) byName[CivilObjectUtils.GetRequiredObject<Part>(transaction, id, OpenMode.ForRead).Name] = id;
      var result = new List<Dictionary<string, object?>>();
      foreach (var name in names)
      {
        if (!byName.TryGetValue(name, out var id))
          throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Part '{name}' was not found in network '{networkName}'. Nothing was changed.");
        var part = CivilObjectUtils.GetRequiredObject<Part>(transaction, id, dryRun ? OpenMode.ForRead : OpenMode.ForWrite);
        var record = part.PartData;
        var all = record.GetAllDataFields();
        var changes = new List<Dictionary<string, object?>>();
        foreach (var (key, node) in values)
        {
          var field = all.FirstOrDefault(f => string.Equals(Safe(() => f.Name), key.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(f => Matches(f, key))
            ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
              $"Part '{name}' has no field '{key}'. Fields: {string.Join(", ", all.Select(Label))}. Nothing was changed.");
          if (Safe(() => field.IsReadOnly))
            throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Field '{Label(field)}' of part '{name}' is read-only. Nothing was changed.");
          var target = Convert(node, field.DataType, field.Name);
          var before = Plain(Safe(() => field.Value));
          if (!dryRun)
          {
            try { field.Value = target; }
            catch (Exception ex) when (ex is not JsonRpcDispatchException)
            {
              throw new JsonRpcDispatchException("CIVIL3D.OPERATION_FAILED", $"Civil 3D refused {field.Name} = {Plain(target)} on part '{name}': {ex.Message} Nothing was changed.");
            }
          }
          changes.Add(new Dictionary<string, object?> { ["field"] = Label(field), ["before"] = before, ["after"] = Plain(target) });
        }
        if (!dryRun)
        {
          part.PartData = record;
          var back = part.PartData;
          foreach (var c in changes)
          {
            var f = back.GetAllDataFields().FirstOrDefault(x => Label(x) == (string?)c["field"]);
            c["readBack"] = Plain(Safe(() => f?.Value));
            if (!Same(c["readBack"], c["after"]))
              throw new JsonRpcDispatchException("CIVIL3D.OPERATION_FAILED",
                $"Civil 3D did not keep {c["field"]} = {c["after"]} on part '{name}' (read back {c["readBack"]}). Nothing was changed.");
          }
        }
        result.Add(new Dictionary<string, object?> { ["part"] = name, ["changes"] = changes,
          ["innerDiameterOrWidth"] = part is Structure st ? Safe(() => st.InnerDiameterOrWidth) : null });
      }
      return new Dictionary<string, object?> { ["network"] = networkName, ["dryRun"] = dryRun, ["parts"] = result,
        ["note"] = "Only the drawn parts are changed; a rebuild of the network (network_build replace) gives the parts list size again." };
    }

    return dryRun ? CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) => Body(doc, civilDoc, database, transaction))
                  : CivilExecution.WriteAsync<object?>(Body);
  }

  private static object Convert(JsonNode? node, PartCatalogDataType type, string field)
  {
    var text = node is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : node?.ToJsonString() ?? "";
    switch (type)
    {
      case PartCatalogDataType.String:
        if (text.Length == 0) break;
        return text;
      case PartCatalogDataType.Double:
        if (double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)) return number;
        break;
      case PartCatalogDataType.Int:
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole)) return whole;
        break;
      case PartCatalogDataType.Bool:
        if (bool.TryParse(text, out var flag)) return flag;
        break;
    }
    throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"'{text}' is not a valid {type} for field '{field}'. Nothing was changed.");
  }

  /// <summary>A field by context (e.g. Material) or description, without case.</summary>
  private static bool Matches(PartDataField field, string key)
  {
    var wanted = key.Trim();
    return string.Equals(Safe(() => field.Name), wanted, StringComparison.OrdinalIgnoreCase)
      || string.Equals(Safe(() => field.Context.ToString()), wanted, StringComparison.OrdinalIgnoreCase)
      || string.Equals(Safe(() => field.Description), wanted, StringComparison.OrdinalIgnoreCase);
  }

  private static string Label(PartDataField field)
  {
    var description = Safe(() => field.Description);
    return string.IsNullOrWhiteSpace(description) ? Safe(() => field.Name) ?? "?" : $"{Safe(() => field.Name)} ({description})";
  }

  private static bool Same(object? a, object? b) => (a, b) switch
  {
    (null, null) => true,
    (null, _) or (_, null) => false,
    (string x, string y) => string.Equals(x.Trim(), y.Trim(), StringComparison.OrdinalIgnoreCase),
    (bool x, bool y) => x == y,
    (IConvertible x, IConvertible y) when IsNumber(x) && IsNumber(y) =>
      Math.Abs(System.Convert.ToDouble(x, CultureInfo.InvariantCulture) - System.Convert.ToDouble(y, CultureInfo.InvariantCulture))
        <= 1e-9 * Math.Max(1.0, Math.Abs(System.Convert.ToDouble(y, CultureInfo.InvariantCulture))),
    _ => string.Equals(System.Convert.ToString(a, CultureInfo.InvariantCulture), System.Convert.ToString(b, CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase),
  };

  private static bool IsNumber(object value) => value is double or float or int or long or short or decimal;

  private static object? Plain(object? value) => value is null or string or double or int or bool ? value : value.ToString();
}
