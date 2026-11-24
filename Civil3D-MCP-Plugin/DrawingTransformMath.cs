namespace Civil3DMcpPlugin;

/// <summary>
/// C94 (2026-10-10): the matrices drawingTransform applies, without AutoCAD types (checked by
/// tools/drawingtransformmath_check). In the plane either a similarity (2D Helmert) {a, b, tx, ty}:
/// x' = a·x − b·y + tx, y' = b·x + a·y + ty (model X = east, model Y = north), or a full affine
/// {m11, m12, m13, m21, m22, m23}: x' = m11·x + m12·y + m13, y' = m21·x + m22·y + m23; heights z' = zScale·z + zOffset.
/// Matrices are row-major 4x4 arrays as Matrix3d(double[]) takes them (translation in the last column).
/// Use case: a municipality's base map is in SWEREF 99 15 00 and a pilot project in SWEREF 99 13 30; the caller fits
/// the parameters over the project area (pyproj), the plugin computes no projections.
/// </summary>
internal static class DrawingTransformMath
{
  /// <summary>The plane part: x' = M11·x + M12·y + M13, y' = M21·x + M22·y + M23.</summary>
  internal readonly record struct Plane(double M11, double M12, double M13, double M21, double M22, double M23);

  internal readonly record struct Shape(double ScaleX, double ScaleY, double RotationDeg, double ShearDeg, double Determinant, bool Similarity);

  // Relative tolerance for "the same scale" and "perpendicular": AutoCAD refuses a non-uniform matrix on circles, arcs,
  // texts and the like at this level (Matrix3d.IsUniscaledOrtho with the global tolerance), so equal means equal.
  private const double Relative = 1e-12;

  internal static Plane Helmert(double a, double b, double tx, double ty) => new(a, -b, tx, b, a, ty);

  internal static Plane Affine(double m11, double m12, double m13, double m21, double m22, double m23) => new(m11, m12, m13, m21, m22, m23);

  /// <summary>Why the parameters cannot be used, null when they can.</summary>
  internal static string? Validate(Plane p, double zScale, double zOffset)
  {
    double[] values = [p.M11, p.M12, p.M13, p.M21, p.M22, p.M23];
    if (values.Any(v => !double.IsFinite(v))) return "transform values must be finite numbers.";
    if (!double.IsFinite(zScale) || zScale <= 0) return "z.scale must be a positive number.";
    if (!double.IsFinite(zOffset)) return "z.offset must be a finite number.";
    var shape = Describe(p);
    if (!(shape.Determinant > 0)) return $"the transform mirrors or collapses the drawing (determinant {shape.Determinant:G6}); a transformation between map projections never does.";
    return null;
  }

  internal static double[] Matrix(Plane p, double zScale, double zOffset) =>
  [
    p.M11, p.M12, 0, p.M13,
    p.M21, p.M22, 0, p.M23,
    0, 0, zScale, zOffset,
    0, 0, 0, 1,
  ];

  /// <summary>Scales of the X and Y axes, rotation of the X axis (degrees, counter-clockwise), shear (how far the
  /// images of the axes are from perpendicular, degrees), determinant, and whether the plane part is a similarity.</summary>
  internal static Shape Describe(Plane p)
  {
    var scaleX = Math.Sqrt(p.M11 * p.M11 + p.M21 * p.M21);
    var scaleY = Math.Sqrt(p.M12 * p.M12 + p.M22 * p.M22);
    var dot = p.M11 * p.M12 + p.M21 * p.M22;
    var determinant = p.M11 * p.M22 - p.M12 * p.M21;
    var rotation = Math.Atan2(p.M21, p.M11) * 180 / Math.PI;
    var cosine = scaleX > 0 && scaleY > 0 ? Math.Clamp(dot / (scaleX * scaleY), -1, 1) : 0;
    var shear = 90 - Math.Acos(cosine) * 180 / Math.PI;
    var similarity = scaleX > 0
      && Math.Abs(scaleX - scaleY) <= Relative * scaleX
      && Math.Abs(dot) <= Relative * scaleX * scaleY
      && determinant > 0;
    return new Shape(scaleX, scaleY, rotation, shear, determinant, similarity);
  }

  /// <summary>Whether the whole 3D matrix scales uniformly (plane part a similarity, z scaled like the plane), so every
  /// entity takes it as it is.</summary>
  internal static bool UniformIn3d(Plane p, double zScale)
  {
    var shape = Describe(p);
    return shape.Similarity && Math.Abs(zScale - shape.ScaleX) <= Relative * shape.ScaleX;
  }

  /// <summary>
  /// The matrix for an entity whose points lie at height <paramref name="elevation"/> when the 3D matrix is not
  /// uniform (heights kept while the plane scale k is not 1): z scaled by k like the plane, then moved so that the
  /// entity lands at zScale·elevation + zOffset, exactly where the full matrix puts it. Circles, arcs, texts and hatches
  /// refuse a non-uniform matrix; this one is uniform. Null when the plane part is not a similarity.
  /// </summary>
  internal static double[]? FlatMatrix(Plane p, double zScale, double zOffset, double elevation)
  {
    var shape = Describe(p);
    if (!shape.Similarity) return null;
    var k = shape.ScaleX;
    return
    [
      p.M11, p.M12, 0, p.M13,
      p.M21, p.M22, 0, p.M23,
      0, 0, k, (zScale - k) * elevation + zOffset,
      0, 0, 0, 1,
    ];
  }

  /// <summary>How far FlatMatrix at the middle height puts a point of an entity spanning minZ..maxZ from where the full
  /// matrix puts it: |zScale − k| · (maxZ − minZ) / 2 (plane coordinates are the same in both).</summary>
  internal static double FlatDeviation(Plane p, double zScale, double minZ, double maxZ) =>
    Math.Abs(zScale - Describe(p).ScaleX) * Math.Abs(maxZ - minZ) / 2;

  internal static (double X, double Y, double Z) Apply(double[] m, double x, double y, double z) =>
  (
    m[0] * x + m[1] * y + m[2] * z + m[3],
    m[4] * x + m[5] * y + m[6] * z + m[7],
    m[8] * x + m[9] * y + m[10] * z + m[11]
  );
}
