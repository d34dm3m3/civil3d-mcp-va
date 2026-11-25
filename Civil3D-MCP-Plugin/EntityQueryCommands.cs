using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AcEntity = Autodesk.AutoCAD.DatabaseServices.Entity;

namespace Civil3DMcpPlugin;

/// <summary>
/// Read plain AutoCAD geometry in model space: lines, polylines (2D, legacy 2D, 3D), circles, arcs, texts,
/// block references and points with their coordinates, filtered by layer and type. Read-only; meant for
/// using what the user has drawn (a line where structures should go, a wall, a boundary) as input.
/// Polylines (2D and legacy 2D) also give the bulge of each segment and, on request, points that follow
/// their arcs. Also moves entities to a layer and erases plain AutoCAD entities by handle.
/// </summary>
public static class EntityQueryCommands
{
  /// <summary>Most points <c>densified</c> may hold for one polyline; beyond that it is left out and
  /// <c>densifiedOmitted</c> gives the number needed.</summary>
  private const int MaxDensifiedPoints = 20000;

  /// <summary>Smallest densifyArcs (model units); coordinates are rounded to 4 decimals.</summary>
  private const double MinDensifyTolerance = 0.001;

  private const double BulgeEpsilon = 1e-9;

  private static readonly Dictionary<string, Func<AcEntity, bool>> TypeFilters = new(StringComparer.OrdinalIgnoreCase)
  {
    ["line"] = e => e is Line,
    ["polyline"] = e => e is Polyline || e is Polyline2d,
    ["polyline3d"] = e => e is Polyline3d,
    ["circle"] = e => e is Circle,
    ["arc"] = e => e is Arc,
    ["text"] = e => e is DBText || e is MText,
    ["mtext"] = e => e is MText,   // C138: only MText ("text" covers DBText and MText)
    ["insert"] = e => e is BlockReference,
    ["point"] = e => e is DBPoint,
    ["image"] = e => e is RasterImage,
    ["hatch"] = e => e is Hatch,
    ["mleader"] = e => e is MLeader,
    ["dimension"] = e => e is Dimension,   // C141: dimensions (createDimension, C139) can be listed and found by script id
  };

  public static Task<object?> ListEntitiesAsync(JsonObject? parameters)
  {
    var layer = PluginRuntime.GetOptionalString(parameters, "layer");
    var layerContains = PluginRuntime.GetOptionalString(parameters, "layerContains");
    var types = (PluginRuntime.GetParameter(parameters, "types") as JsonArray)?.Select(n => n?.GetValue<string>()).OfType<string>().ToList();
    var limit = Math.Clamp(PluginRuntime.GetOptionalInt(parameters, "limit") ?? 200, 1, 5000);
    var maxVertices = Math.Clamp(PluginRuntime.GetOptionalInt(parameters, "maxVertices") ?? 500, 2, 100000);
    var densifyArcs = PluginRuntime.GetOptionalDouble(parameters, "densifyArcs");
    var layoutName = PluginRuntime.GetOptionalString(parameters, "layout");
    // C105: only objects with a script id (ScriptIdCommands XData) starting with idPrefix ("" = any id); id and idData are reported
    var idPrefix = PluginRuntime.GetOptionalString(parameters, "idPrefix");
    // C111: skip the first offset matches (limit is at most 5 000; live 2026-10-11 a base map of 2 002 objects could not be paged)
    var offset = Math.Max(PluginRuntime.GetOptionalInt(parameters, "offset") ?? 0, 0);
    // C65: only entities whose extents touch this area (model units); replaces the never built queryDrawingEntities
    var bbox = PluginRuntime.GetParameter(parameters, "bbox") as JsonObject;
    double[]? area = null;
    if (bbox != null)
    {
      area = new[] { "minX", "minY", "maxX", "maxY" }.Select(k => bbox[k]?.GetValue<double>()
        ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "bbox needs minX, minY, maxX and maxY.")).ToArray();
      if (!(area[0] < area[2] && area[1] < area[3]))
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "bbox must have minX < maxX and minY < maxY.");
    }
    if (densifyArcs is double tolerance && !(tolerance >= MinDensifyTolerance))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"densifyArcs (the largest distance between an arc and the chords that replace it, in model units) must be at least {MinDensifyTolerance}; coordinates are rounded to 4 decimals.");
    if (types != null)
    {
      var unknown = types.Where(t => !TypeFilters.ContainsKey(t)).ToList();
      if (unknown.Count > 0)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Unknown types: {string.Join(", ", unknown)}. Use {string.Join(", ", TypeFilters.Keys)}.");
    }

    // C168: sourcePath reads another drawing as a side database (ReadDwgFile, never opened as a document: its
    // references are not resolved and no dialog can appear). Use case: opening
    // a drawing whose reference could no longer be found crashed Civil 3D (Access Violation) for the third time.
    var sourceRaw = PluginRuntime.GetOptionalString(parameters, "sourcePath");
    var sourcePath = string.IsNullOrWhiteSpace(sourceRaw) ? null : FileBoundary.ResolveLocalReadPath(sourceRaw!, ".dwg");

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, activeDatabase, activeTransaction) =>
    {
      if (sourcePath == null)
        return Run(activeDatabase, activeTransaction);
      using var side = new Database(false, true);
      side.ReadDwgFile(sourcePath, FileOpenMode.OpenForReadAndAllShare, true, "");
      side.CloseInput(true);
      using var t = side.TransactionManager.StartTransaction();
      var result = Run(side, t);
      var xrefs = new List<Dictionary<string, object?>>();
      var classes = new SortedDictionary<string, int>();
      var bt = (BlockTable)t.GetObject(side.BlockTableId, OpenMode.ForRead);
      foreach (ObjectId id in bt)
      {
        var btr = (BlockTableRecord)t.GetObject(id, OpenMode.ForRead);
        if (btr.IsFromExternalReference)
          xrefs.Add(new Dictionary<string, object?> { ["name"] = btr.Name, ["path"] = btr.PathName, ["overlay"] = btr.IsFromOverlayReference });
      }
      foreach (ObjectId id in (BlockTableRecord)t.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(side), OpenMode.ForRead))
        classes[id.ObjectClass.DxfName] = classes.TryGetValue(id.ObjectClass.DxfName, out var n) ? n + 1 : 1;
      t.Commit();
      result["sourcePath"] = sourcePath;
      result["xrefs"] = xrefs;
      result["modelSpaceClasses"] = classes;
      return result;
    });

    Dictionary<string, object?> Run(Database database, Transaction transaction)
    {
      // Model space unless a paper layout is named (its block: title block, north arrow, viewports are skipped).
      var spaceId = string.IsNullOrWhiteSpace(layoutName) || string.Equals(layoutName, "Model", StringComparison.OrdinalIgnoreCase)
        ? SymbolUtilityServices.GetBlockModelSpaceId(database)
        : LayoutCommands.FindLayout(database, transaction, layoutName!).BlockTableRecordId;
      var modelSpace = (BlockTableRecord)transaction.GetObject(spaceId, OpenMode.ForRead);
      var entities = new List<Dictionary<string, object?>>();
      var matched = 0;
      foreach (ObjectId id in modelSpace)
      {
        if (transaction.GetObject(id, OpenMode.ForRead) is not AcEntity entity) continue;
        if (!string.IsNullOrWhiteSpace(layer) && !string.Equals(entity.Layer, layer, StringComparison.OrdinalIgnoreCase)) continue;
        if (!string.IsNullOrWhiteSpace(layerContains) && entity.Layer.IndexOf(layerContains, StringComparison.OrdinalIgnoreCase) < 0) continue;
        var kind = TypeFilters.FirstOrDefault(kv => kv.Value(entity)).Key;
        if (kind == null) continue;
        // C141: an entity matches if any requested filter accepts it (MText is "text" and "mtext"; the first matching
        // key alone hid MText from "mtext", C138)
        if (types != null && !types.Any(tp => TypeFilters[tp](entity))) continue;
        if (area != null)
        {
          Extents3d e;
          try { e = entity.GeometricExtents; }
          catch (Autodesk.AutoCAD.Runtime.Exception) { continue; }
          if (e.MaxPoint.X < area[0] || e.MinPoint.X > area[2] || e.MaxPoint.Y < area[1] || e.MinPoint.Y > area[3]) continue;
        }
        var (scriptId, scriptData) = ScriptIdCommands.Read(entity);
        if (idPrefix != null && (scriptId == null || !scriptId.StartsWith(idPrefix, StringComparison.Ordinal))) continue;
        matched++;
        if (matched <= offset) continue;
        if (entities.Count < limit)
        {
          var described = Describe(entity, kind, transaction, maxVertices, densifyArcs);
          if (scriptId != null) { described["id"] = scriptId; described["idData"] = scriptData; }
          entities.Add(described);
        }
      }

      return new Dictionary<string, object?>
      {
        ["matched"] = matched,
        ["returned"] = entities.Count,
        ["offset"] = offset,
        ["entities"] = entities,
      };
    }
  }

  private static double[] P(Autodesk.AutoCAD.Geometry.Point3d p) => new[] { Math.Round(p.X, 4), Math.Round(p.Y, 4), Math.Round(p.Z, 4) };

  private static Dictionary<string, object?> Describe(AcEntity entity, string kind, Transaction transaction, int maxVertices, double? densifyArcs)
  {
    var d = new Dictionary<string, object?>
    {
      ["handle"] = entity.Handle.ToString(),
      ["type"] = kind,
      ["layer"] = entity.Layer,
      // C65: own display properties (ByLayer unless set), e.g. a legend sample without the plan lines' 0,15 scale
      ["linetype"] = entity.Linetype,
      ["linetypeScale"] = Math.Round(entity.LinetypeScale, 4),
      ["color"] = entity.Color.IsByLayer ? "ByLayer" : entity.Color.ColorIndex.ToString(),
    };
    switch (entity)
    {
      case Line line:
        d["start"] = P(line.StartPoint);
        d["end"] = P(line.EndPoint);
        d["length"] = Math.Round(line.Length, 4);
        break;
      case Polyline pl:
      {
        var count = pl.NumberOfVertices;
        var points = Enumerable.Range(0, count).Select(i => pl.GetPoint3dAt(i)).ToList();
        var bulges = Enumerable.Range(0, count).Select(i => SegmentBulge(pl.GetBulgeAt(i), i, count, pl.Closed)).ToList();
        d["closed"] = pl.Closed;
        if (pl.Closed) { try { d["area"] = Math.Round(pl.Area, 3); } catch (Autodesk.AutoCAD.Runtime.Exception) { } }   // C168
        // C108: a degenerate polyline (two vertices on top of each other, a vertex count of one) throws
        // eDegenerateGeometry from Length, and one such polyline failed the whole listEntities (live 2026-10-11).
        try
        {
          d["length"] = Math.Round(pl.Length, 4);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
          d["length"] = null;
          d["degenerate"] = true;
        }
        d["vertexCount"] = count;
        d["vertices"] = points.Take(maxVertices).Select(p => P(p)).ToList();
        d["bulges"] = bulges.Take(maxVertices).ToList();
        if (bulges.Any(b => b != 0)) d["hasArcs"] = true;
        if (densifyArcs is double tolerance)
        {
          // Points on an arc are taken from the polyline itself at a distance along it, so its plane (OCS) and
          // elevation are applied by AutoCAD. The distances of the arc's ends are read once per arc.
          var cachedSegment = -1;
          double from = 0, to = 0;
          Point3d OnArc(int segment, double fraction)
          {
            if (segment != cachedSegment)
            {
              from = pl.GetDistanceAtParameter(segment);
              to = pl.GetDistanceAtParameter(segment + 1);
              cachedSegment = segment;
            }

            return pl.GetPointAtDist(from + fraction * (to - from));
          }

          AddDensified(d, points, bulges, pl.Closed, tolerance, OnArc);
        }

        break;
      }
      case Polyline2d pl2:
      {
        var drawn = DrawnVertices(pl2, transaction);
        var count = drawn.Count;
        var points = drawn.Select(v => v.Position).ToList();
        var bulges = Enumerable.Range(0, count).Select(i => SegmentBulge(drawn[i].Bulge, i, count, pl2.Closed)).ToList();
        d["closed"] = pl2.Closed;
        d["vertexCount"] = count;
        d["vertices"] = points.Take(maxVertices).Select(p => P(p)).ToList();
        d["bulges"] = bulges.Take(maxVertices).ToList();
        if (bulges.Any(b => b != 0)) d["hasArcs"] = true;
        if (densifyArcs is double tolerance)
        {
          // Vertex positions are in the polyline's own plane (OCS), which is the world XY plane only when the normal
          // is +Z; the arcs are then turned about +Z, in the same coordinates as vertices.
          if (pl2.Normal.IsEqualTo(Vector3d.ZAxis))
            AddDensified(d, points, bulges, pl2.Closed, tolerance, (i, f) => BulgeArcPoint(points[i], points[(i + 1) % count], bulges[i], f));
          else
            d["densifiedOmitted"] = $"The 2D polyline does not lie in the XY plane (normal {Math.Round(pl2.Normal.X, 4)}, {Math.Round(pl2.Normal.Y, 4)}, {Math.Round(pl2.Normal.Z, 4)}); its arcs are not densified.";
        }

        break;
      }
      case Polyline3d pl3:
        d["closed"] = pl3.Closed;
        d["vertices"] = VertexPoints(pl3, transaction, maxVertices);
        break;
      case Circle c:
        d["center"] = P(c.Center);
        d["radius"] = Math.Round(c.Radius, 4);
        break;
      case Arc a:
        d["center"] = P(a.Center);
        d["radius"] = Math.Round(a.Radius, 4);
        d["start"] = P(a.StartPoint);
        d["end"] = P(a.EndPoint);
        break;
      case DBText t:
        d["position"] = P(t.Position);
        d["text"] = t.TextString;
        d["rotationDegrees"] = Math.Round(t.Rotation * 180 / Math.PI, 3);
        d["height"] = Math.Round(t.Height, 4);   // C173
        break;
      case MText m:
        d["position"] = P(m.Location);
        d["text"] = m.Text;
        d["contents"] = m.Contents;
        d["rotationDegrees"] = Math.Round(m.Rotation * 180 / Math.PI, 4);   // C173
        d["height"] = Math.Round(m.TextHeight, 4);   // C173
        d["attachment"] = m.Attachment.ToString();   // C173
        d["width"] = Math.Round(m.Width, 4);   // C173
        break;
      case MLeader ml:
        DescribeMLeader(ml, d);
        break;
      case Dimension dm:   // C141
        d["position"] = P(dm.TextPosition);
        d["text"] = dm.DimensionText;
        d["measurement"] = Math.Round(dm.Measurement, 4);
        d["dimStyle"] = dm.DimensionStyleName;
        break;
      case BlockReference br:
        d["position"] = P(br.Position);
        d["block"] = br.IsDynamicBlock
          ? ((BlockTableRecord)transaction.GetObject(br.DynamicBlockTableRecord, OpenMode.ForRead)).Name
          : br.Name;
        d["rotationDegrees"] = Math.Round(br.Rotation * 180 / Math.PI, 3);
        {   // C168: attribute values (area labels in blocks)
          var atts = new Dictionary<string, string>();
          foreach (ObjectId aid in br.AttributeCollection)
            if (transaction.GetObject(aid, OpenMode.ForRead) is AttributeReference ar && !ar.IsErased) atts[ar.Tag] = ar.TextString;
          if (atts.Count > 0) d["attributes"] = atts;
        }
        break;
      case DBPoint pt:
        d["position"] = P(pt.Position);
        break;
      case RasterImage image:
        d["position"] = P(image.Orientation.Origin);
        d["width"] = Math.Round(image.Orientation.Xaxis.Length, 3);
        d["height"] = Math.Round(image.Orientation.Yaxis.Length, 3);
        // C183: the image's axis vectors (bottom and left edge) and rotation, so a script can map image coordinates to the
        // drawing after the user has fitted the image by hand with ALIGN (a utility owner's scanned map).
        d["xAxis"] = new[] { Math.Round(image.Orientation.Xaxis.X, 6), Math.Round(image.Orientation.Xaxis.Y, 6) };
        d["yAxis"] = new[] { Math.Round(image.Orientation.Yaxis.X, 6), Math.Round(image.Orientation.Yaxis.Y, 6) };
        d["rotationDegrees"] = Math.Round(Math.Atan2(image.Orientation.Xaxis.Y, image.Orientation.Xaxis.X) * 180.0 / Math.PI, 4);
        if (!image.ImageDefId.IsNull && transaction.GetObject(image.ImageDefId, OpenMode.ForRead) is RasterImageDef def)
          d["file"] = def.SourceFileName;
        break;
      case Hatch hatch:
        d["pattern"] = hatch.PatternName;
        d["loops"] = hatch.NumberOfLoops;
        try { d["area"] = Math.Round(hatch.Area, 3); } catch (Autodesk.AutoCAD.Runtime.Exception) { }   // C168
        break;
    }

    return d;
  }

  internal static void DescribeMLeader(MLeader ml, Dictionary<string, object?> d)
  {
    if (ml.ContentType == ContentType.MTextContent)
    {
      using var mtext = ml.MText;
      d["position"] = P(ml.TextLocation);
      d["text"] = mtext?.Text;
      d["contents"] = mtext?.Contents;
      // C117: the background mask (none, mask = background colour, or the ACI fill colour)
      d["mask"] = mtext == null || !mtext.BackgroundFill ? null : mtext.UseBackgroundColor ? "mask" : mtext.BackgroundFillColor.ColorIndex.ToString();
    }

    var arrows = new List<double[]>();
    foreach (var leaderIndex in ml.GetLeaderIndexes())
    {
      foreach (var lineIndex in ml.GetLeaderLineIndexes((int)leaderIndex))
        arrows.Add(P(ml.GetFirstVertex((int)lineIndex)));
    }

    d["arrowPoints"] = arrows;
    d["textHeight"] = Math.Round(ml.TextHeight, 4);
  }

  /// <summary>Moves model space entities, given by handle, to a layer (created when asked). All handles are
  /// checked first; nothing changes if one is missing or not an entity.</summary>
  public static Task<object?> SetEntitiesLayerAsync(JsonObject? parameters)
  {
    var handles = (PluginRuntime.GetParameter(parameters, "handles") as JsonArray)?.Select(n => n?.GetValue<string>()).OfType<string>().Distinct().ToList();
    if (handles == null || handles.Count == 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "handles must list at least one entity handle.");
    var layer = PluginRuntime.GetRequiredString(parameters, "layer");
    var createLayer = PluginRuntime.GetOptionalBool(parameters, "createLayer") ?? false;

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var entities = new List<AcEntity>();
      var problems = new List<string>();
      foreach (var text in handles)
      {
        if (!long.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out var value)
          || !database.TryGetObjectId(new Handle(value), out var id) || id.IsErased)
        {
          problems.Add($"{text}: not found");
          continue;
        }

        if (transaction.GetObject(id, OpenMode.ForRead) is AcEntity entity) entities.Add(entity);
        else problems.Add($"{text}: not an entity");
      }

      if (problems.Count > 0)
        throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"{string.Join("; ", problems)}. Nothing was changed.");

      var table = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
      var created = false;
      if (!table.Has(layer))
      {
        if (!createLayer)
          throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Layer '{layer}' does not exist; pass createLayer=true to create it. Nothing was changed.");
        table.UpgradeOpen();
        var record = new LayerTableRecord { Name = layer };
        table.Add(record);
        transaction.AddNewlyCreatedDBObject(record, true);
        created = true;
      }

      var moved = new List<Dictionary<string, object?>>();
      foreach (var entity in entities)
      {
        var from = entity.Layer;
        entity.UpgradeOpen();
        entity.Layer = layer;
        moved.Add(new Dictionary<string, object?> { ["handle"] = entity.Handle.ToString(), ["from"] = from });
      }

      return new Dictionary<string, object?> { ["layer"] = layer, ["layerCreated"] = created, ["moved"] = moved };
    });
  }

  /// <summary>C177: draw order. Moves entities (handles) and/or every pipe and structure of pipe networks (networkNames)
  /// to the top or bottom of their space's draw order. With includeProfileViewParts (default true) the parts as shown in
  /// profile views (Part.ProfileViewPartId) move too, so e.g. water pipes are drawn over storm pipes at the same level.</summary>
  public static Task<object?> SetDrawOrderAsync(JsonObject? parameters)
  {
    var handles = (PluginRuntime.GetParameter(parameters, "handles") as JsonArray)?.Select(n => n?.GetValue<string>()).OfType<string>().Distinct().ToList() ?? new List<string>();
    var networks = (PluginRuntime.GetParameter(parameters, "networkNames") as JsonArray)?.Select(n => n?.GetValue<string>()).OfType<string>().Distinct().ToList() ?? new List<string>();
    if (handles.Count == 0 && networks.Count == 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Give handles and/or networkNames.");
    var position = (PluginRuntime.GetOptionalString(parameters, "position") ?? "top").Trim().ToLowerInvariant();
    if (position is not ("top" or "bottom"))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"position must be 'top' or 'bottom', not '{position}'.");
    var viewParts = PluginRuntime.GetOptionalBool(parameters, "includeProfileViewParts") ?? true;

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var ids = new List<ObjectId>();
      var problems = new List<string>();
      foreach (var text in handles)
      {
        if (!long.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out var value)
          || !database.TryGetObjectId(new Handle(value), out var id) || id.IsErased)
          problems.Add($"{text}: not found");
        else ids.Add(id);
      }
      var parts = 0; var shownInProfile = 0;
      foreach (var name in networks)
      {
        var network = PipeNetworkCommands.FindPipeNetworkByName(civilDoc, transaction, name, OpenMode.ForRead);
        foreach (var coll in new[] { network.GetPipeIds(), network.GetStructureIds() })
          foreach (ObjectId id in coll)
          {
            ids.Add(id); parts++;
            if (!viewParts) continue;
            var part = transaction.GetObject(id, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Part;
            var vp = part?.ProfileViewPartId ?? ObjectId.Null;
            if (!vp.IsNull && !vp.IsErased) { ids.Add(vp); shownInProfile++; }
          }
      }
      if (problems.Count > 0)
        throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"{string.Join("; ", problems)}. Nothing was changed.");

      var moved = 0;
      foreach (var group in ids.Distinct().Select(id => (id, ent: transaction.GetObject(id, OpenMode.ForRead) as AcEntity))
                 .Where(t => t.ent != null).GroupBy(t => t.ent!.BlockId))
      {
        var space = (BlockTableRecord)transaction.GetObject(group.Key, OpenMode.ForRead);
        var table = (DrawOrderTable)transaction.GetObject(space.DrawOrderTableId, OpenMode.ForWrite);
        var coll = new ObjectIdCollection(group.Select(t => t.id).ToArray());
        if (position == "top") table.MoveToTop(coll); else table.MoveToBottom(coll);
        moved += coll.Count;
      }
      return new Dictionary<string, object?> { ["position"] = position, ["moved"] = moved, ["networkParts"] = parts, ["profileViewParts"] = shownInProfile };
    });
  }

  /// <summary>Plain AutoCAD entities that entities_erase may remove. Civil 3D objects, viewports, attributes,
  /// tables and anything else are refused.</summary>
  private static string? ErasableKind(AcEntity entity) => entity switch
  {
    Autodesk.Civil.DatabaseServices.Entity => null,
    AttributeReference or AttributeDefinition => null,
    Table => null,
    DBText => "text",
    MText => "mtext",
    MLeader => "mleader",
    Line => "line",
    Polyline => "polyline",
    Polyline2d => "polyline2d",
    Polyline3d => "polyline3d",
    Circle => "circle",
    Arc => "arc",
    Hatch => "hatch",
    BlockReference => "insert",
    Leader => "leader",
    Dimension => "dimension",
    DBPoint => "point",   // C133: AutoCAD points (createPoints, C130b) can be erased too
    _ => null,
  };

  /// <summary>Resolves handles to plain AutoCAD entities in model space or a layout, not on locked layers. One
  /// missing or refused handle and nothing is done ("Nothing was {verb}.").</summary>
  private static List<(AcEntity Entity, string Kind)> PlainEntities(List<string> handles, Database database, Transaction transaction, string verb)
  {
    var entities = new List<(AcEntity Entity, string Kind)>();
    var missing = new List<string>();
    var refused = new List<string>();
    var layers = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
    foreach (var text in handles)
    {
      if (!long.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out var value)
        || !database.TryGetObjectId(new Handle(value), out var id) || id.IsErased)
      {
        missing.Add(text);
        continue;
      }

      if (transaction.GetObject(id, OpenMode.ForRead) is not AcEntity entity)
      {
        refused.Add($"{text}: not an entity");
        continue;
      }

      var kind = ErasableKind(entity);
      var owner = transaction.GetObject(entity.OwnerId, OpenMode.ForRead) as BlockTableRecord;
      if (kind == null)
        refused.Add($"{text}: {entity.GetRXClass().DxfName} is not a plain AutoCAD entity");
      else if (owner == null || !owner.IsLayout)
        refused.Add($"{text}: not in model space or a layout");
      else if (((LayerTableRecord)transaction.GetObject(layers[entity.Layer], OpenMode.ForRead)).IsLocked)
        refused.Add($"{text}: on locked layer '{entity.Layer}'");
      else
        entities.Add((entity, kind));
    }

    if (missing.Count > 0)
      throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Not found: {string.Join(", ", missing)}. Nothing was {verb}.");
    if (refused.Count > 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{string.Join("; ", refused)}. Nothing was {verb}.");
    return entities;
  }

  /// <summary>Sets color, linetype, lineweight and/or linetype scale on plain AutoCAD entities given by handle
  /// (same entities and all-or-nothing rule as entities_erase).</summary>
  public static Task<object?> SetEntitiesStyleAsync(JsonObject? parameters)
  {
    var handles = (PluginRuntime.GetParameter(parameters, "handles") as JsonArray)?.Select(n => n?.GetValue<string>()).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    if (handles == null || handles.Count == 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "handles must list at least one entity handle.");
    if (new[] { "color", "linetype", "lineweight", "linetypeScale", "transparency" }.All(k => PluginRuntime.GetParameter(parameters, k) == null))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Give color, linetype, lineweight, linetypeScale and/or transparency. Nothing was changed.");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var entities = PlainEntities(handles, database, transaction, "changed");
      var changed = new List<Dictionary<string, object?>>();
      foreach (var (entity, kind) in entities)
      {
        var before = AcadCommands.StyleOf(entity);
        entity.UpgradeOpen();
        AcadCommands.ApplyEntityStyle(entity, parameters, database, transaction);
        var after = AcadCommands.StyleOf(entity);
        after["handle"] = entity.Handle.ToString();
        after["type"] = kind;
        after["before"] = before;
        changed.Add(after);
      }

      return new Dictionary<string, object?> { ["changed"] = changed, ["count"] = changed.Count };
    });
  }

  /// <summary>Erases plain AutoCAD entities (texts, multileaders, lines, polylines, circles, arcs, hatches, block
  /// references, leaders, dimensions) in model space or a layout, given by handle. All handles are checked
  /// first; one missing, refused or locked handle and nothing is erased.</summary>
  /// <summary>Erases plain AutoCAD entities given by handle (all or nothing), or C212 (2026-11-24) chosen by script id
  /// prefix and/or layer in model space or a named layout: {idPrefix?, layer?, layout?, allOnLayer?, dryRun?}. A layer
  /// alone erases everything on it and needs allOnLayer: true. Chosen entities that cannot be erased (Civil objects,
  /// locked layer) are reported under skipped. Use case: the draw, label and clean-up cycle, 30 scripts
  /// listed their id prefix and sent the handles back.</summary>
  public static Task<object?> EraseEntitiesAsync(JsonObject? parameters)
  {
    var handles = (PluginRuntime.GetParameter(parameters, "handles") as JsonArray)?.Select(n => n?.GetValue<string>()).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    var idPrefix = PluginRuntime.GetOptionalString(parameters, "idPrefix");
    var layerName = PluginRuntime.GetOptionalString(parameters, "layer");
    if (handles is { Count: > 0 } && (idPrefix != null || layerName != null))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Give handles, or idPrefix and/or layer, not both. Nothing was erased.");
    if (handles is not { Count: > 0 })
    {
      if (idPrefix == null && string.IsNullOrWhiteSpace(layerName))
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Give handles (at least one entity handle), idPrefix or layer. Nothing was erased.");
      return EraseSelectedAsync(parameters, idPrefix, string.IsNullOrWhiteSpace(layerName) ? null : layerName!.Trim());
    }

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var entities = PlainEntities(handles, database, transaction, "erased");
      var erased = new List<Dictionary<string, object?>>();
      foreach (var (entity, kind) in entities)
      {
        var layer = entity.Layer;
        entity.UpgradeOpen();
        entity.Erase();
        erased.Add(new Dictionary<string, object?> { ["handle"] = entity.Handle.ToString(), ["type"] = kind, ["layer"] = layer });
      }

      return new Dictionary<string, object?> { ["erased"] = erased, ["count"] = erased.Count };
    });
  }

  /// <summary>C212: the selector form of eraseEntities (idPrefix, layer, layout).</summary>
  private static Task<object?> EraseSelectedAsync(JsonObject? parameters, string? idPrefix, string? layer)
  {
    var layoutName = PluginRuntime.GetOptionalString(parameters, "layout");
    var allOnLayer = PluginRuntime.GetOptionalBool(parameters, "allOnLayer") ?? false;
    var dryRun = PluginRuntime.GetOptionalBool(parameters, "dryRun") ?? false;
    if (idPrefix == null && !allOnLayer)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"layer alone erases every entity on '{layer}'; give allOnLayer: true to mean that, or an idPrefix. Nothing was erased.");

    object? Body(Autodesk.AutoCAD.ApplicationServices.Document doc, Autodesk.Civil.ApplicationServices.CivilDocument civilDoc, Database database, Transaction transaction)
    {
      var spaceId = string.IsNullOrWhiteSpace(layoutName) || string.Equals(layoutName, "Model", StringComparison.OrdinalIgnoreCase)
        ? SymbolUtilityServices.GetBlockModelSpaceId(database)
        : LayoutCommands.FindLayout(database, transaction, layoutName!).BlockTableRecordId;
      var layers = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
      if (layer != null && !layers.Has(layer))
        throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Layer '{layer}' does not exist. Nothing was erased.");
      var space = (BlockTableRecord)transaction.GetObject(spaceId, OpenMode.ForRead);
      var erased = new List<Dictionary<string, object?>>();
      var skipped = new List<Dictionary<string, object?>>();
      var first = true;
      foreach (ObjectId id in space)
      {
        if (transaction.GetObject(id, OpenMode.ForRead) is not AcEntity entity) continue;
        if (entity is Viewport && first) { first = false; continue; }   // a layout's own paper space viewport
        if (layer != null && !string.Equals(entity.Layer, layer, StringComparison.OrdinalIgnoreCase)) continue;
        string? scriptId = null;
        if (idPrefix != null)
        {
          scriptId = ScriptIdCommands.Read(entity).Id;
          if (scriptId == null || !scriptId.StartsWith(idPrefix, StringComparison.Ordinal)) continue;
        }
        var kind = ErasableKind(entity);
        var entry = new Dictionary<string, object?> { ["handle"] = entity.Handle.ToString(), ["type"] = kind ?? entity.GetRXClass().DxfName, ["layer"] = entity.Layer, ["id"] = scriptId };
        if (kind == null) { entry["reason"] = "not a plain AutoCAD entity"; skipped.Add(entry); continue; }
        if (((LayerTableRecord)transaction.GetObject(layers[entity.Layer], OpenMode.ForRead)).IsLocked) { entry["reason"] = "locked layer"; skipped.Add(entry); continue; }
        if (!dryRun)
        {
          entity.UpgradeOpen();
          entity.Erase();
        }
        erased.Add(entry);
      }
      return new Dictionary<string, object?>
      {
        ["erased"] = erased, ["count"] = erased.Count, ["skipped"] = skipped, ["dryRun"] = dryRun,
        ["idPrefix"] = idPrefix, ["layer"] = layer, ["layout"] = string.IsNullOrWhiteSpace(layoutName) ? "Model" : layoutName,
      };
    }

    return dryRun ? CivilExecution.ReadAsync<object?>(Body) : CivilExecution.WriteAsync<object?>(Body);
  }

  /// <summary>C121: scales plain AutoCAD entities given by handle, each about its own centre (circle and arc centre,
  /// block reference insertion point, otherwise the centre of its extents) by factor. Use case: the
  /// surveyed lamp post circles were drawn r 0,673 m, too big at 1:200; SCALE only works interactively. Same entities
  /// and all-or-nothing rule as entities_erase.</summary>
  public static Task<object?> ScaleEntitiesAsync(JsonObject? parameters)
  {
    var handles = (PluginRuntime.GetParameter(parameters, "handles") as JsonArray)?.Select(n => n?.GetValue<string>()).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    if (handles == null || handles.Count == 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "handles must list at least one entity handle.");
    var factor = PluginRuntime.GetOptionalDouble(parameters, "factor")
      ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Give factor (> 0). Nothing was changed.");
    if (!(factor > 0) || double.IsInfinity(factor))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "factor must be > 0. Nothing was changed.");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var entities = PlainEntities(handles, database, transaction, "scaled");
      var scaled = new List<Dictionary<string, object?>>();
      foreach (var (entity, kind) in entities)
      {
        Point3d centre = entity switch
        {
          Circle c => c.Center,
          Arc a => a.Center,
          BlockReference b => b.Position,
          _ => entity.Bounds is Extents3d e ? new Point3d((e.MinPoint.X + e.MaxPoint.X) / 2, (e.MinPoint.Y + e.MaxPoint.Y) / 2, (e.MinPoint.Z + e.MaxPoint.Z) / 2)
               : throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{entity.Handle} has no extents to scale about. Nothing was changed."),
        };
        entity.UpgradeOpen();
        entity.TransformBy(Matrix3d.Scaling(factor, centre));
        var d = new Dictionary<string, object?> { ["handle"] = entity.Handle.ToString(), ["type"] = kind, ["layer"] = entity.Layer, ["about"] = P(centre) };
        if (entity is Circle circle) d["radius"] = Math.Round(circle.Radius, 4);
        scaled.Add(d);
      }

      return new Dictionary<string, object?> { ["scaled"] = scaled, ["count"] = scaled.Count, ["factor"] = factor };
    });
  }

  /// <summary>C146: moves the text (and with it the landing) of multileaders given by handle by dx, dy, dz; the arrow
  /// points stay where they are. MOVE (C114 moveEntities) moves the whole leader. Use case (standard
  /// drawings): a flag's text had shifted 0,25 m when its wording was changed, and the user's placement had to be restored
  /// without moving the leader's point. All handles are checked first; dryRun reports without changing.</summary>
  public static Task<object?> MoveMLeaderTextAsync(JsonObject? parameters)
  {
    var handles = (PluginRuntime.GetParameter(parameters, "handles") as JsonArray)?.Select(n => n?.GetValue<string>()).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    if (handles == null || handles.Count == 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "handles must list at least one multileader handle.");
    var shift = new Vector3d(PluginRuntime.GetOptionalDouble(parameters, "dx") ?? 0.0, PluginRuntime.GetOptionalDouble(parameters, "dy") ?? 0.0,
      PluginRuntime.GetOptionalDouble(parameters, "dz") ?? 0.0);
    var dryRun = PluginRuntime.GetOptionalBool(parameters, "dryRun") ?? false;

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var leaders = handles.Select(h =>
      {
        if (!h.All(Uri.IsHexDigit) || !database.TryGetObjectId(CivilObjectUtils.ParseHandle(h), out var id) || id.IsErased)
          throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"No object with handle {h}. Nothing was changed.");
        return transaction.GetObject(id, OpenMode.ForRead) as MLeader
          ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{h} is not a multileader. Nothing was changed.");
      }).ToList();
      var moved = new List<Dictionary<string, object?>>();
      foreach (var ml in leaders)
      {
        var before = ml.TextLocation;
        if (!dryRun)
        {
          ml.UpgradeOpen();
          ml.TextLocation = before + shift;
        }
        moved.Add(new Dictionary<string, object?>
        {
          ["handle"] = ml.Handle.ToString(), ["textBefore"] = P(before), ["textAfter"] = P(dryRun ? before + shift : ml.TextLocation),
        });
      }

      return new Dictionary<string, object?> { ["moved"] = moved, ["count"] = moved.Count, ["dryRun"] = dryRun };
    });
  }

  /// <summary>C145: reverses the direction of lines, polylines (lightweight, 2D, 3D) and splines given by handle, so
  /// that start and end swap and a directional linetype points the other way; the geometry is unchanged. Arcs are
  /// refused (always counterclockwise in AutoCAD). dryRun reports without changing. Same all-or-nothing rule as
  /// entities_erase. Use case (standard drawings): the pressure sewer was drawn away from the main,
  /// and Svenskt Vatten P109's LPL linetype is drawn in the direction of flow.</summary>
  public static Task<object?> ReverseCurvesAsync(JsonObject? parameters)
  {
    var handles = (PluginRuntime.GetParameter(parameters, "handles") as JsonArray)?.Select(n => n?.GetValue<string>()).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    if (handles == null || handles.Count == 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "handles must list at least one entity handle.");
    var dryRun = PluginRuntime.GetOptionalBool(parameters, "dryRun") ?? false;

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var entities = PlainEntities(handles, database, transaction, "reversed");
      foreach (var (entity, kind) in entities)
      {
        if (entity is not (Line or Polyline or Polyline2d or Polyline3d or Spline))
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{entity.Handle} ({kind}) is not a line, polyline or spline; only those can be reversed. Nothing was changed.");
      }
      var reversed = new List<Dictionary<string, object?>>();
      foreach (var (entity, kind) in entities)
      {
        var curve = (Curve)entity;
        var start = curve.StartPoint;
        var end = curve.EndPoint;
        if (!dryRun)
        {
          curve.UpgradeOpen();
          curve.ReverseCurve();
        }
        reversed.Add(new Dictionary<string, object?>
        {
          ["handle"] = entity.Handle.ToString(), ["type"] = kind, ["layer"] = entity.Layer,
          ["startBefore"] = P(start), ["endBefore"] = P(end),
          ["startAfter"] = P(dryRun ? end : curve.StartPoint), ["endAfter"] = P(dryRun ? start : curve.EndPoint),
        });
      }

      return new Dictionary<string, object?> { ["reversed"] = reversed, ["count"] = reversed.Count, ["dryRun"] = dryRun };
    });
  }

  private static List<double[]> VertexPoints(IEnumerable<ObjectId> vertexIds, Transaction transaction, int maxVertices)
  {
    var points = new List<double[]>();
    foreach (var id in vertexIds)
    {
      if (points.Count >= maxVertices) break;
      var v = transaction.GetObject(id, OpenMode.ForRead);
      if (v is Vertex2d v2 && v2.VertexType != Vertex2dType.SplineControlVertex) points.Add(P(v2.Position));
      else if (v is PolylineVertex3d v3 && v3.VertexType != Vertex3dType.ControlVertex) points.Add(P(v3.Position));
    }

    return points;
  }

  private static List<double[]> VertexPoints(Polyline3d pl, Transaction transaction, int maxVertices) =>
    VertexPoints(pl.Cast<ObjectId>(), transaction, maxVertices);

  /// <summary>The vertices a legacy 2D polyline is drawn through (spline control vertices left out), in order.</summary>
  private static List<Vertex2d> DrawnVertices(Polyline2d pl, Transaction transaction) =>
    pl.Cast<ObjectId>()
      .Select(id => transaction.GetObject(id, OpenMode.ForRead))
      .OfType<Vertex2d>()
      .Where(v => v.VertexType != Vertex2dType.SplineControlVertex)
      .ToList();

  /// <summary>Bulge of the segment that starts at vertex <paramref name="index"/> (tan of a quarter of its included
  /// angle, positive counterclockwise): 0 for a straight segment and for the last vertex of an open polyline, which
  /// starts no segment whatever bulge it stores.</summary>
  private static double SegmentBulge(double bulge, int index, int count, bool closed) =>
    (!closed && index == count - 1) || Math.Abs(bulge) <= BulgeEpsilon ? 0 : Math.Round(bulge, 10);

  /// <summary>Number of equal chords an arc segment (given by its bulge and chord) is split into so that none lies
  /// more than <paramref name="tolerance"/> from the arc: a chord over the angle φ lies 2R·sin²(φ/4) inside an arc
  /// of radius R. 1 for a straight or zero-length segment.</summary>
  private static int ArcPieces(double bulge, double chord, double tolerance)
  {
    if (bulge == 0 || chord < 1e-12) return 1;
    var sweep = 4 * Math.Atan(Math.Abs(bulge));
    var radius = chord / (2 * Math.Sin(sweep / 2));
    var maxAngle = 4 * Math.Asin(Math.Min(1, Math.Sqrt(tolerance / (2 * radius))));
    return (int)Math.Min(MaxDensifiedPoints + 1.0, Math.Max(1, Math.Ceiling(sweep / maxAngle - 1e-9)));
  }

  /// <summary>Sets <c>densified</c>: every vertex in order with the points that split each arc segment into
  /// <see cref="ArcPieces"/> equal chords in between (a closed polyline does not repeat its first vertex, as in
  /// vertices). <paramref name="pointOnArc"/> gives the point at a fraction of segment i. Over
  /// <see cref="MaxDensifiedPoints"/> points, sets <c>densifiedOmitted</c> instead.</summary>
  private static void AddDensified(Dictionary<string, object?> d, IReadOnlyList<Point3d> vertices, IReadOnlyList<double> bulges, bool closed, double tolerance, Func<int, double, Point3d> pointOnArc)
  {
    var count = vertices.Count;
    var segments = count < 2 ? 0 : closed ? count : count - 1;
    var pieces = new int[segments];
    long needed = count;
    for (var i = 0; i < segments; i++)
    {
      pieces[i] = ArcPieces(bulges[i], vertices[i].DistanceTo(vertices[(i + 1) % count]), tolerance);
      needed += pieces[i] - 1;
    }

    if (needed > MaxDensifiedPoints)
    {
      d["densifiedOmitted"] = $"Following the arcs within {tolerance} would take {needed} points, more than {MaxDensifiedPoints}; give a larger densifyArcs.";
      return;
    }

    var points = new List<double[]>((int)needed);
    for (var i = 0; i < count; i++)
    {
      points.Add(P(vertices[i]));
      for (var k = 1; i < segments && k < pieces[i]; k++) points.Add(P(pointOnArc(i, (double)k / pieces[i])));
    }

    d["densified"] = points;
  }

  /// <summary>Point at <paramref name="fraction"/> of the arc from start to end with the given bulge, in the
  /// polyline's plane (normal +Z): the start point turned about the arc centre by that fraction of the included
  /// angle 4·atan(bulge). The centre lies (chord/2)·(1 − b²)/(2b) from the chord midpoint, to the left of the chord
  /// for a positive (counterclockwise) bulge below 1.</summary>
  private static Point3d BulgeArcPoint(Point3d start, Point3d end, double bulge, double fraction)
  {
    var chord = end - start;
    var centre = start + chord * 0.5 + Vector3d.ZAxis.CrossProduct(chord).GetNormal() * (chord.Length * (1 - bulge * bulge) / (4 * bulge));
    return start.RotateBy(4 * Math.Atan(bulge) * fraction, Vector3d.ZAxis, centre);
  }
}
