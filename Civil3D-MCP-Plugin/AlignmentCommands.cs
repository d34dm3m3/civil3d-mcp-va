using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.DatabaseServices;
using System.Text.Json.Nodes;

namespace Civil3DMcpPlugin;

public static class AlignmentCommands
{
  public static Task<object?> ListAlignmentsAsync()
  {
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignments = new List<Dictionary<string, object?>>();
      foreach (ObjectId objectId in civilDoc.GetAlignmentIds())
      {
        var alignment = CivilObjectUtils.GetRequiredObject<Alignment>(transaction, objectId, OpenMode.ForRead);
        alignments.Add(ToAlignmentSummary(alignment));
      }

      return new Dictionary<string, object?>
      {
        ["alignments"] = alignments,
      };
    });
  }

  public static Task<object?> GetAlignmentAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, name);
      // C157: the typed chain reader (as alignmentEntitiesGet); reflection on the entities gave length 0 for every
      // entity (C8, live 2026-10-02: the properties live on the sub-entities).
      var entities = AlignmentChain.Read(alignment.Entities).Select(e => new Dictionary<string, object?>
      {
        ["index"] = e.Index,
        ["type"] = e.Type,
        ["startStation"] = e.StartStation,
        ["endStation"] = e.EndStation,
        ["length"] = e.Length,
      }).ToList();

      var dependentProfiles = alignment.GetProfileIds()
        .Cast<ObjectId>()
        .Select(id => CivilObjectUtils.GetRequiredObject<Profile>(transaction, id, OpenMode.ForRead).Name)
        .ToList();

      return new Dictionary<string, object?>
      {
        ["name"] = alignment.Name,
        ["handle"] = CivilObjectUtils.GetHandle(alignment),
        ["type"] = MapAlignmentType(CivilObjectUtils.GetStringProperty(alignment, "AlignmentType") ?? alignment.GetType().Name),
        ["style"] = CivilObjectUtils.GetName(transaction.GetObject(alignment.StyleId, OpenMode.ForRead)) ?? string.Empty,
        ["layer"] = alignment.Layer,
        ["length"] = alignment.Length,
        ["startStation"] = alignment.StartingStation,
        ["endStation"] = alignment.EndingStation,
        ["entityCount"] = entities.Count,
        ["entities"] = entities,
        ["dependentProfiles"] = dependentProfiles,
        ["dependentCorridors"] = new List<string>(),
        ["isReference"] = CivilObjectUtils.GetPropertyValue<bool?>(alignment, "IsReferenceObject") ?? false,
      };
    });
  }

  /// <summary>
  /// Alignment.PointLocation takes a raw station (no station equations), like every station argument of the
  /// Civil 3D API. stationMode "nominal" converts the station as shown with equations to the raw station first
  /// (AlignmentChain.NominalToRaw); a nominal station that occurs twice or not at all is refused.
  /// </summary>
  public static Task<object?> StationToPointAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var station = PluginRuntime.GetRequiredDouble(parameters, "station");
    var offset = PluginRuntime.GetOptionalDouble(parameters, "offset") ?? 0;
    var stationMode = PluginRuntime.GetOptionalString(parameters, "stationMode") ?? "raw";
    if (stationMode != "raw" && stationMode != "nominal")
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"stationMode must be 'raw' or 'nominal', not '{stationMode}'.");
    }

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, name);
      var rawStation = station;
      if (stationMode == "nominal")
      {
        var raws = AlignmentChain.NominalToRaw(alignment, station);
        if (raws.Count == 0)
        {
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
            $"Nominal station {station} does not occur on alignment '{alignment.Name}' (raw {alignment.StartingStation}-{alignment.EndingStation}, " +
            $"{alignment.StationEquations.Count} station equation(s), ends at nominal {alignment.EndingStationWithEquations}).");
        }

        if (raws.Count > 1)
        {
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
            $"Nominal station {station} occurs {raws.Count} times on alignment '{alignment.Name}' (raw stations {string.Join(", ", raws)}); " +
            "give the raw station with stationMode 'raw'.");
        }

        rawStation = raws[0];
      }

      double x = 0;
      double y = 0;
      alignment.PointLocation(rawStation, offset, ref x, ref y);
      string? label = null;
      try { label = alignment.GetStationStringWithEquations(rawStation); } catch (Exception) { }

      return new Dictionary<string, object?>
      {
        ["x"] = x,
        ["y"] = y,
        ["station"] = station,
        ["offset"] = offset,
        ["units"] = CivilObjectUtils.LinearUnits(database),
        ["stationMode"] = stationMode,
        ["rawStation"] = rawStation,
        ["stationWithEquations"] = label,
      };
    });
  }

  /// <summary>
  /// Every entity of an alignment with exact start/end points (raw stations), the before/after links and the
  /// position along the chain, plus whether the chain is continuous (see AlignmentChain).
  /// </summary>
  public static Task<object?> GetAlignmentEntitiesAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, name);
      var entities = alignment.Entities;
      var infos = AlignmentChain.Read(entities);
      var check = AlignmentChain.Check(infos, entities);

      return new Dictionary<string, object?>
      {
        ["name"] = alignment.Name,
        ["length"] = alignment.Length,
        ["startStation"] = alignment.StartingStation,
        ["endStation"] = alignment.EndingStation,
        ["endStationWithEquations"] = alignment.EndingStationWithEquations,
        ["stationsAreRaw"] = true,
        ["entityCount"] = infos.Count,
        ["firstEntityId"] = check.FirstId,
        ["lastEntityId"] = check.LastId,
        ["continuous"] = check.Continuous,
        ["problems"] = check.Problems,
        ["stationEquations"] = AlignmentChain.DescribeEquations(alignment),
        ["entities"] = DescribeEntities(infos, check),
      };
    });
  }

  public static Task<object?> SampleStationsAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var offset = PluginRuntime.GetOptionalDouble(parameters, "offset") ?? 0;
    var stationsNode = PluginRuntime.GetParameter(parameters, "stations") as JsonArray;
    if (stationsNode == null || stationsNode.Count == 0)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "alignmentSampleStations requires at least one station.");
    }
    // C158 (2026-10-21): up to 10 000 stations (was 200, so scripts called alignmentStationToPoint once per point, 63 call
    // sites), each a number or {station, offset} (its own offset; the call's offset otherwise).
    if (stationsNode.Count > 10000)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "alignmentSampleStations accepts at most 10000 stations.");
    }

    var stations = stationsNode.Select(node => node switch
      {
        JsonObject o => (Station: o["station"]?.GetValue<double>()
            ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Every station object needs a numeric station."),
          Offset: o["offset"]?.GetValue<double>() ?? offset),
        JsonValue v => (Station: v.GetValue<double>(), Offset: offset),
        _ => throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Every station must be a number or {station, offset}."),
      })
      .ToArray();

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, name);
      var units = CivilObjectUtils.LinearUnits(database);
      var samples = new List<Dictionary<string, object?>>(stations.Length);

      foreach (var (station, stationOffset) in stations)
      {
        double x = 0;
        double y = 0;
        alignment.PointLocation(station, stationOffset, ref x, ref y);
        samples.Add(new Dictionary<string, object?>
        {
          ["x"] = x,
          ["y"] = y,
          ["station"] = station,
          ["offset"] = stationOffset,
          ["units"] = units,
        });
      }

      return new Dictionary<string, object?> { ["samples"] = samples };
    });
  }

  /// <summary>
  /// Station and offset of a point. C101 (2026-10-11): the nearest point on the alignment, found by sampling the
  /// alignment and refining around the nearest sample. Alignment.StationOffset returns the first perpendicular
  /// solution instead: live in a pilot project a vertex just before the start gave 0/378,8 and a point on the
  /// outside of the angle point 0/416,7 gave 0/221. A point beyond the start or end gets the end station and
  /// outOfRange true (strict: true makes that an error); maxOffset rejects points farther from the alignment.
  /// Offset is positive to the right of the alignment's direction, as in Civil.
  /// </summary>
  public static Task<object?> PointToStationAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var x = PluginRuntime.GetRequiredDouble(parameters, "x");
    var y = PluginRuntime.GetRequiredDouble(parameters, "y");
    var strict = PluginRuntime.GetOptionalBool(parameters, "strict") ?? false;
    var maxOffset = PluginRuntime.GetOptionalDouble(parameters, "maxOffset");
    if (maxOffset is <= 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "maxOffset must be > 0 (model units).");

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, name);
      var nearest = NearestStation(alignment, x, y);
      var units = CivilObjectUtils.LinearUnits(database);
      if (nearest.OutOfRange && strict)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
          $"({x:0.###}, {y:0.###}) lies beyond the {(nearest.Station <= alignment.StartingStation + 1e-9 ? "start" : "end")} of '{alignment.Name}' " +
          $"({nearest.Distance:0.###} {units} from station {nearest.Station:0.###}).");
      if (maxOffset != null && nearest.Distance > maxOffset.Value)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
          $"({x:0.###}, {y:0.###}) is {nearest.Distance:0.###} {units} from '{alignment.Name}', more than maxOffset {maxOffset.Value:0.###}.");

      var result = new Dictionary<string, object?>
      {
        ["station"] = nearest.Station,
        ["offset"] = nearest.Offset,
        ["distanceFromAlignment"] = nearest.Distance,
        ["outOfRange"] = nearest.OutOfRange,
        ["units"] = units,
      };
      if (nearest.Ties.Count > 1) result["equallyNearStations"] = nearest.Ties;
      if (nearest.OutOfRange)
        result["warning"] = "The point lies beyond the alignment's end; the end station is given (strict: true makes this an error).";
      return result;
    });
  }

  internal readonly record struct NearestOnAlignment(double Station, double Offset, double Distance, bool OutOfRange, IReadOnlyList<double> Ties);

  /// <summary>C101: nearest point on the alignment to (x, y): samples every L/2000 (5 cm to 1 m), then a ternary search
  /// around the nearest sample. Out of range when the nearest point is an end and the point lies beyond it along the
  /// end's tangent.</summary>
  internal static NearestOnAlignment NearestStation(Alignment alignment, double x, double y)
  {
    var start = alignment.StartingStation;
    var end = alignment.EndingStation;
    var length = Math.Max(end - start, 1e-9);
    var step = Math.Clamp(length / 2000.0, 0.05, 1.0);
    double DistanceSq(double station)
    {
      double px = 0, py = 0;
      alignment.PointLocation(Math.Clamp(station, start, end), 0.0, ref px, ref py);
      return (px - x) * (px - x) + (py - y) * (py - y);
    }

    var stations = new List<double>();
    var distances = new List<double>();
    for (var station = start; ; station += step)
    {
      var at = Math.Min(station, end);
      stations.Add(at);
      distances.Add(DistanceSq(at));
      if (at >= end) break;
    }

    double Refine(double around)
    {
      var lo = Math.Max(start, around - step);
      var hi = Math.Min(end, around + step);
      for (var i = 0; i < 80 && hi - lo > 1e-7; i++)
      {
        var m1 = lo + (hi - lo) / 3.0;
        var m2 = hi - (hi - lo) / 3.0;
        if (DistanceSq(m1) <= DistanceSq(m2)) hi = m2; else lo = m1;
      }
      return (lo + hi) / 2.0;
    }

    // Every local minimum of the samples is refined; minima as near as the nearest (within 1 mm) are ties, as for a
    // point on the bisector inside an angle point (a pipe's bend vertex beside the alignment's): their mean station is
    // given, which is the angle point's station for a symmetric tie.
    var candidates = new List<(double Station, double Distance)>();
    for (var i = 0; i < stations.Count; i++)
    {
      var left = i == 0 ? double.MaxValue : distances[i - 1];
      var right = i == stations.Count - 1 ? double.MaxValue : distances[i + 1];
      if (distances[i] <= left && distances[i] <= right)
      {
        var refined = Refine(stations[i]);
        candidates.Add((refined, Math.Sqrt(DistanceSq(refined))));
      }
    }
    candidates.Add((start, Math.Sqrt(DistanceSq(start))));
    candidates.Add((end, Math.Sqrt(DistanceSq(end))));
    var nearestDistance = candidates.Min(c => c.Distance);
    var ties = candidates.Where(c => c.Distance - nearestDistance <= 0.001).Select(c => c.Station)
      .OrderBy(st => st).Aggregate(new List<double>(), (list, st) => { if (list.Count == 0 || st - list[^1] > 1e-4) list.Add(st); return list; });
    var averaged = ties.Count > 1 && ties[^1] - ties[0] <= Math.Max(4 * nearestDistance, 1.0);
    var stationOut = averaged ? ties.Average() : ties[0];
    // The ternary search stops up to 5e-8 short of an end, and at the end the refined station sorts before the exact
    // end and wins the dedup (C101b): snap so the out-of-range test sees the end station.
    if (end - stationOut < 1e-6) stationOut = end;
    if (stationOut - start < 1e-6) stationOut = start;

    // Direction at the station (a short chord, inward at the ends) for the offset's side and the out-of-range test.
    var h = Math.Min(0.01, length / 4.0);
    var s0 = Math.Max(start, stationOut - h);
    var s1 = Math.Min(end, stationOut + h);
    double ax = 0, ay = 0, bx = 0, by = 0, cx = 0, cy = 0;
    alignment.PointLocation(s0, 0.0, ref ax, ref ay);
    alignment.PointLocation(s1, 0.0, ref bx, ref by);
    alignment.PointLocation(stationOut, 0.0, ref cx, ref cy);
    var tx = bx - ax;
    var ty = by - ay;
    var tn = Math.Sqrt(tx * tx + ty * ty);
    if (tn > 0) { tx /= tn; ty /= tn; }
    var vx = x - cx;
    var vy = y - cy;
    // For an averaged tie the distance is still the shortest one (to either side of the angle point).
    var distance = averaged ? nearestDistance : Math.Sqrt(vx * vx + vy * vy);
    var along = vx * tx + vy * ty;
    var outOfRange = (stationOut <= start + 1e-9 && along < -1e-6) || (stationOut >= end - 1e-9 && along > 1e-6);
    // Right of the direction is positive: cross(t, v) < 0.
    var cross = tx * vy - ty * vx;
    var offset = outOfRange ? -cross : (cross > 0 ? -distance : distance);
    return new NearestOnAlignment(stationOut, offset, distance, outOfRange, ties.Count > 1 ? ties : Array.Empty<double>());
  }

  // C96: shortest segment createAlignment accepts (model units; 1 mm in metres).
  private const double MinimumSegment = 0.001;

  /// <summary>
  /// An alignment from a polyline through points. C96 (2026-10-10): `bulges` (one per segment, tan(θ/4), negative =
  /// clockwise) makes arcs, `addCurvesBetweenTangents` false (default true, the old behaviour) keeps the geometry exactly
  /// as given with no curves added, and `startStation` sets the station at the start point; the answer lists the
  /// entities as alignmentEntitiesGet does. Use case: a pilot project recreates an existing alignment exactly in the
  /// project drawing from entities_get in the source drawing; with curves added between tangents at a default radius the
  /// geometry came out wrong. Everything is checked before the first change (points with finite x/y, no segment shorter
  /// than 1 mm, finite bulges below a half circle, a name not in use).
  /// </summary>
  public static Task<object?> CreateAlignmentAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name").Trim();
    var pointsNode = PluginRuntime.GetParameter(parameters, "points") as JsonArray;
    if (pointsNode == null || pointsNode.Count < 2)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "createAlignment requires at least two points. Nothing was changed.");
    }

    // C96: every point is checked; the old loop skipped a non-object but still used the loop index for the vertex.
    var points = new List<Point2d>(pointsNode.Count);
    for (var index = 0; index < pointsNode.Count; index++)
    {
      if (pointsNode[index] is not JsonObject point || !TryNumber(point["x"], out var px) || !TryNumber(point["y"], out var py))
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"points[{index}] must be {{x, y}} with finite numbers. Nothing was changed.");
      points.Add(new Point2d(px, py));
    }

    var bulges = new double[points.Count - 1];
    var bulgesGiven = PluginRuntime.GetParameter(parameters, "bulges") is JsonNode;
    if (PluginRuntime.GetParameter(parameters, "bulges") is JsonNode bulgesNode)
    {
      if (bulgesNode is not JsonArray bulgeArray || bulgeArray.Count != points.Count - 1)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"bulges must list one number per segment ({points.Count - 1}). Nothing was changed.");
      for (var index = 0; index < bulgeArray.Count; index++)
      {
        if (!TryNumber(bulgeArray[index], out var bulge) || Math.Abs(bulge) >= 1)
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"bulges[{index}] must be a finite number with |bulge| < 1 (a half circle or more is not one arc). Nothing was changed.");
        bulges[index] = bulge;
      }
    }

    for (var index = 0; index < points.Count - 1; index++)
    {
      if (points[index].GetDistanceTo(points[index + 1]) < MinimumSegment)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"The segment from points[{index}] to points[{index + 1}] is shorter than 1 mm. Nothing was changed.");
    }

    // Review: with bulges the geometry is meant exactly as given, so no curves are added unless asked for; without
    // bulges the old default (curves between tangents) stays.
    var addCurves = PluginRuntime.GetOptionalBool(parameters, "addCurvesBetweenTangents") ?? !bulgesGiven;
    double? startStation = null;
    if (PluginRuntime.GetParameter(parameters, "startStation") is JsonNode stationNode)
    {
      if (!TryNumber(stationNode, out var station))
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "startStation must be a finite number. Nothing was changed.");
      startStation = station;
    }

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var ids = civilDoc.GetAlignmentIds().Cast<ObjectId>().Concat(civilDoc.GetSitelessAlignmentIds().Cast<ObjectId>()).Distinct();
      if (ids.Any(id => string.Equals(CivilObjectUtils.GetRequiredObject<Alignment>(transaction, id, OpenMode.ForRead).Name, name, StringComparison.OrdinalIgnoreCase)))
        throw new JsonRpcDispatchException("CIVIL3D.CONFLICT", $"An alignment named '{name}' already exists. Nothing was changed.");

      var layerId = LookupUtils.GetLayerId(database, transaction, PluginRuntime.GetOptionalString(parameters, "layer"));
      var styleId = LookupUtils.GetAlignmentStyleId(civilDoc, transaction, PluginRuntime.GetOptionalString(parameters, "style"));
      var labelSetId = LookupUtils.GetAlignmentLabelSetId(civilDoc, transaction, PluginRuntime.GetOptionalString(parameters, "labelSet"));
      var siteId = LookupUtils.GetSiteId(civilDoc, transaction, PluginRuntime.GetOptionalString(parameters, "site"));

      var blockTable = CivilObjectUtils.GetRequiredObject<BlockTable>(transaction, database.BlockTableId, OpenMode.ForRead);
      var modelSpace = CivilObjectUtils.GetRequiredObject<BlockTableRecord>(transaction, blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

      using var polyline = new Polyline();
      for (var index = 0; index < points.Count; index++)
      {
        polyline.AddVertexAt(index, points[index], index < bulges.Length ? bulges[index] : 0, 0, 0);
      }

      var polylineId = modelSpace.AppendEntity(polyline);
      transaction.AddNewlyCreatedDBObject(polyline, true);

      var polylineOptions = new PolylineOptions
      {
        AddCurvesBetweenTangents = addCurves,
        EraseExistingEntities = true,
        PlineId = polylineId,
      };

      ObjectId alignmentId;
      try
      {
        alignmentId = Alignment.Create(civilDoc, polylineOptions, name, siteId, layerId, styleId, labelSetId);
      }
      catch (ArgumentException ex)
      {
        // The transaction is aborted: neither the polyline nor an alignment stays.
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Civil 3D refused the alignment '{name}': {ex.Message} Nothing was changed.");
      }

      var alignment = CivilObjectUtils.GetRequiredObject<Alignment>(transaction, alignmentId, OpenMode.ForWrite);
      var warnings = new List<string>();
      if (startStation is double requested)
      {
        // The reference point of a new alignment is its start point; should it not be, it is moved there first.
        alignment.ReferencePointStation = requested;
        if (Math.Abs(alignment.StartingStation - requested) > 1e-6)
        {
          alignment.ReferencePoint = points[0];
          alignment.ReferencePointStation = requested;
        }
        if (Math.Abs(alignment.StartingStation - requested) > 1e-6)
          warnings.Add($"The start station is {alignment.StartingStation} after setting {requested} at the start point.");
      }

      double startX = 0, startY = 0, endX = 0, endY = 0;
      alignment.PointLocation(alignment.StartingStation, 0, ref startX, ref startY);
      alignment.PointLocation(alignment.EndingStation, 0, ref endX, ref endY);
      var entities = alignment.Entities;
      var infos = AlignmentChain.Read(entities);
      var check = AlignmentChain.Check(infos, entities);

      return new Dictionary<string, object?>
      {
        ["name"] = alignment.Name,
        ["handle"] = CivilObjectUtils.GetHandle(alignment),
        ["created"] = true,
        ["length"] = alignment.Length,
        ["entityCount"] = infos.Count,
        ["startStation"] = alignment.StartingStation,
        ["endStation"] = alignment.EndingStation,
        ["start"] = new Dictionary<string, object?> { ["x"] = startX, ["y"] = startY },
        ["end"] = new Dictionary<string, object?> { ["x"] = endX, ["y"] = endY },
        ["addCurvesBetweenTangents"] = addCurves,
        ["continuous"] = check.Continuous,
        ["entities"] = DescribeEntities(infos, check),
        ["warnings"] = warnings,
      };
    });
  }

  private static bool TryNumber(JsonNode? node, out double value)
  {
    value = 0;
    return node is JsonValue json && json.TryGetValue(out value) && double.IsFinite(value);
  }

  /// <summary>The entities as alignmentEntitiesGet lists them (raw stations, collection order with the chain order).</summary>
  private static List<Dictionary<string, object?>> DescribeEntities(List<AlignmentChain.EntityInfo> infos, AlignmentChain.ChainCheck check)
  {
    var orderOf = check.OrderIds.Select((id, position) => (id, position)).ToDictionary(t => t.id, t => t.position);
    var indexOf = infos.ToDictionary(i => i.Id, i => i.Index);
    int? IndexOf(int? id) => id is int value && indexOf.TryGetValue(value, out var index) ? index : null;
    int? OrderOf(int id) => orderOf.TryGetValue(id, out var position) ? position : null;
    return infos.Select(i => new Dictionary<string, object?>
    {
      ["index"] = i.Index,
      ["entityId"] = i.Id,
      ["order"] = OrderOf(i.Id),
      ["type"] = i.Type,
      ["constraint"] = i.Constraint,
      ["startStation"] = i.StartStation,
      ["endStation"] = i.EndStation,
      ["length"] = i.Length,
      ["start"] = AlignmentChain.Xy(i.Start),
      ["end"] = AlignmentChain.Xy(i.End),
      ["entityBefore"] = i.Before,
      ["entityAfter"] = i.After,
      ["entityBeforeIndex"] = i.Id == check.FirstId ? null : IndexOf(i.Before),
      ["entityAfterIndex"] = i.Id == check.LastId ? null : IndexOf(i.After),
      ["radius"] = i.Radius,
      ["clockwise"] = i.Clockwise,
    }).ToList();
  }

  public static Task<object?> DeleteAlignmentAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, name);
      var writeAlignment = CivilObjectUtils.GetRequiredObject<Alignment>(transaction, alignment.ObjectId, OpenMode.ForWrite);
      writeAlignment.Erase();

      return new Dictionary<string, object?>
      {
        ["name"] = name,
        ["deleted"] = true,
      };
    });
  }

  private static Dictionary<string, object?> ToAlignmentSummary(Alignment alignment)
  {
    return new Dictionary<string, object?>
    {
      ["name"] = alignment.Name,
      ["handle"] = CivilObjectUtils.GetHandle(alignment),
      ["type"] = MapAlignmentType(CivilObjectUtils.GetStringProperty(alignment, "AlignmentType") ?? alignment.GetType().Name),
      ["length"] = alignment.Length,
      ["startStation"] = alignment.StartingStation,
      ["endStation"] = alignment.EndingStation,
      ["site"] = CivilObjectUtils.GetStringProperty(alignment, "SiteName"),
      ["profileCount"] = alignment.GetProfileIds().Count,
      ["isReference"] = CivilObjectUtils.GetPropertyValue<bool?>(alignment, "IsReferenceObject") ?? false,
    };
  }

  private static string MapAlignmentType(string? value)
  {
    var text = value?.ToLowerInvariant() ?? string.Empty;
    if (text.Contains("offset"))
    {
      return "offset";
    }

    if (text.Contains("rail"))
    {
      return "rail";
    }

    if (text.Contains("curb"))
    {
      return "curb_return";
    }

    return "centerline";
  }

  private static string MapAlignmentEntityType(string? value)
  {
    var text = value?.ToLowerInvariant() ?? string.Empty;
    if (text.Contains("spiral"))
    {
      return "spiral";
    }

    if (text.Contains("curve") || text.Contains("arc"))
    {
      return "arc";
    }

    return "line";
  }
}
