using Civil3DMcpPlugin;
using M = Civil3DMcpPlugin.DrawingTransformMath;

// Standalone check of the matrices used by drawingTransform (C94).
// Run: dotnet build -o out && dotnet out/drawingtransformmath_check.dll
var failures = 0;
void Check(string name, bool ok, string detail = "")
{
  if (!ok) failures++;
  Console.WriteLine($"{(ok ? "OK  " : "FAIL")} {name} {detail}");
}
bool Near(double a, double b, double tolerance = 1e-9) => Math.Abs(a - b) <= tolerance;
bool NearPoint((double X, double Y, double Z) p, double x, double y, double z, double tolerance = 1e-9) =>
  Near(p.X, x, tolerance) && Near(p.Y, y, tolerance) && Near(p.Z, z, tolerance);

// Helmert: x' = a·x − b·y + tx, y' = b·x + a·y + ty.
var identity = M.Matrix(M.Helmert(1, 0, 0, 0), 1, 0);
Check("identity Helmert keeps a point", NearPoint(M.Apply(identity, 150000.5, 6230000.25, 87.3), 150000.5, 6230000.25, 87.3));
Check("matrix layout: translation in the last column, z scale and offset in the third row",
  M.Matrix(M.Helmert(1, 0, 11, 22), 2, 33) is var layout && layout[3] == 11 && layout[7] == 22 && layout[10] == 2 && layout[11] == 33 && layout[15] == 1 && layout[2] == 0 && layout[8] == 0);

// SWEREF 99 15 00 -> 13 30 like parameters over a municipality in southern Sweden: scale just above 1, a small rotation, a large shift.
double a = 0.99999437, b = 0.02618213, tx = -163250.12, ty = 2735.48;
var helmert = M.Helmert(a, b, tx, ty);
var matrix = M.Matrix(helmert, 1, 0);
var (x, y, z) = (147512.304, 6236150.718, 61.25);
Check("Helmert follows the formula", NearPoint(M.Apply(matrix, x, y, z), a * x - b * y + tx, b * x + a * y + ty, z, 1e-6), $"{M.Apply(matrix, x, y, z)}");
var quarter = M.Matrix(M.Helmert(0, 1, 0, 0), 1, 0);
Check("b turns counter-clockwise: (1, 0) goes to (0, 1)", NearPoint(M.Apply(quarter, 1, 0, 0), 0, 1, 0));
var shape = M.Describe(helmert);
Check("Helmert scale is sqrt(a^2 + b^2)", Near(shape.ScaleX, Math.Sqrt(a * a + b * b), 1e-15) && Near(shape.ScaleY, shape.ScaleX, 1e-15), $"{shape.ScaleX:R}");
Check("Helmert rotation is atan2(b, a)", Near(shape.RotationDeg, Math.Atan2(b, a) * 180 / Math.PI, 1e-12), $"{shape.RotationDeg:0.000000}");
Check("Helmert is a similarity without shear", shape.Similarity && Near(shape.ShearDeg, 0, 1e-9), $"shear {shape.ShearDeg}");
Check("Helmert determinant is a^2 + b^2", Near(shape.Determinant, a * a + b * b, 1e-15));

// Heights.
var lifted = M.Matrix(M.Helmert(1, 0, 0, 0), 1.5, 10);
Check("z' = scale * z + offset", NearPoint(M.Apply(lifted, 3, 4, 20), 3, 4, 40));

// Affine: x' = m11·x + m12·y + m13, y' = m21·x + m22·y + m23.
var affine = M.Affine(1.0001, 0.0002, 5, -0.0001, 0.9999, -7);
Check("affine follows the formula", NearPoint(M.Apply(M.Matrix(affine, 1, 0), 100, 200, 3), 1.0001 * 100 + 0.0002 * 200 + 5, -0.0001 * 100 + 0.9999 * 200 - 7, 3));
var affineShape = M.Describe(affine);
Check("a general affine is not a similarity", !affineShape.Similarity);
Check("affine axis scales", Near(affineShape.ScaleX, Math.Sqrt(1.0001 * 1.0001 + 0.0001 * 0.0001)) && Near(affineShape.ScaleY, Math.Sqrt(0.0002 * 0.0002 + 0.9999 * 0.9999)));
Check("affine shear from the axes' angle", Near(affineShape.ShearDeg, 90 - Math.Acos((1.0001 * 0.0002 + -0.0001 * 0.9999) / (affineShape.ScaleX * affineShape.ScaleY)) * 180 / Math.PI, 1e-9), $"{affineShape.ShearDeg:0.000000}");
var helmertAsAffine = M.Affine(a, -b, tx, b, a, ty);
Check("an affine written as a Helmert is a similarity", M.Describe(helmertAsAffine).Similarity);
Check("a stretch is not a similarity", !M.Describe(M.Affine(2, 0, 0, 0, 1, 0)).Similarity);

// Validation.
Check("valid parameters pass", M.Validate(helmert, 1, 0) == null);
Check("NaN is refused", M.Validate(M.Helmert(double.NaN, 0, 0, 0), 1, 0) != null);
Check("infinity is refused", M.Validate(M.Affine(1, 0, double.PositiveInfinity, 0, 1, 0), 1, 0) != null);
Check("a mirror is refused", M.Validate(M.Affine(-1, 0, 0, 0, 1, 0), 1, 0) != null);
Check("a collapse is refused", M.Validate(M.Helmert(0, 0, 0, 0), 1, 0) != null);
Check("z.scale 0 is refused", M.Validate(helmert, 0, 0) != null);
Check("negative z.scale is refused", M.Validate(helmert, -1, 0) != null);
Check("non-finite z.offset is refused", M.Validate(helmert, 1, double.NaN) != null);

// Uniform in 3D: every entity takes the matrix as it is.
Check("identity with heights kept is uniform", M.UniformIn3d(M.Helmert(1, 0, 5, 5), 1));
Check("scale above 1 with heights kept is not uniform", !M.UniformIn3d(helmert, 1));
Check("heights scaled like the plane are uniform", M.UniformIn3d(helmert, shape.ScaleX));
Check("an affine is never uniform", !M.UniformIn3d(affine, 1));

// Flat entities: the uniform matrix plus a shift along z lands them where the full matrix does.
var elevation = 61.25;
var flat = M.FlatMatrix(helmert, 1, 0, elevation)!;
foreach (var (px, py) in new[] { (147512.304, 6236150.718), (148000.0, 6235000.0), (0.0, 0.0) })
{
  var full = M.Apply(matrix, px, py, elevation);
  var viaFlat = M.Apply(flat, px, py, elevation);
  Check($"flat route equals the full matrix at ({px}, {py}, {elevation})", NearPoint(viaFlat, full.X, full.Y, full.Z, 1e-7), $"dz {viaFlat.Z - full.Z:E2}");
}
var flatLifted = M.FlatMatrix(helmert, 1.2, -3, 40)!;
var fullLifted = M.Apply(M.Matrix(helmert, 1.2, -3), 10, 20, 40);
Check("flat route honours z scale and offset", NearPoint(M.Apply(flatLifted, 10, 20, 40), fullLifted.X, fullLifted.Y, fullLifted.Z, 1e-9));
double Column(double[] m, int c) => Math.Sqrt(m[c] * m[c] + m[4 + c] * m[4 + c] + m[8 + c] * m[8 + c]);
Check("flat matrix scales all three axes alike", Near(Column(flat, 0), Column(flat, 1), 1e-15) && Near(Column(flat, 2), Column(flat, 0), 1e-15), $"{Column(flat, 0):R} {Column(flat, 2):R}");
Check("no flat route for an affine", M.FlatMatrix(affine, 1, 0, 0) == null);
Check("flat deviation is |zScale - k| times half the height span", Near(M.FlatDeviation(M.Helmert(1.00002, 0, 0, 0), 1, 10, 20), 0.00002 * 5, 1e-15), $"{M.FlatDeviation(M.Helmert(1.00002, 0, 0, 0), 1, 10, 20):E3}");
var spanTop = M.Apply(M.FlatMatrix(M.Helmert(1.00002, 0, 0, 0), 1, 0, 15)!, 0, 0, 20);
Check("flat deviation matches the flat route at the span's edge", Near(Math.Abs(spanTop.Z - 20), M.FlatDeviation(M.Helmert(1.00002, 0, 0, 0), 1, 10, 20), 1e-12), $"{spanTop.Z - 20:E3}");

Console.WriteLine(failures == 0 ? "All checks OK" : $"{failures} check(s) FAILED");
return failures == 0 ? 0 : 1;
