namespace Civil3DMcpPlugin;

/// <summary>
/// Pure math for findSurfaceGaps: triangles with a long edge mark places where the TIN
/// spans missing data. Long triangles that share a vertex are grouped into one gap.
/// Free of Civil 3D types so it can be verified outside AutoCAD (tools/gapmath_check).
/// </summary>
public static class GapMath
{
  public readonly record struct Triangle((double X, double Y) A, (double X, double Y) B, (double X, double Y) C);

  public sealed record Gap(
    int TriangleCount,
    double Area,
    double MaxEdge,
    double MinX,
    double MinY,
    double MaxX,
    double MaxY,
    double CentroidX,
    double CentroidY);

  public static double MaxEdge(Triangle t) =>
    Math.Max(Distance(t.A, t.B), Math.Max(Distance(t.B, t.C), Distance(t.C, t.A)));

  public static double Area(Triangle t) =>
    Math.Abs((t.B.X - t.A.X) * (t.C.Y - t.A.Y) - (t.C.X - t.A.X) * (t.B.Y - t.A.Y)) / 2;

  /// <summary>Gaps sorted by area, largest first, at most <paramref name="maxClusters"/>.</summary>
  public static List<Gap> FindGaps(
    IEnumerable<Triangle> triangles,
    double minEdgeLength,
    IReadOnlyList<(double X, double Y)>? region,
    int maxClusters)
  {
    var longOnes = triangles
      .Where(t => MaxEdge(t) >= minEdgeLength)
      .Where(t => region == null || Contains(region, Centroid(t)))
      .ToList();

    var parent = Enumerable.Range(0, longOnes.Count).ToArray();
    int Find(int i) => parent[i] == i ? i : parent[i] = Find(parent[i]);
    var firstAtVertex = new Dictionary<(long, long), int>();
    for (var i = 0; i < longOnes.Count; i++)
    {
      foreach (var v in new[] { longOnes[i].A, longOnes[i].B, longOnes[i].C })
      {
        var key = ((long)Math.Round(v.X * 1000), (long)Math.Round(v.Y * 1000));
        if (firstAtVertex.TryGetValue(key, out var j))
        {
          parent[Find(i)] = Find(j);
        }
        else
        {
          firstAtVertex[key] = i;
        }
      }
    }

    return longOnes
      .Select((t, i) => (Root: Find(i), Triangle: t))
      .GroupBy(x => x.Root, x => x.Triangle)
      .Select(Summarize)
      .OrderByDescending(g => g.Area)
      .Take(maxClusters)
      .ToList();
  }

  private static Gap Summarize(IEnumerable<Triangle> group)
  {
    var list = group.ToList();
    var points = list.SelectMany(t => new[] { t.A, t.B, t.C }).ToList();
    var area = list.Sum(Area);
    var cx = area > 0 ? list.Sum(t => Centroid(t).X * Area(t)) / area : points.Average(p => p.X);
    var cy = area > 0 ? list.Sum(t => Centroid(t).Y * Area(t)) / area : points.Average(p => p.Y);
    return new Gap(
      list.Count, area, list.Max(MaxEdge),
      points.Min(p => p.X), points.Min(p => p.Y), points.Max(p => p.X), points.Max(p => p.Y),
      cx, cy);
  }

  private static (double X, double Y) Centroid(Triangle t) =>
    ((t.A.X + t.B.X + t.C.X) / 3, (t.A.Y + t.B.Y + t.C.Y) / 3);

  private static double Distance((double X, double Y) p, (double X, double Y) q) =>
    Math.Sqrt((p.X - q.X) * (p.X - q.X) + (p.Y - q.Y) * (p.Y - q.Y));

  // Ray casting; points on the boundary may fall either way.
  private static bool Contains(IReadOnlyList<(double X, double Y)> polygon, (double X, double Y) p)
  {
    var inside = false;
    for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
    {
      var (xi, yi) = polygon[i];
      var (xj, yj) = polygon[j];
      if ((yi > p.Y) != (yj > p.Y) && p.X < (xj - xi) * (p.Y - yi) / (yj - yi) + xi)
      {
        inside = !inside;
      }
    }

    return inside;
  }
}
