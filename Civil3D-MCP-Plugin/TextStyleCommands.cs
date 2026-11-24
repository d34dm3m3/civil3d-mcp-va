using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.GraphicsInterface;

namespace Civil3DMcpPlugin;

/// <summary>
/// Text styles (C67): list them with their fonts and whether the font file is found, and set or create one.
/// Use case: the company title block used isocp.shx, which lacks the en dash, and showed "?NDRINGEN" and "GODK?ND".
/// A font is given as a file (.shx or .ttf, resolved like AutoCAD does, TrueType also in the Windows font folder)
/// or as a TrueType typeface. Existing text picks up the change at the next regeneration (plots regenerate).
/// </summary>
public static class TextStyleCommands
{
  public static Task<object?> ListTextStylesAsync(JsonObject? parameters)
  {
    var contains = PluginRuntime.GetOptionalString(parameters, "contains");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var table = (TextStyleTable)transaction.GetObject(database.TextStyleTableId, OpenMode.ForRead);
      var styles = new List<Dictionary<string, object?>>();
      var shapeFiles = 0;
      foreach (ObjectId id in table)
      {
        var record = (TextStyleTableRecord)transaction.GetObject(id, OpenMode.ForRead);
        if (record.IsShapeFile) { shapeFiles++; continue; }   // linetype shape files, not text styles
        if (!string.IsNullOrWhiteSpace(contains) && record.Name.IndexOf(contains, StringComparison.OrdinalIgnoreCase) < 0) continue;
        styles.Add(Describe(record, database));
      }

      return new Dictionary<string, object?>
      {
        ["styles"] = styles.OrderBy(s => (string?)s["name"], StringComparer.OrdinalIgnoreCase).ToList(),
        ["shapeFiles"] = shapeFiles,
      };
    });
  }

  public static Task<object?> SetTextStyleAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var fileName = PluginRuntime.GetOptionalString(parameters, "fileName");
    var typeface = PluginRuntime.GetOptionalString(parameters, "typeface");
    var bigFont = PluginRuntime.GetOptionalString(parameters, "bigFontFileName");
    var height = PluginRuntime.GetOptionalDouble(parameters, "height");
    var widthFactor = PluginRuntime.GetOptionalDouble(parameters, "widthFactor");
    var obliquing = PluginRuntime.GetOptionalDouble(parameters, "obliquingDegrees");
    var create = PluginRuntime.GetOptionalBool(parameters, "create") ?? false;
    var makeCurrent = PluginRuntime.GetOptionalBool(parameters, "makeCurrent") ?? false;

    if (fileName != null && !IsFontFile(fileName))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"fileName '{fileName}' must be a .shx or .ttf font file. Nothing was changed.");
    if (height is < 0) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "height must be 0 (variable) or positive. Nothing was changed.");
    if (widthFactor is <= 0) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "widthFactor must be positive. Nothing was changed.");
    if (obliquing is < -85 or > 85) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "obliquingDegrees must lie between -85 and 85. Nothing was changed.");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      // Resolve the fonts before anything is touched.
      string? resolvedFile = null;
      if (!string.IsNullOrWhiteSpace(fileName))
        resolvedFile = ResolveFont(database, fileName) ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
          $"Font file '{fileName}' was not found on the AutoCAD support path or in the Windows font folder. Nothing was changed.");
      if (!string.IsNullOrWhiteSpace(bigFont) && ResolveFont(database, bigFont) == null)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Big font file '{bigFont}' was not found. Nothing was changed.");

      var table = (TextStyleTable)transaction.GetObject(database.TextStyleTableId, OpenMode.ForRead);
      TextStyleTableRecord record;
      var created = false;
      if (table.Has(name))
      {
        record = (TextStyleTableRecord)transaction.GetObject(table[name], OpenMode.ForWrite);
        if (record.IsShapeFile)
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"'{name}' is a shape file, not a text style. Nothing was changed.");
      }
      else
      {
        if (!create)
          throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Text style '{name}' was not found (pass create: true to create it). Nothing was changed.");
        record = new TextStyleTableRecord { Name = name };
        table.UpgradeOpen();
        table.Add(record);
        transaction.AddNewlyCreatedDBObject(record, true);
        created = true;
      }

      var before = created ? null : Describe(record, database);
      if (!string.IsNullOrWhiteSpace(fileName))
      {
        record.FileName = fileName;
        // A TrueType file keeps the given typeface (or none); a shape font must not keep an old typeface,
        // which would otherwise take precedence over the .shx file.
        var keepFace = fileName.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) ? typeface ?? "" : "";
        record.Font = new FontDescriptor(keepFace, false, false, 0, 0);
      }
      else if (!string.IsNullOrWhiteSpace(typeface))
      {
        record.Font = new FontDescriptor(typeface, record.Font.Bold, record.Font.Italic, record.Font.CharacterSet, record.Font.PitchAndFamily);
      }
      if (bigFont != null) record.BigFontFileName = bigFont;
      if (height != null) record.TextSize = height.Value;
      if (widthFactor != null) record.XScale = widthFactor.Value;
      if (obliquing != null) record.ObliquingAngle = obliquing.Value * Math.PI / 180.0;
      if (makeCurrent) database.Textstyle = record.ObjectId;

      return new Dictionary<string, object?>
      {
        ["name"] = record.Name,
        ["created"] = created,
        ["before"] = before,
        ["after"] = Describe(record, database),
        ["resolvedFontFile"] = resolvedFile,
        ["note"] = "Existing text, attributes and labels show the new font after the next regeneration; plots regenerate.",
      };
    });
  }

  private static bool IsFontFile(string fileName) =>
    fileName.EndsWith(".shx", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase);

  private static Dictionary<string, object?> Describe(TextStyleTableRecord record, Database database)
  {
    var file = string.IsNullOrWhiteSpace(record.FileName) ? null : record.FileName;
    var face = string.IsNullOrWhiteSpace(record.Font.TypeFace) ? null : record.Font.TypeFace;
    return new Dictionary<string, object?>
    {
      ["name"] = record.Name,
      ["fileName"] = file,
      ["typeface"] = face,
      ["bigFontFileName"] = string.IsNullOrWhiteSpace(record.BigFontFileName) ? null : record.BigFontFileName,
      ["kind"] = face != null ? "truetype" : file != null && file.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) ? "truetype-file" : "shx",
      ["fontFound"] = file == null ? (bool?)null : ResolveFont(database, file) != null,
      ["height"] = Math.Round(record.TextSize, 6),
      ["widthFactor"] = Math.Round(record.XScale, 6),
      ["obliquingDegrees"] = Math.Round(record.ObliquingAngle * 180.0 / Math.PI, 4),
      ["annotative"] = record.Annotative == AnnotativeStates.True,
      ["current"] = record.ObjectId == database.Textstyle,
    };
  }

  private static string? ResolveFont(Database database, string fileName)
  {
    var hint = fileName.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) ? FindFileHint.TrueTypeFontFile : FindFileHint.FontFile;
    try
    {
      var found = HostApplicationServices.Current.FindFile(fileName, database, hint);
      if (!string.IsNullOrWhiteSpace(found) && File.Exists(found)) return found;
    }
    catch (Exception)
    {
      // FindFile throws when the file is not on the support path.
    }

    var windowsFonts = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), Path.GetFileName(fileName));
    return File.Exists(windowsFonts) ? windowsFonts : null;
  }
}
