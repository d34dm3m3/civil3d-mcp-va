using System.Globalization;

namespace Civil3DMcpPlugin;

/// <summary>
/// Pure math for structure_bend_angles (C92), without AutoCAD types so that tools/bendanglemath_check can run it
/// outside Civil 3D. A pipe at a structure is described by its "away" vector: from the pipe's end at the structure to
/// its far end (x, y in plan, z the rise). For a curved pipe the plan part follows the arc's tangent at the structure
/// end, with the arc length as its length.
/// Hv (horizontal angle): the deviation from straight through in plan, 180° minus the plan angle between the two away
/// vectors, 0-180°. Vv (vertical angle): the difference between the pipes' inclinations travelled through the structure
/// (in along one pipe, out along the other), |atan(zb/hb) + atan(za/ha)|, symmetric in the two pipes.
/// </summary>
internal static class BendAngleMath
{
  internal readonly record struct Vec(double X, double Y, double Z);

  /// <summary>Hv and Vv in degrees, and each pipe's inclination along the travel direction (a in, b out), degrees.</summary>
  internal readonly record struct Bend(double HorizontalDeg, double VerticalDeg, double InclinationInDeg, double InclinationOutDeg);

  private const double Degrees = 180.0 / Math.PI;

  /// <summary>Null when a pipe has no plan length (vertical), so no plan direction.</summary>
  internal static Bend? Compute(Vec a, Vec b, double minPlanLength = 1e-6)
  {
    var ha = Math.Sqrt(a.X * a.X + a.Y * a.Y);
    var hb = Math.Sqrt(b.X * b.X + b.Y * b.Y);
    if (!(ha >= minPlanLength) || !(hb >= minPlanLength)) return null;
    var cos = Math.Clamp((a.X * b.X + a.Y * b.Y) / (ha * hb), -1.0, 1.0);
    var between = Math.Acos(cos) * Degrees;
    var inA = Math.Atan(-a.Z / ha) * Degrees;                          // travelling along pipe a toward the structure
    var outB = Math.Atan(b.Z / hb) * Degrees;                          // travelling along pipe b away from it
    return new Bend(180.0 - between, Math.Abs(outB - inA), inA, outB);
  }

  /// <summary>The away vector of a curved pipe: plan direction = the arc's tangent at the structure end (p), turned toward
  /// the far end (o), scaled to the arc length; z = the rise to the far end. Null when p lies on the centre.</summary>
  internal static Vec? ArcAway(double px, double py, double cx, double cy, double ox, double oy, double arcLength, double rise)
  {
    double rx = px - cx, ry = py - cy;
    var r = Math.Sqrt(rx * rx + ry * ry);
    if (!(r > 1e-9)) return null;
    double tx = -ry / r, ty = rx / r;
    if (tx * (ox - px) + ty * (oy - py) < 0) { tx = -tx; ty = -ty; }
    return new Vec(tx * arcLength, ty * arcLength, rise);
  }

  /// <summary>Whole degrees by default, otherwise with a decimal comma (Swedish drawings), rounded half away from zero.</summary>
  internal static string Format(double value, int decimals) =>
    Math.Round(value, decimals, MidpointRounding.AwayFromZero).ToString("F" + decimals, CultureInfo.InvariantCulture).Replace('.', ',');

  /// <summary>"Hv 30°", "Vv 5°" or "Hv 30° Vv 5°": each only when its angle reaches its minimum; "" when neither does.</summary>
  internal static string Text(double hv, double vv, int decimals, double minHv, double minVv)
  {
    var parts = new List<string>();
    if (hv >= minHv) parts.Add($"Hv {Format(hv, decimals)}°");
    if (vv >= minVv) parts.Add($"Vv {Format(vv, decimals)}°");
    return string.Join(" ", parts);
  }

  /// <summary>The structure's new description: the text alone; or, with keepExisting or when there is no text (both
  /// angles below their minimums, or the structure skipped), the existing description without earlier Hv/Vv segments
  /// (" | "-separated segments starting with "Hv " or "Vv ") and the text, if any, after " | ". So a straight-through
  /// structure loses only a stale Hv/Vv text, never a description of its own.</summary>
  internal static string NewDescription(string? existing, string text, bool keepExisting)
  {
    if (!keepExisting && text.Length > 0) return text;
    var kept = (existing ?? "").Split(" | ")
      .Where(s => s.Trim().Length > 0 && !s.TrimStart().StartsWith("Hv ", StringComparison.Ordinal) && !s.TrimStart().StartsWith("Vv ", StringComparison.Ordinal))
      .ToList();
    if (text.Length > 0) kept.Add(text);
    return string.Join(" | ", kept);
  }
}
