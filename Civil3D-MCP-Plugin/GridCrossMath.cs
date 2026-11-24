using System.Globalization;

namespace Civil3DMcpPlugin;

/// <summary>
/// Pure math for gridCrossesAdd (C89): the paper ↔ model transform of a plan-view layout viewport, the grid points
/// (multiples of the interval) that fall inside it, which crosses carry labels with edgeLabels, and where a label goes.
/// Free of AutoCAD types so it can be verified outside AutoCAD (tools/gridcrossmath_check).
/// The transform is the one ViewportCommands works with (C52/C75): DCS = R(twist)·(model − ViewTarget) and
/// paper = CenterPoint + CustomScale·(DCS − ViewCenter), so a model direction at angle a is drawn on the sheet at
/// a + twist and the paper centre shows ViewTarget + R(−twist)·ViewCenter (ViewportCommands.ModelCenter).
/// </summary>
public static class GridCrossMath
{
  /// <summary>A plan-view viewport: centre and size on the paper, view target (model), view centre (DCS), paper units
  /// per model unit (CustomScale) and twist (radians).</summary>
  public readonly record struct Frame(double CenterX, double CenterY, double Width, double Height,
    double TargetX, double TargetY, double ViewCenterX, double ViewCenterY, double Scale, double Twist);

  /// <summary>A grid point inside the viewport: column = easting / interval, row = northing / interval.</summary>
  public sealed record Cross(long Column, long Row, double East, double North, double PaperX, double PaperY);

  /// <summary>Where a label goes: its anchor, readable rotation and whether it is flipped (anchored top right instead of bottom left).</summary>
  public readonly record struct LabelPlacement(double X, double Y, double Rotation, bool Flipped);

  public static (double X, double Y) ModelToPaper(Frame f, double x, double y)
  {
    var dx = x - f.TargetX;
    var dy = y - f.TargetY;
    var cos = Math.Cos(f.Twist);
    var sin = Math.Sin(f.Twist);
    return (f.CenterX + f.Scale * (cos * dx - sin * dy - f.ViewCenterX), f.CenterY + f.Scale * (sin * dx + cos * dy - f.ViewCenterY));
  }

  public static (double X, double Y) PaperToModel(Frame f, double x, double y)
  {
    var dcsX = (x - f.CenterX) / f.Scale + f.ViewCenterX;
    var dcsY = (y - f.CenterY) / f.Scale + f.ViewCenterY;
    var cos = Math.Cos(f.Twist);
    var sin = Math.Sin(f.Twist);
    return (f.TargetX + cos * dcsX + sin * dcsY, f.TargetY - sin * dcsX + cos * dcsY);
  }

  /// <summary>The model area the viewport shows, as the bounding box of its four corners.</summary>
  public static (double MinX, double MinY, double MaxX, double MaxY) ModelBounds(Frame f)
  {
    var corners = new[]
    {
      PaperToModel(f, f.CenterX - f.Width / 2, f.CenterY - f.Height / 2), PaperToModel(f, f.CenterX + f.Width / 2, f.CenterY - f.Height / 2),
      PaperToModel(f, f.CenterX + f.Width / 2, f.CenterY + f.Height / 2), PaperToModel(f, f.CenterX - f.Width / 2, f.CenterY + f.Height / 2),
    };
    return (corners.Min(c => c.X), corners.Min(c => c.Y), corners.Max(c => c.X), corners.Max(c => c.Y));
  }

  /// <summary>A paper point at least <paramref name="margin"/> inside the viewport's edge.</summary>
  public static bool Inside(Frame f, double x, double y, double margin) =>
    Math.Abs(x - f.CenterX) <= f.Width / 2 - margin + 1e-9 && Math.Abs(y - f.CenterY) <= f.Height / 2 - margin + 1e-9;

  /// <summary>How far a cross of arm length <paramref name="size"/>, turned with the grid, reaches from its centre along
  /// the paper's x and y: the whole cross stays inside the viewport.</summary>
  public static double CrossMargin(double size, double twist) => size / 2 * Math.Max(Math.Abs(Math.Cos(twist)), Math.Abs(Math.Sin(twist)));

  /// <summary>Every grid point (easting and northing multiples of <paramref name="interval"/>) whose cross fits inside
  /// the viewport. Throws InvalidOperationException when more than <paramref name="maxCandidates"/> points would be tested.</summary>
  public static List<Cross> Crosses(Frame f, double interval, double margin, long maxCandidates)
  {
    if (!(interval > 0)) throw new ArgumentOutOfRangeException(nameof(interval), "interval must be > 0");
    var (minX, minY, maxX, maxY) = ModelBounds(f);
    var firstColumn = (long)Math.Ceiling(minX / interval - 1e-9);
    var lastColumn = (long)Math.Floor(maxX / interval + 1e-9);
    var firstRow = (long)Math.Ceiling(minY / interval - 1e-9);
    var lastRow = (long)Math.Floor(maxY / interval + 1e-9);
    var candidates = lastColumn < firstColumn || lastRow < firstRow ? 0.0 : (double)(lastColumn - firstColumn + 1) * (lastRow - firstRow + 1);
    if (candidates > maxCandidates)
      throw new InvalidOperationException($"{candidates:0} grid points would be tested (at most {maxCandidates}); the interval is too small for this viewport");

    var crosses = new List<Cross>();
    for (var column = firstColumn; column <= lastColumn; column++)
    {
      for (var row = firstRow; row <= lastRow; row++)
      {
        var east = column * interval;
        var north = row * interval;
        var (x, y) = ModelToPaper(f, east, north);
        if (Inside(f, x, y, margin)) crosses.Add(new Cross(column, row, east, north, x, y));
      }
    }
    return crosses;
  }

  /// <summary>edgeLabels: the northing on the first cross of each row (smallest easting), the easting on the lowest
  /// cross of each column (smallest northing); both labels then run from the edge into the viewport.</summary>
  public static (HashSet<(long Column, long Row)> North, HashSet<(long Column, long Row)> East) EdgeLabelled(IEnumerable<Cross> crosses)
  {
    var list = crosses.ToList();
    var north = list.GroupBy(c => c.Row).Select(g => g.MinBy(c => c.Column)!).Select(c => (c.Column, c.Row)).ToHashSet();
    var east = list.GroupBy(c => c.Column).Select(g => g.MinBy(c => c.Row)!).Select(c => (c.Column, c.Row)).ToHashSet();
    return (north, east);
  }

  /// <summary>A label that runs along a cross arm, outward from the cross at <paramref name="direction"/> (radians), on the
  /// arm's counterclockwise side, <paramref name="gap"/> from the centre and from the arm. The rotation is kept readable
  /// (−90°, 90°]; a flipped label reads toward the cross and is anchored at its top right instead of its bottom left, so
  /// it covers the same place.</summary>
  public static LabelPlacement PlaceLabel(double x, double y, double direction, double gap)
  {
    var dx = Math.Cos(direction);
    var dy = Math.Sin(direction);
    var rotation = Readable(direction, out var flipped);
    return new LabelPlacement(x + gap * (dx - dy), y + gap * (dy + dx), rotation, flipped);
  }

  /// <summary>The angle turned by 180° when it would put text upside down: the result is in (−90°, 90°].</summary>
  public static double Readable(double angle, out bool flipped)
  {
    const double tolerance = 1e-9;
    var a = Normalize(angle);
    flipped = a <= -Math.PI / 2 + tolerance || a > Math.PI / 2 + tolerance;
    return flipped ? Normalize(a + Math.PI) : a;
  }

  /// <summary>An angle in (−π, π].</summary>
  public static double Normalize(double angle)
  {
    var a = Math.IEEERemainder(angle, 2 * Math.PI);
    return a <= -Math.PI ? a + 2 * Math.PI : a;
  }

  /// <summary>A coordinate for a label: whole metres without decimals, otherwise up to three with a decimal comma.</summary>
  public static string Format(double value)
  {
    var rounded = Math.Round(value, 3);
    if (rounded == 0) rounded = 0;                                      // no "-0"
    return rounded.ToString("0.###", CultureInfo.InvariantCulture).Replace('.', ',');
  }
}
