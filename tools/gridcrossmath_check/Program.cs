using Civil3DMcpPlugin;

// Standalone check of the pure coordinate-cross math used by gridCrossesAdd (C89).
// Run: dotnet build -o out && dotnet out/gridcrossmath_check.dll
var failures = 0;
void Check(string name, bool ok, string detail = "")
{
  if (!ok) failures++;
  Console.WriteLine($"{(ok ? "OK  " : "FAIL")} {name} {detail}");
}
bool Near(double a, double b, double tolerance = 1e-9) => Math.Abs(a - b) <= tolerance;
double Deg(double degrees) => degrees * Math.PI / 180;

// An A1 plan viewport, 700 x 500 mm at 1:400 (CustomScale 2.5 paper mm per model metre), twist 0.6 rad and a view
// centre off the target, as a viewport may have after panning inside it.
var f = new GridCrossMath.Frame(CenterX: 400, CenterY: 300, Width: 700, Height: 500, TargetX: 190800, TargetY: 6232900,
  ViewCenterX: 12, ViewCenterY: -7, Scale: 2.5, Twist: 0.6);

// ViewportCommands.ModelCenter: Rotation(-twist, Z, target) * Displacement(target) applied to (ViewCenter, 0).
var (cx, cy) = GridCrossMath.PaperToModel(f, f.CenterX, f.CenterY);
var expectedX = 190800 + Math.Cos(-0.6) * 12 - Math.Sin(-0.6) * -7;
var expectedY = 6232900 + Math.Sin(-0.6) * 12 + Math.Cos(-0.6) * -7;
Check("paper centre shows ViewTarget + R(-twist)*ViewCenter (ViewportCommands.ModelCenter)", Near(cx, expectedX, 1e-6) && Near(cy, expectedY, 1e-6), $"({cx:0.000}, {cy:0.000})");

var (px, py) = GridCrossMath.ModelToPaper(f, cx, cy);
Check("the model centre is drawn at the viewport centre", Near(px, 400, 1e-6) && Near(py, 300, 1e-6), $"({px:0.000}, {py:0.000})");

var roundTrip = true;
foreach (var (x, y) in new[] { (190000.0, 6232000.0), (190812.5, 6232955.25), (191234.0, 6230111.0) })
{
  var (qx, qy) = GridCrossMath.ModelToPaper(f, x, y);
  var (bx, by) = GridCrossMath.PaperToModel(f, qx, qy);
  roundTrip &= Near(bx, x, 1e-6) && Near(by, y, 1e-6);
}
Check("model -> paper -> model round trip", roundTrip);

// A model direction at angle a is drawn at a + twist (ViewportCommands): twist 90 degrees puts east straight up.
var f90 = f with { Twist = Deg(90), ViewCenterX = 0, ViewCenterY = 0 };
(px, py) = GridCrossMath.ModelToPaper(f90, 190810, 6232900);
Check("twist 90: 10 m east of the target is drawn 25 mm above the centre", Near(px, 400, 1e-9) && Near(py, 325, 1e-9), $"({px:0.###}, {py:0.###})");
var f30 = f90 with { Twist = Deg(30) };
(px, py) = GridCrossMath.ModelToPaper(f30, 190810, 6232900);
Check("twist 30: east is drawn at 30 degrees on the sheet", Near(Math.Atan2(py - 300, px - 400), Deg(30), 1e-12) && Near(Math.Sqrt((px - 400) * (px - 400) + (py - 300) * (py - 300)), 25, 1e-9));

// Scale: 1:400, so 100 m east and 40 m north are 250 and 100 mm on the sheet.
var f0 = f with { Twist = 0, ViewCenterX = 0, ViewCenterY = 0 };
(px, py) = GridCrossMath.ModelToPaper(f0, 190900, 6232940);
Check("1:400: 100 m = 250 mm", Near(px, 650, 1e-9) && Near(py, 400, 1e-9), $"({px:0.###}, {py:0.###})");

// Grid without twist: the viewport shows E 190660..190940, N 6232800..6233000; with a 5 mm cross only the middle row fits.
var crosses = GridCrossMath.Crosses(f0, 100, GridCrossMath.CrossMargin(5, 0), 20000);
Check("no twist: three crosses on the middle row", crosses.Count == 3 && crosses.All(c => c.North == 6232900)
  && crosses.Select(c => c.East).SequenceEqual(new[] { 190700.0, 190800.0, 190900.0 }), $"count={crosses.Count}");
Check("column and row are the grid indices", crosses.Count > 0 && crosses[0].Column == 1907 && crosses[0].Row == 62329);
var onEdge = GridCrossMath.Crosses(f0, 100, 0, 20000);
Check("without a margin the crosses on the edge rows are inside too", onEdge.Count == 9, $"count={onEdge.Count}");

// Twisted grid: brute force over a wider area finds the same set.
var margin = GridCrossMath.CrossMargin(5, f.Twist);
var twisted = GridCrossMath.Crosses(f, 50, margin, 20000);
var brute = new List<(double, double)>();
for (var e = 189000.0; e <= 192500; e += 50)
{
  for (var n = 6231500.0; n <= 6234500; n += 50)
  {
    var (bx, by) = GridCrossMath.ModelToPaper(f, e, n);
    if (GridCrossMath.Inside(f, bx, by, margin)) brute.Add((e, n));
  }
}
Check("twisted grid matches brute force", twisted.Count > 0 && twisted.Select(c => (c.East, c.North)).OrderBy(p => p).SequenceEqual(brute.OrderBy(p => p)),
  $"crosses={twisted.Count} brute={brute.Count}");
Check("cross margin at 45 degrees is size/2*cos45", Near(GridCrossMath.CrossMargin(5, Deg(45)), 2.5 * Math.Sqrt(0.5), 1e-12) && Near(GridCrossMath.CrossMargin(5, Deg(90)), 2.5, 1e-12));

var threw = false;
try
{
  GridCrossMath.Crosses(f0, 0.1, 0, 20000);
}
catch (InvalidOperationException)
{
  threw = true;
}
Check("too many grid points to test throws", threw);

// Labels without twist: the northing runs east from the cross above the E-W arm, the easting runs up (90) left of the N-S arm.
var n0 = GridCrossMath.PlaceLabel(100, 100, 0, 0.9);
Check("twist 0, northing label: bottom left at (+g, +g), 0", !n0.Flipped && Near(n0.X, 100.9) && Near(n0.Y, 100.9) && Near(n0.Rotation, 0));
var e0 = GridCrossMath.PlaceLabel(100, 100, Deg(90), 0.9);
Check("twist 0, easting label: bottom left at (-g, +g), 90", !e0.Flipped && Near(e0.X, 99.1) && Near(e0.Y, 100.9) && Near(e0.Rotation, Deg(90)));
var w0 = GridCrossMath.PlaceLabel(100, 100, Deg(180), 0.9);
Check("northing label on the opposite arm: flipped, top right at (-g, -g), 0", w0.Flipped && Near(w0.X, 99.1) && Near(w0.Y, 99.1) && Near(w0.Rotation, 0));
var s0 = GridCrossMath.PlaceLabel(100, 100, Deg(270), 0.9);
Check("easting label on the opposite arm: flipped, top right at (+g, -g), 90", s0.Flipped && Near(s0.X, 100.9) && Near(s0.Y, 99.1) && Near(s0.Rotation, Deg(90)));
var e30 = GridCrossMath.PlaceLabel(0, 0, Deg(120), 1);
Check("twist 30, easting label (120): flipped to -60, anchored at g*v - g*u", e30.Flipped && Near(e30.Rotation, Deg(-60))
  && Near(e30.X, Math.Cos(Deg(120)) - Math.Sin(Deg(120))) && Near(e30.Y, Math.Sin(Deg(120)) + Math.Cos(Deg(120))));

Check("readable: -90 becomes 90", Near(GridCrossMath.Readable(Deg(-90), out var flip1), Deg(90)) && flip1);
Check("readable: 90 stays", Near(GridCrossMath.Readable(Deg(90), out var flip2), Deg(90)) && !flip2);
Check("readable: 180 becomes 0", Near(GridCrossMath.Readable(Deg(180), out var flip3), 0) && flip3);
Check("readable: -45 stays", Near(GridCrossMath.Readable(Deg(-45), out var flip4), Deg(-45)) && !flip4);
Check("readable: 450 is 90", Near(GridCrossMath.Readable(Deg(450), out var flip5), Deg(90), 1e-9) && !flip5);

// edgeLabels on a 3 x 2 grid: northing on the smallest column of each row, easting on the smallest row of each column.
var grid = new List<GridCrossMath.Cross>();
for (long column = 1; column <= 3; column++)
{
  for (long row = 10; row <= 11; row++) grid.Add(new GridCrossMath.Cross(column, row, column * 100, row * 100, 0, 0));
}
var (northLabelled, eastLabelled) = GridCrossMath.EdgeLabelled(grid);
Check("edge labels: northing on column 1 of rows 10 and 11", northLabelled.SetEquals(new[] { (1L, 10L), (1L, 11L) }));
Check("edge labels: easting on row 10 of columns 1-3", eastLabelled.SetEquals(new[] { (1L, 10L), (2L, 10L), (3L, 10L) }));

Check("format: whole metres", GridCrossMath.Format(6232950) == "6232950" && GridCrossMath.Format(190800.0000001) == "190800");
Check("format: decimal comma", GridCrossMath.Format(12.5) == "12,5" && GridCrossMath.Format(0.30000000000000004) == "0,3");
Check("format: no minus zero", GridCrossMath.Format(-0.0001) == "0");

Console.WriteLine(failures == 0 ? "ALL OK" : $"{failures} FAILED");
return failures == 0 ? 0 : 1;
