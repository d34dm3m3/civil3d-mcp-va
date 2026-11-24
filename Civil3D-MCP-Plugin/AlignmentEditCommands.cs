using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.DatabaseServices;
using System.Text.Json.Nodes;

namespace Civil3DMcpPlugin;

/// <summary>
/// Editing commands for Civil 3D horizontal alignments:
/// add_tangent, add_curve, add_spiral, delete_entity,
/// set_station_equation, get_station_offset, replace_curve_with_lines,
/// offset_create, widen_transition.
/// </summary>
public static class AlignmentEditCommands
{
  // ─── alignmentAddTangent ──────────────────────────────────────────────────

  public static Task<object?> AddTangentAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var startX = PluginRuntime.GetRequiredDouble(parameters, "startX");
    var startY = PluginRuntime.GetRequiredDouble(parameters, "startY");
    var endX = PluginRuntime.GetRequiredDouble(parameters, "endX");
    var endY = PluginRuntime.GetRequiredDouble(parameters, "endY");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var writeAlignment = CivilObjectUtils.GetRequiredObject<Alignment>(
        transaction, alignment.ObjectId, OpenMode.ForWrite);

      var entities = writeAlignment.Entities;
      entities.AddFixedLine(new Point3d(startX, startY, 0), new Point3d(endX, endY, 0));

      return new Dictionary<string, object?>
      {
        ["alignmentName"] = alignment.Name,
        ["operation"] = "add_tangent",
        ["entityIndex"] = entities.Count - 1,
        ["startX"] = startX,
        ["startY"] = startY,
        ["endX"] = endX,
        ["endY"] = endY,
        ["success"] = true,
      };
    });
  }

  // ─── alignmentMoveVertex (C99f) ───────────────────────────────────────────

  /// <summary>
  /// C99f: moves a break point of an alignment made of fixed lines (C96 alignments): every AlignmentLine whose pass-through
  /// point lies within tolerance of (x, y) gets the new point (newX, newY), then the alignment is updated. Use case (pilot
  /// project): the east bore pit lay 0,3–1 m from an existing water main and pressure sewer in
  /// service and had to move 2,5 m south; deleting and re-creating the alignment would have deleted its profiles and
  /// profile views. Profiles keep their PVIs by station, so stations downstream of the vertex shift by the change in
  /// length. Refused unless one or two lines pass through the point (a line end, or a vertex between two lines);
  /// dryRun reports without changing.
  /// </summary>
  public static Task<object?> MoveVertexAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var x = PluginRuntime.GetRequiredDouble(parameters, "x");
    var y = PluginRuntime.GetRequiredDouble(parameters, "y");
    var newX = PluginRuntime.GetRequiredDouble(parameters, "newX");
    var newY = PluginRuntime.GetRequiredDouble(parameters, "newY");
    var tolerance = PluginRuntime.GetOptionalDouble(parameters, "tolerance") ?? 0.01;
    var dryRun = PluginRuntime.GetOptionalBool(parameters, "dryRun") ?? false;

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var write = CivilObjectUtils.GetRequiredObject<Alignment>(transaction, alignment.ObjectId, OpenMode.ForWrite);
      var endBefore = write.EndingStation;
      var hits = new List<(AlignmentLine Line, int Index, bool First)>();
      var target = new Point2d(x, y);
      for (var i = 0; i < write.Entities.Count; i++)
      {
        if (write.Entities[i] is not AlignmentLine line) continue;
        if (line.PassThroughPoint1.GetDistanceTo(target) <= tolerance) hits.Add((line, i, true));
        if (line.PassThroughPoint2.GetDistanceTo(target) <= tolerance) hits.Add((line, i, false));
      }
      if (hits.Count is < 1 or > 2)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
          $"{hits.Count} line pass-through points within {tolerance} of ({x}, {y}); expected 1 or 2. Nothing was changed.");
      var changed = hits.Select(h => new Dictionary<string, object?>
      {
        ["entityIndex"] = h.Index,
        ["point"] = h.First ? "PassThroughPoint1" : "PassThroughPoint2",
        ["lengthBefore"] = h.Line.Length,
      }).ToList();
      if (!dryRun)
      {
        var moved = new Point2d(newX, newY);
        foreach (var h in hits)
        {
          if (h.First) h.Line.PassThroughPoint1 = moved;
          else h.Line.PassThroughPoint2 = moved;
        }
        write.Update();
        for (var k = 0; k < hits.Count; k++) changed[k]["lengthAfter"] = hits[k].Line.Length;
      }
      return new Dictionary<string, object?>
      {
        ["alignmentName"] = write.Name,
        ["dryRun"] = dryRun,
        ["lines"] = changed,
        ["endStationBefore"] = endBefore,
        ["endStationAfter"] = dryRun ? endBefore : write.EndingStation,
      };
    });
  }

  // ─── alignmentLabelSet (C175) ─────────────────────────────────────────────

  /// <summary>C175: replaces an alignment's labels with a label set from the drawing {alignmentName, labelSet} (Alignment.
  /// ImportLabelSet; an empty "no display" label set removes them). Use case: an alignment created in a
  /// drawing from the company template got the default set (R=0 and stations at every vertex).</summary>
  public static Task<object?> SetLabelSetAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var labelSet = PluginRuntime.GetRequiredString(parameters, "labelSet");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var cdoc = (Autodesk.Civil.ApplicationServices.CivilDocument)civilDoc;
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var write = CivilObjectUtils.GetRequiredObject<Alignment>(transaction, alignment.ObjectId, OpenMode.ForWrite);
      var sets = cdoc.Styles.LabelSetStyles.AlignmentLabelSetStyles;
      if (!sets.Contains(labelSet))
        throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Alignment label set '{labelSet}' was not found. Nothing was changed.");
      write.ImportLabelSet(sets[labelSet]);
      return new Dictionary<string, object?> { ["alignmentName"] = write.Name, ["labelSet"] = labelSet, ["applied"] = true };
    });
  }

  // ─── alignmentInsertVertex (C169) ─────────────────────────────────────────

  /// <summary>
  /// C169: adds a break point on a fixed line: the line through (x, y) (within tolerance of the segment) is shortened to
  /// end at the point and a new fixed line runs from the point to its old end, in one transaction. The point can then be
  /// moved with alignmentMoveVertex (C99f). Use case: the pipe corridor along the field edge
  /// moves 4-5 m into the field, out of the birches' protection zone, and needs new bends where the alignment had none;
  /// re-creating the alignment would delete its profiles and profile view. Refused unless exactly one line passes within
  /// tolerance and the point is at least 0,5 m from both ends; dryRun reports without changing.
  /// </summary>
  public static Task<object?> InsertVertexAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var x = PluginRuntime.GetRequiredDouble(parameters, "x");
    var y = PluginRuntime.GetRequiredDouble(parameters, "y");
    var tolerance = PluginRuntime.GetOptionalDouble(parameters, "tolerance") ?? 0.05;
    var dryRun = PluginRuntime.GetOptionalBool(parameters, "dryRun") ?? false;

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var write = CivilObjectUtils.GetRequiredObject<Alignment>(transaction, alignment.ObjectId, OpenMode.ForWrite);
      var endBefore = write.EndingStation;
      var target = new Point2d(x, y);
      var hits = new List<(AlignmentLine Line, int Index, Point2d Foot)>();
      for (var i = 0; i < write.Entities.Count; i++)
      {
        if (write.Entities[i] is not AlignmentLine line) continue;
        var a = line.StartPoint; var b = line.EndPoint;
        var ab = b - a; var len2 = ab.DotProduct(ab);
        if (len2 <= 0) continue;
        var t = (target - a).DotProduct(ab) / len2;
        if (t <= 0 || t >= 1) continue;
        var foot = a + ab * t;
        if (foot.GetDistanceTo(target) <= tolerance) hits.Add((line, i, foot));
      }
      if (hits.Count != 1)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
          $"{hits.Count} lines pass within {tolerance} of ({x}, {y}); expected exactly one. Nothing was changed.");
      var (hit, index, at) = hits[0];
      var start = hit.StartPoint; var end = hit.EndPoint;
      if (at.GetDistanceTo(start) < 0.5 || at.GetDistanceTo(end) < 0.5)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
          $"The point is {Math.Min(at.GetDistanceTo(start), at.GetDistanceTo(end)):0.00} m from an end of line {index}; use alignmentMoveVertex there. Nothing was changed.");
      var result = new Dictionary<string, object?>
      {
        ["alignmentName"] = write.Name,
        ["dryRun"] = dryRun,
        ["entityIndex"] = index,
        ["vertex"] = new[] { Math.Round(at.X, 4), Math.Round(at.Y, 4) },
        ["lengthBefore"] = hit.Length,
        ["endStationBefore"] = endBefore,
      };
      if (!dryRun)
      {
        if (hit.PassThroughPoint2.GetDistanceTo(end) <= 1e-6) hit.PassThroughPoint2 = at;
        else hit.PassThroughPoint1 = at;
        var added = write.Entities.AddFixedLine(new Point3d(at.X, at.Y, 0), new Point3d(end.X, end.Y, 0));
        write.Update();
        result["lengthsAfter"] = new[] { hit.Length, added.Length };
        result["newEntityId"] = added.EntityId;
      }
      result["endStationAfter"] = dryRun ? endBefore : write.EndingStation;
      return result;
    });
  }

  // ─── alignmentRemoveVertex (C119) ─────────────────────────────────────────

  /// <summary>
  /// C119: removes a break point between two fixed lines: the line ending at (x, y) is extended to the far end of the line
  /// starting there, and that line is removed, in one transaction (deleting an entity and moving a vertex in two calls
  /// leaves the alignment open in between). Use case (pilot project, variant B): the new package from
  /// the west bore pit hit an existing manhole and had to go straight to the next tangent. Refused unless exactly one line
  /// ends and one line starts at the point; dryRun reports without changing.
  /// </summary>
  public static Task<object?> RemoveVertexAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var x = PluginRuntime.GetRequiredDouble(parameters, "x");
    var y = PluginRuntime.GetRequiredDouble(parameters, "y");
    var tolerance = PluginRuntime.GetOptionalDouble(parameters, "tolerance") ?? 0.01;
    var dryRun = PluginRuntime.GetOptionalBool(parameters, "dryRun") ?? false;

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var write = CivilObjectUtils.GetRequiredObject<Alignment>(transaction, alignment.ObjectId, OpenMode.ForWrite);
      var endBefore = write.EndingStation;
      var target = new Point2d(x, y);
      var ending = new List<(AlignmentLine Line, int Index)>();
      var starting = new List<(AlignmentLine Line, int Index)>();
      for (var i = 0; i < write.Entities.Count; i++)
      {
        if (write.Entities[i] is not AlignmentLine line) continue;
        if (line.PassThroughPoint2.GetDistanceTo(target) <= tolerance) ending.Add((line, i));
        if (line.PassThroughPoint1.GetDistanceTo(target) <= tolerance) starting.Add((line, i));
      }
      if (ending.Count != 1 || starting.Count != 1)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
          $"{ending.Count} line(s) end and {starting.Count} start within {tolerance} of ({x}, {y}); expected one of each. Nothing was changed.");
      var (keep, keepIndex) = ending[0];
      var (drop, dropIndex) = starting[0];
      var newEnd = drop.PassThroughPoint2;
      var result = new Dictionary<string, object?>
      {
        ["alignmentName"] = write.Name,
        ["dryRun"] = dryRun,
        ["keptEntityIndex"] = keepIndex,
        ["removedEntityIndex"] = dropIndex,
        ["lengthBefore"] = keep.Length + drop.Length,
        ["newEnd"] = new[] { newEnd.X, newEnd.Y },
        ["endStationBefore"] = endBefore,
      };
      if (!dryRun)
      {
        write.Entities.RemoveAt(dropIndex);
        keep.PassThroughPoint2 = newEnd;
        write.Update();
        result["lengthAfter"] = keep.Length;
      }
      result["endStationAfter"] = dryRun ? endBefore : write.EndingStation;
      return result;
    });
  }

  // ─── alignmentAddCurve ────────────────────────────────────────────────────

  public static Task<object?> AddCurveAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var passThroughX = PluginRuntime.GetRequiredDouble(parameters, "passThroughX");
    var passThroughY = PluginRuntime.GetRequiredDouble(parameters, "passThroughY");
    var radius = PluginRuntime.GetRequiredDouble(parameters, "radius");

    throw new JsonRpcDispatchException(
      "CIVIL3D.API_ERROR",
      $"Cannot add the requested radius-{radius} curve at ({passThroughX}, {passThroughY}): Civil 3D 2026 requires an explicit clockwise direction or additional geometry. " +
      "The current tool schema does not provide enough information, so no curve was created.");
  }

  // ─── alignmentAddSpiral ───────────────────────────────────────────────────

  public static Task<object?> AddSpiralAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var spiralType = PluginRuntime.GetOptionalString(parameters, "spiralType") ?? "clothoid";
    var startX = PluginRuntime.GetRequiredDouble(parameters, "startX");
    var startY = PluginRuntime.GetRequiredDouble(parameters, "startY");
    var startRadius = PluginRuntime.GetRequiredDouble(parameters, "startRadius");
    var endRadius = PluginRuntime.GetRequiredDouble(parameters, "endRadius");
    var length = PluginRuntime.GetRequiredDouble(parameters, "length");

    throw new JsonRpcDispatchException(
      "CIVIL3D.API_ERROR",
      $"Cannot add {spiralType} spiral '{alignmentName}' from ({startX}, {startY}) with radii {startRadius}/{endRadius} and length {length}: " +
      "the Civil 3D 2026 AddFixedSpiral overloads require a previous entity id and additional geometric constraints not present in this tool schema. No spiral was created.");
  }

  // ─── alignmentDeleteEntity ────────────────────────────────────────────────

  public static Task<object?> DeleteEntityAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var entityIndex = (int)(PluginRuntime.GetRequiredDouble(parameters, "entityIndex"));

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var writeAlignment = CivilObjectUtils.GetRequiredObject<Alignment>(
        transaction, alignment.ObjectId, OpenMode.ForWrite);

      var entities = writeAlignment.Entities;
      var count = entities.Count;
      if (entityIndex < 0 || entityIndex >= count)
      {
        throw new JsonRpcDispatchException(
          "CIVIL3D.INVALID_INPUT",
          $"Entity index {entityIndex} is out of range (alignment has {count} entities).");
      }

      entities.RemoveAt(entityIndex);

      return new Dictionary<string, object?>
      {
        ["alignmentName"] = alignment.Name,
        ["operation"] = "delete_entity",
        ["deletedEntityIndex"] = entityIndex,
        ["success"] = true,
      };
    });
  }

  // ─── alignmentSetStationEquation ─────────────────────────────────────────

  /// <summary>
  /// StationEquations.Add(rawStationBack, stationAhead, type): at raw station rawStation (measured without
  /// equations) the station shown becomes nominalStation (the station ahead). The type says whether the stations
  /// increase or decrease from there on (Civil 3D developer guide: Add(80, 0, Increasing) renumbers from 0 at raw
  /// 80 and keeps counting up), so it is not derived from nominal vs raw: default increasing.
  /// </summary>
  public static Task<object?> SetStationEquationAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var rawStation = PluginRuntime.GetRequiredDouble(parameters, "rawStation");
    var nominalStation = PluginRuntime.GetRequiredDouble(parameters, "nominalStation");
    var typeText = PluginRuntime.GetOptionalString(parameters, "equationType") ?? "increasing";
    if (typeText != "increasing" && typeText != "decreasing")
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"equationType must be 'increasing' or 'decreasing', not '{typeText}'.");
    }

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var writeAlignment = CivilObjectUtils.GetRequiredObject<Alignment>(
        transaction, alignment.ObjectId, OpenMode.ForWrite);

      var equationType = typeText == "increasing"
        ? StationEquationType.Increasing
        : StationEquationType.Decreasing;
      var equation = writeAlignment.StationEquations.Add(rawStation, nominalStation, equationType);

      return new Dictionary<string, object?>
      {
        ["alignmentName"] = alignment.Name,
        ["rawStation"] = rawStation,
        ["nominalStation"] = nominalStation,
        ["stationBack"] = equation.StationBack,
        ["equationType"] = typeText,
        ["endStationWithEquations"] = writeAlignment.EndingStationWithEquations,
        ["stationEquations"] = AlignmentChain.DescribeEquations(writeAlignment),
        ["success"] = true,
      };
    });
  }

  // ─── alignmentReplaceCurveWithLines ──────────────────────────────────────

  private const double LengthTolerance = 0.001;
  private const double MinimumSegment = 0.001;

  /// <summary>
  /// Replaces a curve (arc, spiral or curve group) by straight lines meeting at corners: from the exact end of the
  /// entity before, through the given points, to the exact start of the entity after. Civil 3D links alignment
  /// entities only through EntityBefore/EntityAfter, so a line added with AddFixedLine(start, end) at approximate
  /// coordinates stays loose and everything after it drops off the alignment (live 2026-10-07, Schakt_CL_HK).
  /// Each way of linking is tried in a nested transaction: (1) AddFixedLine(previousEntityId, start, end) per line
  /// with the exact end points; (2) the same for all but the last line, the last one AddFreeLine(previous, next).
  /// An attempt counts only if the chain is continuous from FirstEntity to LastEntity, the neighbours have not moved,
  /// every new line runs between the intended points and the length is old - curve + new lines (within 1 mm);
  /// otherwise it is rolled back. When no attempt passes, the whole transaction is aborted and nothing changes.
  /// </summary>
  public static Task<object?> ReplaceCurveWithLinesAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var entityIndex = PluginRuntime.GetOptionalInt(parameters, "entityIndex");
    var station = PluginRuntime.GetOptionalDouble(parameters, "station");
    if ((entityIndex == null) == (station == null))
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Give either entityIndex or station (raw) of the curve. Nothing was changed.");
    }

    var points = new List<Point2d>();
    if (PluginRuntime.GetParameter(parameters, "points") is JsonArray pointsNode)
    {
      if (pointsNode.Count > 50)
      {
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "points accepts at most 50 corner points. Nothing was changed.");
      }

      foreach (var node in pointsNode)
      {
        if (node is not JsonObject point || point["x"] == null || point["y"] == null)
        {
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Every point needs numeric x and y. Nothing was changed.");
        }

        points.Add(new Point2d(point["x"]!.GetValue<double>(), point["y"]!.GetValue<double>()));
      }
    }

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var alignmentId = alignment.ObjectId;
      var before = AlignmentChain.Read(alignment.Entities);
      var chainBefore = AlignmentChain.Check(before, alignment.Entities);
      if (!chainBefore.Continuous)
      {
        throw new JsonRpcDispatchException("CIVIL3D.CONFLICT",
          $"Alignment '{alignment.Name}' is not continuous before the change: {string.Join("; ", chainBefore.Problems)}. " +
          "Repair it first (entities_get shows the links). Nothing was changed.");
      }

      AlignmentChain.EntityInfo curve;
      if (entityIndex != null)
      {
        if (entityIndex < 0 || entityIndex >= before.Count)
        {
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
            $"Entity index {entityIndex} is out of range (alignment has {before.Count} entities). Nothing was changed.");
        }

        curve = before[entityIndex.Value];
      }
      else
      {
        AlignmentEntity atStation;
        try
        {
          atStation = alignment.Entities.EntityAtStation(station!.Value);
        }
        catch (Exception ex)
        {
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
            $"No entity at raw station {station} (alignment runs {alignment.StartingStation}-{alignment.EndingStation}): {ex.Message} Nothing was changed.");
        }

        curve = before.First(i => i.Id == atStation.EntityId);
      }

      if (curve.Type == "line")
      {
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
          $"Entity index {curve.Index} (stations {curve.StartStation:F4}-{curve.EndStation:F4}) is a line, not a curve. Nothing was changed.");
      }

      // The chain is continuous, so an inner entity has both links (checked by AlignmentChain.Check).
      var previous = curve.Id == chainBefore.FirstId ? null : before.First(i => i.Id == curve.Before);
      var next = curve.Id == chainBefore.LastId ? null : before.First(i => i.Id == curve.After);
      var vertices = new List<Point2d> { previous?.End ?? curve.Start };
      vertices.AddRange(points);
      vertices.Add(next?.Start ?? curve.End);
      for (var k = 0; k + 1 < vertices.Count; k++)
      {
        if (vertices[k].GetDistanceTo(vertices[k + 1]) < MinimumSegment)
        {
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
            $"Line {k + 1} of the replacement would be shorter than 1 mm ({vertices[k].X}, {vertices[k].Y}). Nothing was changed.");
        }
      }

      var oldLength = alignment.Length;
      var oldEndStation = alignment.EndingStation;
      var newLinesLength = Enumerable.Range(0, vertices.Count - 1).Sum(k => vertices[k].GetDistanceTo(vertices[k + 1]));
      var expectedLength = oldLength - curve.Length + newLinesLength;

      var methods = new List<string> { "fixedLinesAfterPrevious" };
      if (next != null && (previous != null || vertices.Count > 2)) methods.Add("freeLineToNext");

      var failed = new List<Dictionary<string, object?>>();
      foreach (var method in methods)
      {
        using var attempt = database.TransactionManager.StartTransaction();
        Dictionary<string, object?>? result;
        string? failure;
        try
        {
          var writeAlignment = CivilObjectUtils.GetRequiredObject<Alignment>(attempt, alignmentId, OpenMode.ForWrite);
          (result, failure) = TryReplaceCurve(writeAlignment, method, curve, previous, next, points, expectedLength);
        }
        catch (Exception ex)
        {
          (result, failure) = (null, $"{ex.GetType().Name}: {ex.Message}");
        }

        if (result == null)
        {
          failed.Add(new Dictionary<string, object?> { ["method"] = method, ["reason"] = failure });
          continue; // attempt is disposed without Commit: rolled back
        }

        attempt.Commit();
        result["alignmentName"] = alignment.Name;
        result["operation"] = "replace_curve_with_lines";
        result["linkMethod"] = method;
        result["oldLength"] = oldLength;
        result["expectedLength"] = expectedLength;
        result["lengthChange"] = (double)result["newLength"]! - oldLength;
        result["oldEndStation"] = oldEndStation;
        result["oldEntityCount"] = before.Count;
        result["removedEntity"] = new Dictionary<string, object?>
        {
          ["index"] = curve.Index,
          ["entityId"] = curve.Id,
          ["type"] = curve.Type,
          ["startStation"] = curve.StartStation,
          ["endStation"] = curve.EndStation,
          ["length"] = curve.Length,
          ["radius"] = curve.Radius,
          ["start"] = AlignmentChain.Xy(curve.Start),
          ["end"] = AlignmentChain.Xy(curve.End),
        };
        result["previousEntity"] = previous == null ? null : new Dictionary<string, object?> { ["index"] = previous.Index, ["entityId"] = previous.Id, ["type"] = previous.Type };
        result["nextEntity"] = next == null ? null : new Dictionary<string, object?> { ["index"] = next.Index, ["entityId"] = next.Id, ["type"] = next.Type };
        result["dependentProfiles"] = alignment.GetProfileIds().Cast<ObjectId>()
          .Select(id => CivilObjectUtils.GetRequiredObject<Profile>(transaction, id, OpenMode.ForRead).Name).ToList();
        result["note"] = "Stations are raw. Everything after the replaced curve moves by lengthChange along the alignment; " +
          "profiles, profile views and corridor regions keep their stations.";
        result["failedAttempts"] = failed;
        return result;
      }

      throw new JsonRpcDispatchException("CIVIL3D.API_ERROR",
        $"The curve at index {curve.Index} of '{alignment.Name}' could not be replaced with the alignment kept continuous: " +
        string.Join(" | ", failed.Select(f => $"{f["method"]}: {f["reason"]}")) +
        ". The transaction was aborted; nothing was changed.");
    });
  }

  /// <summary>One attempt (inside a nested transaction). Returns the result, or null and the reason it failed.</summary>
  private static (Dictionary<string, object?>? Result, string? Failure) TryReplaceCurve(
    Alignment alignment,
    string method,
    AlignmentChain.EntityInfo curve,
    AlignmentChain.EntityInfo? previous,
    AlignmentChain.EntityInfo? next,
    List<Point2d> points,
    double expectedLength)
  {
    var entities = alignment.Entities;
    entities.Remove(entities.EntityAtId(curve.Id));

    var removed = AlignmentChain.Read(entities);
    var previousNow = previous == null ? null : removed.FirstOrDefault(i => i.Id == previous.Id);
    var nextNow = next == null ? null : removed.FirstOrDefault(i => i.Id == next.Id);
    var moved = NeighbourMoved(previous, previousNow, "previous") ?? NeighbourMoved(next, nextNow, "next");
    if (moved != null)
    {
      var meet = previousNow != null && nextNow != null && previousNow.End.GetDistanceTo(nextNow.Start) <= AlignmentChain.LinkTolerance
        ? " The two neighbours then meet each other (the curve was probably a free curve between them): delete_entity alone may already give that corner (check with entities_get)."
        : string.Empty;
      return (null, $"removing the curve changed the {moved}.{meet}");
    }

    // The exact stored end points of the neighbours, so that Civil can link the new lines to them.
    var vertices = new List<Point2d> { previousNow?.End ?? curve.Start };
    vertices.AddRange(points);
    vertices.Add(nextNow?.Start ?? curve.End);
    var segmentCount = vertices.Count - 1;

    var newIds = new List<int>();
    int? lastId = previous?.Id;
    for (var k = 0; k < segmentCount; k++)
    {
      AlignmentLine line;
      if (method == "freeLineToNext" && k == segmentCount - 1)
        line = entities.AddFreeLine(lastId!.Value, next!.Id);
      else if (lastId == null)
        line = entities.AddFixedLine(To3d(vertices[k]), To3d(vertices[k + 1]));
      else
        line = entities.AddFixedLine(lastId.Value, To3d(vertices[k]), To3d(vertices[k + 1]));
      newIds.Add(line.EntityId);
      lastId = line.EntityId;
    }

    var after = AlignmentChain.Read(entities);
    var check = AlignmentChain.Check(after, entities);
    if (!check.Continuous) return (null, "the alignment is not continuous afterwards: " + string.Join("; ", check.Problems));

    var byId = after.ToDictionary(i => i.Id);
    var movedAfter = NeighbourMoved(previous, previous == null ? null : byId.GetValueOrDefault(previous.Id), "previous")
      ?? NeighbourMoved(next, next == null ? null : byId.GetValueOrDefault(next.Id), "next");
    if (movedAfter != null) return (null, $"adding the lines changed the {movedAfter}");

    var newLines = newIds.Select(id => byId[id]).ToList();
    for (var k = 0; k < newLines.Count; k++)
    {
      var line = newLines[k];
      if (line.Start.GetDistanceTo(vertices[k]) > LengthTolerance || line.End.GetDistanceTo(vertices[k + 1]) > LengthTolerance)
      {
        return (null, $"new line {k + 1} runs ({line.Start.X}, {line.Start.Y}) -> ({line.End.X}, {line.End.Y}) instead of " +
          $"({vertices[k].X}, {vertices[k].Y}) -> ({vertices[k + 1].X}, {vertices[k + 1].Y})");
      }
    }

    var newLength = alignment.Length;
    if (Math.Abs(newLength - expectedLength) > LengthTolerance)
    {
      return (null, $"alignment length {newLength:F4} instead of {expectedLength:F4} (old - curve + new lines)");
    }

    // Corners: the joint with the previous entity, the given points, the joint with the next entity.
    // A neighbour's direction that cannot be computed only leaves its deflection empty; it does not fail the attempt.
    Vector2d? NeighbourDirection(AlignmentChain.EntityInfo neighbour, bool atEnd)
    {
      try
      {
        var direction = AlignmentChain.Direction(byId[neighbour.Id], atEnd, alignment);
        return double.IsFinite(direction.X) && double.IsFinite(direction.Y) && direction.Length > 0.5 ? direction : null;
      }
      catch (Exception)
      {
        return null;
      }
    }

    var corners = new List<Dictionary<string, object?>>();
    for (var k = 0; k <= segmentCount; k++)
    {
      if ((k == 0 && previous == null) || (k == segmentCount && next == null)) continue;
      var incoming = k > 0 ? (newLines[k - 1].End - newLines[k - 1].Start).GetNormal() : NeighbourDirection(previous!, true);
      var outgoing = k < segmentCount ? (newLines[k].End - newLines[k].Start).GetNormal() : NeighbourDirection(next!, false);
      double? deflection = incoming == null || outgoing == null ? null : AlignmentChain.DeflectionDegrees(incoming.Value, outgoing.Value);
      corners.Add(new Dictionary<string, object?>
      {
        ["at"] = k == 0 ? "previous entity" : k == segmentCount ? "next entity" : $"point {k}",
        ["point"] = AlignmentChain.Xy(vertices[k]),
        ["station"] = k < segmentCount ? newLines[k].StartStation : newLines[k - 1].EndStation,
        ["deflectionDegrees"] = deflection,
        ["turn"] = deflection == null ? "unknown" : Math.Abs(deflection.Value) < 1e-9 ? "straight" : deflection > 0 ? "left" : "right",
      });
    }

    return (new Dictionary<string, object?>
    {
      ["newLength"] = newLength,
      ["newEndStation"] = alignment.EndingStation,
      ["newEntityCount"] = after.Count,
      ["newEntities"] = newLines.Select(line => new Dictionary<string, object?>
      {
        ["index"] = line.Index,
        ["entityId"] = line.Id,
        ["constraint"] = line.Constraint,
        ["startStation"] = line.StartStation,
        ["endStation"] = line.EndStation,
        ["length"] = line.Length,
        ["start"] = AlignmentChain.Xy(line.Start),
        ["end"] = AlignmentChain.Xy(line.End),
      }).ToList(),
      ["pis"] = corners,
    }, null);
  }

  private static string? NeighbourMoved(AlignmentChain.EntityInfo? before, AlignmentChain.EntityInfo? now, string role)
  {
    if (before == null) return null;
    if (now == null) return $"{role} entity (id {before.Id}): it is gone";
    var shift = Math.Max(before.Start.GetDistanceTo(now.Start), before.End.GetDistanceTo(now.End));
    return shift > AlignmentChain.LinkTolerance
      ? $"{role} entity (id {before.Id}, {before.Type}): it now runs ({now.Start.X}, {now.Start.Y}) -> ({now.End.X}, {now.End.Y}) instead of " +
        $"({before.Start.X}, {before.Start.Y}) -> ({before.End.X}, {before.End.Y})"
      : null;
  }

  private static Point3d To3d(Point2d point) => new(point.X, point.Y, 0);

  // ─── alignmentGetStationOffset ────────────────────────────────────────────

  public static Task<object?> GetStationOffsetAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var x = PluginRuntime.GetRequiredDouble(parameters, "x");
    var y = PluginRuntime.GetRequiredDouble(parameters, "y");

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      double station = 0;
      double offset = 0;
      alignment.StationOffset(x, y, ref station, ref offset);

      return new Dictionary<string, object?>
      {
        ["station"] = station,
        ["offset"] = offset,
        ["distanceFromAlignment"] = Math.Abs(offset),
        ["units"] = CivilObjectUtils.LinearUnits(database),
      };
    });
  }

  // ─── alignmentOffsetCreate ────────────────────────────────────────────────

  public static Task<object?> OffsetCreateAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var offsetName = PluginRuntime.GetRequiredString(parameters, "offsetName");
    var offset = PluginRuntime.GetRequiredDouble(parameters, "offset");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var baseAlignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var layerId = LookupUtils.GetLayerId(database, transaction, PluginRuntime.GetOptionalString(parameters, "layer"));
      var styleId = LookupUtils.GetAlignmentStyleId(civilDoc, transaction, PluginRuntime.GetOptionalString(parameters, "style"));
      var labelSetId = LookupUtils.GetAlignmentLabelSetId(civilDoc, transaction, PluginRuntime.GetOptionalString(parameters, "labelSet"));
      var siteId = LookupUtils.GetSiteId(civilDoc, transaction, null);

      // Alignment.CreateOffsetAlignment(name, baseAlignmentId, offset, siteId, layerId, styleId, labelSetId)
      var offsetAlignmentId = (ObjectId)(
        CivilObjectUtils.InvokeStaticMethod(typeof(Alignment), "CreateOffsetAlignment",
          offsetName, baseAlignment.ObjectId, offset, siteId, layerId, styleId, labelSetId)
        ?? CivilObjectUtils.InvokeStaticMethod(typeof(Alignment), "CreateOffsetAlignment",
          offsetName, baseAlignment.ObjectId, offset, layerId, styleId, labelSetId)
        ?? throw new JsonRpcDispatchException(
          "CIVIL3D.TRANSACTION_FAILED",
          "Alignment.CreateOffsetAlignment is not available in this Civil 3D version."));

      var offsetAlignment = CivilObjectUtils.GetRequiredObject<Alignment>(transaction, offsetAlignmentId, OpenMode.ForRead);

      return new Dictionary<string, object?>
      {
        ["baseAlignmentName"] = baseAlignment.Name,
        ["offsetName"] = offsetAlignment.Name,
        ["offset"] = offset,
        ["handle"] = CivilObjectUtils.GetHandle(offsetAlignment),
        ["success"] = true,
      };
    });
  }

  // ─── alignmentWidenTransition ─────────────────────────────────────────────

  public static Task<object?> WidenTransitionAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var side = PluginRuntime.GetRequiredString(parameters, "side");
    var startStation = PluginRuntime.GetRequiredDouble(parameters, "startStation");
    var endStation = PluginRuntime.GetRequiredDouble(parameters, "endStation");
    var startOffset = PluginRuntime.GetRequiredDouble(parameters, "startOffset");
    var endOffset = PluginRuntime.GetRequiredDouble(parameters, "endOffset");
    var offsetName = PluginRuntime.GetOptionalString(parameters, "offsetName")
      ?? $"{alignmentName}_Widen_{side}";

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var baseAlignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);

      // Build a variable-offset alignment using AlignmentOffsetOptions if available,
      // or fall back to creating a simple offset and noting limitations.
      var layerId = LookupUtils.GetLayerId(database, transaction, null);
      var styleId = LookupUtils.GetAlignmentStyleId(civilDoc, transaction, null);
      var labelSetId = LookupUtils.GetAlignmentLabelSetId(civilDoc, transaction, null);
      var siteId = LookupUtils.GetSiteId(civilDoc, transaction, null);

      // Attempt variable offset via Alignment.CreateOffsetAlignment with OffsetOptions
      var offsetAlignmentId = TryCreateWidenTransition(
        civilDoc, transaction, database,
        baseAlignment, offsetName, side,
        startStation, endStation, startOffset, endOffset,
        siteId, layerId, styleId, labelSetId);

      var resultAlignment = CivilObjectUtils.GetRequiredObject<Alignment>(
        transaction, offsetAlignmentId, OpenMode.ForRead);

      return new Dictionary<string, object?>
      {
        ["baseAlignmentName"] = baseAlignment.Name,
        ["offsetName"] = resultAlignment.Name,
        ["side"] = side,
        ["startStation"] = startStation,
        ["endStation"] = endStation,
        ["startOffset"] = startOffset,
        ["endOffset"] = endOffset,
        ["handle"] = CivilObjectUtils.GetHandle(resultAlignment),
        ["success"] = true,
      };
    });
  }

  // ─── Private helpers ─────────────────────────────────────────────────────

  private static ObjectId TryCreateWidenTransition(
    Autodesk.Civil.ApplicationServices.CivilDocument civilDoc,
    Transaction transaction,
    Database database,
    Alignment baseAlignment,
    string offsetName,
    string side,
    double startStation,
    double endStation,
    double startOffset,
    double endOffset,
    ObjectId siteId,
    ObjectId layerId,
    ObjectId styleId,
    ObjectId labelSetId)
  {
    throw new JsonRpcDispatchException(
      "CIVIL3D.API_ERROR",
      "Civil 3D 2026 does not expose Alignment.CreateWideningAlignment in the managed API. No constant-offset substitute was created.");
  }
}
