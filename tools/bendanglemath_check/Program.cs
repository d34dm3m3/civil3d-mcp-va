using Civil3DMcpPlugin;
using V = Civil3DMcpPlugin.BendAngleMath.Vec;

// Standalone check of the Hv/Vv math used by structureBendAngles (C92).
// Run: dotnet build -o out && dotnet out/bendanglemath_check.dll
var failures = 0;
void Check(string name, bool ok, string detail = "")
{
  if (!ok) failures++;
  Console.WriteLine($"{(ok ? "OK  " : "FAIL")} {name} {detail}");
}
bool Near(double a, double b, double tolerance = 1e-9) => Math.Abs(a - b) <= tolerance;
double Rad(double degrees) => degrees * Math.PI / 180;

// Away vectors: from the structure to each pipe's far end. Straight through: a points west, b east.
var straight = BendAngleMath.Compute(new V(-40, 0, 0.2), new V(50, 0, -0.25))!.Value;
Check("straight through in plan gives Hv 0", Near(straight.HorizontalDeg, 0, 1e-9), $"{straight.HorizontalDeg}");
// Grade 5 per mille falling through: in along a falls toward the structure (a's far end is 0.2 higher over 40 m), out falls on.
Check("same grade in and out gives Vv 0", Near(straight.VerticalDeg, 0, 1e-9), $"{straight.VerticalDeg}");
Check("inclination in is the fall toward the structure", Near(straight.InclinationInDeg, Math.Atan(-0.005) * 180 / Math.PI), $"{straight.InclinationInDeg}");

// A 30 degree turn: b leaves 30 degrees left of straight on.
var turn = BendAngleMath.Compute(new V(-40, 0, 0), new V(50 * Math.Cos(Rad(30)), 50 * Math.Sin(Rad(30)), 0))!.Value;
Check("a 30 degree turn gives Hv 30", Near(turn.HorizontalDeg, 30, 1e-9), $"{turn.HorizontalDeg}");
var right = BendAngleMath.Compute(new V(-40, 0, 0), new V(50 * Math.Cos(Rad(-30)), 50 * Math.Sin(Rad(-30)), 0))!.Value;
Check("a turn to the right is also Hv 30 (no sign)", Near(right.HorizontalDeg, 30, 1e-9), $"{right.HorizontalDeg}");
var square = BendAngleMath.Compute(new V(0, -10, 0), new V(10, 0, 0))!.Value;
Check("a right angle gives Hv 90", Near(square.HorizontalDeg, 90, 1e-9), $"{square.HorizontalDeg}");
var back = BendAngleMath.Compute(new V(10, 0, 0), new V(10, 0.0, 0))!.Value;
Check("pipes on the same side give Hv 180", Near(back.HorizontalDeg, 180, 1e-9), $"{back.HorizontalDeg}");

// Vertical: flat in, then 10 % down out: Vv = atan(0.1) = 5.71 degrees.
var drop = BendAngleMath.Compute(new V(-20, 0, 0), new V(30, 0, -3))!.Value;
Check("flat in, 10 % down out gives Vv atan(0.1)", Near(drop.VerticalDeg, Math.Atan(0.1) * 180 / Math.PI, 1e-9), $"{drop.VerticalDeg:0.0000}");
var swapped = BendAngleMath.Compute(new V(30, 0, -3), new V(-20, 0, 0))!.Value;
Check("Vv does not depend on which pipe is first", Near(swapped.VerticalDeg, drop.VerticalDeg, 1e-12));
Check("Hv does not depend on which pipe is first", Near(swapped.HorizontalDeg, drop.HorizontalDeg, 1e-12));
// A sag: falling 2 % in, rising 3 % out: Vv = atan(0.03) + atan(0.02).
var sag = BendAngleMath.Compute(new V(-50, 0, 1), new V(40, 0, 1.2))!.Value;
Check("a sag adds the two grades", Near(sag.VerticalDeg, (Math.Atan(0.03) + Math.Atan(0.02)) * 180 / Math.PI, 1e-9), $"{sag.VerticalDeg:0.0000}");
Check("the plan turn is measured in plan only (rise ignored)", Near(sag.HorizontalDeg, 0, 1e-9));
Check("a vertical pipe gives no result", BendAngleMath.Compute(new V(0, 0, 3), new V(10, 0, 0)) == null);

// Curved pipe: the tangent at the structure end, toward the far end, scaled to the arc length.
// Arc centred at (0, 0), radius 10, from the structure at (10, 0) counter-clockwise to (0, 10).
var arc = BendAngleMath.ArcAway(10, 0, 0, 0, 0, 10, Math.PI * 5, 0.5)!.Value;
Check("arc tangent at the structure points along +Y toward the far end", Near(arc.X, 0, 1e-9) && Near(arc.Y, Math.PI * 5, 1e-9) && Near(arc.Z, 0.5), $"({arc.X:0.000}, {arc.Y:0.000}, {arc.Z})");
var arcCw = BendAngleMath.ArcAway(10, 0, 0, 0, 0, -10, Math.PI * 5, 0)!.Value;
Check("clockwise arc turns the tangent to -Y", Near(arcCw.Y, -Math.PI * 5, 1e-9));
Check("a structure on the arc's centre gives no tangent", BendAngleMath.ArcAway(0, 0, 0, 0, 5, 5, 3, 0) == null);
// A straight pipe in, then the arc out: the turn at the structure is the tangent's, not the chord's (45 degrees).
var arcBend = BendAngleMath.Compute(new V(0, -20, 0), arc)!.Value;
Check("a curved pipe continuing straight on gives Hv 0 by its tangent", Near(arcBend.HorizontalDeg, 0, 1e-9), $"{arcBend.HorizontalDeg}");

// Text and description.
Check("whole degrees by default", BendAngleMath.Text(30.4, 5.6, 0, 1, 1) == "Hv 30° Vv 6°", BendAngleMath.Text(30.4, 5.6, 0, 1, 1));
Check("half rounds away from zero", BendAngleMath.Format(22.5, 0) == "23", BendAngleMath.Format(22.5, 0));
Check("decimals with a decimal comma", BendAngleMath.Text(22.46, 0.2, 1, 1, 1) == "Hv 22,5°", BendAngleMath.Text(22.46, 0.2, 1, 1, 1));
Check("below both minimums gives no text", BendAngleMath.Text(0.4, 0.9, 0, 1, 1) == "");
Check("only Vv when Hv is below its minimum", BendAngleMath.Text(0.2, 4.0, 0, 1, 1) == "Vv 4°");
Check("replace: the text alone", BendAngleMath.NewDescription("Nedstigningsbrunn", "Hv 30°", false) == "Hv 30°");
Check("keepExisting appends after ' | '", BendAngleMath.NewDescription("Nedstigningsbrunn", "Hv 30°", true) == "Nedstigningsbrunn | Hv 30°");
Check("keepExisting replaces an earlier Hv/Vv text", BendAngleMath.NewDescription("Nedstigningsbrunn | Hv 12° Vv 2°", "Hv 30°", true) == "Nedstigningsbrunn | Hv 30°");
Check("keepExisting with no new text drops the old one", BendAngleMath.NewDescription("Nedstigningsbrunn | Vv 3°", "", true) == "Nedstigningsbrunn");
Check("keepExisting on an empty description", BendAngleMath.NewDescription(null, "Vv 5°", true) == "Vv 5°");
Check("no text without keepExisting keeps a description of its own", BendAngleMath.NewDescription("Nedstigningsbrunn", "", false) == "Nedstigningsbrunn");
Check("no text without keepExisting clears a stale Hv/Vv text", BendAngleMath.NewDescription("Hv 12° Vv 2°", "", false) == "");

Console.WriteLine(failures == 0 ? "ALL OK" : $"{failures} FAILED");
return failures == 0 ? 0 : 1;
