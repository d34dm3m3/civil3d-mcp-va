using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using System.Text.Json.Nodes;

namespace Civil3DMcpPlugin;

public static class AcadCommands
{
  /// <summary>Text rotation in radians (DBText/MText.Rotation, counterclockwise from +X): rotationDegrees when
  /// given, otherwise rotation, which has always been radians.</summary>
  private static double TextRotation(JsonObject? parameters) =>
    PluginRuntime.GetOptionalDouble(parameters, "rotationDegrees") is double degrees
      ? degrees * Math.PI / 180.0
      : PluginRuntime.GetOptionalDouble(parameters, "rotation") ?? 0d;

  /// <summary>Layer of a new entity: a named layer must exist or be created on request (createLayer); never the
  /// current layer instead. Null when no layer is named (the current layer is used).</summary>
  private static ObjectId? NamedLayer(Database database, Transaction transaction, string? layerName, bool createLayer) =>
    string.IsNullOrWhiteSpace(layerName) ? null : LookupUtils.GetOrCreateLayerId(database, transaction, layerName, createLayer);

  /// <summary>C99g: text mask behind an MText (background fill in the drawing's background colour, or an ACI colour),
  /// so that lines under a label are hidden in the plot. Parameters: backgroundMask (bool), maskScale (border factor,
  /// default 1.25), backgroundColor (ACI 1-255; 0 or absent = background colour). Use case: label placement on drawings
  /// that follows layout principles for text placement.</summary>
  internal static void ApplyTextMask(MText mtext, JsonObject? parameters)
  {
    if (PluginRuntime.GetOptionalBool(parameters, "backgroundMask") != true)
      return;
    var scale = PluginRuntime.GetOptionalDouble(parameters, "maskScale") ?? 1.25;
    if (scale < 1.0 || scale > 5.0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "maskScale must be between 1 and 5. Nothing was changed.");
    var colour = PluginRuntime.GetOptionalInt(parameters, "backgroundColor") ?? 0;
    if (colour is < 0 or > 255)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "backgroundColor must be an ACI colour 1-255, or 0 for the background colour. Nothing was changed.");
    mtext.BackgroundFill = true;
    mtext.UseBackgroundColor = colour == 0;
    if (colour > 0)
      mtext.BackgroundFillColor = Autodesk.AutoCAD.Colors.Color.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, (short)colour);
    mtext.BackgroundScaleFactor = scale;
  }

  /// <summary>Optional per-object display properties: linetype (must be loaded in the drawing), color (ACI 0-256
  /// or "ByLayer"/"ByBlock"), lineweight (hundredths of a mm, e.g. 35, or "ByLayer"/"ByBlock"/"Default") and
  /// linetypeScale.</summary>
  internal static void ApplyEntityStyle(Autodesk.AutoCAD.DatabaseServices.Entity entity, JsonObject? parameters, Database database, Transaction transaction)
  {
    var linetype = PluginRuntime.GetOptionalString(parameters, "linetype");
    if (!string.IsNullOrWhiteSpace(linetype)) entity.LinetypeId = LoadedLinetypeId(database, transaction, linetype);

    if (PluginRuntime.GetParameter(parameters, "color") is JsonValue color)
    {
      entity.Color = color.GetValueKind() == System.Text.Json.JsonValueKind.Number
        ? color.GetValue<int>() switch
        {
          0 => Autodesk.AutoCAD.Colors.Color.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByBlock, 0),
          256 => Autodesk.AutoCAD.Colors.Color.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByLayer, 256),
          var aci and > 0 and < 256 => Autodesk.AutoCAD.Colors.Color.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, (short)aci),
          var other => throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"color {other} is not an ACI colour 0-256. Nothing was changed."),
        }
        : color.GetValue<string>().ToLowerInvariant() switch
        {
          "bylayer" => Autodesk.AutoCAD.Colors.Color.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByLayer, 256),
          "byblock" => Autodesk.AutoCAD.Colors.Color.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByBlock, 0),
          var other => throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"color '{other}' must be an ACI number, ByLayer or ByBlock. Nothing was changed."),
        };
    }

    if (PluginRuntime.GetParameter(parameters, "lineweight") is JsonValue lineweight)
    {
      entity.LineWeight = lineweight.GetValueKind() == System.Text.Json.JsonValueKind.Number
        ? StandardLineWeight(lineweight.GetValue<int>())
        : lineweight.GetValue<string>().ToLowerInvariant() switch
        {
          "bylayer" => LineWeight.ByLayer,
          "byblock" => LineWeight.ByBlock,
          "default" => LineWeight.ByLineWeightDefault,
          var other => throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"lineweight '{other}' must be a number, ByLayer, ByBlock or Default. Nothing was changed."),
        };
    }

    if (PluginRuntime.GetOptionalDouble(parameters, "linetypeScale") is double scale)
    {
      if (scale <= 0)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "linetypeScale must be > 0. Nothing was changed.");
      entity.LinetypeScale = scale;
    }

    // C99j: transparency ByLayer, ByBlock or a percent 0-90 (an ESRI export can carry an explicit 0 % on every object, which
    // overrides the layer's transparency in the plot; seen in a pilot project)
    if (PluginRuntime.GetParameter(parameters, "transparency") is JsonValue transparency)
    {
      entity.Transparency = transparency.GetValueKind() == System.Text.Json.JsonValueKind.Number
        ? transparency.GetValue<double>() is var pct and >= 0 and <= 90
          ? new Autodesk.AutoCAD.Colors.Transparency(LayerStandardCommands.TransparencyAlpha(pct))
          : throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "transparency must be a percent 0-90, ByLayer or ByBlock. Nothing was changed.")
        : transparency.GetValue<string>().ToLowerInvariant() switch
        {
          "bylayer" => new Autodesk.AutoCAD.Colors.Transparency(Autodesk.AutoCAD.Colors.TransparencyMethod.ByLayer),
          "byblock" => new Autodesk.AutoCAD.Colors.Transparency(Autodesk.AutoCAD.Colors.TransparencyMethod.ByBlock),
          var other => throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"transparency '{other}' must be a percent, ByLayer or ByBlock. Nothing was changed."),
        };
    }
  }

  /// <summary>A linetype loaded in the drawing (ByLayer and ByBlock are records too); otherwise OBJECT_NOT_FOUND
  /// listing the loaded ones.</summary>
  internal static ObjectId LoadedLinetypeId(Database database, Transaction transaction, string name)
  {
    var table = CivilObjectUtils.GetRequiredObject<LinetypeTable>(transaction, database.LinetypeTableId, OpenMode.ForRead);
    if (table.Has(name)) return table[name];
    var loaded = table.Cast<ObjectId>().Select(id => ((LinetypeTableRecord)transaction.GetObject(id, OpenMode.ForRead)).Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase);
    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Linetype '{name}' is not loaded in the drawing. Loaded: {string.Join(", ", loaded)}. Nothing was changed.");
  }

  internal static LineWeight StandardLineWeight(int hundredths) =>
    hundredths >= 0 && Enum.IsDefined((LineWeight)hundredths)
      ? (LineWeight)hundredths
      : throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"lineweight {hundredths} is not a standard lineweight (hundredths of a mm: 0, 5, 9, 13, 15, 18, 20, 25, 30, 35, 40, 50, 53, 60, 70, 80, 90, 100, 106, 120, 140, 158, 200, 211). Nothing was changed.");

  internal static Dictionary<string, object?> StyleOf(Autodesk.AutoCAD.DatabaseServices.Entity entity) => new()
  {
    ["linetype"] = entity.Linetype,
    ["color"] = entity.Color.IsByLayer ? "ByLayer" : entity.Color.IsByBlock ? "ByBlock" : entity.Color.ColorIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
    ["lineweight"] = entity.LineWeight.ToString(),
    ["linetypeScale"] = entity.LinetypeScale,
    ["transparency"] = LayerStandardCommands.TransparencyText(entity.Transparency),
  };

  /// <summary>
  /// C130b (2026-10-13): AutoCAD points (DBPoint) in model space, [{x, y, z?, layer?}], one transaction. Use case:
  /// staking points for the survey of the river bed exported to DXF (COGO points become proxy objects in DXF).
  /// </summary>
  public static Task<object?> CreatePointsAsync(JsonObject? parameters)
  {
    var pointsNode = PluginRuntime.GetParameter(parameters, "points") as JsonArray;
    if (pointsNode == null || pointsNode.Count == 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "createPoints requires a non-empty points array.");
    var defaultLayer = PluginRuntime.GetOptionalString(parameters, "layer");
    var createLayer = PluginRuntime.GetOptionalBool(parameters, "createLayer") ?? false;
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var space = CivilObjectUtils.GetRequiredObject<BlockTableRecord>(transaction, SymbolUtilityServices.GetBlockModelSpaceId(database), OpenMode.ForWrite);
      var handles = new List<string>();
      for (var i = 0; i < pointsNode.Count; i++)
      {
        if (pointsNode[i] is not JsonObject p)
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"points[{i}] must be {{x, y, z?, layer?}}. Nothing was created.");
        var x = p["x"]?.GetValue<double>() ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"points[{i}] is missing x. Nothing was created.");
        var y = p["y"]?.GetValue<double>() ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"points[{i}] is missing y. Nothing was created.");
        var z = p["z"]?.GetValue<double>() ?? 0.0;
        var layer = p["layer"]?.GetValue<string>() ?? defaultLayer;
        using var point = new DBPoint(new Point3d(x, y, z));
        point.LayerId = string.IsNullOrWhiteSpace(layer) ? database.Clayer : LookupUtils.GetOrCreateLayerId(database, transaction, layer, createLayer);
        space.AppendEntity(point);
        transaction.AddNewlyCreatedDBObject(point, true);
        handles.Add(point.Handle.ToString());
      }
      return new Dictionary<string, object?> { ["created"] = handles.Count, ["handles"] = handles };
    });
  }

  /// <summary>
  /// C131 (2026-10-13): zoom the model-space view to a window (use case: jump to a named place in the plan). zoomTo {x, y,
  /// width?, height?} centre plus size in drawing units (default 500 m wide, height from the window's aspect), or
  /// {minX, minY, maxX, maxY}. Switches to the Model tab first if a layout is active.
  /// </summary>
  public static Task<object?> ZoomToAsync(JsonObject? parameters)
  {
    double? Get(string name) => PluginRuntime.GetOptionalDouble(parameters, name);
    double cx, cy, w; double? h;
    if (Get("minX") is double x0 && Get("minY") is double y0 && Get("maxX") is double x1 && Get("maxY") is double y1)
    {
      if (x1 <= x0 || y1 <= y0)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "maxX/maxY must be greater than minX/minY.");
      cx = (x0 + x1) / 2; cy = (y0 + y1) / 2; w = x1 - x0; h = y1 - y0;
    }
    else
    {
      cx = Get("x") ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "zoomTo requires x and y, or minX, minY, maxX and maxY.");
      cy = Get("y") ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "zoomTo requires x and y, or minX, minY, maxX and maxY.");
      w = Get("width") ?? 500.0; h = Get("height");
      if (w <= 0 || h is <= 0)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "width and height must be positive.");
    }
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      if (!database.TileMode)
        Autodesk.AutoCAD.ApplicationServices.Application.SetSystemVariable("TILEMODE", 1);
      var ed = doc.Editor;
      using var view = ed.GetCurrentView();
      var aspect = view.Height > 0 ? view.Width / view.Height : 1.0;
      var width = w; var height = h ?? w / aspect;
      if (width / height > aspect) height = width / aspect; else width = height * aspect;
      var toDcs = (Matrix3d.PlaneToWorld(view.ViewDirection) * Matrix3d.Displacement(view.Target - Point3d.Origin)
                   * Matrix3d.Rotation(-view.ViewTwist, view.ViewDirection, view.Target)).Inverse();
      var c = new Point3d(cx, cy, 0).TransformBy(toDcs);
      view.CenterPoint = new Point2d(c.X, c.Y);
      view.Width = width;
      view.Height = height;
      ed.SetCurrentView(view);
      return new Dictionary<string, object?> { ["center"] = new[] { cx, cy }, ["width"] = width, ["height"] = height };
    });
  }

  /// <summary>
  /// C130 (2026-10-13): model-space objects on the given layers (or the given handles) written to a DXF file through
  /// Database.Wblock (world coordinates, base point 0,0), nothing else of the drawing. version 2010, 2013 (default) or
  /// 2018. The file lies in the export roots. Use case: a staking DXF for the surveyor made from the drawing.
  /// </summary>
  public static Task<object?> ExportDxfAsync(JsonObject? parameters)
  {
    var overwrite = PluginRuntime.GetOptionalBool(parameters, "overwrite") ?? false;
    var output = FileBoundary.ResolveLocalExportPath(PluginRuntime.GetRequiredString(parameters, "output"), overwrite, ".dxf");
    var layers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    if (PluginRuntime.GetParameter(parameters, "layers") is JsonArray la)
      foreach (var n in la) if (n?.GetValue<string>() is { Length: > 0 } s) layers.Add(s);
    var handles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    if (PluginRuntime.GetParameter(parameters, "handles") is JsonArray ha)
      foreach (var n in ha) if (n?.GetValue<string>() is { Length: > 0 } s) handles.Add(s);
    if (layers.Count + handles.Count == 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Give layers or handles. Nothing was exported.");
    var version = (PluginRuntime.GetOptionalString(parameters, "version") ?? "2013") switch
    {
      "2010" => DwgVersion.AC1024,
      "2013" => DwgVersion.AC1027,
      "2018" => DwgVersion.AC1032,
      var v => throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"version {v}: use 2010, 2013 or 2018."),
    };
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var ids = new ObjectIdCollection();
      var counts = new Dictionary<string, int>();
      var space = CivilObjectUtils.GetRequiredObject<BlockTableRecord>(transaction, SymbolUtilityServices.GetBlockModelSpaceId(database), OpenMode.ForRead);
      foreach (ObjectId id in space)
      {
        if (transaction.GetObject(id, OpenMode.ForRead) is not Entity e) continue;
        if (!layers.Contains(e.Layer) && !handles.Contains(e.Handle.ToString())) continue;
        ids.Add(id);
        var key = $"{e.Layer}|{id.ObjectClass.DxfName}";
        counts[key] = counts.TryGetValue(key, out var c) ? c + 1 : 1;
      }
      if (ids.Count == 0)
        throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", "No model-space objects on those layers or with those handles. Nothing was exported.");
      using var side = database.Wblock(ids, Point3d.Origin);
      side.DxfOut(output, 16, version);
      return new Dictionary<string, object?> { ["output"] = output, ["objects"] = ids.Count, ["byLayerAndType"] = counts, ["bytes"] = new FileInfo(output).Length };
    });
  }

  public static Task<object?> CreatePolylineAsync(JsonObject? parameters)
  {
    var pointsNode = PluginRuntime.GetParameter(parameters, "points") as JsonArray;
    if (pointsNode == null || pointsNode.Count < 2)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "createPolyline requires at least two points.");
    }

    var closed = PluginRuntime.GetOptionalInt(parameters, "closed") == 1;
    var layerName = PluginRuntime.GetOptionalString(parameters, "layer");
    var createLayer = PluginRuntime.GetOptionalBool(parameters, "createLayer") ?? false;
    var constantWidth = PluginRuntime.GetOptionalDouble(parameters, "width");
    if (constantWidth is < 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "width (constant global width) must be >= 0. Nothing was created.");
    // C58: a paper layout instead of model space (sheet lines such as the divider between plan and profile)
    var layoutName = PluginRuntime.GetOptionalString(parameters, "layout");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      // A named layer must exist (or be created on request); never fall back to the current layer.
      var layerId = string.IsNullOrWhiteSpace(layerName)
        ? database.Clayer
        : LookupUtils.GetOrCreateLayerId(database, transaction, layerName, createLayer);
      var blockTable = CivilObjectUtils.GetRequiredObject<BlockTable>(transaction, database.BlockTableId, OpenMode.ForRead);
      var spaceId = string.IsNullOrWhiteSpace(layoutName) || string.Equals(layoutName, "Model", StringComparison.OrdinalIgnoreCase)
        ? blockTable[BlockTableRecord.ModelSpace]
        : LayoutCommands.FindLayout(database, transaction, layoutName!).BlockTableRecordId;
      var modelSpace = CivilObjectUtils.GetRequiredObject<BlockTableRecord>(transaction, spaceId, OpenMode.ForWrite);

      using var polyline = new Polyline();
      for (var index = 0; index < pointsNode.Count; index++)
      {
        // C108: points as {x, y} or [x, y]. Anything else was skipped silently, and [x, y] pairs gave a polyline without
        // vertices that later failed listEntities (eDegenerateGeometry, live 2026-10-11).
        double x, y;
        if (pointsNode[index] is JsonObject point)
        {
          x = point["x"]?.GetValue<double>() ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"points[{index}] is missing x. Nothing was created.");
          y = point["y"]?.GetValue<double>() ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"points[{index}] is missing y. Nothing was created.");
        }
        else if (pointsNode[index] is JsonArray pair && pair.Count >= 2)
        {
          x = pair[0]!.GetValue<double>();
          y = pair[1]!.GetValue<double>();
        }
        else
        {
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"points[{index}] must be {{x, y}} or [x, y]. Nothing was created.");
        }

        polyline.AddVertexAt(index, new Point2d(x, y), 0, 0, 0);
      }

      polyline.Closed = closed;
      polyline.LayerId = layerId;
      ApplyEntityStyle(polyline, parameters, database, transaction);
      if (constantWidth != null) polyline.ConstantWidth = constantWidth.Value;

      var polylineId = modelSpace.AppendEntity(polyline);
      transaction.AddNewlyCreatedDBObject(polyline, true);

      var created = CivilObjectUtils.GetRequiredObject<Polyline>(transaction, polylineId, OpenMode.ForRead);

      var result = new Dictionary<string, object?>
      {
        ["handle"] = CivilObjectUtils.GetHandle(created),
        ["vertexCount"] = created.NumberOfVertices,
        ["closed"] = created.Closed,
        ["layer"] = created.Layer,
        ["constantWidth"] = constantWidth,
      };
      foreach (var (key, value) in StyleOf(created)) result[key] = value;
      return result;
    });
  }

  /// <summary>Points as [{x, y} | [x, y]] (at least <paramref name="minimum"/>), for hatch loops; INVALID_INPUT names the item.</summary>
  private static List<Point2d> Point2dList(JsonArray? node, string what, int minimum)
  {
    if (node == null || node.Count < minimum)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{what} needs at least {minimum} points. Nothing was created.");
    var points = new List<Point2d>();
    for (var i = 0; i < node.Count; i++)
    {
      if (node[i] is JsonObject o && o["x"] != null && o["y"] != null)
        points.Add(new Point2d(o["x"]!.GetValue<double>(), o["y"]!.GetValue<double>()));
      else if (node[i] is JsonArray pair && pair.Count >= 2)
        points.Add(new Point2d(pair[0]!.GetValue<double>(), pair[1]!.GetValue<double>()));
      else
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{what}[{i}] must be {{x, y}} or [x, y]. Nothing was created.");
    }
    return points;
  }

  /// <summary>C135: hatch (HATCH) from boundary points, not associative: {boundary: [[x, y]...] (outer loop, closed
  /// automatically), holes?: [[[x, y]...], ...], pattern? (predefined name from acad.pat/acadiso.pat, default SOLID),
  /// scale? (1), angle? (degrees, 0), layer?, createLayer?, color/linetype/lineweight/transparency?, layout? (paper space),
  /// toBack? (draw order: under everything else in the space)}. Use case:
  /// stage drawings (cofferdams, stone protection, zones) and details were hatched outside Civil via ezdxf xrefs because
  /// the plugin had no hatch.</summary>
  public static Task<object?> CreateHatchAsync(JsonObject? parameters)
  {
    var outer = Point2dList(PluginRuntime.GetParameter(parameters, "boundary") as JsonArray, "boundary", 3);
    var holes = (PluginRuntime.GetParameter(parameters, "holes") as JsonArray)?
      .Select((h, i) => Point2dList(h as JsonArray, $"holes[{i}]", 3)).ToList() ?? new List<List<Point2d>>();
    var pattern = (PluginRuntime.GetOptionalString(parameters, "pattern") ?? "SOLID").Trim().ToUpperInvariant();
    var scale = PluginRuntime.GetOptionalDouble(parameters, "scale") ?? 1.0;
    var angleDeg = PluginRuntime.GetOptionalDouble(parameters, "angle") ?? 0.0;
    if (scale <= 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "scale must be > 0. Nothing was created.");
    var layerName = PluginRuntime.GetOptionalString(parameters, "layer");
    var createLayer = PluginRuntime.GetOptionalBool(parameters, "createLayer") ?? false;
    var layoutName = PluginRuntime.GetOptionalString(parameters, "layout");
    var toBack = PluginRuntime.GetOptionalBool(parameters, "toBack") ?? false;

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var layerId = string.IsNullOrWhiteSpace(layerName)
        ? database.Clayer
        : LookupUtils.GetOrCreateLayerId(database, transaction, layerName, createLayer);
      var blockTable = CivilObjectUtils.GetRequiredObject<BlockTable>(transaction, database.BlockTableId, OpenMode.ForRead);
      var spaceId = string.IsNullOrWhiteSpace(layoutName) || string.Equals(layoutName, "Model", StringComparison.OrdinalIgnoreCase)
        ? blockTable[BlockTableRecord.ModelSpace]
        : LayoutCommands.FindLayout(database, transaction, layoutName!).BlockTableRecordId;
      var space = CivilObjectUtils.GetRequiredObject<BlockTableRecord>(transaction, spaceId, OpenMode.ForWrite);

      using var hatch = new Hatch();
      hatch.SetDatabaseDefaults(database);
      hatch.LayerId = layerId;
      ApplyEntityStyle(hatch, parameters, database, transaction);
      var hatchId = space.AppendEntity(hatch);
      transaction.AddNewlyCreatedDBObject(hatch, true);
      // the pattern must exist before angle and scale can be set (eInvalidInput otherwise, live 2026-10-16); set it again
      // afterwards so that the pattern lines are generated with the new angle and scale
      try
      {
        hatch.SetHatchPattern(HatchPatternType.PreDefined, pattern);
        if (pattern != "SOLID")
        {
          hatch.PatternAngle = angleDeg * Math.PI / 180.0;
          hatch.PatternScale = scale;
          hatch.SetHatchPattern(HatchPatternType.PreDefined, pattern);
        }
      }
      catch (Autodesk.AutoCAD.Runtime.Exception ex)
      {
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Hatch pattern '{pattern}' could not be set ({ex.ErrorStatus}); it must be a predefined pattern in acad.pat/acadiso.pat. Nothing was created.");
      }
      hatch.Associative = false;
      hatch.HatchStyle = HatchStyle.Normal;

      static (Point2dCollection Points, DoubleCollection Bulges) Loop(List<Point2d> points)
      {
        var closed = new List<Point2d>(points);
        if (closed[0].GetDistanceTo(closed[^1]) > 1e-9) closed.Add(closed[0]);
        var bulges = new DoubleCollection();
        foreach (var _ in closed) bulges.Add(0.0);
        return (new Point2dCollection(closed.ToArray()), bulges);
      }

      var (op, ob) = Loop(outer);
      hatch.AppendLoop(HatchLoopTypes.External, op, ob);
      foreach (var hole in holes)
      {
        var (hp, hb) = Loop(hole);
        hatch.AppendLoop(HatchLoopTypes.Default, hp, hb);
      }
      try
      {
        hatch.EvaluateHatch(true);
      }
      catch (Autodesk.AutoCAD.Runtime.Exception ex)
      {
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"The hatch could not be evaluated ({ex.ErrorStatus}); check that the loops are closed and do not cross themselves. Nothing was created.");
      }

      if (toBack)
      {
        var drawOrder = CivilObjectUtils.GetRequiredObject<DrawOrderTable>(transaction, space.DrawOrderTableId, OpenMode.ForWrite);
        drawOrder.MoveToBottom(new ObjectIdCollection { hatchId });
      }

      double? area = null;
      try { area = Math.Round(hatch.Area, 4); } catch (Autodesk.AutoCAD.Runtime.Exception) { }
      var result = new Dictionary<string, object?>
      {
        ["handle"] = CivilObjectUtils.GetHandle(hatch),
        ["pattern"] = hatch.PatternName,
        ["scale"] = hatch.PatternScale,
        ["angle"] = angleDeg,
        ["loops"] = hatch.NumberOfLoops,
        ["area"] = area,
        ["layer"] = hatch.Layer,
        ["toBack"] = toBack,
      };
      foreach (var (key, value) in StyleOf(hatch)) result[key] = value;
      return result;
    });
  }

  /// <summary>C136: circle {x, y, z? (0), radius, layer?, createLayer?, color/linetype/lineweight/transparency?, layout?}.
  /// Use case: pipe sections and markers were polylines with 36-48 vertices.</summary>
  public static Task<object?> CreateCircleAsync(JsonObject? parameters)
  {
    var x = PluginRuntime.GetRequiredDouble(parameters, "x");
    var y = PluginRuntime.GetRequiredDouble(parameters, "y");
    var z = PluginRuntime.GetOptionalDouble(parameters, "z") ?? 0d;
    var radius = PluginRuntime.GetRequiredDouble(parameters, "radius");
    if (radius <= 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "radius must be > 0. Nothing was created.");
    var layerName = PluginRuntime.GetOptionalString(parameters, "layer");
    var createLayer = PluginRuntime.GetOptionalBool(parameters, "createLayer") ?? false;
    var layoutName = PluginRuntime.GetOptionalString(parameters, "layout");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var layerId = string.IsNullOrWhiteSpace(layerName)
        ? database.Clayer
        : LookupUtils.GetOrCreateLayerId(database, transaction, layerName, createLayer);
      var blockTable = CivilObjectUtils.GetRequiredObject<BlockTable>(transaction, database.BlockTableId, OpenMode.ForRead);
      var spaceId = string.IsNullOrWhiteSpace(layoutName) || string.Equals(layoutName, "Model", StringComparison.OrdinalIgnoreCase)
        ? blockTable[BlockTableRecord.ModelSpace]
        : LayoutCommands.FindLayout(database, transaction, layoutName!).BlockTableRecordId;
      var space = CivilObjectUtils.GetRequiredObject<BlockTableRecord>(transaction, spaceId, OpenMode.ForWrite);
      using var circle = new Circle(new Point3d(x, y, z), Vector3d.ZAxis, radius);
      circle.LayerId = layerId;
      ApplyEntityStyle(circle, parameters, database, transaction);
      space.AppendEntity(circle);
      transaction.AddNewlyCreatedDBObject(circle, true);
      var result = new Dictionary<string, object?>
      {
        ["handle"] = CivilObjectUtils.GetHandle(circle),
        ["center"] = new[] { x, y, z },
        ["radius"] = radius,
        ["layer"] = circle.Layer,
      };
      foreach (var (key, value) in StyleOf(circle)) result[key] = value;
      return result;
    });
  }

  public static Task<object?> CreateTextAsync(JsonObject? parameters)
  {
    var text = PluginRuntime.GetRequiredString(parameters, "text");
    var x = PluginRuntime.GetRequiredDouble(parameters, "x");
    var y = PluginRuntime.GetRequiredDouble(parameters, "y");
    var z = PluginRuntime.GetOptionalDouble(parameters, "z") ?? 0d;
    var height = PluginRuntime.GetOptionalDouble(parameters, "height") ?? 2.5d;
    var rotation = TextRotation(parameters);
    var layerName = PluginRuntime.GetOptionalString(parameters, "layer");
    var createLayer = PluginRuntime.GetOptionalBool(parameters, "createLayer") ?? false;

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var blockTable = CivilObjectUtils.GetRequiredObject<BlockTable>(transaction, database.BlockTableId, OpenMode.ForRead);
      var modelSpace = CivilObjectUtils.GetRequiredObject<BlockTableRecord>(transaction, blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

      using var dbText = new DBText
      {
        TextString = text,
        Position = new Point3d(x, y, z),
        Height = height,
        Rotation = rotation,
      };

      if (NamedLayer(database, transaction, layerName, createLayer) is ObjectId layerId)
      {
        dbText.LayerId = layerId;
      }

      var textId = modelSpace.AppendEntity(dbText);
      transaction.AddNewlyCreatedDBObject(dbText, true);

      var created = CivilObjectUtils.GetRequiredObject<DBText>(transaction, textId, OpenMode.ForRead);

      return new Dictionary<string, object?>
      {
        ["handle"] = CivilObjectUtils.GetHandle(created),
        ["text"] = created.TextString,
        ["x"] = created.Position.X,
        ["y"] = created.Position.Y,
        ["z"] = created.Position.Z,
        ["height"] = created.Height,
        ["rotation"] = created.Rotation,
        ["layer"] = created.Layer,
      };
    });
  }

  public static Task<object?> Create3dPolylineAsync(JsonObject? parameters)
  {
    var pointsNode = PluginRuntime.GetParameter(parameters, "points") as JsonArray;
    if (pointsNode == null || pointsNode.Count < 2)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "create3dPolyline requires at least two points.");
    }

    var closed = PluginRuntime.GetOptionalInt(parameters, "closed") == 1;
    var layerName = PluginRuntime.GetOptionalString(parameters, "layer");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var blockTable = CivilObjectUtils.GetRequiredObject<BlockTable>(transaction, database.BlockTableId, OpenMode.ForRead);
      var modelSpace = CivilObjectUtils.GetRequiredObject<BlockTableRecord>(transaction, blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

      var pointCollection = new Point3dCollection();
      for (var index = 0; index < pointsNode.Count; index++)
      {
        if (pointsNode[index] is not JsonObject point)
        {
          continue;
        }

        var x = point["x"]?.GetValue<double>() ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Point is missing x.");
        var y = point["y"]?.GetValue<double>() ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Point is missing y.");
        var z = point["z"]?.GetValue<double>() ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Point is missing z.");
        pointCollection.Add(new Point3d(x, y, z));
      }

      if (pointCollection.Count < 2)
      {
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "create3dPolyline requires at least two valid points.");
      }

      using var polyline3d = new Polyline3d(Poly3dType.SimplePoly, pointCollection, closed);

      if (!string.IsNullOrWhiteSpace(layerName))
      {
        var layerId = LookupUtils.GetLayerId(database, transaction, layerName);
        polyline3d.LayerId = layerId;
      }

      var polylineId = modelSpace.AppendEntity(polyline3d);
      transaction.AddNewlyCreatedDBObject(polyline3d, true);

      var created = CivilObjectUtils.GetRequiredObject<Polyline3d>(transaction, polylineId, OpenMode.ForRead);
      var vertexCount = 0;
      foreach (ObjectId vertexId in created)
      {
        _ = vertexId;
        vertexCount++;
      }

      return new Dictionary<string, object?>
      {
        ["handle"] = CivilObjectUtils.GetHandle(created),
        ["vertexCount"] = vertexCount,
        ["closed"] = created.Closed,
        ["layer"] = created.Layer,
      };
    });
  }

  public static Task<object?> CreateLineSegmentAsync(JsonObject? parameters)
  {
    var startX = PluginRuntime.GetRequiredDouble(parameters, "startX");
    var startY = PluginRuntime.GetRequiredDouble(parameters, "startY");
    var startZ = PluginRuntime.GetOptionalDouble(parameters, "startZ") ?? 0d;
    var endX = PluginRuntime.GetRequiredDouble(parameters, "endX");
    var endY = PluginRuntime.GetRequiredDouble(parameters, "endY");
    var endZ = PluginRuntime.GetOptionalDouble(parameters, "endZ") ?? 0d;
    var layerName = PluginRuntime.GetOptionalString(parameters, "layer");
    var createLayer = PluginRuntime.GetOptionalBool(parameters, "createLayer") ?? false;

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var blockTable = CivilObjectUtils.GetRequiredObject<BlockTable>(transaction, database.BlockTableId, OpenMode.ForRead);
      var modelSpace = CivilObjectUtils.GetRequiredObject<BlockTableRecord>(transaction, blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

      using var line = new Line(new Point3d(startX, startY, startZ), new Point3d(endX, endY, endZ));

      if (NamedLayer(database, transaction, layerName, createLayer) is ObjectId layerId)
      {
        line.LayerId = layerId;
      }

      ApplyEntityStyle(line, parameters, database, transaction);
      var lineId = modelSpace.AppendEntity(line);
      transaction.AddNewlyCreatedDBObject(line, true);

      var created = CivilObjectUtils.GetRequiredObject<Line>(transaction, lineId, OpenMode.ForRead);

      var result = new Dictionary<string, object?>
      {
        ["lineId"] = CivilObjectUtils.GetHandle(created),
        ["startX"] = created.StartPoint.X,
        ["startY"] = created.StartPoint.Y,
        ["startZ"] = created.StartPoint.Z,
        ["endX"] = created.EndPoint.X,
        ["endY"] = created.EndPoint.Y,
        ["endZ"] = created.EndPoint.Z,
        ["length"] = created.Length,
        ["layer"] = created.Layer,
      };
      foreach (var (key, value) in StyleOf(created)) result[key] = value;
      return result;
    });
  }

  public static Task<object?> CreateMTextAsync(JsonObject? parameters)
  {
    var text = PluginRuntime.GetRequiredString(parameters, "text");
    var x = PluginRuntime.GetRequiredDouble(parameters, "x");
    var y = PluginRuntime.GetRequiredDouble(parameters, "y");
    var z = PluginRuntime.GetOptionalDouble(parameters, "z") ?? 0d;
    var width = PluginRuntime.GetOptionalDouble(parameters, "width") ?? 0d;
    var textHeight = PluginRuntime.GetOptionalDouble(parameters, "textHeight") ?? 2.5d;
    var rotation = TextRotation(parameters);
    var layerName = PluginRuntime.GetOptionalString(parameters, "layer");
    var createLayer = PluginRuntime.GetOptionalBool(parameters, "createLayer") ?? false;

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var blockTable = CivilObjectUtils.GetRequiredObject<BlockTable>(transaction, database.BlockTableId, OpenMode.ForRead);
      var modelSpace = CivilObjectUtils.GetRequiredObject<BlockTableRecord>(transaction, blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

      using var mtext = new MText
      {
        Contents = text,
        Location = new Point3d(x, y, z),
        TextHeight = textHeight,
        Rotation = rotation,
      };

      if (width > 0)
      {
        mtext.Width = width;
      }

      if (NamedLayer(database, transaction, layerName, createLayer) is ObjectId layerId)
      {
        mtext.LayerId = layerId;
      }

      // C99g: attachment (TopLeft default, e.g. BottomLeft, MiddleCenter) and text mask
      if (PluginRuntime.GetOptionalString(parameters, "attachment") is string att && !string.IsNullOrWhiteSpace(att))
      {
        if (!Enum.TryParse<AttachmentPoint>(att, true, out var ap))
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"attachment '{att}' is not an AttachmentPoint (TopLeft, MiddleCenter, BottomLeft ...). Nothing was changed.");
        mtext.Attachment = ap;
      }
      ApplyTextMask(mtext, parameters);

      var mtextId = modelSpace.AppendEntity(mtext);
      transaction.AddNewlyCreatedDBObject(mtext, true);

      var created = CivilObjectUtils.GetRequiredObject<MText>(transaction, mtextId, OpenMode.ForRead);

      return new Dictionary<string, object?>
      {
        ["handle"] = CivilObjectUtils.GetHandle(created),
        ["text"] = created.Contents,
        ["x"] = created.Location.X,
        ["y"] = created.Location.Y,
        ["z"] = created.Location.Z,
        ["textHeight"] = created.TextHeight,
        ["rotation"] = created.Rotation,
        ["width"] = created.Width,
        ["layer"] = created.Layer,
      };
    });
  }
}
