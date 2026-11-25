using Civil3DMcpPlugin;

// Standalone check of SheetMath (C240 buildPlanProfileSheet) against the original Python sheet script: the same
// cases as its unit tests, and reference values it computed for a bent line (printed by Python 2026-11-25: twist,
// plan centre and slide error with s0 3, s1 L - 1.5, m the middle + 4.3; fictitious coordinates).
// Run: dotnet build -o out && dotnet out/sheetmath_check.dll
var failures = 0;
void Check(string name, bool ok, string detail = "")
{
  if (!ok) failures++;
  Console.WriteLine($"{(ok ? "OK  " : "FAIL")} {name} {detail}");
}

Func<double, (double X, double Y)> Polyline((double X, double Y)[] pts, out double length)
{
  var acc = new List<double> { 0 };
  for (var i = 1; i < pts.Length; i++)
    acc.Add(acc[^1] + Math.Sqrt((pts[i].X - pts[i - 1].X) * (pts[i].X - pts[i - 1].X) + (pts[i].Y - pts[i - 1].Y) * (pts[i].Y - pts[i - 1].Y)));
  length = acc[^1];
  var total = length;
  return s =>
  {
    s = Math.Min(Math.Max(s, 0), total);
    for (var i = 0; i < pts.Length - 1; i++)
      if (s <= acc[i + 1] + 1e-12)
      {
        var t = (s - acc[i]) / (acc[i + 1] - acc[i]);
        return (pts[i].X + t * (pts[i + 1].X - pts[i].X), pts[i].Y + t * (pts[i + 1].Y - pts[i].Y));
      }
    return pts[^1];
  };
}

// the chord drawn to the left (sheet direction 180 degrees) or to the right (0)
foreach (var v in new[] { -170.0, -30.0, 0.0, 45.0, 120.0 })
{
  var hv = ((v + SheetMath.Twist(v, true) - 180 + 180) % 360 + 360) % 360 - 180;
  var vh = ((v + SheetMath.Twist(v, false) + 180) % 360 + 360) % 360 - 180;
  Check($"twist {v}", Math.Abs(hv) < 1e-9 && Math.Abs(vh) < 1e-9, $"{hv} {vh}");
}

// a straight line: the middle, no slide
{
  var p = Polyline(new[] { (0.0, 0.0), (100.0, 50.0) }, out var l);
  var a = SheetMath.Chord(p(0), p(l)).AngleDeg;
  foreach (var rl in new[] { true, false })
  {
    var tw = SheetMath.Twist(a, rl);
    var c = SheetMath.PlanCenter(p, 0, l, l / 2, tw, rl);
    var m = p(l / 2);
    var e = SheetMath.SlideErrors(p, 0, l, l / 2, c, tw, rl);
    Check($"straight line centre ({(rl ? "hv" : "vh")})", Math.Sqrt((c.X - m.X) * (c.X - m.X) + (c.Y - m.Y) * (c.Y - m.Y)) < 1e-6 && e.Max(Math.Abs) < 1e-6);
  }
}

// a bent line: equal slide both ways (minimax)
{
  var p = Polyline(new[] { (0.0, 0.0), (60.0, 0.0), (110.0, 30.0), (170.0, 30.0) }, out var l);
  var tw = SheetMath.Twist(SheetMath.Chord(p(0), p(l)).AngleDeg, true);
  var c = SheetMath.PlanCenter(p, 0, l, l / 2, tw, true);
  var e = SheetMath.SlideErrors(p, 0, l, l / 2, c, tw, true);
  Check("bent line minimax", Math.Abs(e.Max() + e.Min()) < 1e-6 && e.Max() > 0.1, $"{e.Min():F4} {e.Max():F4}");
}

// reference values from the Python sheet script
{
  var p = Polyline(new[] { (211000.0, 6216000.0), (211060.0, 6216010.0), (211110.0, 6216045.0), (211170.0, 6216050.0) }, out var l);
  Check("reference length", Math.Abs(l - 182.0683762756122) < 1e-9, $"{l}");
  foreach (var (rl, twRef, cx, cy, eMin, eMax) in new[]
  {
    (true, 163.39020141084828, 211089.2326587086, 6216028.492263836, -2.4069875922831727, 2.406987592434035),
    (false, 343.3902014108483, 211089.2326587086, 6216028.492263836, -2.406987592434035, 2.406987592283187),
  })
  {
    double s0 = 3.0, s1 = l - 1.5, m = (s0 + s1) / 2 + 4.3;
    var tw = SheetMath.Twist(SheetMath.Chord(p(s0), p(s1)).AngleDeg, rl);
    var c = SheetMath.PlanCenter(p, s0, s1, m, tw, rl);
    var e = SheetMath.SlideErrors(p, s0, s1, m, c, tw, rl);
    Check($"reference {(rl ? "hv" : "vh")}: twist, centre, slide",
      Math.Abs(tw - twRef) < 1e-9 && Math.Abs(c.X - cx) < 1e-6 && Math.Abs(c.Y - cy) < 1e-6 && Math.Abs(e.Min() - eMin) < 1e-6 && Math.Abs(e.Max() - eMax) < 1e-6 && e.Count == 90,
      $"tw {tw} c ({c.X:F6}, {c.Y:F6}) e [{e.Min():F9}, {e.Max():F9}] n {e.Count}");
  }
}

// frame corners
{
  var h = SheetMath.FrameCorners((10.0, 20.0), 30.0, 272.4, 98.0);
  double D((double X, double Y) a, (double X, double Y) b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
  var ang = Math.Atan2(h[1].Y - h[0].Y, h[1].X - h[0].X) * 180 / Math.PI;
  Check("frame corners", Math.Abs(D(h[0], h[1]) - 272.4) < 1e-9 && Math.Abs(D(h[1], h[2]) - 98.0) < 1e-9 && Math.Abs(ang + 30.0) < 1e-9
    && Math.Abs(h[0].X - -132.45265999544054) < 1e-9 && Math.Abs(h[0].Y - 45.66475521456248) < 1e-9, $"{h[0]}");
}

// station texts and the sheet length
Check("station text", SheetMath.StationText(0) == "0/000" && SheetMath.StationText(215.4) == "0/215" && SheetMath.StationText(1234.6) == "1/235"
  && SheetMath.StationText(-12.6) == "-0/013" && SheetMath.StationText(1234.5) == "1/234" && SheetMath.StationText(1235.5) == "1/236" && SheetMath.StationText(0.4) == "0/000");
{
  var a = SheetMath.SheetLength(0, 215, 400);
  var b = SheetMath.SheetLength(100, 330, 400);
  var c = SheetMath.SheetLength(0, 236, 400);
  var d = SheetMath.SheetLength(0, 117, 200);
  var e = SheetMath.SheetLength(0, 118, 200);
  Check("sheet length 215 normal, 235 roof", a.Status == "ok" && b.Status == "warning" && c.Status == "stop" && Math.Abs(c.Normal - 215) < 1e-9 && Math.Abs(c.Max - 234.8) < 1e-9
    && d.Status == "warning" && e.Status == "stop", $"{a.Status} {b.Status} {c.Status} {d.Status} {e.Status}");
}
Check("connection text", SheetMath.ConnectionText(101, 102, true) == "-02 | -01" && SheetMath.ConnectionText(101, 102, false) == "-01 | -02");

Console.WriteLine(failures == 0 ? "ALL OK" : $"{failures} FAILED");
return failures == 0 ? 0 : 1;
