using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace Civil3DMcpPlugin;

/// <summary>
/// C94 (2026-10-10): a copy of a drawing in other plane coordinates. Use case: a municipality's base map is in SWEREF 99 15 00
/// and has to go into a project in SWEREF 99 13 30. The caller fits a
/// 2D similarity (Helmert) or an affine over the project area (pyproj) and passes its parameters; the plugin computes
/// no projections and sets no coordinate system.
/// The source is read into a side database (never opened in the editor), every model space entity is moved with
/// Entity.TransformBy (blocks, texts, hatches, surfaces, external reference insertions alike), and the result is
/// written to a new file in the source's DWG version through a temporary file. Entities that refuse the matrix are
/// listed and stay where they were. Layouts, block definitions and the source file are not touched.
/// Heights: when the 3D matrix is not uniform (heights kept while the plane scale k is not exactly 1), circles, arcs,
/// texts and hatches refuse it; an entity whose points all have one height h is then moved with a uniform matrix
/// (z scaled by k) plus a shift that puts it at z.scale·h + z.offset, exactly where the full matrix would
/// (<see cref="DrawingTransformMath.FlatMatrix"/>). An entity spanning heights that refuses the full matrix gets the
/// same treatment at its middle height when that moves no point more than 0,5 mm in height, reported as approximated.
/// </summary>
public static class DrawingTransformCommands
{
  private const int MaxListed = 200;
  private const double FlatTolerance = 1e-9;          // model units: an entity with this little height span is flat
  private const double MaxApproximationMm = 0.5;      // largest height error of the approximated route

  private static readonly HashSet<DwgVersion> SavableVersions =
  [
    DwgVersion.AC1015, DwgVersion.AC1800, DwgVersion.AC1021, DwgVersion.AC1024, DwgVersion.AC1027, DwgVersion.AC1032,
  ];

  private sealed class Bounds
  {
    private double _minX = double.PositiveInfinity, _minY = double.PositiveInfinity, _minZ = double.PositiveInfinity;
    private double _maxX = double.NegativeInfinity, _maxY = double.NegativeInfinity, _maxZ = double.NegativeInfinity;
    public int Count { get; private set; }

    public void Add(Extents3d? extents)
    {
      if (extents is not { } e) return;
      Count++;
      _minX = Math.Min(_minX, e.MinPoint.X); _minY = Math.Min(_minY, e.MinPoint.Y); _minZ = Math.Min(_minZ, e.MinPoint.Z);
      _maxX = Math.Max(_maxX, e.MaxPoint.X); _maxY = Math.Max(_maxY, e.MaxPoint.Y); _maxZ = Math.Max(_maxZ, e.MaxPoint.Z);
    }

    public Dictionary<string, object?>? ToResult() => Count == 0 ? null : new Dictionary<string, object?>
    {
      ["minX"] = Math.Round(_minX, 4), ["minY"] = Math.Round(_minY, 4), ["minZ"] = Math.Round(_minZ, 4),
      ["maxX"] = Math.Round(_maxX, 4), ["maxY"] = Math.Round(_maxY, 4), ["maxZ"] = Math.Round(_maxZ, 4),
      ["entities"] = Count,
    };
  }

  public static Task<object?> TransformDrawingAsync(JsonObject? parameters)
  {
    var sourcePath = FileBoundary.ResolveLocalReadPath(PluginRuntime.GetRequiredString(parameters, "sourcePath"), ".dwg");
    var overwrite = PluginRuntime.GetOptionalBool(parameters, "overwrite") ?? false;
    var dryRun = PluginRuntime.GetOptionalBool(parameters, "dryRun") ?? false;
    var outputPath = FileBoundary.ResolveLocalExportPath(PluginRuntime.GetRequiredString(parameters, "outputPath"), overwrite || dryRun, ".dwg");
    // Review: also by file identity, so a short 8.3 name or a hard link to the source cannot be overwritten.
    if (string.Equals(sourcePath, outputPath, StringComparison.OrdinalIgnoreCase) || (File.Exists(outputPath) && FileBoundary.SameFile(sourcePath, outputPath)))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "outputPath must not be the source drawing (the source is never overwritten). Nothing was written.");
    var includeCivilObjects = PluginRuntime.GetOptionalBool(parameters, "includeCivilObjects") ?? false;
    var (plane, kind) = ParseTransform(parameters);
    var (zScale, zOffset) = ParseZ(parameters);
    if (DrawingTransformMath.Validate(plane, zScale, zOffset) is { } invalid)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{invalid} Nothing was written.");

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var warnings = new List<string>();
      var openDocuments = DocumentCommands.Documents();
      if (!dryRun && openDocuments.Any(d => string.Equals(DocumentCommands.PathOf(d), outputPath, StringComparison.OrdinalIgnoreCase)))
        throw new JsonRpcDispatchException("CIVIL3D.CONFLICT", $"'{outputPath}' is open in Civil 3D; close it first (document_close). Nothing was written.");
      if (openDocuments.Any(d => string.Equals(DocumentCommands.PathOf(d), sourcePath, StringComparison.OrdinalIgnoreCase)))
        warnings.Add("The source drawing is open in Civil 3D: the file on disk was read; changes not saved there are not in the output.");

      var shape = DrawingTransformMath.Describe(plane);
      if (!shape.Similarity)
        warnings.Add($"The affine is not a similarity (axis scales {shape.ScaleX:R}/{shape.ScaleY:R}, shear {shape.ShearDeg:0.######}°): circles, arcs, texts, hatches and blocks may refuse it; a Helmert fit keeps them.");
      if (Math.Abs(shape.ScaleX - 1) > 0.01 || Math.Abs(shape.ScaleY - 1) > 0.01)
        warnings.Add($"The scale differs from 1 by more than 1 % ({shape.ScaleX:R}); between SWEREF 99 zones it is within 0,1 %, check the parameters.");

      using var source = BlockCommands.ReadSource(sourcePath);
      Outcome outcome;
      DwgVersion? savedVersion = null;
      // Review: the side database is the working database while it is changed and saved, so dimension blocks and text
      // extents are worked out with its own styles, not the active drawing's.
      var working = HostApplicationServices.WorkingDatabase;
      try
      {
        HostApplicationServices.WorkingDatabase = source;
        outcome = Transform(source, plane, zScale, zOffset, includeCivilObjects, warnings);
        Inspect(source, sourcePath, outputPath, warnings);

        if (!dryRun)
        {
          source.UpdateExt(true);
          var version = SavableVersions.Contains(source.OriginalFileVersion) ? source.OriginalFileVersion : DwgVersion.Current;
          if (version != source.OriginalFileVersion)
            warnings.Add($"The source version {source.OriginalFileVersion} cannot be written; the output is {version}.");
          FileBoundary.WriteFileAtomic(outputPath, overwrite, temp =>
          {
            try
            {
              Save(source, temp, version, warnings);
            }
            finally
            {
              // Review: a database keeps the file it saved to open; it is released before the file is moved into
              // place (or the temporary file removed after a failed save).
              HostApplicationServices.WorkingDatabase = working;
              source.Dispose();
            }
          }, ".dwg");
          savedVersion = version;
          PluginLog.Info("DrawingTransform", $"drawingTransform: {sourcePath} -> {outputPath}, {outcome.Transformed}/{outcome.Total} entities, {outcome.Failed} failed, {outcome.Skipped} skipped.");
        }
      }
      finally
      {
        HostApplicationServices.WorkingDatabase = working;
      }

      return new Dictionary<string, object?>
      {
        ["sourcePath"] = sourcePath,
        ["outputPath"] = dryRun ? null : outputPath,
        ["dryRun"] = dryRun,
        ["written"] = !dryRun,
        ["dwgVersion"] = savedVersion?.ToString(),
        ["transform"] = new Dictionary<string, object?>
        {
          ["kind"] = kind,
          ["matrix"] = new[] { new[] { plane.M11, plane.M12, plane.M13 }, new[] { plane.M21, plane.M22, plane.M23 } },
          ["scaleX"] = shape.ScaleX,
          ["scaleY"] = shape.ScaleY,
          ["rotationDegrees"] = shape.RotationDeg,
          ["shearDegrees"] = shape.ShearDeg,
          ["determinant"] = shape.Determinant,
          ["similarity"] = shape.Similarity,
        },
        ["z"] = new Dictionary<string, object?> { ["scale"] = zScale, ["offset"] = zOffset },
        ["entities"] = new Dictionary<string, object?>
        {
          ["total"] = outcome.Total,
          ["transformed"] = outcome.Transformed,
          ["failed"] = outcome.Failed,
          ["skipped"] = outcome.Skipped,
          ["flatZ"] = outcome.Flat,
          ["zApproximated"] = outcome.Approximated,
        },
        ["maxZDeviationMm"] = outcome.Approximated > 0 ? Math.Round(outcome.MaxDeviation * outcome.MillimetresPerUnit, 3) : null,
        ["byType"] = outcome.ByType,
        ["failures"] = outcome.Failures,
        ["skippedCivilObjects"] = outcome.SkippedTypes,
        ["xrefs"] = outcome.Xrefs.Values.ToList(),
        ["extentsBefore"] = outcome.Before.ToResult(),
        ["extentsAfter"] = outcome.After.ToResult(),
        ["warnings"] = warnings,
      };
    });
  }

  private sealed class Outcome
  {
    public int Total, Transformed, Failed, Skipped, Flat, Approximated;
    public double MaxDeviation;                          // drawing units
    public double MillimetresPerUnit = 1000;
    public readonly SortedDictionary<string, int> SkippedTypes = new(StringComparer.Ordinal);
    public readonly SortedDictionary<string, Dictionary<string, int>> ByType = new(StringComparer.Ordinal);
    public readonly List<Dictionary<string, object?>> Failures = [];
    public readonly SortedDictionary<string, Dictionary<string, object?>> Xrefs = new(StringComparer.OrdinalIgnoreCase);
    public readonly Bounds Before = new(), After = new();

    public void Count(string type, bool ok)
    {
      if (!ByType.TryGetValue(type, out var counts)) ByType[type] = counts = new Dictionary<string, int> { ["transformed"] = 0, ["failed"] = 0 };
      counts[ok ? "transformed" : "failed"]++;
      if (ok) Transformed++;
      else Failed++;
    }
  }

  /// <summary>Moves every model space entity in one transaction (so an associative hatch or dimension and the
  /// geometry it follows move together, as with MOVE). Civil 3D objects (DXF names AECC_/AEC_: surfaces, alignments,
  /// pipe networks, their labels) are skipped unless asked for: Civil has not opened the side database, a failure in
  /// its native code is not an exception the plugin can catch, and a label could move with its parent and again on its
  /// own. Types by DXF name (LINE, LWPOLYLINE, TEXT, INSERT).</summary>
  private static Outcome Transform(Database source, DrawingTransformMath.Plane plane, double zScale, double zOffset, bool includeCivilObjects, List<string> warnings)
  {
    var outcome = new Outcome { MillimetresPerUnit = MillimetresPerUnit(source.Insunits) };
    var maxApproximation = MaxApproximationMm / outcome.MillimetresPerUnit;
    var full = new Matrix3d(DrawingTransformMath.Matrix(plane, zScale, zOffset));
    var uniform = DrawingTransformMath.UniformIn3d(plane, zScale);
    var similarity = DrawingTransformMath.Describe(plane).Similarity;
    using (var transaction = source.TransactionManager.StartTransaction())
    {
      var modelSpace = (BlockTableRecord)transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(source), OpenMode.ForRead);
      foreach (ObjectId id in modelSpace)
      {
        outcome.Total++;
        var type = string.IsNullOrEmpty(id.ObjectClass.DxfName) ? id.ObjectClass.Name : id.ObjectClass.DxfName;
        if (!includeCivilObjects && (type.StartsWith("AECC_", StringComparison.OrdinalIgnoreCase) || type.StartsWith("AEC_", StringComparison.OrdinalIgnoreCase)))
        {
          outcome.Skipped++;
          outcome.SkippedTypes[type] = outcome.SkippedTypes.GetValueOrDefault(type) + 1;
          continue;
        }
        string? layer = null;
        try
        {
          // forceOpenOnLockedLayer: a locked layer keeps the user's hands off, not this copy.
          var entity = (Entity)transaction.GetObject(id, OpenMode.ForWrite, false, true);
          layer = entity.Layer;
          var before = Extents(entity);
          outcome.Before.Add(before);
          if (entity is BlockReference reference) NoteXref(transaction, reference, outcome);

          if (uniform || !similarity)
          {
            entity.TransformBy(full);
          }
          else if (FlatHeight(entity, before) is double height)
          {
            entity.TransformBy(new Matrix3d(DrawingTransformMath.FlatMatrix(plane, zScale, zOffset, height)!));
            outcome.Flat++;
          }
          else
          {
            try
            {
              entity.TransformBy(full);
            }
            catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.CannotScaleNonUniformly
              && before is { } span
              && DrawingTransformMath.FlatDeviation(plane, zScale, span.MinPoint.Z, span.MaxPoint.Z) is var deviation
              && deviation <= maxApproximation)
            {
              var middle = (span.MinPoint.Z + span.MaxPoint.Z) / 2;
              entity.TransformBy(new Matrix3d(DrawingTransformMath.FlatMatrix(plane, zScale, zOffset, middle)!));
              outcome.Approximated++;
              outcome.MaxDeviation = Math.Max(outcome.MaxDeviation, deviation);
            }
          }

          outcome.After.Add(Extents(entity));
          outcome.Count(type, ok: true);
        }
        catch (System.Exception ex)
        {
          outcome.Count(type, ok: false);
          if (outcome.Failures.Count < MaxListed)
          {
            outcome.Failures.Add(new Dictionary<string, object?>
            {
              ["handle"] = id.Handle.ToString(),
              ["type"] = type,
              ["layer"] = layer,
              ["error"] = ex is Autodesk.AutoCAD.Runtime.Exception autocad ? autocad.ErrorStatus.ToString() : $"{ex.GetType().Name}: {ex.Message}",
            });
          }
        }
      }

      transaction.Commit();
    }

    if (outcome.Failed > 0)
      warnings.Add($"{outcome.Failed} entities could not be transformed and stay at the source coordinates in the output{(outcome.Failed > MaxListed ? $" (the first {MaxListed} are listed)" : "")}.");
    if (outcome.Approximated > 0)
      warnings.Add($"{outcome.Approximated} entities spanning heights refused the non-uniform matrix and were moved with the plane scale also on z around their middle height (at most {outcome.MaxDeviation * outcome.MillimetresPerUnit:0.###} mm from the requested heights); give z.scale equal to the plane scale to avoid it.");
    if (outcome.Skipped > 0)
      warnings.Add($"{outcome.Skipped} Civil 3D objects were not transformed and stay at the source coordinates in the output ({string.Join(", ", outcome.SkippedTypes.Select(kv => $"{kv.Key} {kv.Value}"))}); includeCivilObjects: true tries them (test on a copy first). A Civil object that keeps a link to AutoCAD geometry that was moved (a surface's breaklines or boundary) mixes both coordinate systems if it is rebuilt in the output.");
    if (outcome.Xrefs.Count > 0)
      warnings.Add($"{outcome.Xrefs.Count} external reference(s): their insertions are transformed, the referenced drawings are not; this is right only when they are in the same source system.");
    return outcome;
  }

  /// <summary>The height of an entity whose points all have one height: from its extents, or from its plane when it
  /// has no extents (an empty text); null for an entity spanning heights.</summary>
  private static double? FlatHeight(Entity entity, Extents3d? extents)
  {
    if (extents is { } e)
      return Math.Abs(e.MaxPoint.Z - e.MinPoint.Z) <= FlatTolerance ? (e.MinPoint.Z + e.MaxPoint.Z) / 2 : null;
    try
    {
      if (!entity.IsPlanar) return null;
      var plane = entity.GetPlane();
      return plane.Normal.IsParallelTo(Vector3d.ZAxis) ? plane.PointOnPlane.Z : null;
    }
    catch (Autodesk.AutoCAD.Runtime.Exception)
    {
      return null;
    }
  }

  private static Extents3d? Extents(Entity entity)
  {
    try
    {
      var extents = entity.GeometricExtents;
      return double.IsFinite(extents.MinPoint.X) && double.IsFinite(extents.MaxPoint.X) ? extents : null;
    }
    catch (Autodesk.AutoCAD.Runtime.Exception)
    {
      return null;                                     // no extents (an empty text, an unresolved external reference)
    }
  }

  private static void NoteXref(Transaction transaction, BlockReference reference, Outcome outcome)
  {
    if (transaction.GetObject(reference.BlockTableRecord, OpenMode.ForRead) is not BlockTableRecord definition || !definition.IsFromExternalReference) return;
    if (!outcome.Xrefs.TryGetValue(definition.Name, out var xref))
    {
      outcome.Xrefs[definition.Name] = xref = new Dictionary<string, object?>
      {
        ["name"] = definition.Name,
        ["path"] = definition.PathName,
        ["overlay"] = definition.IsFromOverlayReference,
        ["inserts"] = 0,
      };
    }
    xref["inserts"] = (int)xref["inserts"]! + 1;
  }

  /// <summary>What the transformation leaves at the source coordinates: layout viewports, geographic location, the
  /// insertion base point.</summary>
  private static void Inspect(Database source, string sourcePath, string outputPath, List<string> warnings)
  {
    using var transaction = source.TransactionManager.StartOpenCloseTransaction();
    var viewports = 0;
    var table = (BlockTable)transaction.GetObject(source.BlockTableId, OpenMode.ForRead);
    foreach (ObjectId id in table)
    {
      var record = (BlockTableRecord)transaction.GetObject(id, OpenMode.ForRead);
      if (!record.IsLayout || id == SymbolUtilityServices.GetBlockModelSpaceId(source)) continue;
      // The first viewport of a layout is its paper space view itself, not a view of model space.
      var inLayout = 0;
      foreach (ObjectId entityId in record)
        if (entityId.ObjectClass.DxfName == "VIEWPORT") inLayout++;
      viewports += Math.Max(0, inLayout - 1);
    }
    if (viewports > 0)
      warnings.Add($"{viewports} layout viewport(s) still show the source coordinates (layouts are not transformed).");

    try
    {
      if (!source.GeoDataObject.IsNull)
        warnings.Add("The drawing has a geographic location (GEOGRAPHICLOCATION); it was not changed and no longer matches the coordinates.");
    }
    catch (Autodesk.AutoCAD.Runtime.Exception)
    {
      // no geographic location
    }

    if (source.Insbase.DistanceTo(Point3d.Origin) > 1e-9)
      warnings.Add($"The insertion base point (INSBASE) is ({source.Insbase.X:0.###}, {source.Insbase.Y:0.###}, {source.Insbase.Z:0.###}) and was not changed; attach the output at 0,0 only with INSBASE at the origin.");

    // Review: named views keep the source coordinates; references with relative paths break in another folder.
    var views = 0;
    foreach (ObjectId _ in (ViewTable)transaction.GetObject(source.ViewTableId, OpenMode.ForRead)) views++;
    if (views > 0)
      warnings.Add($"{views} named view(s) still show the source coordinates; zoom to the extents after opening the output.");
    if (!string.Equals(Path.GetDirectoryName(sourcePath), Path.GetDirectoryName(outputPath), StringComparison.OrdinalIgnoreCase))
    {
      var relative = new List<string>();
      foreach (ObjectId id in table)
      {
        var record = (BlockTableRecord)transaction.GetObject(id, OpenMode.ForRead);
        if ((record.IsFromExternalReference || record.IsFromOverlayReference) && IsRelative(record.PathName)) relative.Add($"xref {record.Name}");
      }
      var images = RasterImageDef.GetImageDictionary(source);
      if (!images.IsNull)
        foreach (var entry in (DBDictionary)transaction.GetObject(images, OpenMode.ForRead))
          if (transaction.GetObject(entry.Value, OpenMode.ForRead) is RasterImageDef image && IsRelative(image.SourceFileName)) relative.Add($"image {entry.Key}");
      var named = (DBDictionary)transaction.GetObject(source.NamedObjectsDictionaryId, OpenMode.ForRead);
      foreach (var key in new[] { "ACAD_PDFDEFINITIONS", "ACAD_DWFDEFINITIONS", "ACAD_DGNDEFINITIONS" })
        if (named.Contains(key))
          foreach (var entry in (DBDictionary)transaction.GetObject(named.GetAt(key), OpenMode.ForRead))
            if (transaction.GetObject(entry.Value, OpenMode.ForRead) is UnderlayDefinition underlay && IsRelative(underlay.SourceFileName)) relative.Add($"underlay {entry.Key}");
      if (relative.Count > 0)
        warnings.Add($"{relative.Count} reference(s) with a path relative to the source folder will not resolve from the output folder: {string.Join(", ", relative.Take(10))}{(relative.Count > 10 ? ", …" : "")}.");
    }
    transaction.Commit();
  }

  private static bool IsRelative(string? path) => !string.IsNullOrWhiteSpace(path) && !Path.IsPathRooted(path);

  /// <summary>Millimetres per drawing unit (INSUNITS); metres when the drawing does not say.</summary>
  private static double MillimetresPerUnit(UnitsValue units) => units switch
  {
    UnitsValue.Millimeters => 1,
    UnitsValue.Centimeters => 10,
    UnitsValue.Decimeters => 100,
    UnitsValue.Kilometers => 1_000_000,
    UnitsValue.Inches => 25.4,
    UnitsValue.Feet => 304.8,
    _ => 1000,
  };

  /// <summary>Saves the side database to the temporary file.</summary>
  private static void Save(Database source, string path, DwgVersion version, List<string> warnings)
  {
    try
    {
      source.SaveAs(path, false, version, source.SecurityParameters);
    }
    catch (Autodesk.AutoCAD.Runtime.Exception first)
    {
      // C73: the first save of a drawing with 3D solids that carry history fails once (eExcessiveItemCount), the
      // second works; a second failure goes to the caller.
      source.SaveAs(path, false, version, source.SecurityParameters);
      warnings.Add($"The first save failed ({first.ErrorStatus}) and the second succeeded (C73).");
    }
  }

  private static (DrawingTransformMath.Plane Plane, string Kind) ParseTransform(JsonObject? parameters)
  {
    if (PluginRuntime.GetParameter(parameters, "transform") is not JsonObject transform)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "transform must be {a, b, tx, ty} (Helmert) or {m11, m12, m13, m21, m22, m23} (affine). Nothing was written.");
    string[] helmertKeys = ["a", "b", "tx", "ty"];
    string[] affineKeys = ["m11", "m12", "m13", "m21", "m22", "m23"];
    var keys = transform.Select(kv => kv.Key).ToList();
    var unknown = keys.Where(k => !helmertKeys.Contains(k) && !affineKeys.Contains(k)).ToList();
    if (unknown.Count > 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Unknown transform keys: {string.Join(", ", unknown)}. Give {{a, b, tx, ty}} or {{m11, m12, m13, m21, m22, m23}}. Nothing was written.");
    var helmert = keys.Any(helmertKeys.Contains);
    var affine = keys.Any(affineKeys.Contains);
    if (helmert == affine)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "transform must be either a Helmert {a, b, tx, ty} or an affine {m11, m12, m13, m21, m22, m23}, not both or neither. Nothing was written.");

    var required = helmert ? helmertKeys : affineKeys;
    var missing = required.Where(k => !keys.Contains(k)).ToList();
    if (missing.Count > 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"transform lacks {string.Join(", ", missing)}. Nothing was written.");
    var v = required.Select(k => Number(transform[k], $"transform.{k}")).ToArray();
    return helmert
      ? (DrawingTransformMath.Helmert(v[0], v[1], v[2], v[3]), "helmert")
      : (DrawingTransformMath.Affine(v[0], v[1], v[2], v[3], v[4], v[5]), "affine");
  }

  private static (double Scale, double Offset) ParseZ(JsonObject? parameters)
  {
    var node = PluginRuntime.GetParameter(parameters, "z");
    if (node == null) return (1, 0);
    if (node is not JsonObject z)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "z must be {scale, offset} (z' = scale·z + offset). Nothing was written.");
    var unknown = z.Select(kv => kv.Key).Where(k => k is not ("scale" or "offset")).ToList();
    if (unknown.Count > 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Unknown z keys: {string.Join(", ", unknown)}; give scale and/or offset. Nothing was written.");
    return (z["scale"] == null ? 1 : Number(z["scale"], "z.scale"), z["offset"] == null ? 0 : Number(z["offset"], "z.offset"));
  }

  private static double Number(JsonNode? node, string name)
  {
    if (node is JsonValue value && value.TryGetValue<double>(out var number)) return number;
    throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{name} must be a number. Nothing was written.");
  }
}
