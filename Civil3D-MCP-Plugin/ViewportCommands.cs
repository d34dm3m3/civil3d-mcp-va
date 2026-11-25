using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace Civil3DMcpPlugin;

/// <summary>
/// Floating viewports on a paper layout: create, change and erase. The view is given as the MODEL point at the
/// centre of the viewport (ViewTarget = that point, ViewCenter = (0,0) in DCS, ViewDirection +Z, so it holds for
/// any twist), the twist (Viewport.TwistAngle: a model direction at angle a, counterclockwise from +X, is drawn on
/// the sheet at a + twist; twistDegrees = -a makes it horizontal, left to right) and the scale as a denominator:
/// CustomScale = modelUnitsInMm / scaleDenominator (paper in mm; model in metres -> modelUnitsInMm 1000, so 1:400
/// gives CustomScale 2.5).
/// </summary>
public static class ViewportCommands
{
  private sealed record ViewArgs(
    double? CenterX, double? CenterY, double? TwistDegrees, double? ScaleDenominator, double ModelUnitsInMm,
    bool? Locked, string? Layer, bool CreateLayer, List<string>? FrozenLayers)
  {
    public bool ChangesView => CenterX != null || TwistDegrees != null || ScaleDenominator != null;
    public bool Any => ChangesView || Locked != null || Layer != null || FrozenLayers != null;
  }

  private static ViewArgs ReadViewArgs(JsonObject? parameters)
  {
    var centerX = PluginRuntime.GetOptionalDouble(parameters, "viewCenterX");
    var centerY = PluginRuntime.GetOptionalDouble(parameters, "viewCenterY");
    if ((centerX == null) != (centerY == null))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Give both viewCenterX and viewCenterY (model coordinates) or neither. Nothing was changed.");
    var denominator = PluginRuntime.GetOptionalDouble(parameters, "scaleDenominator");
    if (denominator is <= 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "scaleDenominator must be > 0 (400 for 1:400). Nothing was changed.");
    var modelUnitsInMm = PluginRuntime.GetOptionalDouble(parameters, "modelUnitsInMm") ?? 1000.0;
    if (modelUnitsInMm <= 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "modelUnitsInMm must be > 0 (1000 when the model is in metres). Nothing was changed.");
    var frozen = (PluginRuntime.GetParameter(parameters, "frozenLayers") as JsonArray)?.Select(n => n?.GetValue<string>()).OfType<string>()
      .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    return new ViewArgs(centerX, centerY, PluginRuntime.GetOptionalDouble(parameters, "twistDegrees"), denominator, modelUnitsInMm,
      PluginRuntime.GetOptionalBool(parameters, "locked"), PluginRuntime.GetOptionalString(parameters, "layer"),
      PluginRuntime.GetOptionalBool(parameters, "createLayer") ?? false, frozen);
  }

  /// <summary>The model (WCS) point shown at the centre of the viewport: ViewCenter (DCS) taken to WCS.</summary>
  internal static Point3d ModelCenter(Viewport vp)
  {
    var dcsToWcs = Matrix3d.Rotation(-vp.TwistAngle, vp.ViewDirection, vp.ViewTarget)
      * Matrix3d.Displacement(vp.ViewTarget - Point3d.Origin)
      * Matrix3d.PlaneToWorld(vp.ViewDirection);
    return new Point3d(vp.ViewCenter.X, vp.ViewCenter.Y, 0).TransformBy(dcsToWcs);
  }

  /// <summary>C218a (2026-11-24): the model area a viewport shows: its paper rectangle taken to model coordinates
  /// (a paper offset d from the viewport centre shows the model point centre + R(-twist) d / CustomScale); null when
  /// the view is degenerate. A non-rectangular clip is not followed (the rectangle is its bounding box on paper).</summary>
  internal static List<double[]>? ModelPolygon(Viewport vp)
  {
    if (!(vp.CustomScale > 0)) return null;
    Point3d c;
    try { c = ModelCenter(vp); }
    catch (Exception ex) when (ex is Autodesk.AutoCAD.Runtime.Exception or ArgumentException or InvalidOperationException) { return null; }
    var t = -vp.TwistAngle;
    double cos = Math.Cos(t), sin = Math.Sin(t), hw = vp.Width / 2 / vp.CustomScale, hh = vp.Height / 2 / vp.CustomScale;
    return new[] { (-hw, -hh), (hw, -hh), (hw, hh), (-hw, hh) }
      .Select(d => new[] { Math.Round(c.X + d.Item1 * cos - d.Item2 * sin, 3), Math.Round(c.Y + d.Item1 * sin + d.Item2 * cos, 3) })
      .ToList();
  }

  internal static bool InPolygon(List<double[]> polygon, double x, double y)
  {
    var inside = false;
    for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
    {
      double xi = polygon[i][0], yi = polygon[i][1], xj = polygon[j][0], yj = polygon[j][1];
      if ((yi > y) != (yj > y) && x < (xj - xi) * (y - yi) / (yj - yi) + xi) inside = !inside;
    }
    return inside;
  }

  /// <summary>C218a: what a viewport shows, for listLayouts: "plan" when an alignment or a pipe network structure lies
  /// in its model polygon, else "profile" when its centre lies in a profile view (with the profile view's name),
  /// otherwise "other".</summary>
  internal static void AddRoles(List<Dictionary<string, object?>> layouts, Autodesk.Civil.ApplicationServices.CivilDocument civilDoc, Transaction transaction)
  {
    var profileViews = new List<(string Name, Extents3d E)>();
    var alignmentPoints = new List<(double X, double Y)>();
    foreach (ObjectId aid in civilDoc.GetAlignmentIds())
    {
      if (transaction.GetObject(aid, OpenMode.ForRead) is not Autodesk.Civil.DatabaseServices.Alignment alignment) continue;
      foreach (ObjectId pid in alignment.GetProfileViewIds())
        if (transaction.GetObject(pid, OpenMode.ForRead) is Autodesk.Civil.DatabaseServices.ProfileView pv)
          try { profileViews.Add((pv.Name, pv.GeometricExtents)); } catch (Autodesk.AutoCAD.Runtime.Exception) { }
      try
      {
        var step = Math.Max((alignment.EndingStation - alignment.StartingStation) / 400, 2.0);
        for (var st = alignment.StartingStation; st <= alignment.EndingStation; st += step)
        {
          double x = 0, y = 0;
          alignment.PointLocation(st, 0, ref x, ref y);
          alignmentPoints.Add((x, y));
        }
      }
      catch (Exception) { }
    }
    var structurePoints = new List<(double X, double Y)>();
    foreach (ObjectId nid in civilDoc.GetPipeNetworkIds())
      if (transaction.GetObject(nid, OpenMode.ForRead) is Autodesk.Civil.DatabaseServices.Network network)
        foreach (ObjectId sid in network.GetStructureIds())
          if (transaction.GetObject(sid, OpenMode.ForRead) is Autodesk.Civil.DatabaseServices.Structure structure)
            structurePoints.Add((structure.Location.X, structure.Location.Y));
    foreach (var layout in layouts)
      if (layout["viewports"] is List<Dictionary<string, object?>> viewports)
        foreach (var vp in viewports)
        {
          var polygon = vp["modelPolygon"] as List<double[]>;
          var centre = vp["modelCenter"] as double[];
          string role = "other";
          string? profileView = null;
          // plan first: a profile view's extents can reach over the plan (live 2026-11-24, a profile view without a
          // profile spanned the plan area); an alignment or structure in the view makes it a plan
          if (polygon != null
              && (alignmentPoints.Any(q => InPolygon(polygon, q.X, q.Y)) || structurePoints.Any(q => InPolygon(polygon, q.X, q.Y))))
            role = "plan";
          else if (centre != null)
            foreach (var (name, e) in profileViews)
              if (centre[0] >= e.MinPoint.X && centre[0] <= e.MaxPoint.X && centre[1] >= e.MinPoint.Y && centre[1] <= e.MaxPoint.Y)
              {
                role = "profile";
                profileView = name;
                break;
              }
          vp["role"] = role;
          vp["profileView"] = profileView;
        }
  }

  internal static Dictionary<string, object?> Describe(Viewport vp, Transaction transaction)
  {
    Point3d? center;
    try
    {
      center = ModelCenter(vp);
    }
    catch (Exception ex) when (ex is Autodesk.AutoCAD.Runtime.Exception or ArgumentException or InvalidOperationException)
    {
      center = null;                                       // degenerate view direction; layout_list must still work
    }

    var frozen = new List<string>();
    foreach (ObjectId layerId in vp.GetFrozenLayers())
    {
      if (transaction.GetObject(layerId, OpenMode.ForRead) is LayerTableRecord layer) frozen.Add(layer.Name);
    }

    return new Dictionary<string, object?>
    {
      ["handle"] = vp.Handle.ToString(),
      ["center"] = new[] { Math.Round(vp.CenterPoint.X, 2), Math.Round(vp.CenterPoint.Y, 2) },
      ["width"] = Math.Round(vp.Width, 2),
      ["height"] = Math.Round(vp.Height, 2),
      ["scale"] = vp.CustomScale > 0 ? $"1:{Math.Round(1.0 / vp.CustomScale, 1)}" : null,
      ["customScale"] = vp.CustomScale,
      // Paper mm per model metre: 1000 / CustomScale (CustomScale 2.5 -> 1:400).
      ["scaleDenominatorMetres"] = vp.CustomScale > 0 ? Math.Round(1000.0 / vp.CustomScale, 3) : null,
      ["modelCenter"] = center is Point3d c ? new[] { Math.Round(c.X, 3), Math.Round(c.Y, 3) } : null,
      ["viewTarget"] = new[] { Math.Round(vp.ViewTarget.X, 3), Math.Round(vp.ViewTarget.Y, 3), Math.Round(vp.ViewTarget.Z, 3) },
      ["viewCenter"] = new[] { Math.Round(vp.ViewCenter.X, 2), Math.Round(vp.ViewCenter.Y, 2) },
      ["twistDegrees"] = Math.Round(vp.TwistAngle * 180 / Math.PI, 3),
      ["locked"] = vp.Locked,
      ["on"] = vp.On,
      ["layer"] = vp.Layer,
      ["frozenLayers"] = frozen,
      ["modelPolygon"] = ModelPolygon(vp),   // C218a
    };
  }

  private static void Apply(Viewport vp, ViewArgs args, Database database, Transaction transaction)
  {
    if (args.Layer != null)
      vp.LayerId = LookupUtils.GetOrCreateLayerId(database, transaction, args.Layer, args.CreateLayer);

    if (args.FrozenLayers != null)
    {
      var table = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
      var missing = args.FrozenLayers.Where(n => !table.Has(n)).ToList();
      if (missing.Count > 0)
        throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Layers not found: {string.Join(", ", missing)}. Nothing was changed.");
      vp.ThawAllLayersInViewport();
      if (args.FrozenLayers.Count > 0)
        vp.FreezeLayersInViewport(args.FrozenLayers.Select(n => table[n]).ToArray().GetEnumerator());
    }

    // A locked viewport silently keeps its view (live 2026-10-07: twist and centre of a re-used, locked plan
    // viewport stayed those of an earlier run): unlock for the change; Locked is applied last below.
    var wasLocked = vp.Locked;
    if (args.ChangesView && wasLocked) vp.Locked = false;
    if (args.ChangesView)
    {
      var scale = args.ScaleDenominator != null ? args.ModelUnitsInMm / args.ScaleDenominator.Value : vp.CustomScale;
      if (args.CenterX != null || args.TwistDegrees != null)
      {
        var center = args.CenterX != null ? new Point3d(args.CenterX.Value, args.CenterY!.Value, 0) : ModelCenter(vp);
        var twist = args.TwistDegrees != null ? args.TwistDegrees.Value * Math.PI / 180.0 : vp.TwistAngle;
        // AutoCAD keeps TwistAngle in [0, 2π); a negative value is normalised here.
        twist %= 2 * Math.PI;
        if (twist < 0) twist += 2 * Math.PI;
        vp.ViewDirection = Vector3d.ZAxis;
        vp.ViewTarget = center;
        vp.ViewCenter = Point2d.Origin;
        vp.TwistAngle = twist;
      }

      // Setting CustomScale is enough: StandardScale = CustomScale is not settable and throws eInvalidInput
      // (live 2026-10-07, every view change on a copied A1 layout failed until this was removed).
      if (scale > 0 && Math.Abs(vp.CustomScale - scale) > 1e-12)
        vp.CustomScale = scale;
    }

    if (args.Locked != null) vp.Locked = args.Locked.Value;
    else if (args.ChangesView && wasLocked) vp.Locked = true;
  }

  internal static (Layout Layout, Viewport Viewport) FindViewport(Database database, Transaction transaction, string layoutName, string handle, OpenMode mode)
  {
    var layout = LayoutCommands.FindLayout(database, transaction, layoutName);
    if (!long.TryParse(handle, System.Globalization.NumberStyles.HexNumber, null, out var value)
      || !database.TryGetObjectId(new Handle(value), out var id) || id.IsErased
      || transaction.GetObject(id, OpenMode.ForRead) is not Viewport vp || vp.OwnerId != layout.BlockTableRecordId)
      throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"No viewport with handle {handle} on layout '{layout.LayoutName}'. Nothing was changed.");
    if (PaperViewportId(layout, transaction) == vp.ObjectId)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{handle} is the paper space viewport of '{layout.LayoutName}' itself. Nothing was changed.");
    if (mode == OpenMode.ForWrite) vp.UpgradeOpen();
    return (layout, vp);
  }

  /// <summary>The layout's own paper space viewport: the first viewport in its block (as in layout_list).</summary>
  internal static ObjectId PaperViewportId(Layout layout, Transaction transaction)
  {
    var space = (BlockTableRecord)transaction.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
    foreach (ObjectId id in space)
    {
      if (transaction.GetObject(id, OpenMode.ForRead) is Viewport) return id;
    }

    return ObjectId.Null;
  }

  /// <summary>C218c/C239: the extents of the model space objects whose script id starts with the prefix (null: none).</summary>
  internal static Extents3d? ModelExtentsByScriptId(Database database, Transaction transaction, string prefix)
  {
    Extents3d? all = null;
    foreach (ObjectId id in (BlockTableRecord)transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(database), OpenMode.ForRead))
    {
      if (transaction.GetObject(id, OpenMode.ForRead) is not Entity entity) continue;
      var sid = ScriptIdCommands.Read(entity).Id;
      if (sid == null || !sid.StartsWith(prefix, StringComparison.Ordinal)) continue;
      Extents3d e;
      try { e = entity.GeometricExtents; } catch (Autodesk.AutoCAD.Runtime.Exception) { continue; }
      if (all is Extents3d a) { a.AddExtents(e); all = a; } else all = e;
    }
    return all;
  }

  public static Task<object?> CreateViewportAsync(JsonObject? parameters)
  {
    var layoutName = PluginRuntime.GetRequiredString(parameters, "layout");
    var centerX = PluginRuntime.GetRequiredDouble(parameters, "centerX");
    var centerY = PluginRuntime.GetRequiredDouble(parameters, "centerY");
    var width = PluginRuntime.GetRequiredDouble(parameters, "width");
    var height = PluginRuntime.GetRequiredDouble(parameters, "height");
    var args = ReadViewArgs(parameters);
    if (width <= 0 || height <= 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "width and height must be > 0 (paper units). Nothing was created.");
    // C218c (2026-11-24): fitTo {bbox: {minX, minY, maxX, maxY}} or {idPrefix} (model space objects with that script id)
    // and margin (model units, default 0): the view centre is the area's centre (in the view's twisted frame), and
    // without scaleDenominator the scale is the largest whole denominator that shows the area. Use case: detail sheets
    // computed centre and scale of each view from the drawn objects' extents.
    var fitTo = PluginRuntime.GetParameter(parameters, "fitTo") as JsonObject;
    if (fitTo != null && args.CenterX != null)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Give fitTo or viewCenterX/viewCenterY, not both. Nothing was created.");
    if (fitTo == null && (args.CenterX == null || args.ScaleDenominator == null))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "viewCenterX, viewCenterY and scaleDenominator (or fitTo) are required for a new viewport. Nothing was created.");
    var fitBox = fitTo?["bbox"] as JsonObject;
    var fitPrefix = fitTo?["idPrefix"]?.GetValue<string>();
    var fitMargin = fitTo?["margin"]?.GetValue<double>() ?? 0;
    if (fitTo != null && (fitBox == null) == (fitPrefix == null))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "fitTo needs bbox {minX, minY, maxX, maxY} or idPrefix. Nothing was created.");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      Dictionary<string, object?>? fitted = null;
      if (fitTo != null)
      {
        double minX, minY, maxX, maxY;
        if (fitBox != null)
        {
          double V(string k) => fitBox[k]?.GetValue<double>() ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "fitTo.bbox needs minX, minY, maxX and maxY. Nothing was created.");
          (minX, minY, maxX, maxY) = (V("minX"), V("minY"), V("maxX"), V("maxY"));
        }
        else
        {
          var e = ModelExtentsByScriptId(database, transaction, fitPrefix!)
            ?? throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"No model space object has a script id starting with '{fitPrefix}'. Nothing was created.");
          (minX, minY, maxX, maxY) = (e.MinPoint.X, e.MinPoint.Y, e.MaxPoint.X, e.MaxPoint.Y);
        }
        if (!(maxX > minX || maxY > minY))
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "The area to fit has no extent. Nothing was created.");
        (minX, minY, maxX, maxY) = (minX - fitMargin, minY - fitMargin, maxX + fitMargin, maxY + fitMargin);
        // the area in the view's frame: paper x shows the model direction R(-twist) (1, 0)
        var t = -(args.TwistDegrees ?? 0) * Math.PI / 180.0;
        double ux = Math.Cos(t), uy = Math.Sin(t), vx = -uy, vy = ux;
        var corners = new[] { (minX, minY), (maxX, minY), (maxX, maxY), (minX, maxY) };
        var us = corners.Select(q => q.Item1 * ux + q.Item2 * uy).ToList();
        var vs = corners.Select(q => q.Item1 * vx + q.Item2 * vy).ToList();
        double mu = (us.Min() + us.Max()) / 2, mv = (vs.Min() + vs.Max()) / 2, wu = us.Max() - us.Min(), hv = vs.Max() - vs.Min();
        var centerX = mu * ux + mv * vx;
        var centerY = mu * uy + mv * vy;
        var denominator = args.ScaleDenominator
          ?? Math.Ceiling(Math.Max(wu * args.ModelUnitsInMm / width, hv * args.ModelUnitsInMm / height) - 1e-9);
        if (!(denominator > 0)) denominator = 1;
        args = args with { CenterX = centerX, CenterY = centerY, ScaleDenominator = denominator };
        fitted = new Dictionary<string, object?>
        {
          ["area"] = new[] { Math.Round(minX, 3), Math.Round(minY, 3), Math.Round(maxX, 3), Math.Round(maxY, 3) },
          ["viewCenter"] = new[] { Math.Round(centerX, 3), Math.Round(centerY, 3) }, ["scaleDenominator"] = denominator,
          ["scaleFromFit"] = PluginRuntime.GetOptionalDouble(parameters, "scaleDenominator") == null,
        };
      }
      var layout = LayoutCommands.FindLayout(database, transaction, layoutName);
      if (layout.ModelType)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Viewports go on a paper layout, not Model. Nothing was created.");
      if (PaperViewportId(layout, transaction).IsNull)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
          $"Layout '{layout.LayoutName}' has no paper space viewport yet (never opened); open it once or copy it with layout_create. Nothing was created.");

      var space = (BlockTableRecord)transaction.GetObject(layout.BlockTableRecordId, OpenMode.ForWrite);
      var vp = new Viewport();
      vp.SetDatabaseDefaults(database);
      vp.CenterPoint = new Point3d(centerX, centerY, 0);
      vp.Width = width;
      vp.Height = height;
      space.AppendEntity(vp);
      transaction.AddNewlyCreatedDBObject(vp, true);
      Apply(vp, args with { Locked = args.Locked ?? false }, database, transaction);
      string? warning = null;
      try
      {
        vp.On = true;
      }
      catch (Autodesk.AutoCAD.Runtime.Exception ex)
      {
        warning = $"The viewport was created but could not be turned on ({ex.ErrorStatus}); it shows once the layout has been opened and the viewport turned on.";
      }

      var result = Describe(vp, transaction);
      result["layout"] = layout.LayoutName;
      result["fitted"] = fitted;
      if (warning != null) result["warning"] = warning;
      return result;
    });
  }

  public static Task<object?> SetViewportAsync(JsonObject? parameters)
  {
    var layoutName = PluginRuntime.GetRequiredString(parameters, "layout");
    var handle = PluginRuntime.GetRequiredString(parameters, "handle");
    var args = ReadViewArgs(parameters);
    // The viewport on the paper (as in viewport_create): centre and size in paper units. Live 2026-10-07 these
    // were silently ignored here, and a template's full-sheet viewport kept covering the profile below it.
    var centerX = PluginRuntime.GetOptionalDouble(parameters, "centerX");
    var centerY = PluginRuntime.GetOptionalDouble(parameters, "centerY");
    var width = PluginRuntime.GetOptionalDouble(parameters, "width");
    var height = PluginRuntime.GetOptionalDouble(parameters, "height");
    if ((centerX == null) != (centerY == null))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Give both centerX and centerY (paper units). Nothing was changed.");
    if (width is <= 0 || height is <= 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "width and height must be > 0 (paper units). Nothing was changed.");
    // C75: on/off (the company template's A0 and A3L viewports were off and showed nothing).
    var on = PluginRuntime.GetOptionalBool(parameters, "on");
    var paper = centerX != null || width != null || height != null || on != null;
    if (!args.Any && !paper)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Give at least one of centerX/Y, width, height, on, viewCenterX/Y, twistDegrees, scaleDenominator, locked, layer, frozenLayers. Nothing was changed.");

    // C100: a view or paper change runs with the viewport's layout current, and the new view is pushed to the graphics.
    // A layout that has been current once (a plot makes it current) keeps its viewports' views in the display cache
    // (LAYOUTREGENCTL 2), and making it current again wrote the cached view back over the database (live 2026-10-11:
    // twist 30 read back 30, plotted 0 and read back 0 after the plot; UpdateDisplay alone on a layout that is not current
    // did not help). On/off needs the current layout too (C75).
    var view = args.ChangesView || centerX != null || width != null || height != null;
    var needsLayout = view || on != null;

    object? Body(Autodesk.AutoCAD.ApplicationServices.Document doc, Autodesk.Civil.ApplicationServices.CivilDocument civilDoc, Database database, Transaction transaction)
    {
      var (layout, vp) = FindViewport(database, transaction, layoutName, handle, OpenMode.ForWrite);
      var manager = LayoutManager.Current;
      var previous = manager.CurrentLayout;
      var switched = needsLayout && !string.Equals(previous, layout.LayoutName, StringComparison.OrdinalIgnoreCase);
      if (switched) manager.CurrentLayout = layout.LayoutName;
      try
      {
        var before = Describe(vp, transaction);
        // Paper first. C76: AutoCAD keeps the view HEIGHT through a resize, so a new height changed the scale (live
        // 2026-10-09: an A1 viewport made half as high went from 1:400 to 1:825.6). Model centre, twist and scale are
        // kept unless given.
        var keptScale = vp.CustomScale;
        if (centerX != null) vp.CenterPoint = new Point3d(centerX.Value, centerY!.Value, 0);
        if (width != null) vp.Width = width.Value;
        if (height != null) vp.Height = height.Value;
        if ((width != null || height != null) && args.ScaleDenominator == null && Math.Abs(vp.CustomScale - keptScale) > 1e-12)
        {
          var locked = vp.Locked;
          if (locked) vp.Locked = false;
          vp.CustomScale = keptScale;
          if (locked) vp.Locked = true;
        }
        Apply(vp, args, database, transaction);
        // On/off is refused (eNotInPaperspace) unless the viewport's layout is the current one (live 2026-10-09).
        if (on != null && vp.On != on.Value) vp.On = on.Value;
        if (view && vp.On) vp.UpdateDisplay();
        var result = Describe(vp, transaction);
        result["layout"] = layout.LayoutName;
        result["before"] = before;
        return result;
      }
      finally
      {
        if (switched) manager.CurrentLayout = previous;
      }
    }

    return needsLayout
      ? CivilExecution.WriteInCommandAsync<object?>(Body, "setViewport", TimeSpan.FromSeconds(60))
      : CivilExecution.WriteAsync<object?>(Body);
  }

  public static Task<object?> EraseViewportAsync(JsonObject? parameters)
  {
    var layoutName = PluginRuntime.GetRequiredString(parameters, "layout");
    var handle = PluginRuntime.GetRequiredString(parameters, "handle");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var (layout, vp) = FindViewport(database, transaction, layoutName, handle, OpenMode.ForWrite);
      var described = Describe(vp, transaction);
      vp.Erase();
      return new Dictionary<string, object?> { ["layout"] = layout.LayoutName, ["erased"] = described };
    });
  }
}
