using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using ObjectLayerModifierType = Autodesk.Civil.Settings.ObjectLayerModifierType;
using SettingsObjectLayer = Autodesk.Civil.Settings.SettingsObjectLayer;
using SettingsObjectLayerType = Autodesk.Civil.Settings.SettingsObjectLayerType;

namespace Civil3DMcpPlugin;

/// <summary>
/// Layer standard (C88): a layer list from CSV into the drawing (C88a: create and update layers with colour, linetype,
/// lineweight, plot, description, lock, freeze, on, transparency), linetypes from a .lin file (C88b), Civil's object
/// layers (C88c, Drawing Settings → Object Layers: the layer Civil puts each new object type on, with prefix/suffix
/// modifier), named layer states (C88d) and purging unused layers (C88e). Use case: the company template is to carry the company layer standard, its object layers and named
/// layer states without keying them in through the Layer Properties Manager, the Drawing Settings dialog and the Layer
/// States Manager, and project drawings are to be brought to the same standard from the same files. The existing
/// layer_list/layer_set (DisplayCommands) are left as they are. Every row, layer, linetype, object type and state a call
/// names is read and checked before the first change ("Nothing was changed.").
/// </summary>
public static class LayerStandardCommands
{
  private enum Column { Layer, Color, Linetype, Lineweight, Plot, Description, Locked, Frozen, On, Transparency }

  // Header names compared without case, Swedish diacritics (folded to a/a/o) and punctuation: Swedish as in the layer standard, or English.
  private static readonly Dictionary<string, Column> Headers = new(StringComparer.Ordinal)
  {
    ["lager"] = Column.Layer, ["lagernamn"] = Column.Layer, ["layer"] = Column.Layer, ["layername"] = Column.Layer, ["namn"] = Column.Layer, ["name"] = Column.Layer,
    ["farg"] = Column.Color, ["color"] = Column.Color, ["colour"] = Column.Color,
    ["linjetyp"] = Column.Linetype, ["linetype"] = Column.Linetype,
    ["linjebredd"] = Column.Lineweight, ["lineweight"] = Column.Lineweight,
    ["plott"] = Column.Plot, ["plot"] = Column.Plot,
    ["beskrivning"] = Column.Description, ["description"] = Column.Description,
    ["las"] = Column.Locked, ["last"] = Column.Locked, ["locked"] = Column.Locked, ["lock"] = Column.Locked,
    ["fryst"] = Column.Frozen, ["frozen"] = Column.Frozen, ["freeze"] = Column.Frozen,
    ["tand"] = Column.On, ["on"] = Column.On, ["pa"] = Column.On,
    ["genomskinlighet"] = Column.Transparency, ["transparens"] = Column.Transparency, ["transparency"] = Column.Transparency,
  };

  // C88e: AutoCAD's layer transparency goes to 90 percent (the Layer Properties Manager refuses more).
  private const double MaxTransparencyPercent = 90;

  // C88e: layers_purge counts entities per layer for its reasons up to this many entities.
  private const int MaxUsageScan = 200_000;

  private sealed record LayerRow(int Line, string Name, short? Color, string? Linetype, LineWeight? Lineweight, bool? Plot,
    string? Description, bool? Locked, bool? Frozen, bool? On, double? Transparency);

  private sealed record RowError(int Line, string? Layer, string Message, bool MissingLinetype = false);

  private sealed record CsvLayers(List<LayerRow> Rows, List<RowError> Errors, List<string> IgnoredColumns, char Separator, int DataRows);

  private static readonly HashSet<string> BuiltInLinetypes = new(StringComparer.OrdinalIgnoreCase) { "ByLayer", "ByBlock", "Continuous" };

  private static readonly LayerStateMasks[] MaskFlags =
  {
    LayerStateMasks.On, LayerStateMasks.Frozen, LayerStateMasks.Locked, LayerStateMasks.Plot, LayerStateMasks.NewViewport,
    LayerStateMasks.Color, LayerStateMasks.LineType, LayerStateMasks.LineWeight, LayerStateMasks.PlotStyle,
    LayerStateMasks.CurrentViewport, LayerStateMasks.Transparency,
  };

  // The .NET enum has no All (ObjectARX kAll): every property flag, without LastRestored.
  private static readonly LayerStateMasks AllMasks = MaskFlags.Aggregate(LayerStateMasks.None, (all, flag) => all | flag);

  private static readonly HashSet<string> ObjectLayerKeys = new(StringComparer.Ordinal) { "layer", "modifier", "modifierValue", "locked" };

  private sealed record ObjectLayerChange(string Key, SettingsObjectLayerType Type, string? Layer, ObjectLayerModifierType? Modifier,
    string? ModifierValue, bool? Locked);

  // ---------------------------------------------------------------- C88a layers from CSV

  /// <summary>
  /// C88a: layers from a CSV file (UTF-8 with or without BOM; a file that is not valid UTF-8 is read as Windows-1252,
  /// Excel's "CSV (semicolon delimited)"): header row, semicolon or comma, columns Lager, Farg (ACI 1-255; ByLayer or
  /// empty leaves the colour, a layer has no ByLayer colour of its own), Linjetyp (loaded, or loaded from linFile),
  /// Linjebredd (hundredths of a mm, mm with a decimal comma, or Default), Plott, Beskrivning and optionally Las, Fryst,
  /// Tand (ja/nej) and Genomskinlighet (C88e: 0-90 percent, as the Layer Properties Manager). An empty cell changes nothing. Missing layers are created (create), existing ones get the row's
  /// values (update). Any invalid row stops the whole import with its row number; dryRun reports the plan and the
  /// errors without changing anything.
  /// </summary>
  public static Task<object?> ImportLayersAsync(JsonObject? parameters)
  {
    var path = FileBoundary.ResolveImportPath(PluginRuntime.GetRequiredString(parameters, "path"), ".csv", ".txt");
    var create = PluginRuntime.GetOptionalBool(parameters, "create") ?? true;
    var update = PluginRuntime.GetOptionalBool(parameters, "update") ?? true;
    var dryRun = PluginRuntime.GetOptionalBool(parameters, "dryRun") ?? false;
    var linFile = PluginRuntime.GetOptionalString(parameters, "linFile");
    if (!create && !update && !dryRun)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
        "create and update are both false, so nothing would change; use dryRun to only check the file. Nothing was changed.");
    var text = DecodeText(ReadFile(path), out var encoding);
    var csv = ReadLayerRows(text, Path.GetFileName(path));
    if (csv.DataRows == 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{Path.GetFileName(path)} has no layer rows under the header. Nothing was changed.");

    object? Body(Autodesk.AutoCAD.ApplicationServices.Document doc, Autodesk.Civil.ApplicationServices.CivilDocument civilDoc, Database database, Transaction transaction)
    {
      var table = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
      var linetypes = (LinetypeTable)transaction.GetObject(database.LinetypeTableId, OpenMode.ForRead);
      string? linPath = null;
      Dictionary<string, string>? linNames = null;
      if (linFile != null)
      {
        linPath = ResolveLinFile(linFile, database);
        linNames = ReadLinNames(linPath);
      }

      var problems = new List<RowError>(csv.Errors);
      foreach (var row in csv.Rows)
      {
        // C88f (2026-10-11): an existing xref-dependent layer ("Xref|Layer") may be updated, as a host-side override kept
        // by VISRETAIN (a utility owner's drawing: the administrative boundaries grey like the base map without touching their
        // file); such a layer is never created.
        var dependent = row.Name.Contains('|') && table.Has(row.Name)
          && ((LayerTableRecord)transaction.GetObject(table[row.Name], OpenMode.ForRead)).IsDependent;
        if (!dependent)
        {
          try
          {
            SymbolUtilityServices.ValidateSymbolName(row.Name, false);
          }
          catch (Autodesk.AutoCAD.Runtime.Exception)
          {
            problems.Add(new RowError(row.Line, row.Name, $"'{row.Name}' is not a valid layer name"));
            continue;
          }
        }

        if (row.Linetype != null)
        {
          if (string.Equals(row.Linetype, "ByLayer", StringComparison.OrdinalIgnoreCase) || string.Equals(row.Linetype, "ByBlock", StringComparison.OrdinalIgnoreCase))
            problems.Add(new RowError(row.Line, row.Name, "a layer's linetype must be a real linetype, not ByLayer/ByBlock"));
          else if (!linetypes.Has(row.Linetype) && (linNames == null || !linNames.ContainsKey(row.Linetype)))
            problems.Add(new RowError(row.Line, row.Name, linNames == null
              ? $"linetype '{row.Linetype}' is not loaded (give linFile to load it)"
              : $"linetype '{row.Linetype}' is neither loaded nor in {Path.GetFileName(linPath)}", MissingLinetype: true));
        }

        if (row.Frozen == true && table.Has(row.Name) && table[row.Name] == database.Clayer)
          problems.Add(new RowError(row.Line, row.Name, "the current layer cannot be frozen"));
      }

      problems = problems.OrderBy(p => p.Line).ToList();
      if (problems.Count > 0 && !dryRun) throw RowErrors(problems, path);

      var bad = problems.Select(p => p.Line).ToHashSet();
      var created = new List<string>();
      var changed = new List<Dictionary<string, object?>>();
      var unchanged = new List<string>();
      var skipped = new List<Dictionary<string, object?>>();
      var toCreate = new List<LayerRow>();
      var toUpdate = new List<(LayerRow Row, ObjectId Id)>();
      foreach (var row in csv.Rows)
      {
        if (bad.Contains(row.Line)) continue;                              // dry run with errors: the rest is still planned
        if (table.Has(row.Name))
        {
          var layer = (LayerTableRecord)transaction.GetObject(table[row.Name], OpenMode.ForRead);
          if (!update)
          {
            skipped.Add(new Dictionary<string, object?> { ["layer"] = layer.Name, ["row"] = row.Line, ["reason"] = "exists; update is false" });
            continue;
          }

          var changes = Diff(layer, row, transaction);
          if (changes.Count == 0) unchanged.Add(layer.Name);
          else
          {
            changed.Add(new Dictionary<string, object?> { ["layer"] = layer.Name, ["row"] = row.Line, ["changes"] = changes });
            toUpdate.Add((row, layer.ObjectId));
          }
        }
        else if (!create)
        {
          skipped.Add(new Dictionary<string, object?> { ["layer"] = row.Name, ["row"] = row.Line, ["reason"] = "missing; create is false" });
        }
        else
        {
          created.Add(row.Name);
          toCreate.Add(row);
        }
      }

      // Only linetypes the created or changed layers need are loaded (a planned row's unloaded linetype is in linFile).
      var load = toCreate.Concat(toUpdate.Select(u => u.Row)).Select(r => r.Linetype).OfType<string>()
        .Where(name => !linetypes.Has(name)).Select(name => linNames![name])
        .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();

      if (!dryRun)
      {
        foreach (var name in load) LoadLinetype(database, linetypes, name, linPath!);
        if (toCreate.Count > 0) table.UpgradeOpen();
        foreach (var row in toCreate)
        {
          // Added before the properties are set: a layer description is stored with the database (xdata).
          var record = new LayerTableRecord { Name = row.Name };
          table.Add(record);
          transaction.AddNewlyCreatedDBObject(record, true);
          if (row.Linetype == null) record.LinetypeObjectId = database.ContinuousLinetype;
          Apply(record, row, linetypes);
        }

        foreach (var (row, id) in toUpdate) Apply((LayerTableRecord)transaction.GetObject(id, OpenMode.ForWrite), row, linetypes);
      }

      return new Dictionary<string, object?>
      {
        ["path"] = path,
        ["encoding"] = encoding,
        ["separator"] = csv.Separator.ToString(),
        ["rows"] = csv.DataRows,
        ["dryRun"] = dryRun,
        ["created"] = created,
        ["changed"] = changed,
        ["unchanged"] = unchanged,
        ["skipped"] = skipped,
        ["linetypesLoaded"] = load,
        ["errors"] = problems.Select(p => new Dictionary<string, object?> { ["row"] = p.Line, ["layer"] = p.Layer, ["error"] = p.Message }).ToList(),
        ["ignoredColumns"] = csv.IgnoredColumns,
        ["note"] = dryRun
          ? "Dry run: nothing was changed." + (problems.Count > 0 ? " Without dryRun these errors stop the whole import." : "")
          : FrozenNote(toCreate.Concat(toUpdate.Select(u => u.Row))),
      };
    }

    return dryRun ? CivilExecution.ReadAsync<object?>(Body) : CivilExecution.WriteAsync<object?>(Body);

    static string? FrozenNote(IEnumerable<LayerRow> rows) =>
      rows.Any(r => r.Frozen != null || r.On != null) ? "Freezing, thawing or turning layers on/off may need REGEN before the view updates." : null;
  }

  private static void Apply(LayerTableRecord record, LayerRow row, LinetypeTable linetypes)
  {
    if (row.Color != null) record.Color = Color.FromColorIndex(ColorMethod.ByAci, row.Color.Value);
    if (row.Linetype != null) record.LinetypeObjectId = linetypes[row.Linetype];
    if (row.Lineweight != null) record.LineWeight = row.Lineweight.Value;
    if (row.Plot != null) record.IsPlottable = row.Plot.Value;
    if (row.Description != null) record.Description = row.Description;
    if (row.Locked != null) record.IsLocked = row.Locked.Value;
    if (row.Frozen != null) record.IsFrozen = row.Frozen.Value;
    if (row.On != null) record.IsOff = !row.On.Value;
    if (row.Transparency != null) record.Transparency = new Transparency(TransparencyAlpha(row.Transparency.Value));
  }

  /// <summary>C88e: percent (0 = opaque, 90 = the most a layer takes) to the alpha AutoCAD stores (255 = opaque).</summary>
  internal static byte TransparencyAlpha(double percent) => (byte)Math.Round(255 * (100 - percent) / 100.0);

  /// <summary>A layer's transparency in percent as the Layer Properties Manager shows it; ByLayer/ByBlock/invalid by name.</summary>
  internal static object? TransparencyText(Transparency transparency) =>
    transparency.IsByAlpha ? (int)Math.Round((255 - transparency.Alpha) * 100 / 255.0)
    : transparency.IsByLayer ? "ByLayer" : transparency.IsByBlock ? "ByBlock" : transparency.IsInvalid ? null : transparency.ToString();

  private static Dictionary<string, object?> Diff(LayerTableRecord layer, LayerRow row, Transaction transaction)
  {
    var changes = new Dictionary<string, object?>();
    void Add(string key, object? from, object? to) => changes[key] = new Dictionary<string, object?> { ["from"] = from, ["to"] = to };
    if (row.Color != null && !(layer.Color.ColorMethod == ColorMethod.ByAci && layer.Color.ColorIndex == row.Color.Value))
      Add("color", ColorText(layer.Color), (int)row.Color.Value);
    if (row.Linetype != null)
    {
      var current = layer.LinetypeObjectId.IsNull ? null : (transaction.GetObject(layer.LinetypeObjectId, OpenMode.ForRead) as LinetypeTableRecord)?.Name;
      if (!string.Equals(current, row.Linetype, StringComparison.OrdinalIgnoreCase)) Add("linetype", current, row.Linetype);
    }
    if (row.Lineweight != null && layer.LineWeight != row.Lineweight.Value) Add("lineweight", WeightText(layer.LineWeight), WeightText(row.Lineweight.Value));
    if (row.Plot != null && layer.IsPlottable != row.Plot.Value) Add("plot", layer.IsPlottable, row.Plot.Value);
    if (row.Description != null && !string.Equals(layer.Description ?? "", row.Description, StringComparison.Ordinal)) Add("description", layer.Description, row.Description);
    if (row.Locked != null && layer.IsLocked != row.Locked.Value) Add("locked", layer.IsLocked, row.Locked.Value);
    if (row.Frozen != null && layer.IsFrozen != row.Frozen.Value) Add("frozen", layer.IsFrozen, row.Frozen.Value);
    if (row.On != null && layer.IsOff == row.On.Value) Add("on", !layer.IsOff, row.On.Value);
    // Compared in percent within half a percent: the Layer Properties Manager may store 50 % as alpha 127 where
    // TransparencyAlpha gives 128, and such a layer is not to be reported (and rewritten) as changed.
    if (row.Transparency != null && !(layer.Transparency.IsByAlpha
        && Math.Abs((255 - layer.Transparency.Alpha) * 100 / 255.0 - row.Transparency.Value) < 0.5))
      Add("transparency", TransparencyText(layer.Transparency), row.Transparency.Value);
    return changes;
  }

  private static object ColorText(Color color) => color.ColorMethod == ColorMethod.ByAci ? (int)color.ColorIndex : color.ToString();

  private static object WeightText(LineWeight weight) => weight switch
  {
    LineWeight.ByLineWeightDefault => "Default",
    LineWeight.ByLayer => "ByLayer",
    LineWeight.ByBlock => "ByBlock",
    _ => (int)weight,
  };

  private static JsonRpcDispatchException RowErrors(List<RowError> problems, string path)
  {
    const int shown = 40;
    var listed = problems.Take(shown).Select(p => $"row {p.Line}{(p.Layer != null ? $" ({p.Layer})" : "")}: {p.Message}");
    var more = problems.Count > shown ? $"; and {problems.Count - shown} more" : "";
    var code = problems.All(p => p.MissingLinetype) ? "CIVIL3D.OBJECT_NOT_FOUND" : "CIVIL3D.INVALID_INPUT";
    return new JsonRpcDispatchException(code,
      $"{Path.GetFileName(path)}: {problems.Count} error(s), the import was stopped: {string.Join("; ", listed)}{more}. Nothing was changed.");
  }

  private static CsvLayers ReadLayerRows(string text, string fileName)
  {
    var lines = text.Split('\n');
    var first = lines.Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
    char separator;
    var skipHint = false;
    if (first.StartsWith("sep=", StringComparison.OrdinalIgnoreCase) && first.Length >= 5)
    {
      separator = first[4];                                             // Excel's "sep=;" line before the header
      skipHint = true;
    }
    else
    {
      // Separators inside quoted header names do not count.
      int commas = 0, semicolons = 0;
      var quoted = false;
      foreach (var c in first)
      {
        if (c == '"') quoted = !quoted;
        else if (!quoted && c == ',') commas++;
        else if (!quoted && c == ';') semicolons++;
      }
      separator = commas > semicolons ? ',' : ';';
    }

    var records = ParseCsv(text, separator).Where(r => r.Fields.Any(f => f.Trim().Length > 0)).ToList();
    if (skipHint && records.Count > 0)
    {
      // Excel does not show its "sep=" line, so row numbers are counted as Excel shows them.
      records.RemoveAt(0);
      records = records.Select(r => (r.Line - 1, r.Fields)).ToList();
    }
    if (records.Count == 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{fileName} is empty. Nothing was changed.");

    var header = records[0];
    var columns = new Dictionary<Column, int>();
    var ignored = new List<string>();
    for (var i = 0; i < header.Fields.Count; i++)
    {
      var name = header.Fields[i].Trim();
      if (name.Length == 0) continue;
      if (!Headers.TryGetValue(NormalizeHeader(name), out var column))
      {
        ignored.Add(name);
        continue;
      }

      if (columns.ContainsKey(column))
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{fileName}: the header (row {header.Line}) has the {column} column twice. Nothing was changed.");
      columns[column] = i;
    }

    if (!columns.ContainsKey(Column.Layer))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
        $"{fileName}: the header (row {header.Line}) has no Lager column ({string.Join(separator, header.Fields)}). Expected Lager, Farg, Linjetyp, Linjebredd, Plott, Beskrivning and optionally Las, Fryst, Tand, Genomskinlighet. Nothing was changed.");

    var rows = new List<LayerRow>();
    var errors = new List<RowError>();
    var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    foreach (var record in records.Skip(1))
    {
      string Cell(Column column) => columns.TryGetValue(column, out var index) && index < record.Fields.Count ? record.Fields[index].Trim() : "";
      var messages = new List<string>();
      var name = Cell(Column.Layer);
      if (record.Fields.Skip(header.Fields.Count).Any(f => f.Trim().Length > 0))
        messages.Add($"{record.Fields.Count} fields, the header has {header.Fields.Count}");
      if (name.Length == 0) messages.Add("Lager is empty");
      else if (seen.TryGetValue(name, out var firstRow)) messages.Add($"layer '{name}' is also on row {firstRow}");
      else seen[name] = record.Line;

      var color = ParseColor(Cell(Column.Color), messages);
      var linetype = Cell(Column.Linetype) is { Length: > 0 } lt ? lt : null;
      var lineweight = ParseLineweight(Cell(Column.Lineweight), messages);
      var plot = ParseYesNo(Cell(Column.Plot), "Plott", messages);
      var description = Cell(Column.Description) is { Length: > 0 } d ? d : null;
      var locked = ParseYesNo(Cell(Column.Locked), "Las", messages);
      var frozen = ParseYesNo(Cell(Column.Frozen), "Fryst", messages);
      var on = ParseYesNo(Cell(Column.On), "Tand", messages);
      var transparency = ParseTransparency(Cell(Column.Transparency), messages);
      if (messages.Count > 0)
      {
        errors.AddRange(messages.Select(m => new RowError(record.Line, name.Length > 0 ? name : null, m)));
        continue;
      }

      rows.Add(new LayerRow(record.Line, name, color, linetype, lineweight, plot, description, locked, frozen, on, transparency));
    }

    return new CsvLayers(rows, errors, ignored, separator, records.Count - 1);
  }

  // A unit or note in brackets is ignored: "Linjebredd (1/100 mm)" and "Farg [ACI]" are Linjebredd and Farg.
  private static string NormalizeHeader(string header) => new(Regex.Replace(header, @"\([^)]*\)|\[[^\]]*\]", "").Trim().ToLowerInvariant()
    .Replace('å', 'a').Replace('ä', 'a').Replace('ö', 'o').Where(char.IsLetterOrDigit).ToArray());

  private static short? ParseColor(string text, List<string> messages)
  {
    if (text.Length == 0 || string.Equals(text, "ByLayer", StringComparison.OrdinalIgnoreCase)) return null;
    if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var aci) && aci is >= 1 and <= 255) return (short)aci;
    messages.Add($"colour '{text}' is not an ACI number 1-255 or ByLayer");
    return null;
  }

  private static LineWeight? ParseLineweight(string text, List<string> messages)
  {
    if (text.Length == 0) return null;
    if (string.Equals(text, "Default", StringComparison.OrdinalIgnoreCase)) return LineWeight.ByLineWeightDefault;
    int hundredths;
    if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole)) hundredths = whole;
    else if (double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var mm)) hundredths = (int)Math.Round(mm * 100);
    else
    {
      messages.Add($"lineweight '{text}' is not hundredths of a mm (35), mm (0,35) or Default");
      return null;
    }

    if (hundredths >= 0 && Enum.IsDefined((LineWeight)hundredths)) return (LineWeight)hundredths;
    messages.Add($"lineweight '{text}' is not a standard lineweight (hundredths of a mm: 0, 5, 9, 13, 15, 18, 20, 25, 30, 35, 40, 50, 53, 60, 70, 80, 90, 100, 106, 120, 140, 158, 200, 211)");
    return null;
  }

  /// <summary>C88e: Genomskinlighet in percent, 0-90, whole or with a decimal comma, a trailing % allowed; empty leaves it.</summary>
  private static double? ParseTransparency(string text, List<string> messages)
  {
    var value = text.TrimEnd('%', ' ');
    if (value.Length == 0) return null;
    if (double.TryParse(value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var percent)
        && double.IsFinite(percent) && percent is >= 0 and <= MaxTransparencyPercent)
      return percent;
    messages.Add($"Genomskinlighet '{text}' is not a percentage 0-{MaxTransparencyPercent:0}");
    return null;
  }

  private static bool? ParseYesNo(string text, string column, List<string> messages)
  {
    switch (text.ToLowerInvariant())
    {
      case "": return null;
      case "ja" or "j" or "yes" or "y" or "true" or "1" or "x": return true;
      case "nej" or "n" or "no" or "false" or "0": return false;
      default:
        messages.Add($"{column} '{text}' is not ja/nej");
        return null;
    }
  }

  /// <summary>Records with the line they start on; quoted fields may hold the separator, "" and line breaks.</summary>
  private static List<(int Line, List<string> Fields)> ParseCsv(string text, char separator)
  {
    var records = new List<(int, List<string>)>();
    var fields = new List<string>();
    var field = new StringBuilder();
    var quoted = false;
    var line = 1;
    var start = 1;
    for (var i = 0; i < text.Length; i++)
    {
      var c = text[i];
      if (quoted)
      {
        if (c == '"')
        {
          if (i + 1 < text.Length && text[i + 1] == '"')
          {
            field.Append('"');
            i++;
          }
          else quoted = false;
        }
        else
        {
          if (c == '\n') line++;
          if (c != '\r') field.Append(c);
        }
        continue;
      }

      if (c == '"' && field.ToString().Trim().Length == 0)
      {
        field.Clear();
        quoted = true;
      }
      else if (c == separator)
      {
        fields.Add(field.ToString());
        field.Clear();
      }
      else if (c == '\n')
      {
        fields.Add(field.ToString());
        field.Clear();
        records.Add((start, fields));
        fields = new List<string>();
        line++;
        start = line;
      }
      else if (c != '\r')
      {
        field.Append(c);
      }
    }

    if (quoted)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Row {start}: a quoted field is not closed. Nothing was changed.");
    if (field.Length > 0 || fields.Count > 0)
    {
      fields.Add(field.ToString());
      records.Add((start, fields));
    }
    return records;
  }

  private static byte[] ReadFile(string path)
  {
    try
    {
      return File.ReadAllBytes(path);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
      throw new JsonRpcDispatchException("CIVIL3D.FILE_IO_ERROR", $"Unable to read '{path}': {ex.Message}. Nothing was changed.");
    }
  }

  /// <summary>Text by its byte order mark; without one UTF-8 if valid, otherwise Windows-1252 (Excel's ANSI CSV, AutoCAD's .lin).</summary>
  private static string DecodeText(byte[] bytes, out string encoding)
  {
    if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
    {
      encoding = "utf-8 (BOM)";
      return new UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3);
    }
    if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
    {
      encoding = "utf-16le";
      return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
    }
    if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
    {
      encoding = "utf-16be";
      return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
    }

    try
    {
      encoding = "utf-8";
      return new UTF8Encoding(false, true).GetString(bytes);
    }
    catch (DecoderFallbackException)
    {
      encoding = "windows-1252";
      return (CodePagesEncodingProvider.Instance.GetEncoding(1252) ?? Encoding.Latin1).GetString(bytes);
    }
  }

  // ---------------------------------------------------------------- C88e purge unused layers

  /// <summary>
  /// C88e: purges layers nothing uses, by names or a name pattern (regex, case-insensitive), dry run by default.
  /// Use case: the company template and project drawings carry layers from earlier standards and imports; after layers_import
  /// the unused ones are to go without PURGE's dialog. Database.Purge decides which layers nothing refers to (entities,
  /// blocks, styles, viewport overrides …). Layer 0, Defpoints, the current layer, xref-dependent layers and the layers
  /// Civil's object layer settings name (Drawing Settings → Object Layers, held by name, so Purge does not see them) are
  /// never purged. For the layers in use the entities on them are counted per owner (Model, layouts, block definitions);
  /// a layer with none is referenced by other objects. Result: purgeable/purged, inUse with reason, skipped with reason,
  /// notFound.
  /// </summary>
  public static Task<object?> PurgeLayersAsync(JsonObject? parameters)
  {
    var names = (PluginRuntime.GetParameter(parameters, "names") as JsonArray)?.Select(n => n?.GetValue<string>()?.Trim()).OfType<string>()
      .Where(n => n.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
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

    object? Body(Autodesk.AutoCAD.ApplicationServices.Document doc, Autodesk.Civil.ApplicationServices.CivilDocument civilDoc, Database database, Transaction transaction)
    {
      var objectLayers = ObjectLayerNames(civilDoc);
      var table = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
      var candidates = new Dictionary<ObjectId, string>();
      var skipped = new List<Dictionary<string, object?>>();
      var matchedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      foreach (ObjectId id in table)
      {
        if (id.IsErased) continue;
        var layer = (LayerTableRecord)transaction.GetObject(id, OpenMode.ForRead);
        bool match;
        try
        {
          match = (names != null && names.Contains(layer.Name)) || (pattern != null && pattern.IsMatch(layer.Name));
        }
        catch (RegexMatchTimeoutException)
        {
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"pattern took too long on layer '{layer.Name}'; simplify it. Nothing was purged.");
        }
        if (!match) continue;
        matchedNames.Add(layer.Name);
        var reason = id == database.LayerZero ? "layer 0"
          : string.Equals(layer.Name, "Defpoints", StringComparison.OrdinalIgnoreCase) ? "Defpoints"
          : id == database.Clayer ? "current layer"
          : layer.IsDependent ? "xref-dependent"
          : objectLayers.Contains(layer.Name) ? "named in Civil's object layer settings (Drawing Settings → Object Layers)"
          : null;
        if (reason != null) skipped.Add(new Dictionary<string, object?> { ["layer"] = layer.Name, ["reason"] = reason });
        else candidates[id] = layer.Name;
      }

      var purgeable = new ObjectIdCollection(candidates.Keys.ToArray());
      if (purgeable.Count > 0) database.Purge(purgeable);
      var purgeableIds = purgeable.Cast<ObjectId>().ToHashSet();
      var kept = candidates.Keys.Where(id => !purgeableIds.Contains(id)).ToHashSet();
      var usage = EntityCountsByOwner(database, kept, out var scanComplete);
      var inUse = kept.Select(id => new Dictionary<string, object?>
      {
        ["layer"] = candidates[id],
        ["reason"] = usage.TryGetValue(id, out var owners)
          ? "entities: " + string.Join(", ", owners.OrderByDescending(o => o.Value).Take(4).Select(o => $"{o.Key} {o.Value}"))
            + (owners.Count > 4 ? $" and {owners.Count - 4} more owners" : "")
          : scanComplete ? "referenced by other objects (styles, viewport overrides, layer filters or similar), no entities on it"
          : "in use (the entity count stopped early in this large drawing)",
      }).OrderBy(d => (string)d["layer"]!, StringComparer.OrdinalIgnoreCase).ToList();
      var purged = purgeableIds.Select(id => candidates[id]).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
      if (!dryRun)
      {
        foreach (var id in purgeableIds) transaction.GetObject(id, OpenMode.ForWrite).Erase();
      }

      return new Dictionary<string, object?>
      {
        ["dryRun"] = dryRun,
        [dryRun ? "wouldPurge" : "purged"] = purged,
        ["inUse"] = inUse,
        ["skipped"] = skipped.OrderBy(d => (string)d["layer"]!, StringComparer.OrdinalIgnoreCase).ToList(),
        ["notFound"] = names?.Where(n => !matchedNames.Contains(n)).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList() ?? new List<string>(),
        ["usageCountComplete"] = kept.Count == 0 ? null : scanComplete,
      };
    }

    return dryRun ? CivilExecution.ReadAsync<object?>(Body) : CivilExecution.WriteAsync<object?>(Body);
  }

  /// <summary>Layer names Civil's object layer settings hold (a type the drawing cannot give is skipped).</summary>
  private static HashSet<string> ObjectLayerNames(Autodesk.Civil.ApplicationServices.CivilDocument civilDoc)
  {
    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var settings = civilDoc.Settings.DrawingSettings.ObjectLayerSettings;
    foreach (var type in Enum.GetValues<SettingsObjectLayerType>())
    {
      try
      {
        if (settings.GetObjectLayerSetting(type).LayerName is { Length: > 0 } layer) names.Add(layer);
      }
      catch (Exception ex) when (ex is not JsonRpcDispatchException)
      {
        // not readable in this drawing (C88c lists such types with their error)
      }
    }
    return names;
  }

  /// <summary>For the given layers: entities on them per owner ("Model", a layout's name, "block 'X'"), over every block
  /// table record. Read in an open-close transaction per owner, so a large drawing is not held open at once; stops after
  /// MaxUsageScan entities (complete false), since only the reasons depend on it and a 500 MB drawing must not hit
  /// the bridge's time limit.</summary>
  private static Dictionary<ObjectId, Dictionary<string, int>> EntityCountsByOwner(Database database, HashSet<ObjectId> layers, out bool complete)
  {
    var counts = new Dictionary<ObjectId, Dictionary<string, int>>();
    complete = true;
    if (layers.Count == 0) return counts;
    var scanned = 0;
    List<ObjectId> owners;
    using (var tr = database.TransactionManager.StartOpenCloseTransaction())
    {
      owners = ((BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead)).Cast<ObjectId>().ToList();
      tr.Commit();
    }

    foreach (var ownerId in owners)
    {
      using var tr = database.TransactionManager.StartOpenCloseTransaction();
      var owner = (BlockTableRecord)tr.GetObject(ownerId, OpenMode.ForRead);
      if (owner.IsFromExternalReference || owner.IsDependent) continue;
      var label = !owner.IsLayout ? $"block '{owner.Name}'"
        : string.Equals(owner.Name, BlockTableRecord.ModelSpace, StringComparison.OrdinalIgnoreCase) ? "Model"
        : ((Layout)tr.GetObject(owner.LayoutId, OpenMode.ForRead)).LayoutName;
      foreach (ObjectId entityId in owner)
      {
        if (++scanned > MaxUsageScan)
        {
          complete = false;
          return counts;
        }
        if (tr.GetObject(entityId, OpenMode.ForRead) is not Entity entity || !layers.Contains(entity.LayerId)) continue;
        if (!counts.TryGetValue(entity.LayerId, out var byOwner)) counts[entity.LayerId] = byOwner = new Dictionary<string, int>();
        byOwner[label] = byOwner.GetValueOrDefault(label) + 1;
      }
      tr.Commit();
    }
    return counts;
  }

  // ---------------------------------------------------------------- C88b linetypes from a .lin file

  /// <summary>
  /// C88b: named linetypes (or all with all: true) from a .lin file into the drawing with Database.LoadLineTypeFile.
  /// Linetypes already in the drawing are skipped unless replace: true; then they are redefined from the file in place
  /// (ReplaceLinetypes; layers and entities keep the linetype). ByLayer, ByBlock and Continuous are never replaced. The file is an absolute path inside the import roots or a bare file name found in
  /// AutoCAD's support file search path (acadiso.lin), as the LINETYPE command finds it.
  /// </summary>
  public static async Task<object?> LoadLinetypesAsync(JsonObject? parameters)
  {
    var rawPath = PluginRuntime.GetRequiredString(parameters, "path");
    var all = PluginRuntime.GetOptionalBool(parameters, "all") ?? false;
    var replace = PluginRuntime.GetOptionalBool(parameters, "replace") ?? false;
    var names = (PluginRuntime.GetParameter(parameters, "names") as JsonArray)?.Select(n => n?.GetValue<string>()?.Trim() ?? "").ToList();
    if (names != null && names.Any(n => n.Length == 0))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "names must not contain empty names. Nothing was changed.");
    if (all && names is { Count: > 0 })
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Give names or all: true, not both. Nothing was changed.");
    if (!all && names is not { Count: > 0 })
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Give names (linetypes in the .lin file) or all: true. Nothing was changed.");
    var wanted = names?.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    var first = await CivilExecution.WriteAsync<(Dictionary<string, object?> Result, string Path, List<string> Reload)>((doc, civilDoc, database, transaction) =>
    {
      var path = ResolveLinFile(rawPath, database);
      var inFile = ReadLinNames(path);
      if (inFile.Count == 0)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{Path.GetFileName(path)} defines no linetypes (no *NAME lines). Nothing was changed.");
      var table = (LinetypeTable)transaction.GetObject(database.LinetypeTableId, OpenMode.ForRead);
      var load = new List<string>();
      var reload = new List<string>();
      var present = new List<string>();
      var missing = new List<string>();
      foreach (var name in all ? inFile.Values.ToList() : wanted!)
      {
        var spelled = inFile.GetValueOrDefault(name);
        if (table.Has(name))
        {
          var loaded = ((LinetypeTableRecord)transaction.GetObject(table[name], OpenMode.ForRead)).Name;
          if (!replace || BuiltInLinetypes.Contains(name)) present.Add(loaded);
          else if (spelled == null) missing.Add(loaded);                 // replace asked, but the file does not define it
          else reload.Add(spelled);
        }
        else if (spelled == null) missing.Add(name);
        else load.Add(spelled);
      }

      foreach (var name in load) LoadLinetype(database, table, name, path);
      if (reload.Count > 0 && CivilExecution.InBatch)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "linetypesLoad with replace runs on its own, not in a batch. Nothing was changed.");
      static List<string> Sorted(List<string> list) => list.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
      return (new Dictionary<string, object?>
      {
        ["path"] = path,
        ["loaded"] = Sorted(load),
        ["replaced"] = Sorted(reload),
        ["alreadyLoaded"] = Sorted(present),
        ["missingInFile"] = Sorted(missing),
        ["linetypesInFile"] = inFile.Count,
      }, path, reload);
    });

    foreach (var name in first.Reload) await ReplaceLinetypeAsync(first.Path, name);
    return first.Result;
  }

  /// <summary>Redefines a linetype the drawing has: LoadLineTypeFile refuses a name the drawing has, so the existing record
  /// is renamed aside, AutoCAD loads the linetype from the file (finding its text and shape styles as LINETYPE does), the
  /// loaded definition is copied onto the existing record (the same object, so layers and entities keep the linetype) and
  /// the loaded copy is erased. Not cloned from a side database with DuplicateRecordCloning.Replace: that may also replace
  /// the text styles a complex linetype refers to (Standard).
  /// C159 (2026-10-21): the three steps run in three committed transactions. Done in one (C88b) the file was loaded
  /// while the renamed record was still open for write, and Civil 3D crashed on a linetype already loaded
  /// in a project. A failed load renames the record back.</summary>
  private static async Task ReplaceLinetypeAsync(string path, string name)
  {
    static string TempName() => "C88-" + Guid.NewGuid().ToString("N");
    var aside = TempName();
    var existingHandle = await CivilExecution.WriteAsync((doc, civilDoc, database, transaction) =>
    {
      var table = (LinetypeTable)transaction.GetObject(database.LinetypeTableId, OpenMode.ForRead);
      var existing = (LinetypeTableRecord)transaction.GetObject(table[name], OpenMode.ForWrite);
      existing.Name = aside;
      return existing.Handle.Value;
    });

    Exception? loadError = null;
    try
    {
      await CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
      {
        LoadLinetype(database, (LinetypeTable)transaction.GetObject(database.LinetypeTableId, OpenMode.ForRead), name, path);
        return null;
      });
    }
    catch (Exception ex)
    {
      loadError = ex;
    }

    await CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var table = (LinetypeTable)transaction.GetObject(database.LinetypeTableId, OpenMode.ForRead);
      var existing = (LinetypeTableRecord)transaction.GetObject(database.GetObjectId(false, new Handle(existingHandle), 0), OpenMode.ForWrite);
      if (loadError == null && table.Has(name))
      {
        var loaded = (LinetypeTableRecord)transaction.GetObject(table[name], OpenMode.ForWrite);
        try
        {
          CopyDefinition(loaded, existing);
        }
        catch (Exception ex)
        {
          loadError = ex;                                               // the record keeps its name; the error is reported
        }
        loaded.Name = TempName();                                       // frees the name before the record is erased
        loaded.Erase();
      }
      existing.Name = name;
      return null;
    });
    if (loadError != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(loadError).Throw();
  }

  /// <summary>C159: the text of a dash, null for a dash without text (TextAt throws eNotApplicable there, live 2026-10-21 on
  /// HIDDEN; in C88b the exception aborted the replacement after the file had been loaded).</summary>
  private static string? DashText(LinetypeTableRecord record, int index)
  {
    try
    {
      return record.TextAt(index);
    }
    catch (Autodesk.AutoCAD.Runtime.Exception)
    {
      return null;
    }
  }

  private static void CopyDefinition(LinetypeTableRecord from, LinetypeTableRecord to)
  {
    to.AsciiDescription = from.AsciiDescription;
    to.Comments = from.Comments;
    to.IsScaledToFit = from.IsScaledToFit;
    to.PatternLength = from.PatternLength;
    to.NumDashes = 0;                                                   // drop the old dashes' shapes and texts
    to.NumDashes = from.NumDashes;
    for (var i = 0; i < from.NumDashes; i++)
    {
      to.SetDashLengthAt(i, from.DashLengthAt(i));
      to.SetShapeStyleAt(i, from.ShapeStyleAt(i));
      to.SetShapeNumberAt(i, from.ShapeNumberAt(i));
      to.SetShapeOffsetAt(i, from.ShapeOffsetAt(i));
      to.SetShapeScaleAt(i, from.ShapeScaleAt(i));
      to.SetShapeRotationAt(i, from.ShapeRotationAt(i));
      to.SetShapeIsUcsOrientedAt(i, from.ShapeIsUcsOrientedAt(i));
      to.SetShapeIsUprightAt(i, from.ShapeIsUprightAt(i));
      if (DashText(from, i) is { Length: > 0 } text) to.SetTextAt(i, text);
    }
  }

  /// <summary>LoadLineTypeFile takes the name as a wildcard pattern (# @ . * ? ~ [ ] ` are special, so "JIS_02_0.7" could
  /// load other linetypes too): special characters are escaped with a backquote; when that loads nothing the plain name
  /// is tried (in case the name is taken literally); a linetype still missing is an error.</summary>
  private static void LoadLinetype(Database database, LinetypeTable table, string name, string path)
  {
    const string special = "#@.*?~[]`";
    var escaped = string.Concat(name.Select(c => special.Contains(c) ? "`" + c : c.ToString()));
    if (escaped == name)
    {
      database.LoadLineTypeFile(name, path);
    }
    else
    {
      try
      {
        database.LoadLineTypeFile(escaped, path);
      }
      catch (Autodesk.AutoCAD.Runtime.Exception)
      {
        // the escaped pattern matched nothing; the plain name follows
      }
      if (!table.Has(name)) database.LoadLineTypeFile(name, path);
    }

    if (!table.Has(name))
      throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"AutoCAD did not load linetype '{name}' from {Path.GetFileName(path)}.");
  }

  /// <summary>A .lin file: an absolute path inside the import roots (FileBoundary), or a bare file name found through
  /// AutoCAD's support file search path.</summary>
  internal static string ResolveLinFile(string raw, Database database)
  {
    var trimmed = raw.Trim();
    if (!string.Equals(Path.GetExtension(trimmed), ".lin", StringComparison.OrdinalIgnoreCase))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"'{raw}' is not a .lin file. Nothing was changed.");
    if (Path.IsPathFullyQualified(trimmed)) return FileBoundary.ResolveImportPath(trimmed, ".lin");
    if (!string.Equals(Path.GetFileName(trimmed), trimmed, StringComparison.Ordinal))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
        $"Give the .lin file as an absolute path or as a bare file name such as acadiso.lin, not '{raw}'. Nothing was changed.");

    string? found = null;
    try
    {
      found = HostApplicationServices.Current.FindFile(trimmed, database, FindFileHint.Default);
    }
    catch (Autodesk.AutoCAD.Runtime.Exception)
    {
      // not in the support file search path
    }

    if (string.IsNullOrWhiteSpace(found) || !File.Exists(found))
      throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND",
        $"'{trimmed}' was not found in AutoCAD's support file search path; give an absolute path. Nothing was changed.");
    return found;
  }

  /// <summary>Linetype names defined in a .lin file ("*NAME,description" lines), by name without case, first wins.</summary>
  internal static Dictionary<string, string> ReadLinNames(string path)
  {
    var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var raw in DecodeText(ReadFile(path), out _).Split('\n'))
    {
      var line = raw.Trim();
      if (!line.StartsWith('*')) continue;
      var comma = line.IndexOf(',');
      var name = (comma < 0 ? line[1..] : line[1..comma]).Trim();
      if (name.Length > 0) names.TryAdd(name, name);
    }
    return names;
  }

  // ---------------------------------------------------------------- C88c Civil object layers

  /// <summary>C88c: Civil's object layers (Drawing Settings → Object Layers) for every SettingsObjectLayerType: layer,
  /// modifier (None, Prefix, Suffix), modifier value and lock. A type the drawing cannot give is listed with its error.</summary>
  public static Task<object?> GetObjectLayersAsync(JsonObject? parameters)
  {
    var contains = PluginRuntime.GetOptionalString(parameters, "contains");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var settings = civilDoc.Settings.DrawingSettings.ObjectLayerSettings;
      var list = new List<Dictionary<string, object?>>();
      foreach (var name in Enum.GetNames<SettingsObjectLayerType>())
      {
        if (!string.IsNullOrWhiteSpace(contains) && name.IndexOf(contains, StringComparison.OrdinalIgnoreCase) < 0) continue;
        try
        {
          list.Add(DescribeObjectLayer(name, settings.GetObjectLayerSetting(Enum.Parse<SettingsObjectLayerType>(name))));
        }
        catch (Exception ex) when (ex is not JsonRpcDispatchException)
        {
          list.Add(new Dictionary<string, object?> { ["objectType"] = name, ["error"] = ex.Message });
        }
      }

      bool? controlledByLayer = null;
      try
      {
        controlledByLayer = settings.ObjectControlledByLayer;
      }
      catch (Exception ex) when (ex is not JsonRpcDispatchException)
      {
        // not readable in this drawing; the list is still useful
      }

      return new Dictionary<string, object?> { ["objectLayers"] = list, ["objectControlledByLayer"] = controlledByLayer };
    });
  }

  /// <summary>C88c: object layers by type name: {"Pipe": {layer, modifier, modifierValue, locked}, …}. Type names and
  /// modifiers are checked against Civil's enums; the layers must exist unless createLayers.</summary>
  public static Task<object?> SetObjectLayersAsync(JsonObject? parameters)
  {
    if (PluginRuntime.GetParameter(parameters, "values") is not JsonObject values || values.Count == 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
        "Give values: {\"Pipe\": {\"layer\": \"VA-LEDN\", \"modifier\": \"Suffix\", \"modifierValue\": \"-*\"}, …}. Nothing was changed.");
    var createLayers = PluginRuntime.GetOptionalBool(parameters, "createLayers") ?? false;
    var typeNames = Enum.GetNames<SettingsObjectLayerType>();
    var plan = new List<ObjectLayerChange>();
    foreach (var (key, node) in values)
    {
      var typeName = typeNames.FirstOrDefault(n => string.Equals(n, key.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
          $"'{key}' is not a Civil object layer type. Types: {string.Join(", ", typeNames)}. Nothing was changed.");
      var type = Enum.Parse<SettingsObjectLayerType>(typeName);
      if (plan.Any(p => p.Type == type))
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{typeName} is given twice. Nothing was changed.");
      if (node is not JsonObject o || o.Count == 0)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{key}: give {{layer?, modifier?, modifierValue?, locked?}}. Nothing was changed.");
      var unknown = o.Select(p => p.Key).Where(k => !ObjectLayerKeys.Contains(k)).ToList();
      if (unknown.Count > 0)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
          $"{key}: unknown {string.Join(", ", unknown)} (give layer, modifier, modifierValue, locked). Nothing was changed.");
      var layer = o["layer"]?.GetValue<string>()?.Trim();
      if (layer is "") throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{key}: layer is empty. Nothing was changed.");
      ObjectLayerModifierType? modifier = null;
      if (o["modifier"]?.GetValue<string>() is string modifierText)
      {
        var modifierName = Enum.GetNames<ObjectLayerModifierType>().FirstOrDefault(n => string.Equals(n, modifierText.Trim(), StringComparison.OrdinalIgnoreCase))
          ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
            $"{key}: modifier '{modifierText}' is not one of {string.Join(", ", Enum.GetNames<ObjectLayerModifierType>())}. Nothing was changed.");
        modifier = Enum.Parse<ObjectLayerModifierType>(modifierName);
      }

      plan.Add(new ObjectLayerChange(typeName, type, layer, modifier, o["modifierValue"]?.GetValue<string>(), o["locked"]?.GetValue<bool>()));
    }

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var settings = civilDoc.Settings.DrawingSettings.ObjectLayerSettings;
      var layers = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
      var missing = plan.Select(p => p.Layer).OfType<string>().Where(l => !layers.Has(l)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
      if (missing.Count > 0 && !createLayers)
        throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND",
          $"Layers not found: {string.Join(", ", missing)}; pass createLayers: true to create them. Nothing was changed.");
      foreach (var name in missing)
      {
        try
        {
          SymbolUtilityServices.ValidateSymbolName(name, false);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"'{name}' is not a valid layer name. Nothing was changed.");
        }
      }

      // Every setting is read before the first one is written.
      var targets = new List<(ObjectLayerChange Change, SettingsObjectLayer Setting)>();
      foreach (var change in plan)
      {
        try
        {
          targets.Add((change, settings.GetObjectLayerSetting(change.Type)));
        }
        catch (Exception ex) when (ex is not JsonRpcDispatchException)
        {
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{change.Key}: the object layer setting cannot be read ({ex.Message}). Nothing was changed.");
        }
      }

      foreach (var name in missing) LookupUtils.GetOrCreateLayerId(database, transaction, name, true);
      var changed = new List<Dictionary<string, object?>>();
      foreach (var (change, setting) in targets)
      {
        var before = DescribeObjectLayer(change.Key, setting);
        if (change.Layer != null) setting.LayerName = ((LayerTableRecord)transaction.GetObject(layers[change.Layer], OpenMode.ForRead)).Name;
        if (change.Modifier != null) setting.Modifier = change.Modifier.Value;
        if (change.ModifierValue != null) setting.ModifierValue = change.ModifierValue;
        if (change.Locked != null) setting.Locked = change.Locked.Value;
        changed.Add(new Dictionary<string, object?> { ["objectType"] = change.Key, ["before"] = before, ["after"] = DescribeObjectLayer(change.Key, setting) });
      }

      return new Dictionary<string, object?> { ["changed"] = changed, ["createdLayers"] = missing };
    });
  }

  private static Dictionary<string, object?> DescribeObjectLayer(string objectType, SettingsObjectLayer setting) => new()
  {
    ["objectType"] = objectType,
    ["layer"] = setting.LayerName,
    ["modifier"] = setting.Modifier.ToString(),
    ["modifierValue"] = setting.ModifierValue,
    ["locked"] = setting.Locked,
  };

  // ---------------------------------------------------------------- C88d layer states

  /// <summary>C88d: named layer states (LayerStateManager): description, saved properties, viewport data, how many
  /// layers the state holds and how many drawing layers it does not know (added later); with name also the layer names.</summary>
  public static Task<object?> ListLayerStatesAsync(JsonObject? parameters)
  {
    var only = PluginRuntime.GetOptionalString(parameters, "name")?.Trim();
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var manager = database.LayerStateManager;
      var names = StateNames(manager);
      if (!string.IsNullOrEmpty(only))
      {
        if (!manager.HasLayerState(only))
          throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Layer state '{only}' not found. Layer states: {string.Join(", ", names)}.");
        names = new List<string> { names.FirstOrDefault(n => string.Equals(n, only, StringComparison.OrdinalIgnoreCase)) ?? only };
      }

      return new Dictionary<string, object?>
      {
        ["layerStates"] = names.Select(n => DescribeState(manager, n, !string.IsNullOrEmpty(only))).ToList(),
        ["lastRestored"] = string.IsNullOrEmpty(manager.LastRestoredLayerState) ? null : manager.LastRestoredLayerState,
      };
    });
  }

  /// <summary>C88d: saves the drawing's layer settings as a named state with every property (on, frozen, locked, plot,
  /// new-viewport and current-viewport freeze, colour, linetype, lineweight, plot style, transparency); with viewport
  /// (a layout viewport's handle) that viewport's VP freeze is saved with it (C88e: layer_state_restore into a viewport
  /// uses the state's on/off and freeze, not this VP data).
  /// An existing state is only replaced with overwrite: true (its description is kept unless a new one is given).</summary>
  public static Task<object?> SaveLayerStateAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name").Trim();
    var description = PluginRuntime.GetOptionalString(parameters, "description");
    var overwrite = PluginRuntime.GetOptionalBool(parameters, "overwrite") ?? false;
    var viewportHandle = PluginRuntime.GetOptionalString(parameters, "viewport")?.Trim();
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var manager = database.LayerStateManager;
      var existed = manager.HasLayerState(name);
      if (existed && !overwrite)
        throw new JsonRpcDispatchException("CIVIL3D.CONFLICT", $"Layer state '{name}' already exists; overwrite: true replaces it. Nothing was changed.");
      var viewportId = string.IsNullOrEmpty(viewportHandle) ? ObjectId.Null : FindViewportId(database, transaction, viewportHandle);
      var keptDescription = existed && description == null ? manager.GetLayerStateDescription(name) : null;
      if (existed)
      {
        try
        {
          manager.SaveLayerState(name, AllMasks, viewportId);
        }
        // C99l: Civil 3D 2026 also answers eDuplicateKey when an existing state is saved again (M40 2026-10-11)
        catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus is Autodesk.AutoCAD.Runtime.ErrorStatus.DuplicateRecordName
                                                              or Autodesk.AutoCAD.Runtime.ErrorStatus.DuplicateKey)
        {
          manager.DeleteLayerState(name);                                 // in case saving does not replace an existing state
          manager.SaveLayerState(name, AllMasks, viewportId);
        }
      }
      else
      {
        manager.SaveLayerState(name, AllMasks, viewportId);
      }

      // A given description is set as given ("" clears it); otherwise an overwritten state keeps its old one.
      if (description != null) manager.SetLayerStateDescription(name, description);
      else if (!string.IsNullOrEmpty(keptDescription)) manager.SetLayerStateDescription(name, keptDescription);
      return new Dictionary<string, object?>
      {
        ["saved"] = DescribeState(manager, name, false), ["overwritten"] = existed, ["viewport"] = string.IsNullOrEmpty(viewportHandle) ? null : viewportHandle,
      };
    });
  }

  /// <summary>C88d: restores a named layer state (every property it holds). Layers the state does not know are left as
  /// they are. C88e: with viewport (a layout viewport's handle) only that viewport's layer freeze is set: the layers the
  /// state has off or frozen are frozen in the viewport, its other layers thawed there; the drawing's global on/off and
  /// freeze stay as they were (see RestoreIntoViewport).</summary>
  public static Task<object?> RestoreLayerStateAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name").Trim();
    var viewportHandle = PluginRuntime.GetOptionalString(parameters, "viewport")?.Trim();
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var manager = database.LayerStateManager;
      if (!manager.HasLayerState(name))
        throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND",
          $"Layer state '{name}' not found. Layer states: {string.Join(", ", StateNames(manager))}. Nothing was changed.");
      if (!string.IsNullOrEmpty(viewportHandle))
        return RestoreIntoViewport(database, transaction, manager, name, FindViewportId(database, transaction, viewportHandle), viewportHandle);

      // 0 = layers not in the state are left alone (AutoCAD's default; 1 would turn them off, 2 freeze them).
      manager.RestoreLayerState(name, ObjectId.Null, 0, AllMasks);
      return new Dictionary<string, object?>
      {
        ["restored"] = DescribeState(manager, name, false),
        ["viewport"] = null,
        ["lastRestored"] = manager.LastRestoredLayerState,
        ["note"] = "REGEN may be needed before the view updates.",
      };
    });
  }

  /// <summary>
  /// C88e: a layer state into one viewport as VP freeze only. Need: the live test of 2026-10-10 showed that
  /// RestoreLayerState with a viewport also sets the drawing's global on/off and freeze to the state's, so restoring the
  /// "Plan" state into a sheet viewport switched layers off in every other viewport and in model space. .NET gives the
  /// state's layer names (GetLayerStateLayers) but not their saved on/off and freeze, so they are read by letting AutoCAD
  /// restore only On and Frozen globally, reading the layers, and putting every layer's global on/off and freeze back
  /// as it was (snapshot taken in an open-close transaction, so that no layer is held open while AutoCAD restores);
  /// then the state's off or frozen layers (by the properties the state saves) are frozen in the viewport and its other
  /// layers thawed there. Layers the state does not hold keep their VP freeze; VP freeze data saved with the state is not
  /// used. Every check comes before the restore.
  /// </summary>
  private static Dictionary<string, object?> RestoreIntoViewport(Database database, Transaction transaction, LayerStateManager manager,
    string name, ObjectId viewportId, string viewportHandle)
  {
    const LayerStateMasks visibility = LayerStateMasks.On | LayerStateMasks.Frozen;
    var mask = manager.GetLayerStateMask(name);
    if ((mask & visibility) == LayerStateMasks.None)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
        $"Layer state '{name}' saves neither on/off nor freeze, so it does not say which layers to show in the viewport. Nothing was changed.");
    List<string>? stateLayers;
    try
    {
      stateLayers = manager.GetLayerStateLayers(name, false).Cast<object?>().Select(o => o?.ToString()).OfType<string>().ToList();
    }
    catch (Autodesk.AutoCAD.Runtime.Exception ex)
    {
      throw new JsonRpcDispatchException("CIVIL3D.OPERATION_FAILED", $"The layers of state '{name}' cannot be read ({ex.Message}). Nothing was changed.");
    }
    var inState = stateLayers.ToHashSet(StringComparer.OrdinalIgnoreCase);
    bool onSaved = (mask & LayerStateMasks.On) != 0, frozenSaved = (mask & LayerStateMasks.Frozen) != 0;

    // The snapshot and the viewport layer's lock are read in an open-close transaction, so that this call's transaction
    // holds no layer record open while AutoCAD restores.
    var before = new Dictionary<ObjectId, (string Name, bool Off, bool Frozen)>();
    string? lockedViewportLayer = null;
    using (var probe = database.TransactionManager.StartOpenCloseTransaction())
    {
      var probeViewport = (Viewport)probe.GetObject(viewportId, OpenMode.ForRead);
      if (probe.GetObject(probeViewport.LayerId, OpenMode.ForRead) is LayerTableRecord { IsLocked: true } locked) lockedViewportLayer = locked.Name;
      var table = (LayerTable)probe.GetObject(database.LayerTableId, OpenMode.ForRead);
      foreach (ObjectId id in table)
      {
        if (id.IsErased) continue;
        var layer = (LayerTableRecord)probe.GetObject(id, OpenMode.ForRead);
        before[id] = (layer.Name, layer.IsOff, layer.IsFrozen);
      }
      probe.Commit();
    }
    if (lockedViewportLayer != null)
      throw new JsonRpcDispatchException("CIVIL3D.CONFLICT",
        $"Viewport {viewportHandle} is on locked layer '{lockedViewportLayer}', so its layer freeze cannot be changed. Nothing was changed.");

    var currentLayer = database.Clayer;
    manager.RestoreLayerState(name, ObjectId.Null, 0, visibility);

    // What the state gave: only the properties it saved count (a flag it does not save is the drawing's own).
    var after = new Dictionary<ObjectId, (bool Off, bool Frozen)>();
    var hide = new List<ObjectId>();
    var show = new List<ObjectId>();
    var notInState = 0;
    foreach (var (id, was) in before)
    {
      var layer = (LayerTableRecord)transaction.GetObject(id, OpenMode.ForRead);
      after[id] = (layer.IsOff, layer.IsFrozen);
      if (!inState.Contains(was.Name)) notInState++;
      else if ((onSaved && layer.IsOff) || (frozenSaved && layer.IsFrozen)) hide.Add(id);
      else show.Add(id);
    }

    // Global on/off and freeze back as they were: thaw and turn on first, then the current layer (should the restore
    // have changed it, the original must be thawed before it can be current again), then freeze and turn off.
    var putBack = new HashSet<ObjectId>();
    foreach (var (id, was) in before)
    {
      var now = after[id];
      bool thawBack = now.Frozen && !was.Frozen, onBack = now.Off && !was.Off;
      if (!thawBack && !onBack) continue;
      var layer = (LayerTableRecord)transaction.GetObject(id, OpenMode.ForWrite);
      if (thawBack) layer.IsFrozen = false;
      if (onBack) layer.IsOff = false;
      putBack.Add(id);
    }
    if (database.Clayer != currentLayer) database.Clayer = currentLayer;
    foreach (var (id, was) in before)
    {
      var now = after[id];
      bool freezeBack = !now.Frozen && was.Frozen, offBack = !now.Off && was.Off;
      if (!freezeBack && !offBack) continue;
      var layer = (LayerTableRecord)transaction.GetObject(id, OpenMode.ForWrite);
      if (freezeBack) layer.IsFrozen = true;
      if (offBack) layer.IsOff = true;
      putBack.Add(id);
    }
    var globalReverted = putBack.Count;

    var viewport = (Viewport)transaction.GetObject(viewportId, OpenMode.ForWrite);
    var freeze = hide.Where(id => !viewport.IsLayerFrozenInViewport(id)).ToList();
    var thaw = show.Where(id => viewport.IsLayerFrozenInViewport(id)).ToList();
    if (freeze.Count > 0) viewport.FreezeLayersInViewport(new ObjectIdCollection(freeze.ToArray()).GetEnumerator());
    if (thaw.Count > 0) viewport.ThawLayersInViewport(new ObjectIdCollection(thaw.ToArray()).GetEnumerator());
    List<string> Names(IEnumerable<ObjectId> ids) => ids.Select(id => before[id].Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    return new Dictionary<string, object?>
    {
      ["restored"] = DescribeState(manager, name, false),
      ["viewport"] = viewportHandle,
      ["mode"] = "viewport freeze only; global on/off and freeze unchanged",
      ["frozenInViewport"] = freeze.Count,
      ["thawedInViewport"] = thaw.Count,
      ["alreadyFrozen"] = hide.Count - freeze.Count,
      ["alreadyThawed"] = show.Count - thaw.Count,
      ["layersNotInState"] = notInState,
      ["frozenNames"] = Names(freeze),
      ["thawedNames"] = Names(thaw),
      ["globalLayersPutBack"] = globalReverted,
      ["lastRestored"] = manager.LastRestoredLayerState,
      ["note"] = "REGEN may be needed before the viewport updates. AutoCAD now records the state as last restored, although only the viewport changed.",
    };
  }

  /// <summary>C88d: deletes a named layer state.</summary>
  public static Task<object?> DeleteLayerStateAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name").Trim();
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var manager = database.LayerStateManager;
      if (!manager.HasLayerState(name))
        throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND",
          $"Layer state '{name}' not found. Layer states: {string.Join(", ", StateNames(manager))}. Nothing was changed.");
      var described = DescribeState(manager, name, false);
      manager.DeleteLayerState(name);
      return new Dictionary<string, object?> { ["deleted"] = described };
    });
  }

  private static List<string> StateNames(LayerStateManager manager) =>
    manager.GetLayerStateNames(false, false).Cast<object?>().Select(o => o?.ToString()).OfType<string>()
      .Where(n => n.Length > 0).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

  private static Dictionary<string, object?> DescribeState(LayerStateManager manager, string name, bool withLayers)
  {
    List<string>? Layers(bool invert)
    {
      try
      {
        return manager.GetLayerStateLayers(name, invert).Cast<object?>().Select(o => o?.ToString()).OfType<string>()
          .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
      }
      catch (Autodesk.AutoCAD.Runtime.Exception)
      {
        return null;
      }
    }

    var inState = Layers(false);
    var notInState = Layers(true);
    var mask = manager.GetLayerStateMask(name);
    return new Dictionary<string, object?>
    {
      ["name"] = name,
      ["description"] = manager.GetLayerStateDescription(name) is { Length: > 0 } text ? text : null,
      ["properties"] = MaskFlags.Where(f => (mask & f) == f).Select(f => f.ToString()).ToList(),
      ["viewportData"] = manager.LayerStateHasViewportData(name),
      ["layerCount"] = inState?.Count,
      ["layersNotInState"] = notInState?.Count,
      ["layers"] = withLayers ? inState : null,
      ["layersNotInStateNames"] = withLayers ? notInState : null,
    };
  }

  /// <summary>A floating viewport on a paper layout by handle; a layout's own paper space viewport is refused.</summary>
  private static ObjectId FindViewportId(Database database, Transaction transaction, string handle)
  {
    if (!long.TryParse(handle, NumberStyles.HexNumber, null, out var value)
      || !database.TryGetObjectId(new Handle(value), out var id) || id.IsErased
      || transaction.GetObject(id, OpenMode.ForRead) is not Viewport viewport
      || transaction.GetObject(viewport.OwnerId, OpenMode.ForRead) is not BlockTableRecord { IsLayout: true } space)
      throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"No layout viewport with handle {handle}. Nothing was changed.");
    var layout = (Layout)transaction.GetObject(space.LayoutId, OpenMode.ForRead);
    if (layout.ModelType || ViewportCommands.PaperViewportId(layout, transaction) == id)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
        $"{handle} is not a floating viewport (it is the paper space viewport of '{layout.LayoutName}' or a model space viewport). Nothing was changed.");
    return id;
  }
}
