namespace Civil3DMcpPlugin;

/// <summary>
/// Pure cross-section and volume math for corridor quantities. Free of Civil 3D
/// types so it can be verified outside AutoCAD (tools/quantitymath_check).
/// Coordinates are (offset, elevation) relative to the baseline.
/// </summary>
public static class QuantityMath
{
  /// <summary>Absolute shoelace area of a simple polygon; orientation-independent.</summary>
  public static double PolygonArea(IReadOnlyList<(double X, double Y)> points)
  {
    if (points.Count < 3) return 0;
    var twiceArea = 0.0;
    for (var i = 0; i < points.Count; i++)
    {
      var (x1, y1) = points[i];
      var (x2, y2) = points[(i + 1) % points.Count];
      twiceArea += x1 * y2 - x2 * y1;
    }
    return Math.Abs(twiceArea) / 2;
  }

  /// <summary>
  /// Trench excavation area bounded by the bottom corners and the daylight points.
  /// Exact when the ground is straight between the two daylight points.
  /// </summary>
  public static double ExcavationArea(
    (double Offset, double Elevation) bottomLeft,
    (double Offset, double Elevation) bottomRight,
    (double Offset, double Elevation) daylightRight,
    (double Offset, double Elevation) daylightLeft)
  {
    return PolygonArea(new[] { bottomLeft, bottomRight, daylightRight, daylightLeft });
  }

  /// <summary>
  /// Picks the sections a region integrates: inside the optional station window, one per
  /// station (the region's own assembly preferred), ordered by station. Returns indices
  /// into <paramref name="sections"/> and the stations where only a foreign section exists:
  /// at a shared boundary station Civil 3D builds just the previous region's section and
  /// the corridor model transitions from it, so it is used and reported, never dropped.
  /// </summary>
  public static (IReadOnlyList<int> Selected, IReadOnlyList<double> ForeignStations) SelectRegionSections(
    IReadOnlyList<(double Station, bool OwnAssembly)> sections,
    double? startStation,
    double? endStation)
  {
    const double tolerance = 1e-9;
    var ordered = Enumerable.Range(0, sections.Count)
      .Where(i => (!startStation.HasValue || sections[i].Station >= startStation.Value - tolerance) &&
                  (!endStation.HasValue || sections[i].Station <= endStation.Value + tolerance))
      .OrderBy(i => sections[i].Station)
      .ThenByDescending(i => sections[i].OwnAssembly)
      .ToList();

    var selected = new List<int>();
    foreach (var i in ordered)
    {
      if (selected.Count > 0 && sections[i].Station - sections[selected[^1]].Station <= tolerance) continue;
      selected.Add(i);
    }

    var foreign = selected.Where(i => !sections[i].OwnAssembly).Select(i => sections[i].Station).ToList();
    return (selected, foreign);
  }

  /// <summary>
  /// Average-end-area volume over strictly increasing stations. A segment is
  /// skipped (and counted) when either end area is missing; nothing is interpolated.
  /// </summary>
  public static (double Volume, int SkippedSegments) AverageEndAreaVolume(
    IReadOnlyList<double> stations,
    IReadOnlyList<double?> areas)
  {
    if (stations.Count != areas.Count)
      throw new ArgumentException("stations and areas must have the same length.");

    var volume = 0.0;
    var skipped = 0;
    for (var i = 1; i < stations.Count; i++)
    {
      var length = stations[i] - stations[i - 1];
      if (length <= 0)
        throw new ArgumentException($"Stations must be strictly increasing (index {i}: {stations[i - 1]} → {stations[i]}).");

      if (areas[i - 1] is double a0 && areas[i] is double a1)
        volume += (a0 + a1) / 2 * length;
      else
        skipped++;
    }
    return (volume, skipped);
  }
}
