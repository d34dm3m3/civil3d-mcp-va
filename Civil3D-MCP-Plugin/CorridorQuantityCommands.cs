using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.DatabaseServices;

namespace Civil3DMcpPlugin;

/// <summary>
/// Per-station corridor quantities read directly from applied assemblies:
/// shape areas by code (CalculatedShape.Area) and an excavation polygon from
/// the bottom and daylight point codes, integrated with average end areas
/// inside each baseline region. Gaps between regions are never bridged.
/// </summary>
public static class CorridorQuantityCommands
{
  private const string ExcavationKey = "excavation";

  public static Task<object?> CorridorQuantitiesAsync(JsonObject? parameters)
  {
    var corridorName = PluginRuntime.GetRequiredString(parameters, "corridorName");
    var baselineIndex = PluginRuntime.GetOptionalInt(parameters, "baselineIndex") ?? 0;
    var startStation = PluginRuntime.GetOptionalDouble(parameters, "startStation");
    var endStation = PluginRuntime.GetOptionalDouble(parameters, "endStation");
    var requestedCodes = (PluginRuntime.GetParameter(parameters, "shapeCodes") as JsonArray)?
      .Select(node => node?.GetValue<string>())
      .Where(code => !string.IsNullOrWhiteSpace(code))
      .Cast<string>()
      .ToList();
    var bottomCode = PluginRuntime.GetOptionalString(parameters, "bottomCode") ?? "Trench_Bottom";
    var daylightCode = PluginRuntime.GetOptionalString(parameters, "daylightCode") ?? "Trench_Daylight";
    var includeRows = PluginRuntime.GetOptionalBool(parameters, "includeRows") ?? false;

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var corridor = CivilObjectUtils.FindCorridorByName(civilDoc, transaction, corridorName, OpenMode.ForRead);
      if (corridor.IsOutOfDate)
        throw new JsonRpcDispatchException("CIVIL3D.CONFLICT", $"Corridor '{corridorName}' is out of date; rebuild before taking quantities.");

      var baseline = CorridorEditingCommands.GetBaseline(corridor, baselineIndex);
      var shapeCodes = requestedCodes ?? corridor.GetShapeCodes().ToList();
      var unknown = shapeCodes.Except(corridor.GetShapeCodes(), StringComparer.Ordinal).ToList();
      if (unknown.Count > 0)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
          $"Unknown shape code(s) {string.Join(", ", unknown)}. Available: {string.Join(", ", corridor.GetShapeCodes())}.");

      var quantityKeys = shapeCodes.Append(ExcavationKey).ToList();
      var regionResults = new List<Dictionary<string, object?>>();
      var rows = new List<Dictionary<string, object?>>();
      var totals = quantityKeys.ToDictionary(key => key, _ => 0.0);
      var totalLength = 0.0;

      for (var regionIndex = 0; regionIndex < baseline.BaselineRegions.Count; regionIndex++)
      {
        var region = baseline.BaselineRegions[regionIndex];
        // Enumerate the region's sections and tag each with its assembly. At a shared
        // boundary station Civil 3D builds only the previous region's section (live
        // 2026-10-02); it is integrated as the corridor model does and reported.
        var regionStations = region.AppliedAssemblies.Stations();
        var appliedList = new List<AppliedAssembly>();
        foreach (AppliedAssembly item in region.AppliedAssemblies) appliedList.Add(item);
        if (appliedList.Count != regionStations.Length)
          throw new JsonRpcDispatchException("CIVIL3D.API_ERROR",
            $"Region '{region.Name}' returned {appliedList.Count} applied assemblies for {regionStations.Length} stations; quantities were not computed.");

        var pairingMismatches = 0;
        var candidates = appliedList.Select((applied, i) =>
        {
          var pointStation = SectionStation(applied);
          if (pointStation.HasValue && Math.Abs(pointStation.Value - regionStations[i]) > 1e-6) pairingMismatches++;
          return (Station: pointStation ?? regionStations[i], OwnAssembly: applied.AssemblyId == region.AssemblyId);
        }).ToList();
        var (selected, foreignStations) = QuantityMath.SelectRegionSections(candidates, startStation, endStation);
        if (selected.Count == 0) continue;
        var stations = selected.Select(i => candidates[i].Station).ToList();

        var areasByKey = quantityKeys.ToDictionary(key => key, _ => new List<double?>());
        var missingExcavation = 0;

        foreach (var index in selected)
        {
          var applied = appliedList[index];
          var station = candidates[index].Station;
          var row = new Dictionary<string, object?>
          {
            ["regionIndex"] = regionIndex,
            ["station"] = station,
          };

          foreach (var code in shapeCodes)
          {
            var area = applied.GetShapesByCode(code).Cast<CalculatedShape>().Sum(shape => shape.Area);
            areasByKey[code].Add(area);
            row[code] = area;
          }

          var excavation = ExcavationAt(applied, bottomCode, daylightCode, out var bottomWidth, out var topWidth, out var depth);
          if (excavation == null) missingExcavation++;
          areasByKey[ExcavationKey].Add(excavation);
          row[ExcavationKey] = excavation;
          row["bottomWidth"] = bottomWidth;
          row["topWidth"] = topWidth;
          row["meanDepth"] = depth;
          if (includeRows)
          {
            // Plan position of the outermost daylight points, for footprint boundaries.
            var (daylightLeftXY, daylightRightXY) = DaylightXY(applied, daylightCode);
            row["daylightLeft"] = daylightLeftXY;
            row["daylightRight"] = daylightRightXY;
            rows.Add(row);
          }
        }

        var length = stations[^1] - stations[0];
        totalLength += length;
        var volumes = new Dictionary<string, object?>();
        var skippedSegments = new Dictionary<string, object?>();
        foreach (var key in quantityKeys)
        {
          var (volume, skipped) = QuantityMath.AverageEndAreaVolume(stations, areasByKey[key]);
          volumes[key] = volume;
          totals[key] += volume;
          if (skipped > 0) skippedSegments[key] = skipped;
        }

        regionResults.Add(new Dictionary<string, object?>
        {
          ["regionIndex"] = regionIndex,
          ["regionName"] = region.Name,
          ["assemblyName"] = CivilObjectUtils.GetName(transaction.GetObject(region.AssemblyId, OpenMode.ForRead)) ?? string.Empty,
          ["startStation"] = stations[0],
          ["endStation"] = stations[^1],
          ["length"] = length,
          ["stationCount"] = stations.Count,
          ["volumes"] = volumes,
          ["perMeter"] = length > 0 ? volumes.ToDictionary(pair => pair.Key, pair => (object?)((double)pair.Value! / length)) : null,
          ["skippedSegments"] = skippedSegments,
          ["stationsWithoutExcavation"] = missingExcavation,
          ["foreignSections"] = foreignStations,
          ["stationPairingMismatches"] = pairingMismatches,
        });
      }

      var result = new Dictionary<string, object?>
      {
        ["corridorName"] = corridor.Name,
        ["baselineIndex"] = baselineIndex,
        ["shapeCodes"] = shapeCodes,
        ["excavationFrom"] = new Dictionary<string, object?> { ["bottomCode"] = bottomCode, ["daylightCode"] = daylightCode },
        ["method"] = "average end area per region; excavation polygon assumes straight ground between daylight points; " +
          "at a shared boundary station the neighbouring region's section is used (listed in foreignSections), as in the corridor model",
        ["regions"] = regionResults,
        ["totals"] = totals,
        ["totalLength"] = totalLength,
        ["units"] = new Dictionary<string, object?>
        {
          ["area"] = "m2",
          ["volume"] = CivilObjectUtils.VolumeUnits(database),
          ["length"] = CivilObjectUtils.LinearUnits(database),
        },
      };
      if (includeRows) result["rows"] = rows;
      return result;
    });
  }

  /// <summary>
  /// Excavation area from the outermost bottom points and daylight points.
  /// Returns null when either pair is incomplete (e.g. no daylight found).
  /// </summary>
  private static double? ExcavationAt(
    AppliedAssembly applied, string bottomCode, string daylightCode,
    out double? bottomWidth, out double? topWidth, out double? meanDepth)
  {
    bottomWidth = topWidth = meanDepth = null;
    var bottom = OffsetElevations(applied.GetPointsByCode(bottomCode));
    var daylight = OffsetElevations(applied.GetPointsByCode(daylightCode));
    if (bottom.Count < 2 || daylight.Count < 2) return null;

    var bottomLeft = bottom.MinBy(point => point.Offset);
    var bottomRight = bottom.MaxBy(point => point.Offset);
    var daylightLeft = daylight.MinBy(point => point.Offset);
    var daylightRight = daylight.MaxBy(point => point.Offset);

    bottomWidth = bottomRight.Offset - bottomLeft.Offset;
    topWidth = daylightRight.Offset - daylightLeft.Offset;
    meanDepth = (daylightLeft.Elevation + daylightRight.Elevation) / 2 - (bottomLeft.Elevation + bottomRight.Elevation) / 2;
    return QuantityMath.ExcavationArea(bottomLeft, bottomRight, daylightRight, daylightLeft);
  }

  /// <summary>World XY of the leftmost and rightmost daylight points; null when fewer than two.</summary>
  private static (double[]? Left, double[]? Right) DaylightXY(AppliedAssembly applied, string daylightCode)
  {
    var points = applied.GetPointsByCode(daylightCode).Cast<CalculatedPoint>().ToList();
    if (points.Count < 2) return (null, null);
    var left = points.MinBy(point => point.StationOffsetElevationToBaseline.Y)!.XYZ;
    var right = points.MaxBy(point => point.StationOffsetElevationToBaseline.Y)!.XYZ;
    return (new[] { left.X, left.Y }, new[] { right.X, right.Y });
  }

  /// <summary>Station of a section taken from its own calculated points; null when it has none.</summary>
  private static double? SectionStation(AppliedAssembly applied)
  {
    foreach (CalculatedPoint point in applied.Points)
      return point.StationOffsetElevationToBaseline.X;
    return null;
  }

  private static List<(double Offset, double Elevation)> OffsetElevations(CalculatedPointCollection points)
  {
    // StationOffsetElevationToBaseline: X = station, Y = offset, Z = elevation.
    return points.Cast<CalculatedPoint>()
      .Select(point => point.StationOffsetElevationToBaseline)
      .Select(soe => (soe.Y, soe.Z))
      .ToList();
  }
}
