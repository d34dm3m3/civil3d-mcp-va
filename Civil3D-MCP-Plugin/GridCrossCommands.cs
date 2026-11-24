using System.Text.Json.Nodes;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace Civil3DMcpPlugin;

/// <summary>
/// Coordinate crosses and the north arrow on a sheet (C89). C89a draws coordinate crosses in paper space over a plan
/// viewport: every model point whose easting and northing are multiples of intervalM and whose cross fits inside the
/// viewport gets a cross (two lines along the grid directions, turned with the twist) and the labels "X 6232950"
/// (northing, model Y) and "Y 190800" (easting, model X) by Swedish convention, or "N …"/"E …"; each label runs along
/// its grid line and is kept readable. C89b turns the north arrow block by the viewport's twist so that it points at
/// model north. Use case: the review of the company template: G10, the model plan/profile sheet has coordinate
/// crosses with X/Y labels, which the template could not make; G11, the north arrow stays put while plan production
/// turns the viewports. The paper ↔ model transform is computed here (GridCrossMath, no reflection) from the viewport's
/// centre, size, view target, view centre, scale and twist, as ViewportCommands sets them.
/// </summary>
public static class GridCrossCommands
{
  private const int MaxCrosses = 2000;
  private const long MaxCandidates = 20000;

  /// <summary>
  /// C89a: coordinate crosses for one viewport (layout, handle) in paper space on layer (default RIT-KOORDINATKRYSS,
  /// created when missing), size sizeMm (default 5) and labels in textStyle (default ISO) at textHeightMm (default 1,8),
  /// labels XY (default) or NE. Only crosses that fit inside the viewport's edge are drawn. edgeLabels: labels only on
  /// the outermost crosses (northing on the first cross of each row, easting on the lowest cross of each column).
  /// A label that would cross the viewport's edge goes to the opposite arm. replace: earlier entities on the layer whose
  /// centre lies inside the viewport are erased first.
  /// </summary>
  public static Task<object?> AddGridCrossesAsync(JsonObject? parameters)
  {
    var layoutName = PluginRuntime.GetRequiredString(parameters, "layout");
    var handle = PluginRuntime.GetRequiredString(parameters, "handle");
    var interval = PluginRuntime.GetOptionalDouble(parameters, "intervalM") ?? 100.0;
    var size = PluginRuntime.GetOptionalDouble(parameters, "sizeMm") ?? 5.0;
    var layerName = (PluginRuntime.GetOptionalString(parameters, "layer") ?? "RIT-KOORDINATKRYSS").Trim();
    var styleName = (PluginRuntime.GetOptionalString(parameters, "textStyle") ?? "ISO").Trim();
    var textHeight = PluginRuntime.GetOptionalDouble(parameters, "textHeightMm") ?? 1.8;
    var labels = (PluginRuntime.GetOptionalString(parameters, "labels") ?? "XY").Trim().ToUpperInvariant();
    var edgeLabels = PluginRuntime.GetOptionalBool(parameters, "edgeLabels") ?? false;
    var replace = PluginRuntime.GetOptionalBool(parameters, "replace") ?? false;
    if (!(interval > 0) || !(size > 0) || !(textHeight > 0) || !double.IsFinite(interval) || !double.IsFinite(size) || !double.IsFinite(textHeight))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "intervalM, sizeMm and textHeightMm must be > 0. Nothing was changed.");
    if (labels is not ("XY" or "NE"))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "labels must be XY (X northing, Y easting, Swedish convention) or NE. Nothing was changed.");
    if (layerName.Length == 0 || styleName.Length == 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "layer and textStyle must not be empty. Nothing was changed.");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var (layout, viewport) = ViewportCommands.FindViewport(database, transaction, layoutName, handle, OpenMode.ForRead);
      var frame = PlanFrame(viewport);
      var styles = (TextStyleTable)transaction.GetObject(database.TextStyleTableId, OpenMode.ForRead);
      if (!styles.Has(styleName))
        throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Text style '{styleName}' was not found; give textStyle. Nothing was changed.");
      var warnings = new List<string>();
      var layers = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
      if (layers.Has(layerName))
      {
        var layer = (LayerTableRecord)transaction.GetObject(layers[layerName], OpenMode.ForRead);
        if (replace && layer.IsLocked)
          throw new JsonRpcDispatchException("CIVIL3D.CONFLICT", $"Layer '{layer.Name}' is locked, so the earlier crosses cannot be erased. Nothing was changed.");
        if (layer.IsFrozen || layer.IsOff) warnings.Add($"Layer '{layer.Name}' is {(layer.IsFrozen ? "frozen" : "off")}: the crosses are not shown.");
      }
      else
      {
        try
        {
          SymbolUtilityServices.ValidateSymbolName(layerName, false);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"'{layerName}' is not a valid layer name. Nothing was changed.");
        }
      }
      if (viewport.NonRectClipOn) warnings.Add("The viewport is clipped by a boundary; the crosses fill its rectangle.");

      List<GridCrossMath.Cross> crosses;
      try
      {
        crosses = GridCrossMath.Crosses(frame, interval, GridCrossMath.CrossMargin(size, frame.Twist), MaxCandidates);
      }
      catch (InvalidOperationException ex)
      {
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{ex.Message} (intervalM {interval}). Nothing was changed.");
      }
      if (crosses.Count > MaxCrosses)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{crosses.Count} crosses (at most {MaxCrosses}); increase intervalM. Nothing was changed.");

      var space = (BlockTableRecord)transaction.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
      var toErase = replace ? Existing(space, layerName, frame, transaction) : new List<ObjectId>();

      // Everything is checked; the changes start here.
      var layerId = LookupUtils.GetOrCreateLayerId(database, transaction, layerName, true);
      foreach (var id in toErase) transaction.GetObject(id, OpenMode.ForWrite).Erase();
      space.UpgradeOpen();
      var styleId = styles[styleName];
      var (northPrefix, eastPrefix) = labels == "XY" ? ("X ", "Y ") : ("N ", "E ");
      HashSet<(long Column, long Row)>? northLabelled = null, eastLabelled = null;
      if (edgeLabels) (northLabelled, eastLabelled) = GridCrossMath.EdgeLabelled(crosses);
      var eastAngle = frame.Twist;                                      // model +X on the sheet
      var northAngle = frame.Twist + Math.PI / 2;                       // model +Y on the sheet
      var gap = textHeight / 2;
      var ux = Math.Cos(eastAngle) * size / 2;
      var uy = Math.Sin(eastAngle) * size / 2;
      int labelCount = 0, outside = 0;
      foreach (var cross in crosses)
      {
        AddLine(space, transaction, database, layerId, cross.PaperX - ux, cross.PaperY - uy, cross.PaperX + ux, cross.PaperY + uy);
        AddLine(space, transaction, database, layerId, cross.PaperX + uy, cross.PaperY - ux, cross.PaperX - uy, cross.PaperY + ux);
        var key = (cross.Column, cross.Row);
        if (northLabelled == null || northLabelled.Contains(key))
        {
          labelCount++;
          if (!AddLabel(space, transaction, database, layerId, styleId, textHeight, northPrefix + GridCrossMath.Format(cross.North), cross, eastAngle, gap, frame)) outside++;
        }
        if (eastLabelled == null || eastLabelled.Contains(key))
        {
          labelCount++;
          if (!AddLabel(space, transaction, database, layerId, styleId, textHeight, eastPrefix + GridCrossMath.Format(cross.East), cross, northAngle, gap, frame)) outside++;
        }
      }

      if (outside > 0) warnings.Add($"{outside} label(s) reach past the viewport's edge on both sides of their cross (viewport too small for the text).");
      return new Dictionary<string, object?>
      {
        ["layout"] = layout.LayoutName,
        ["viewport"] = viewport.Handle.ToString(),
        ["crosses"] = crosses.Count,
        ["intervalM"] = interval,
        // Scale denominator with the paper in mm and the model in metres: 1000 / CustomScale (2.5 -> 400), as layout_list reports it.
        ["scaleDenominatorMetres"] = Math.Round(1000.0 / frame.Scale, 3),
        ["customScale"] = frame.Scale,
        ["twistDegrees"] = Math.Round(frame.Twist * 180 / Math.PI, 3),
        ["labels"] = labelCount,
        ["labelsOutside"] = outside,
        ["erased"] = toErase.Count,
        ["layer"] = layerName,
        ["textStyle"] = styleName,
        ["east"] = crosses.Count > 0 ? new[] { crosses.Min(c => c.East), crosses.Max(c => c.East) } : null,
        ["north"] = crosses.Count > 0 ? new[] { crosses.Min(c => c.North), crosses.Max(c => c.North) } : null,
        ["warnings"] = warnings,
        ["note"] = crosses.Count == 0 ? "No grid point with this interval fits inside the viewport." : null,
      };
    });
  }

  /// <summary>
  /// C89b: turns the north arrow block (blockName, default Norrpil; every reference of it in the layout, dynamic blocks
  /// by their own name) so that it points at model north in the viewport (handle, or the layout's first viewport, with a
  /// warning when the layout has more): rotation = the viewport's twist, for a block drawn pointing up. Attributes turn
  /// with it (TransformBy, as C75). A reference with a flipped normal or a negative Y scale is refused (rotation would
  /// point it elsewhere).
  /// </summary>
  public static Task<object?> AlignNorthArrowAsync(JsonObject? parameters)
  {
    var layoutName = PluginRuntime.GetRequiredString(parameters, "layout");
    var blockName = (PluginRuntime.GetOptionalString(parameters, "blockName") ?? "Norrpil").Trim();
    var handle = PluginRuntime.GetOptionalString(parameters, "handle")?.Trim();
    if (blockName.Length == 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "blockName must not be empty. Nothing was changed.");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var layout = LayoutCommands.FindLayout(database, transaction, layoutName);
      if (layout.ModelType)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "The north arrow is turned on a paper layout, not Model. Nothing was changed.");
      var warnings = new List<string>();
      Viewport viewport;
      if (!string.IsNullOrEmpty(handle))
      {
        viewport = ViewportCommands.FindViewport(database, transaction, layoutName, handle, OpenMode.ForRead).Viewport;
      }
      else
      {
        var viewports = FloatingViewports(layout, transaction);
        viewport = viewports.FirstOrDefault()
          ?? throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Layout '{layout.LayoutName}' has no viewport. Nothing was changed.");
        // A Plan+Profil sheet also has a profile viewport (a top view, twist 0): the first one may not be the plan.
        if (viewports.Count > 1)
          warnings.Add($"Layout '{layout.LayoutName}' has {viewports.Count} viewports ({string.Join(", ", viewports.Select(v => v.Handle.ToString()))}); "
            + $"the first, {viewport.Handle}, was used. Give handle for another.");
      }
      PlanFrame(viewport);                                              // the twist only means north in a plan view
      var blocks = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
      if (!blocks.Has(blockName))
        throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Block '{blockName}' was not found. Nothing was changed.");

      var space = (BlockTableRecord)transaction.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
      var references = new List<BlockReference>();
      foreach (ObjectId id in space)
      {
        if (transaction.GetObject(id, OpenMode.ForRead) is BlockReference reference and not Table
          && string.Equals(EffectiveName(reference, transaction), blockName, StringComparison.OrdinalIgnoreCase))
          references.Add(reference);
      }
      if (references.Count == 0)
        throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Layout '{layout.LayoutName}' has no '{blockName}'. Nothing was changed.");
      foreach (var reference in references)
      {
        var layer = (LayerTableRecord)transaction.GetObject(reference.LayerId, OpenMode.ForRead);
        if (layer.IsLocked)
          throw new JsonRpcDispatchException("CIVIL3D.CONFLICT", $"'{blockName}' {reference.Handle} is on the locked layer '{layer.Name}'. Nothing was changed.");
        var normal = reference.Normal;
        if (Math.Abs(normal.X) > 1e-9 || Math.Abs(normal.Y) > 1e-9 || normal.Z <= 0 || reference.ScaleFactors.Y < 0)
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
            $"'{blockName}' {reference.Handle} is mirrored (normal {normal.Z:0.###} or Y scale {reference.ScaleFactors.Y:0.###}); a rotation would not point it north. Nothing was changed.");
      }

      var target = viewport.TwistAngle % (2 * Math.PI);
      if (target < 0) target += 2 * Math.PI;
      var arrows = new List<Dictionary<string, object?>>();
      foreach (var reference in references)
      {
        var before = reference.Rotation;
        var delta = GridCrossMath.Normalize(target - before);
        var turn = Math.Abs(delta) > 1e-12;
        if (turn)
        {
          reference.UpgradeOpen();
          reference.TransformBy(Matrix3d.Rotation(delta, reference.Normal, reference.Position));
        }
        arrows.Add(new Dictionary<string, object?>
        {
          ["handle"] = reference.Handle.ToString(),
          ["beforeDegrees"] = Math.Round(before * 180 / Math.PI, 4),
          ["afterDegrees"] = Math.Round(reference.Rotation * 180 / Math.PI, 4),
          ["changed"] = turn,
        });
      }

      return new Dictionary<string, object?>
      {
        ["layout"] = layout.LayoutName,
        ["viewport"] = viewport.Handle.ToString(),
        ["twistDegrees"] = Math.Round(target * 180 / Math.PI, 4),
        ["block"] = blockName,
        ["northArrows"] = arrows,
        ["warnings"] = warnings,
      };
    });
  }

  /// <summary>The viewport as a plan view frame; a 3D view (or a view from below) has no paper ↔ model plan transform.</summary>
  internal static GridCrossMath.Frame PlanFrame(Viewport viewport)
  {
    var direction = viewport.ViewDirection;
    var length = direction.Length;
    if (length < 1e-12 || Math.Abs(direction.X) > 1e-6 * length || Math.Abs(direction.Y) > 1e-6 * length || direction.Z <= 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
        $"Viewport {viewport.Handle} does not show a plan (top) view (view direction {direction.X:0.###}, {direction.Y:0.###}, {direction.Z:0.###}). Nothing was changed.");
    if (viewport.PerspectiveOn)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Viewport {viewport.Handle} shows a perspective view. Nothing was changed.");
    if (!(viewport.CustomScale > 0))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Viewport {viewport.Handle} has no scale. Nothing was changed.");
    return new GridCrossMath.Frame(viewport.CenterPoint.X, viewport.CenterPoint.Y, viewport.Width, viewport.Height,
      viewport.ViewTarget.X, viewport.ViewTarget.Y, viewport.ViewCenter.X, viewport.ViewCenter.Y, viewport.CustomScale, viewport.TwistAngle);
  }

  /// <summary>Entities on the layer in this layout whose centre (by extents; a text without extents by its position)
  /// lies inside the viewport: the crosses and labels of an earlier run.</summary>
  private static List<ObjectId> Existing(BlockTableRecord space, string layerName, GridCrossMath.Frame frame, Transaction transaction)
  {
    var found = new List<ObjectId>();
    foreach (ObjectId id in space)
    {
      if (transaction.GetObject(id, OpenMode.ForRead) is not Entity entity || entity is Viewport) continue;
      if (!string.Equals(entity.Layer, layerName, StringComparison.OrdinalIgnoreCase)) continue;
      double x, y;
      try
      {
        var extents = entity.GeometricExtents;
        x = (extents.MinPoint.X + extents.MaxPoint.X) / 2;
        y = (extents.MinPoint.Y + extents.MaxPoint.Y) / 2;
      }
      catch (Autodesk.AutoCAD.Runtime.Exception)
      {
        if (entity is not DBText text) continue;
        x = text.Position.X;
        y = text.Position.Y;
      }
      if (GridCrossMath.Inside(frame, x, y, 0)) found.Add(id);
    }
    return found;
  }

  private static void AddLine(BlockTableRecord space, Transaction transaction, Database database, ObjectId layerId, double x1, double y1, double x2, double y2)
  {
    var line = new Line(new Point3d(x1, y1, 0), new Point3d(x2, y2, 0));
    line.SetDatabaseDefaults(database);
    ByLayer(line, layerId);
    space.AppendEntity(line);
    transaction.AddNewlyCreatedDBObject(line, true);
  }

  /// <summary>A label along the arm at <paramref name="direction"/>; when it would reach past the viewport's edge it goes
  /// to the opposite arm, and back to the first place (false) when neither fits.</summary>
  private static bool AddLabel(BlockTableRecord space, Transaction transaction, Database database, ObjectId layerId, ObjectId styleId,
    double height, string text, GridCrossMath.Cross cross, double direction, double gap, GridCrossMath.Frame frame)
  {
    var label = new DBText();
    label.SetDatabaseDefaults(database);
    label.TextString = text;
    label.Height = height;
    label.TextStyleId = styleId;
    ByLayer(label, layerId);
    space.AppendEntity(label);
    transaction.AddNewlyCreatedDBObject(label, true);
    foreach (var attempt in new[] { direction, direction + Math.PI })
    {
      Place(label, GridCrossMath.PlaceLabel(cross.PaperX, cross.PaperY, attempt, gap), database);
      if (Fits(label, frame)) return true;
    }
    Place(label, GridCrossMath.PlaceLabel(cross.PaperX, cross.PaperY, direction, gap), database);
    return false;
  }

  private static void Place(DBText label, GridCrossMath.LabelPlacement placement, Database database)
  {
    var at = new Point3d(placement.X, placement.Y, 0);
    label.Rotation = placement.Rotation;
    label.Justify = placement.Flipped ? AttachmentPoint.TopRight : AttachmentPoint.BottomLeft;
    label.Position = at;
    label.AlignmentPoint = at;
    BlockDefinitionCommands.Realign(label, database);
  }

  private static bool Fits(Entity entity, GridCrossMath.Frame frame)
  {
    try
    {
      var extents = entity.GeometricExtents;
      return GridCrossMath.Inside(frame, extents.MinPoint.X, extents.MinPoint.Y, 0) && GridCrossMath.Inside(frame, extents.MaxPoint.X, extents.MaxPoint.Y, 0);
    }
    catch (Autodesk.AutoCAD.Runtime.Exception)
    {
      return true;                                                      // no extents (empty text): nothing reaches out
    }
  }

  // Sheet content is ByLayer, whatever the current colour, linetype and lineweight are.
  private static void ByLayer(Entity entity, ObjectId layerId)
  {
    entity.LayerId = layerId;
    entity.Color = Color.FromColorIndex(ColorMethod.ByLayer, 256);
    entity.Linetype = "ByLayer";
    entity.LineWeight = LineWeight.ByLayer;
  }

  private static string EffectiveName(BlockReference reference, Transaction transaction) => reference.IsDynamicBlock
    ? ((BlockTableRecord)transaction.GetObject(reference.DynamicBlockTableRecord, OpenMode.ForRead)).Name
    : reference.Name;

  /// <summary>The layout's floating viewports in drawing order (as layout_list lists them), without its paper space viewport.</summary>
  private static List<Viewport> FloatingViewports(Layout layout, Transaction transaction)
  {
    var paper = ViewportCommands.PaperViewportId(layout, transaction);
    var space = (BlockTableRecord)transaction.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
    var viewports = new List<Viewport>();
    foreach (ObjectId id in space)
    {
      if (id != paper && transaction.GetObject(id, OpenMode.ForRead) is Viewport viewport) viewports.Add(viewport);
    }
    return viewports;
  }
}
