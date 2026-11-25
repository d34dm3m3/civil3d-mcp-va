namespace Civil3DMcpPlugin;

/// <summary>
/// C240 (F9, 2026-11-25): the plan and profile sheet's geometry as pure functions, ported from the original Python
/// sheet script (tested there by its own unit tests, here by tools/sheetmath_check): the plan is turned after
/// the sheet's chord so the stations grow the way the profile runs, the plan and the profile show the same middle
/// station, and the slide between plan and profile along the sheet is shared equally both ways (minimax).
/// No AutoCAD types, so the check harness compiles this file alone.
/// </summary>
public static class SheetMath
{
  /// <summary>Start point, unit vector and angle (degrees) of the chord p0 -> p1.</summary>
  public static ((double X, double Y) Start, (double X, double Y) Unit, double AngleDeg) Chord((double X, double Y) p0, (double X, double Y) p1)
  {
    var l = Math.Sqrt((p1.X - p0.X) * (p1.X - p0.X) + (p1.Y - p0.Y) * (p1.Y - p0.Y));
    if (!(l > 1e-9)) throw new ArgumentException("the sheet's start and end points coincide");
    var u = ((p1.X - p0.X) / l, (p1.Y - p0.Y) / l);
    return (p0, u, Math.Atan2(u.Item2, u.Item1) * 180 / Math.PI);
  }

  private static double Mod360(double v) => ((v % 360.0) + 360.0) % 360.0;

  /// <summary>The viewport twist that draws the chord to the left (rightToLeft: the profile runs right to left)
  /// or to the right.</summary>
  public static double Twist(double chordAngleDeg, bool rightToLeft) => rightToLeft ? Mod360(180.0 - chordAngleDeg) : Mod360(-chordAngleDeg);

  /// <summary>The sheet's x axis (to the right) as a model direction for a viewport with the given twist (degrees).</summary>
  public static (double X, double Y) SheetX(double twistDeg) => (Math.Cos(-twistDeg * Math.PI / 180), Math.Sin(-twistDeg * Math.PI / 180));

  /// <summary>+1 when the stations grow to the left on the sheet (right to left), -1 when they grow to the right.</summary>
  public static double Sign(bool rightToLeft) => rightToLeft ? 1.0 : -1.0;

  /// <summary>The stations the slide is checked at: every step from s0, and s1 (as the Python sheet script).</summary>
  public static List<double> SlideStations(double s0, double s1, double step)
  {
    var list = new List<double>();
    var n = (int)Math.Floor((s1 - s0) / step);
    for (var i = 0; i <= n; i++) list.Add(s0 + i * step);
    list.Add(s1);
    return list;
  }

  /// <summary>The plan's deviation along the sheet from the profile (model units) at each slide station:
  /// e(s) = (P(s) - c)·x_sheet + sign·(s - m).</summary>
  public static List<double> SlideErrors(Func<double, (double X, double Y)> point, double s0, double s1, double m, (double X, double Y) c,
    double twistDeg, bool rightToLeft, double step = 2.0)
  {
    var (hx, hy) = SheetX(twistDeg);
    var sg = Sign(rightToLeft);
    return SlideStations(s0, s1, step).Select(s => { var p = point(s); return (p.X - c.X) * hx + (p.Y - c.Y) * hy + sg * (s - m); }).ToList();
  }

  /// <summary>The plan viewport's model centre: on the chord at the projection of the middle station, moved across to
  /// the middle between the line's largest and least distance from the chord, and along the sheet so the slide is the
  /// same both ways (minimax). point(s) gives (x, y) on the alignment.</summary>
  public static (double X, double Y) PlanCenter(Func<double, (double X, double Y)> point, double s0, double s1, double m, double twistDeg, bool rightToLeft,
    int n = 20, double step = 2.0)
  {
    var ((ax, ay), u, _) = Chord(point(s0), point(s1));
    var (px, py) = point(m);
    var t = (px - ax) * u.X + (py - ay) * u.Y;
    var nv = (X: -u.Y, Y: u.X);
    var tv = new List<double>();
    for (var i = 0; i <= n; i++)
    {
      var (qx, qy) = point(s0 + i * (s1 - s0) / n);
      tv.Add((qx - ax) * nv.X + (qy - ay) * nv.Y);
    }
    var d = (tv.Max() + tv.Min()) / 2.0;
    var c = (X: ax + t * u.X + d * nv.X, Y: ay + t * u.Y + d * nv.Y);
    var e = SlideErrors(point, s0, s1, m, c, twistDeg, rightToLeft, step);
    var dl = (e.Max() + e.Min()) / 2.0;
    var (hx, hy) = SheetX(twistDeg);
    return (c.X + dl * hx, c.Y + dl * hy);
  }

  /// <summary>The profile viewport's model x centre: the graph from left to right plus the band titles to the left
  /// (titleMm, and 0.5 m as the Python sheet script) and the right axis texts (axisMm); paper mm times modelPerMm.</summary>
  public static double ProfileCenterX(double graphLeft, double graphRight, double modelPerMm, double titleMm, double axisMm) =>
    ((graphLeft - titleMm * modelPerMm - 0.5) + (graphRight + axisMm * modelPerMm)) / 2.0;

  /// <summary>The sheet length check: ok, warning (over the normal length) or stop (over the roof: the profile viewport
  /// minus band titles and right axis), with the length, normal and roof in model units.</summary>
  public static (string Status, double Length, double Normal, double Max) SheetLength(double s0, double s1, double scale,
    double profileViewportMm = 681.0, double normalMm = 537.5, double titleMm = 68.0, double axisMm = 26.0)
  {
    double length = s1 - s0, normal = normalMm * scale / 1000.0, max = (profileViewportMm - titleMm - axisMm) * scale / 1000.0;
    return (length > max + 1e-6 ? "stop" : length > normal + 1e-6 ? "warning" : "ok", length, normal, max);
  }

  /// <summary>The plan viewport's four corners in the model (width and height in model units), counterclockwise from
  /// the lower left.</summary>
  public static List<(double X, double Y)> FrameCorners((double X, double Y) c, double twistDeg, double width, double height)
  {
    var (hx, hy) = SheetX(twistDeg);
    double ux = -hy, uy = hx;
    return new[] { (-1, -1), (1, -1), (1, 1), (-1, 1) }
      .Select(q => (c.X + q.Item1 * width / 2 * hx + q.Item2 * height / 2 * ux, c.Y + q.Item1 * width / 2 * hy + q.Item2 * height / 2 * uy)).ToList();
  }

  /// <summary>"0/215", "1/235"; a station before 0/000 gets a minus.</summary>
  public static string StationText(double s)
  {
    var r = Math.Round(s, MidpointRounding.ToEven);   // Python round()
    if (r < 0) return "-" + StationText(-s);
    var v = (long)r;
    return $"{v / 1000}/{v % 1000:000}";
  }

  /// <summary>The connection line at a sheet boundary g: across the whole plan viewport height through g, and the
  /// text's insertion point (as the Python sheet script); modelPerMm = scale / 1000.</summary>
  public static ((double X, double Y) A, (double X, double Y) B, (double X, double Y) Text, double TextHeight) Connection((double X, double Y) g,
    (double X, double Y) c, double twistDeg, double heightModel, double modelPerMm, int textLength)
  {
    var (hx, hy) = SheetX(twistDeg);
    var up = (X: -hy, Y: hx);
    var v = (g.X - c.X) * up.X + (g.Y - c.Y) * up.Y;
    double a = -heightModel / 2 - v, e = heightModel / 2 - v;
    var th = 2.4 * modelPerMm / 0.4;
    var off = e - 4.0 * modelPerMm / 0.4;
    var bx = g.X + up.X * off - hx * (textLength * th * 0.45);
    var by = g.Y + up.Y * off - hy * (textLength * th * 0.45);
    return ((g.X + up.X * a, g.Y + up.Y * a), (g.X + up.X * e, g.Y + up.Y * e), (bx, by), th);
  }

  /// <summary>"-02 | -01": the sheet to the left first (right to left: the later sheet is to the left).</summary>
  public static string ConnectionText(int lowerSheet, int higherSheet, bool rightToLeft)
  {
    var (left, right) = rightToLeft ? (higherSheet, lowerSheet) : (lowerSheet, higherSheet);
    return $"-{Math.Abs(left) % 100:00} | -{Math.Abs(right) % 100:00}";
  }
}
