using System.Text.RegularExpressions;

namespace Civil3DMcpPlugin;

/// <summary>
/// C241 (F9, 2026-11-25): the flag placement of the original Python text layout (its placer class) with the hard rules
/// of the projects' Python flag scripts (2026-10-26): a text box may not
/// touch another flag's box or leader, a network part (pipes buffered, structure bodies), another owner's object or an
/// existing text, and a leader may not run through another flag's box; among the free candidates the soft score
/// decides: line length inside the box (network lines x 10), leader crossings x 2, leader length x 0.05, overlap with
/// placed or reserved areas x 100 + 50 x area, leaders through them x 20, outside the drawing area x 1000, plus
/// 5 x crossings with placed leaders and 0.2 x the leader's length in text heights. Candidates: the item's directions
/// (default the text to the right of the leader, a drafting rule) at 2.5-14 text heights, then all right-hand directions
/// out to 37; with no free candidate the best soft candidate (3-10 text heights) is used and reported as noFreeSpot.
///
/// Everything runs in the sheet's frame (model rotated by -rotationDeg around an origin), where the flags' boxes are axis
/// aligned. No AutoCAD types, so tools/flagplacement_check compiles this file alone and compares it with the Python
/// placement on the same cases.
/// </summary>
public sealed class FlagPlacer
{
  public const double CharWidth = 0.68;     // isocp: character width / text height (0.66 measured in the plot)
  public const double LineSpacing = 1.667;  // MText line spacing / text height
  public const double NetworkWeight = 10.0; // a flag must not cover a pipe (drafting rule, 2026-10-11)

  public static readonly double[] FirstDistances = { 2.5, 3.5, 5.0, 7.0, 9.0, 11.5, 14.0 };
  public static readonly double[] WideDistances = { 2.5, 4.0, 6.0, 8.5, 11.5, 15.0, 19.0, 24.0, 30.0, 37.0 };
  public static readonly double[] SoftDistances = { 3.0, 4.5, 6.0, 8.0, 10.0 };

  public readonly record struct Pt(double X, double Y);

  public readonly record struct Box(double MinX, double MinY, double MaxX, double MaxY)
  {
    public double Area => Math.Max(0, MaxX - MinX) * Math.Max(0, MaxY - MinY);
    public bool Touches(Box o) => MinX <= o.MaxX && o.MinX <= MaxX && MinY <= o.MaxY && o.MinY <= MaxY;
    public Pt[] Corners => new[] { new Pt(MinX, MinY), new Pt(MaxX, MinY), new Pt(MaxX, MaxY), new Pt(MinX, MaxY) };
  }

  private sealed record Soft(Pt A, Pt B, double Weight, int Group, bool StartIsEnd, bool EndIsEnd, Box Bounds);

  /// <summary>A hard obstacle: points (a point, a polyline, or a closed convex polygon) grown by Radius.</summary>
  private sealed record Hard(Pt[] Points, bool Closed, double Radius, Box Bounds);

  /// <summary>A placed or reserved area (convex polygon) for the soft score.</summary>
  private sealed record Area(Pt[] Poly, Box Bounds);

  public sealed record Placement(double TextX, double TextY, double Score, bool Free, string Attach, double Distance, double AngleDeg,
    Pt[] BoxModel, int Candidates);

  private readonly double _ox, _oy, _cos, _sin;
  private readonly List<Soft> _soft = new();
  private readonly List<Hard> _hard = new();
  private readonly List<Area> _placed = new();          // Placerare.placerade (flags and reserved areas)
  private readonly List<Box> _flagBoxes = new();        // Fritt.rutor
  private readonly List<(Pt A, Pt B)> _leaders = new(); // Fritt.ledare
  private readonly Pt[] _drawing;                        // the drawing area in the sheet frame
  private readonly double _margin;
  private int _group;

  public double RotationDeg { get; }

  /// <param name="rotationDeg">the sheet's x axis in the model (degrees, = -twist of the viewport)</param>
  /// <param name="area">the drawing area in model coordinates (a polygon)</param>
  /// <param name="margin">the distance a box keeps from the area's edge (Placerare marginal)</param>
  public FlagPlacer(double rotationDeg, IReadOnlyList<(double X, double Y)> area, double margin, double? originX = null, double? originY = null)
  {
    if (area.Count < 3) throw new ArgumentException("the drawing area needs at least 3 points");
    RotationDeg = rotationDeg;
    _ox = originX ?? area.Average(p => p.X);
    _oy = originY ?? area.Average(p => p.Y);
    _cos = Math.Cos(rotationDeg * Math.PI / 180);
    _sin = Math.Sin(rotationDeg * Math.PI / 180);
    _drawing = area.Select(p => ToSheet(p.X, p.Y)).ToArray();
    _margin = margin;
  }

  public Pt ToSheet(double x, double y)
  {
    double dx = x - _ox, dy = y - _oy;
    return new Pt(dx * _cos + dy * _sin, -dx * _sin + dy * _cos);
  }

  public (double X, double Y) ToModel(Pt p) => (_ox + p.X * _cos - p.Y * _sin, _oy + p.X * _sin + p.Y * _cos);

  private static Box BoundsOf(IEnumerable<Pt> pts, double grow = 0)
  {
    double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
    foreach (var p in pts) { x0 = Math.Min(x0, p.X); y0 = Math.Min(y0, p.Y); x1 = Math.Max(x1, p.X); y1 = Math.Max(y1, p.Y); }
    return new Box(x0 - grow, y0 - grow, x1 + grow, y1 + grow);
  }

  // ---------- obstacles ----------

  /// <summary>A soft line (weight 1; networks NetworkWeight): its length inside a box and its crossings with a leader
  /// are scored. A polyline counts once per crossing leader (shapely crosses on the whole line).</summary>
  public void AddSoftLine(IReadOnlyList<(double X, double Y)> points, double weight = 1.0)
  {
    if (points.Count < 2) return;
    var g = _group++;
    var s = points.Select(p => ToSheet(p.X, p.Y)).ToArray();
    for (var i = 0; i + 1 < s.Length; i++)
    {
      if (Math.Abs(s[i].X - s[i + 1].X) < 1e-12 && Math.Abs(s[i].Y - s[i + 1].Y) < 1e-12) continue;
      _soft.Add(new Soft(s[i], s[i + 1], weight, g, i == 0, i + 2 == s.Length, BoundsOf(new[] { s[i], s[i + 1] })));
    }
  }

  /// <summary>A hard obstacle: a box may not come within radius of it (a point = a circle, a polyline = a buffered
  /// line, closed = a convex polygon, e.g. an existing text's box).</summary>
  public void AddHard(IReadOnlyList<(double X, double Y)> points, double radius, bool closed = false)
  {
    if (points.Count == 0) return;
    var s = points.Select(p => ToSheet(p.X, p.Y)).ToArray();
    _hard.Add(new Hard(s, closed && s.Length >= 3, Math.Max(0, radius), BoundsOf(s, Math.Max(0, radius))));
  }

  /// <summary>A reserved area for the soft score (Placerare.reservera), e.g. a pipe buffered by 0.35: overlapping it
  /// costs 100 + 50 x area, a leader through it 20.</summary>
  public void Reserve(IReadOnlyList<(double X, double Y)> convexPolygon)
  {
    var s = convexPolygon.Select(p => ToSheet(p.X, p.Y)).ToArray();
    if (s.Length >= 3) _placed.Add(new Area(s, BoundsOf(s)));
  }

  /// <summary>A line buffered by radius as a convex polygon per segment (round caps, 16 points per quarter as shapely),
  /// reserved for the soft score.</summary>
  public void ReserveBuffered(IReadOnlyList<(double X, double Y)> points, double radius)
  {
    for (var i = 0; i + 1 < points.Count; i++)
    {
      var a = ToSheet(points[i].X, points[i].Y);
      var b = ToSheet(points[i + 1].X, points[i + 1].Y);
      var poly = Capsule(a, b, radius);
      _placed.Add(new Area(poly, BoundsOf(poly)));
    }
  }

  private static Pt[] Capsule(Pt a, Pt b, double r, int quad = 16)
  {
    var ang = Math.Atan2(b.Y - a.Y, b.X - a.X);
    var list = new List<Pt>();
    // around b from +90 to -90 degrees (relative to the direction), then around a from -90 to -270
    for (var k = 0; k <= 2 * quad; k++)
    {
      var t = ang + Math.PI / 2 - k * Math.PI / (2 * quad);
      list.Add(new Pt(b.X + r * Math.Cos(t), b.Y + r * Math.Sin(t)));
    }
    for (var k = 0; k <= 2 * quad; k++)
    {
      var t = ang - Math.PI / 2 - k * Math.PI / (2 * quad);
      list.Add(new Pt(a.X + r * Math.Cos(t), a.Y + r * Math.Sin(t)));
    }
    list.Reverse();   // counterclockwise
    return list.ToArray();
  }

  /// <summary>A flag that is already there (a hand-moved one kept): its box and leader count as placed.</summary>
  public void AddPlacedFlag(double anchorX, double anchorY, double textX, double textY, string text, double h, string attach = "ML")
  {
    var a = ToSheet(anchorX, anchorY);
    var t = ToSheet(textX, textY);
    var (w, hh) = TextSize(text, h);
    var box = FlagBox(t, w, hh, attach, h);
    _placed.Add(new Area(box.Corners, box));
    _flagBoxes.Add(box);
    _leaders.Add((a, t));
  }

  // ---------- text ----------

  private static readonly Regex Codes = new(@"\\[ACcFfHLlOoQTWpqKkSs][^;\\{}]*;|\\[LlOoKk]|[{}]", RegexOptions.Compiled);

  /// <summary>The MText's visible lines (\P and newline split, formatting codes removed).</summary>
  public static string[] Lines(string text) => text.Replace("\\P", "\n").Replace("\r\n", "\n").Split('\n').Select(l => Codes.Replace(l, "")).ToArray();

  /// <summary>Width and height of a text in model units (as the original Python text layout measures it).</summary>
  public static (double W, double H) TextSize(string text, double h)
  {
    var lines = Lines(text);
    return (lines.Max(l => l.Length) * h * CharWidth, h + (lines.Length - 1) * h * LineSpacing);
  }

  /// <summary>The flag's box in the sheet frame (Placerare._ruta): a multileader's text attaches at the middle of its
  /// top line (h1), so a flag of several lines grows downwards; frame and air half a line height around it.</summary>
  public static Box FlagBox(Pt t, double w, double hh, string attach, double h1)
  {
    var pad = 0.5 * h1;
    double top = t.Y + h1 / 2 + pad, bottom = t.Y + h1 / 2 - hh - pad;
    return attach == "MR" ? new Box(t.X - w - pad, bottom, t.X + pad, top) : new Box(t.X - pad, bottom, t.X + w + pad, top);
  }

  // ---------- geometry ----------

  /// <summary>Liang-Barsky: the parameter range of segment a-b inside the closed box, or null.</summary>
  private static (double T0, double T1)? Clip(Pt a, Pt b, Box box)
  {
    double t0 = 0, t1 = 1, dx = b.X - a.X, dy = b.Y - a.Y;
    bool Edge(double p, double q)
    {
      if (Math.Abs(p) < 1e-15) return q >= 0;
      var r = q / p;
      if (p < 0) { if (r > t1) return false; if (r > t0) t0 = r; }
      else { if (r < t0) return false; if (r < t1) t1 = r; }
      return true;
    }
    if (Edge(-dx, a.X - box.MinX) && Edge(dx, box.MaxX - a.X) && Edge(-dy, a.Y - box.MinY) && Edge(dy, box.MaxY - a.Y)) return (t0, t1);
    return null;
  }

  private static double Len(Pt a, Pt b) => Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));

  private static double PointBox(Pt p, Box b)
  {
    var dx = Math.Max(Math.Max(b.MinX - p.X, 0), p.X - b.MaxX);
    var dy = Math.Max(Math.Max(b.MinY - p.Y, 0), p.Y - b.MaxY);
    return Math.Sqrt(dx * dx + dy * dy);
  }

  private static double PointSeg(Pt p, Pt a, Pt b)
  {
    double dx = b.X - a.X, dy = b.Y - a.Y, l2 = dx * dx + dy * dy;
    var t = l2 < 1e-24 ? 0 : Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / l2, 0, 1);
    return Len(p, new Pt(a.X + t * dx, a.Y + t * dy));
  }

  private static double SegBox(Pt a, Pt b, Box box)
  {
    if (Clip(a, b, box) != null) return 0;
    var d = Math.Min(PointBox(a, box), PointBox(b, box));
    foreach (var c in box.Corners) d = Math.Min(d, PointSeg(c, a, b));
    return d;
  }

  private static bool InPolygon(Pt[] poly, Pt p)
  {
    var inside = false;
    for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
      if ((poly[i].Y > p.Y) != (poly[j].Y > p.Y) && p.X < (poly[j].X - poly[i].X) * (p.Y - poly[i].Y) / (poly[j].Y - poly[i].Y) + poly[i].X) inside = !inside;
    return inside;
  }

  private static double HardDistance(Hard h, Box box)
  {
    if (h.Points.Length == 1) return PointBox(h.Points[0], box);
    var d = double.MaxValue;
    var n = h.Closed ? h.Points.Length : h.Points.Length - 1;
    for (var i = 0; i < n; i++) d = Math.Min(d, SegBox(h.Points[i], h.Points[(i + 1) % h.Points.Length], box));
    if (h.Closed && d > 0 && InPolygon(h.Points, new Pt((box.MinX + box.MaxX) / 2, (box.MinY + box.MaxY) / 2))) return 0;
    return d;
  }

  /// <summary>Segments a-b and c-d cross: the leader (a-b) in its interior, the obstacle (c-d) in its interior or at
  /// an inner vertex of its polyline (shapely crosses on the whole line).</summary>
  private static bool Crosses(Pt a, Pt b, Pt c, Pt d, bool cStrict = true, bool dStrict = true)
  {
    double rx = b.X - a.X, ry = b.Y - a.Y, sx = d.X - c.X, sy = d.Y - c.Y;
    var den = rx * sy - ry * sx;
    if (Math.Abs(den) < 1e-15) return false;
    double qx = c.X - a.X, qy = c.Y - a.Y;
    var t = (qx * sy - qy * sx) / den;
    var u = (qx * ry - qy * rx) / den;
    // 1e-6 m from the ends: an anchor on the line touches it (its coordinates are rounded off the line), not a crossing
    var e = 1e-6 / Math.Max(Math.Sqrt(rx * rx + ry * ry), 1e-9);
    var f = 1e-6 / Math.Max(Math.Sqrt(sx * sx + sy * sy), 1e-9);
    if (!(t > e && t < 1 - e)) return false;
    var u0 = cStrict ? f : -f;
    var u1 = dStrict ? 1 - f : 1 + f;
    return u > u0 && u < u1;
  }

  /// <summary>Sutherland-Hodgman: the convex polygon clipped to the box.</summary>
  private static List<Pt> ClipPoly(Pt[] poly, Box b)
  {
    var list = poly.ToList();
    foreach (var (inside, cut) in new (Func<Pt, bool>, Func<Pt, Pt, Pt>)[]
    {
      (p => p.X >= b.MinX, (p, q) => new Pt(b.MinX, p.Y + (q.Y - p.Y) * (b.MinX - p.X) / (q.X - p.X))),
      (p => p.X <= b.MaxX, (p, q) => new Pt(b.MaxX, p.Y + (q.Y - p.Y) * (b.MaxX - p.X) / (q.X - p.X))),
      (p => p.Y >= b.MinY, (p, q) => new Pt(p.X + (q.X - p.X) * (b.MinY - p.Y) / (q.Y - p.Y), b.MinY)),
      (p => p.Y <= b.MaxY, (p, q) => new Pt(p.X + (q.X - p.X) * (b.MaxY - p.Y) / (q.Y - p.Y), b.MaxY)),
    })
    {
      if (list.Count == 0) break;
      var outList = new List<Pt>();
      for (var i = 0; i < list.Count; i++)
      {
        Pt cur = list[i], prev = list[(i + list.Count - 1) % list.Count];
        bool ci = inside(cur), pi = inside(prev);
        if (ci) { if (!pi) outList.Add(cut(prev, cur)); outList.Add(cur); }
        else if (pi) outList.Add(cut(prev, cur));
      }
      list = outList;
    }
    return list;
  }

  private static double PolyArea(List<Pt> p)
  {
    var a = 0.0;
    for (var i = 0; i < p.Count; i++) { var q = p[(i + 1) % p.Count]; a += p[i].X * q.Y - q.X * p[i].Y; }
    return Math.Abs(a) / 2;
  }

  /// <summary>Convex polygon and box share a point (separating axis test, closed).</summary>
  private static bool Touches(Pt[] poly, Box polyBounds, Box b)
  {
    if (!polyBounds.Touches(b)) return false;
    var corners = b.Corners;
    for (var i = 0; i < poly.Length; i++)
    {
      Pt p = poly[i], q = poly[(i + 1) % poly.Length];
      double nx = q.Y - p.Y, ny = p.X - q.X;
      var pmin = double.MaxValue; var pmax = double.MinValue;
      foreach (var v in poly) { var s = v.X * nx + v.Y * ny; pmin = Math.Min(pmin, s); pmax = Math.Max(pmax, s); }
      var bmin = double.MaxValue; var bmax = double.MinValue;
      foreach (var v in corners) { var s = v.X * nx + v.Y * ny; bmin = Math.Min(bmin, s); bmax = Math.Max(bmax, s); }
      var tol = 1e-12 * (Math.Abs(nx) + Math.Abs(ny));
      if (pmax < bmin - tol || bmax < pmin - tol) return false;
    }
    return true;
  }

  private static bool SegTouchesPoly(Pt a, Pt b, Pt[] poly, Box bounds)
  {
    if (!BoundsOf(new[] { a, b }).Touches(bounds)) return false;
    if (InPolygon(poly, a) || InPolygon(poly, b)) return true;
    for (var i = 0; i < poly.Length; i++)
      if (SegSegTouch(a, b, poly[i], poly[(i + 1) % poly.Length])) return true;
    return false;
  }

  private static bool SegSegTouch(Pt a, Pt b, Pt c, Pt d)
  {
    static double Cross(Pt o, Pt p, Pt q) => (p.X - o.X) * (q.Y - o.Y) - (p.Y - o.Y) * (q.X - o.X);
    double d1 = Cross(c, d, a), d2 = Cross(c, d, b), d3 = Cross(a, b, c), d4 = Cross(a, b, d);
    if (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0))) return true;
    bool On(Pt o, Pt p, Pt q) => Math.Min(o.X, p.X) - 1e-12 <= q.X && q.X <= Math.Max(o.X, p.X) + 1e-12 && Math.Min(o.Y, p.Y) - 1e-12 <= q.Y && q.Y <= Math.Max(o.Y, p.Y) + 1e-12;
    return (Math.Abs(d1) < 1e-15 && On(c, d, a)) || (Math.Abs(d2) < 1e-15 && On(c, d, b)) || (Math.Abs(d3) < 1e-15 && On(a, b, c)) || (Math.Abs(d4) < 1e-15 && On(a, b, d));
  }

  /// <summary>The box lies inside the drawing area shrunk by the margin (rityta.buffer(-marginal).contains).</summary>
  private bool InsideDrawing(Box b)
  {
    foreach (var c in b.Corners) if (!InPolygon(_drawing, c)) return false;
    for (var i = 0; i < _drawing.Length; i++)
      if (SegBox(_drawing[i], _drawing[(i + 1) % _drawing.Length], b) < _margin - 1e-12) return false;
    return true;
  }

  /// <summary>Whether a point lies inside the drawing area shrunk by inset (anchors outside are not flagged).</summary>
  public bool InsideArea(double x, double y, double inset)
  {
    var p = ToSheet(x, y);
    if (!InPolygon(_drawing, p)) return false;
    for (var i = 0; i < _drawing.Length; i++)
      if (PointSeg(p, _drawing[i], _drawing[(i + 1) % _drawing.Length]) < inset) return false;
    return true;
  }

  // ---------- scoring ----------

  /// <summary>Placerare.poang: the soft score of a box and its leader.</summary>
  private double Score(Box box, Pt la, Pt lb, bool withLeader)
  {
    var p = 0.0;
    foreach (var s in _soft)
    {
      if (!s.Bounds.Touches(box)) continue;
      if (Clip(s.A, s.B, box) is { } r && r.T1 > r.T0) p += s.Weight * (r.T1 - r.T0) * Len(s.A, s.B);
    }
    if (withLeader)
    {
      var ll = Len(la, lb);
      if (ll > 0)
      {
        var lbounds = BoundsOf(new[] { la, lb });
        var crossed = new HashSet<int>();
        foreach (var s in _soft)
          if (!crossed.Contains(s.Group) && s.Bounds.Touches(lbounds) && Crosses(la, lb, s.A, s.B, s.StartIsEnd, s.EndIsEnd)) crossed.Add(s.Group);
        p += 2.0 * crossed.Count;
        p += 0.05 * ll;
        foreach (var a in _placed)
          if (SegTouchesPoly(la, lb, a.Poly, a.Bounds)) p += 20.0;
      }
    }
    foreach (var a in _placed)
      if (Touches(a.Poly, a.Bounds, box)) p += 100.0 + 50.0 * PolyArea(ClipPoly(a.Poly, box));
    if (!InsideDrawing(box)) p += 1000.0;
    return p;
  }

  /// <summary>Fritt.ok: the box is free of the hard obstacles, of the flags' boxes and leaders, and the leader runs
  /// through no flag's box.</summary>
  private bool Free(Box box, Pt la, Pt lb)
  {
    foreach (var h in _hard)
      if (h.Bounds.Touches(box) && HardDistance(h, box) <= h.Radius) return false;
    foreach (var r in _flagBoxes) if (r.Touches(box)) return false;
    foreach (var (a, b) in _leaders) if (Clip(a, b, box) != null) return false;
    foreach (var r in _flagBoxes) if (Clip(la, lb, r) != null) return false;
    return true;
  }

  private int LeaderCrossings(Pt la, Pt lb) => _leaders.Count(l => Crosses(la, lb, l.A, l.B));

  /// <summary>The right-hand directions of plan_flaggor (2, 12, ... degrees with the text to the right of the leader).</summary>
  public static double[] RightHand() => Enumerable.Range(0, 36).Select(i => 2.0 + 10 * i).Where(a => Math.Cos(a * Math.PI / 180) > 0).ToArray();

  /// <summary>Places a flag with its arrow at (x, y): the best free candidate (placera), else the best soft one
  /// (Placerare.flagga) marked not free. The box and leader are kept for the following flags.</summary>
  public Placement Place(double x, double y, string text, double h, IReadOnlyList<double>? directions = null,
    IReadOnlyList<double>? first = null, IReadOnlyList<double>? wide = null, IReadOnlyList<double>? soft = null)
  {
    var anchor = ToSheet(x, y);
    var (w, hh) = TextSize(text, h);
    var right = RightHand();
    var dirs = directions is { Count: > 0 } ? directions : right;
    var n = 0;
    foreach (var (ds, angles) in new[] { (first ?? FirstDistances, dirs), (wide ?? WideDistances, (IReadOnlyList<double>)right) })
    {
      (double P, Pt T, Box B, double D, double A)? best = null;
      foreach (var d0 in ds)
      {
        var d = d0 * h;
        foreach (var a in angles)
        {
          n++;
          var t = new Pt(anchor.X + d * Math.Cos(a * Math.PI / 180), anchor.Y + d * Math.Sin(a * Math.PI / 180));
          var box = FlagBox(t, w, hh, "ML", h);
          if (!Free(box, anchor, t)) continue;
          var p = Score(box, anchor, t, true) + 5.0 * LeaderCrossings(anchor, t) + 0.2 * d;
          if (best == null || p < best.Value.P - 1e-9) best = (p, t, box, d, a);   // ties: the first candidate
        }
      }
      if (best is { } b)
      {
        Keep(b.B, anchor, b.T);
        var (mx, my) = ToModel(b.T);
        return new Placement(mx, my, b.P, true, "ML", b.D, b.A, b.B.Corners.Select(c => { var (cx, cy) = ToModel(c); return new Pt(cx, cy); }).ToArray(), n);
      }
    }
    // no free spot: the soft placement (Placerare.flagga with the item's directions)
    (double P, Pt T, Box B, double D, double A, string Attach)? s = null;
    foreach (var d0 in soft ?? SoftDistances)
    {
      var d = d0 * h;
      foreach (var a in dirs)
      {
        n++;
        double ax = d * Math.Cos(a * Math.PI / 180), ay = d * Math.Sin(a * Math.PI / 180);
        var t = new Pt(anchor.X + ax, anchor.Y + ay);
        var attach = ax >= 0 ? "ML" : "MR";
        var box = FlagBox(t, w, hh, attach, h);
        var p = Score(box, anchor, t, true);
        if (s == null || p < s.Value.P - 1e-9) s = (p, t, box, d, a, attach);
      }
    }
    var sb = s!.Value;
    Keep(sb.B, anchor, sb.T);
    var (sx, sy) = ToModel(sb.T);
    return new Placement(sx, sy, sb.P, false, sb.Attach, sb.D, sb.A, sb.B.Corners.Select(c => { var (cx, cy) = ToModel(c); return new Pt(cx, cy); }).ToArray(), n);
  }

  private void Keep(Box box, Pt anchor, Pt t)
  {
    _placed.Add(new Area(box.Corners, box));
    _flagBoxes.Add(box);
    _leaders.Add((anchor, t));
  }
}
