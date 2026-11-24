using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using System.Text.Json.Nodes;

namespace Civil3DMcpPlugin;

/// <summary>C139-C140: dimensions and dimension styles. Use case: the
/// details were dimensioned in ezdxf drawings attached as xrefs because the plugin could not create dimensions.</summary>
public static class DimensionCommands
{
  /// <summary>C139: dimension {x1, y1, x2, y2, kind? aligned|rotated (aligned), rotationDegrees? (rotated: direction of
  /// the dimension line), offset? (distance from the measured points to the dimension line, positive to the left of
  /// p1 -> p2 or of the rotation direction; 0), lineX?/lineY? (dimension line point, overrides offset), text? (override,
  /// "&lt;&gt;" = the measured value), dimStyle? (existing style, else the current), layer?, createLayer?,
  /// color/linetype/lineweight/transparency?, layout? (paper space)}.</summary>
  public static Task<object?> CreateDimensionAsync(JsonObject? parameters)
  {
    var x1 = PluginRuntime.GetRequiredDouble(parameters, "x1");
    var y1 = PluginRuntime.GetRequiredDouble(parameters, "y1");
    var x2 = PluginRuntime.GetRequiredDouble(parameters, "x2");
    var y2 = PluginRuntime.GetRequiredDouble(parameters, "y2");
    var kind = (PluginRuntime.GetOptionalString(parameters, "kind") ?? "aligned").Trim().ToLowerInvariant();
    if (kind != "aligned" && kind != "rotated")
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"kind '{kind}' must be aligned or rotated. Nothing was created.");
    var rotationDeg = PluginRuntime.GetOptionalDouble(parameters, "rotationDegrees") ?? 0.0;
    var offset = PluginRuntime.GetOptionalDouble(parameters, "offset") ?? 0.0;
    var lineX = PluginRuntime.GetOptionalDouble(parameters, "lineX");
    var lineY = PluginRuntime.GetOptionalDouble(parameters, "lineY");
    if ((lineX == null) != (lineY == null))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Give lineX together with lineY. Nothing was created.");
    if (kind == "aligned" && Math.Abs(x2 - x1) < 1e-9 && Math.Abs(y2 - y1) < 1e-9)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "The two points coincide. Nothing was created.");
    var text = PluginRuntime.GetOptionalString(parameters, "text") ?? "";
    var styleName = PluginRuntime.GetOptionalString(parameters, "dimStyle");
    var layerName = PluginRuntime.GetOptionalString(parameters, "layer");
    var createLayer = PluginRuntime.GetOptionalBool(parameters, "createLayer") ?? false;
    var layoutName = PluginRuntime.GetOptionalString(parameters, "layout");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var styleId = database.Dimstyle;
      if (!string.IsNullOrWhiteSpace(styleName))
      {
        var table = CivilObjectUtils.GetRequiredObject<DimStyleTable>(transaction, database.DimStyleTableId, OpenMode.ForRead);
        if (!table.Has(styleName))
        {
          var names = table.Cast<ObjectId>().Select(id => ((DimStyleTableRecord)transaction.GetObject(id, OpenMode.ForRead)).Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase);
          throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Dimension style '{styleName}' does not exist. Styles: {string.Join(", ", names)}. Nothing was created.");
        }
        styleId = table[styleName];
      }
      var layerId = string.IsNullOrWhiteSpace(layerName)
        ? database.Clayer
        : LookupUtils.GetOrCreateLayerId(database, transaction, layerName, createLayer);
      var blockTable = CivilObjectUtils.GetRequiredObject<BlockTable>(transaction, database.BlockTableId, OpenMode.ForRead);
      var spaceId = string.IsNullOrWhiteSpace(layoutName) || string.Equals(layoutName, "Model", StringComparison.OrdinalIgnoreCase)
        ? blockTable[BlockTableRecord.ModelSpace]
        : LayoutCommands.FindLayout(database, transaction, layoutName!).BlockTableRecordId;
      var space = CivilObjectUtils.GetRequiredObject<BlockTableRecord>(transaction, spaceId, OpenMode.ForWrite);

      var p1 = new Point3d(x1, y1, 0);
      var p2 = new Point3d(x2, y2, 0);
      var rotation = rotationDeg * Math.PI / 180.0;
      var dir = kind == "aligned" ? new Vector2d(x2 - x1, y2 - y1).GetNormal() : new Vector2d(Math.Cos(rotation), Math.Sin(rotation));
      var normal = new Vector2d(-dir.Y, dir.X);
      var linePoint = lineX != null
        ? new Point3d(lineX.Value, lineY!.Value, 0)
        : new Point3d((x1 + x2) / 2 + normal.X * offset, (y1 + y2) / 2 + normal.Y * offset, 0);

      Dimension dim = kind == "aligned"
        ? new AlignedDimension(p1, p2, linePoint, text, styleId)
        : new RotatedDimension(rotation, p1, p2, linePoint, text, styleId);
      using (dim)
      {
        dim.SetDatabaseDefaults(database);
        dim.DimensionStyle = styleId;          // SetDatabaseDefaults sets the current style
        dim.LayerId = layerId;
        AcadCommands.ApplyEntityStyle(dim, parameters, database, transaction);
        space.AppendEntity(dim);
        transaction.AddNewlyCreatedDBObject(dim, true);
        dim.RecomputeDimensionBlock(true);
        var result = new Dictionary<string, object?>
        {
          ["handle"] = CivilObjectUtils.GetHandle(dim),
          ["kind"] = kind,
          ["measurement"] = Math.Round(dim.Measurement, 4),
          ["dimStyle"] = ((DimStyleTableRecord)transaction.GetObject(styleId, OpenMode.ForRead)).Name,
          ["layer"] = dim.Layer,
        };
        foreach (var (key, value) in AcadCommands.StyleOf(dim)) result[key] = value;
        return result;
      }
    });
  }

  /// <summary>C140: creates or changes a dimension style {name, copyFrom? (only when created), textHeight?, arrowSize?,
  /// tickSize? (oblique ticks instead of arrows when > 0), extensionOffset?, extensionExtend?, textGap?, decimals?,
  /// decimalSeparator? ("," or "."), textStyle? (existing text style), textAbove? (text above the dimension line),
  /// alignedText? (text along the line, not horizontal), color? (ACI for line, extension lines and text), scale?
  /// (overall DIMSCALE)}. Values not given are left as they are (or as copyFrom).</summary>
  public static Task<object?> SetDimStyleAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name").Trim();
    var copyFrom = PluginRuntime.GetOptionalString(parameters, "copyFrom");
    double? D(string key)
    {
      var v = PluginRuntime.GetOptionalDouble(parameters, key);
      if (v is < 0)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{key} must be >= 0. Nothing was changed.");
      return v;
    }
    var textHeight = D("textHeight");
    var arrowSize = D("arrowSize");
    var tickSize = D("tickSize");
    var extensionOffset = D("extensionOffset");
    var extensionExtend = D("extensionExtend");
    var textGap = D("textGap");
    var scale = D("scale");
    var decimals = PluginRuntime.GetOptionalInt(parameters, "decimals");
    if (decimals is < 0 or > 8)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "decimals must be 0-8. Nothing was changed.");
    var separator = PluginRuntime.GetOptionalString(parameters, "decimalSeparator");
    if (separator != null && separator.Length != 1)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "decimalSeparator must be one character. Nothing was changed.");
    var textStyle = PluginRuntime.GetOptionalString(parameters, "textStyle");
    var textAbove = PluginRuntime.GetOptionalBool(parameters, "textAbove");
    var alignedText = PluginRuntime.GetOptionalBool(parameters, "alignedText");
    var color = PluginRuntime.GetOptionalInt(parameters, "color");
    if (color is < 0 or > 256)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "color must be an ACI number 0-256. Nothing was changed.");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var table = CivilObjectUtils.GetRequiredObject<DimStyleTable>(transaction, database.DimStyleTableId, OpenMode.ForRead);
      var created = false;
      DimStyleTableRecord rec;
      if (table.Has(name))
      {
        rec = (DimStyleTableRecord)transaction.GetObject(table[name], OpenMode.ForWrite);
      }
      else
      {
        rec = new DimStyleTableRecord();
        if (!string.IsNullOrWhiteSpace(copyFrom))
        {
          if (!table.Has(copyFrom))
            throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Dimension style '{copyFrom}' (copyFrom) does not exist. Nothing was changed.");
          rec.CopyFrom(transaction.GetObject(table[copyFrom], OpenMode.ForRead));
        }
        rec.Name = name;
        table.UpgradeOpen();
        table.Add(rec);
        transaction.AddNewlyCreatedDBObject(rec, true);
        created = true;
      }
      ObjectId? textStyleId = null;
      if (!string.IsNullOrWhiteSpace(textStyle))
      {
        var ts = CivilObjectUtils.GetRequiredObject<TextStyleTable>(transaction, database.TextStyleTableId, OpenMode.ForRead);
        if (!ts.Has(textStyle))
          throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Text style '{textStyle}' does not exist. Nothing was changed.");
        textStyleId = ts[textStyle];
      }
      if (textHeight != null) rec.Dimtxt = textHeight.Value;
      if (arrowSize != null) rec.Dimasz = arrowSize.Value;
      if (tickSize != null) rec.Dimtsz = tickSize.Value;
      if (extensionOffset != null) rec.Dimexo = extensionOffset.Value;
      if (extensionExtend != null) rec.Dimexe = extensionExtend.Value;
      if (textGap != null) rec.Dimgap = textGap.Value;
      if (scale != null) rec.Dimscale = scale.Value;
      if (decimals != null) rec.Dimdec = decimals.Value;
      if (separator != null) rec.Dimdsep = separator[0];
      if (textStyleId != null) rec.Dimtxsty = textStyleId.Value;
      if (textAbove != null) rec.Dimtad = textAbove.Value ? 1 : 0;
      if (alignedText != null) { rec.Dimtih = !alignedText.Value; rec.Dimtoh = !alignedText.Value; }
      if (color != null)
      {
        var c = color.Value switch
        {
          0 => Autodesk.AutoCAD.Colors.Color.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByBlock, 0),
          256 => Autodesk.AutoCAD.Colors.Color.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByLayer, 256),
          var aci => Autodesk.AutoCAD.Colors.Color.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, (short)aci),
        };
        rec.Dimclrd = c;
        rec.Dimclre = c;
        rec.Dimclrt = c;
      }
      return new Dictionary<string, object?>
      {
        ["name"] = rec.Name,
        ["created"] = created,
        ["textHeight"] = rec.Dimtxt,
        ["arrowSize"] = rec.Dimasz,
        ["tickSize"] = rec.Dimtsz,
        ["extensionOffset"] = rec.Dimexo,
        ["extensionExtend"] = rec.Dimexe,
        ["textGap"] = rec.Dimgap,
        ["scale"] = rec.Dimscale,
        ["decimals"] = rec.Dimdec,
        ["decimalSeparator"] = rec.Dimdsep.ToString(),
        ["textAbove"] = rec.Dimtad == 1,
        ["alignedText"] = !rec.Dimtih,
      };
    });
  }
}
