using Civil3DMcpPlugin;

// Standalone check of the pure gap math used by findSurfaceGaps.
// Run: dotnet build -o out && dotnet out/gapmath_check.dll
var failures = 0;
void Check(string name, bool ok, string detail = "")
{
  if (!ok) failures++;
  Console.WriteLine($"{(ok ? "OK  " : "FAIL")} {name} {detail}");
}

// Two small triangles (edges <= 1.5 m) and two long ones sharing an edge, far away a third long one.
var tris = new List<GapMath.Triangle>
{
  new((0, 0), (1, 0), (0, 1)),
  new((1, 0), (1, 1), (0, 1)),
  new((100, 0), (130, 0), (100, 20)),
  new((130, 0), (130, 20), (100, 20)),
  new((500, 500), (520, 500), (500, 515)),
};
Check("max edge of 3-4-5 triangle", Math.Abs(GapMath.MaxEdge(new((0, 0), (3, 0), (0, 4))) - 5) < 1e-12);
Check("area of 3-4-5 triangle", Math.Abs(GapMath.Area(new((0, 0), (3, 0), (0, 4))) - 6) < 1e-12);

var clusters = GapMath.FindGaps(tris, minEdgeLength: 10, region: null, maxClusters: 50);
Check("short triangles ignored, long ones in two clusters", clusters.Count == 2, $"count={clusters.Count}");
Check("largest cluster first (two triangles, 600 m2)", clusters.Count > 0 && clusters[0].TriangleCount == 2 && Math.Abs(clusters[0].Area - 600) < 1e-9,
  clusters.Count > 0 ? $"n={clusters[0].TriangleCount} area={clusters[0].Area}" : "");
Check("cluster bounding box", clusters.Count > 0 && clusters[0].MinX == 100 && clusters[0].MaxX == 130 && clusters[0].MinY == 0 && clusters[0].MaxY == 20);
Check("longest edge reported", clusters.Count > 0 && Math.Abs(clusters[0].MaxEdge - Math.Sqrt(30 * 30 + 20 * 20)) < 1e-9);

var region = new (double X, double Y)[] { (90, -10), (140, -10), (140, 30), (90, 30) };
var inRegion = GapMath.FindGaps(tris, 10, region, 50);
Check("region keeps only triangles whose centroid is inside", inRegion.Count == 1 && inRegion[0].TriangleCount == 2);

var capped = GapMath.FindGaps(tris, 10, null, 1);
Check("maxClusters caps the list", capped.Count == 1);

Console.WriteLine(failures == 0 ? "ALL OK" : $"{failures} FAILED");
return failures == 0 ? 0 : 1;
