using Civil3DMcpPlugin;

// Standalone check of the pure quantity math used by corridorQuantities.
// Run: dotnet build -o out && dotnet out/quantitymath_check.dll
var failures = 0;
void Check(string name, double actual, double expected, double tol = 1e-9)
{
  var ok = Math.Abs(actual - expected) <= tol;
  if (!ok) failures++;
  Console.WriteLine($"{(ok ? "OK  " : "FAIL")} {name}: {actual:G10} (expected {expected:G10})");
}

// 1:1 trench, bottom width 1.885, depth 1.725, flat ground: A = h(B + h).
const double B = 1.885, H = 1.725;
var trench = QuantityMath.ExcavationArea(
  bottomLeft: (-B / 2, 0), bottomRight: (B / 2, 0),
  daylightRight: (B / 2 + H, H), daylightLeft: (-B / 2 - H, H));
Check("trench area h(B+h)", trench, H * (B + H));

// Orientation must not change the polygon area.
var cw = QuantityMath.PolygonArea(new[] { (0.0, 0.0), (0.0, 2.0), (3.0, 2.0), (3.0, 0.0) });
var ccw = QuantityMath.PolygonArea(new[] { (0.0, 0.0), (3.0, 0.0), (3.0, 2.0), (0.0, 2.0) });
Check("rectangle cw", cw, 6.0);
Check("rectangle ccw", ccw, 6.0);

// Average end area; a missing area skips only the adjacent segments.
var (volume, skipped) = QuantityMath.AverageEndAreaVolume(
  new[] { 0.0, 10.0, 20.0, 30.0 }, new double?[] { 1.0, 3.0, null, 5.0 });
Check("average end area volume", volume, 20.0);
Check("skipped segments", skipped, 2);

// Unsorted or duplicate stations are rejected rather than silently reordered.
try
{
  QuantityMath.AverageEndAreaVolume(new[] { 0.0, 10.0, 5.0 }, new double?[] { 1, 1, 1 });
  failures++;
  Console.WriteLine("FAIL unsorted stations accepted");
}
catch (ArgumentException)
{
  Console.WriteLine("OK   unsorted stations rejected");
}

void CheckSeq<T>(string name, IReadOnlyList<T> actual, IReadOnlyList<T> expected)
{
  var ok = actual.SequenceEqual(expected);
  if (!ok) failures++;
  Console.WriteLine($"{(ok ? "OK  " : "FAIL")} {name}: [{string.Join(", ", actual)}] (expected [{string.Join(", ", expected)}])");
}

// Live 2026-10-02: at a station shared by two regions Civil 3D builds only the previous
// region's section (TP_HI width at 324.4 in the TP_IJ region's own collection). The corridor
// model transitions from it, so it is kept (no gap between regions) and reported.
var boundary = QuantityMath.SelectRegionSections(
  new[] { (324.4, false), (325.0, true), (330.0, true) }, null, null);
CheckSeq("boundary section from neighbour kept", boundary.Selected, new[] { 0, 1, 2 });
CheckSeq("boundary station from neighbour reported", boundary.ForeignStations, new[] { 324.4 });

// When the region has its own section at a station, it wins over a foreign one.
var both = QuantityMath.SelectRegionSections(
  new[] { (5.0, false), (5.0, true), (10.0, true) }, null, null);
CheckSeq("own section preferred at same station", both.Selected, new[] { 1, 2 });
CheckSeq("no foreign station when own exists", both.ForeignStations, Array.Empty<double>());

var window = QuantityMath.SelectRegionSections(
  new[] { (0.0, true), (5.0, true), (10.0, true), (15.0, true) }, 5.0, 10.0);
CheckSeq("station window inclusive", window.Selected, new[] { 1, 2 });

var unordered = QuantityMath.SelectRegionSections(
  new[] { (10.0, true), (0.0, true), (5.0, true), (5.0, true) }, null, null);
CheckSeq("sorted by station, duplicates dropped", unordered.Selected, new[] { 1, 2, 0 });
CheckSeq("no foreign sections", unordered.ForeignStations, Array.Empty<double>());

Console.WriteLine(failures == 0 ? "ALL OK" : $"{failures} FAILURE(S)");
return failures == 0 ? 0 : 1;
