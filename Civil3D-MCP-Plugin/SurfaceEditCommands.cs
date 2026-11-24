using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil;
using Autodesk.Civil.DatabaseServices;

namespace Civil3DMcpPlugin;

/// <summary>
/// Surface editing used when patching a ground model from point cloud data:
/// paste a helper surface, simplify, build options, and a gap check (long TIN edges).
/// </summary>
public static class SurfaceEditCommands
{
  public static Task<object?> PasteSurfaceAsync(JsonObject? parameters)
  {
    var surfaceName = PluginRuntime.GetRequiredString(parameters, "surfaceName");
    var sourceName = PluginRuntime.GetRequiredString(parameters, "sourceSurfaceName");
    if (string.Equals(surfaceName, sourceName, StringComparison.OrdinalIgnoreCase))
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "A surface cannot be pasted into itself.");
    }

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var target = RequireTin(CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, surfaceName, OpenMode.ForWrite));
      var source = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, sourceName, OpenMode.ForRead);
      // C97 (live 2026-10-10): a new, empty target has no properties to read (Civil 3D throws); 0 points then. Pasting
      // surfaces into an empty one is the usual way to build a combined surface.
      var before = SurfaceCommands.PointCount(target);

      target.PasteSurface(source.ObjectId);
      target.Rebuild();

      return new Dictionary<string, object?>
      {
        ["surfaceName"] = target.Name,
        ["pastedSurface"] = source.Name,
        ["pointsBefore"] = before,
        ["pointsAfter"] = SurfaceCommands.PointCount(target),
        ["copyDeletedDependentObjects"] = target.BuildOptions.CopyDeletedDependentObjects,
      };
    });
  }

  public static Task<object?> SimplifySurfaceAsync(JsonObject? parameters)
  {
    var surfaceName = PluginRuntime.GetRequiredString(parameters, "surfaceName");
    var methodName = PluginRuntime.GetOptionalString(parameters, "method") ?? "point_removal";
    var method = methodName switch
    {
      "point_removal" => SurfaceSimplifyType.PointRemoval,
      "edge_contraction" => SurfaceSimplifyType.EdgeContraction,
      _ => throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Unknown simplify method '{methodName}'. Use point_removal or edge_contraction."),
    };
    var maxChange = PluginRuntime.GetRequiredDouble(parameters, "maxElevationChange");
    if (maxChange <= 0)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "maxElevationChange must be greater than zero.");
    }

    var region = ParseOptionalRing(parameters, "region");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var tin = RequireTin(CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, surfaceName, OpenMode.ForWrite));
      var before = tin.GetGeneralProperties().NumberOfPoints;

      var options = new SurfaceSimplifyOptions(method)
      {
        UseMaximumChangeInElevation = true,
        MaximumChangeInElevation = maxChange,
      };
      if (region == null)
      {
        options.SetSurfaceBorderAsRegion();
      }
      else
      {
        options.UserSpecifiedPolygonRegion = ToClosedPoint3dCollection(region);
      }

      tin.SimplifySurface(options);
      tin.Rebuild();
      var after = tin.GetGeneralProperties().NumberOfPoints;

      return new Dictionary<string, object?>
      {
        ["surfaceName"] = tin.Name,
        ["method"] = methodName,
        ["maxElevationChange"] = maxChange,
        ["region"] = region == null ? "surface border" : "polygon",
        ["pointsBefore"] = before,
        ["pointsAfter"] = after,
        ["pointsRemoved"] = before - after,
      };
    });
  }

  public static Task<object?> SetSurfaceBuildOptionsAsync(JsonObject? parameters)
  {
    var surfaceName = PluginRuntime.GetRequiredString(parameters, "surfaceName");
    var copyDeleted = PluginRuntime.GetOptionalBool(parameters, "copyDeletedDependentObjects");
    var useMaxLength = PluginRuntime.GetOptionalBool(parameters, "useMaximumTriangleLength");
    var maxLength = PluginRuntime.GetOptionalDouble(parameters, "maximumTriangleLength");
    if (copyDeleted == null && useMaxLength == null && maxLength == null)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Give at least one build option. Nothing was changed.");
    }

    if (maxLength is <= 0)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "maximumTriangleLength must be greater than zero.");
    }

    // A length without an explicit switch means "use it".
    useMaxLength ??= maxLength != null ? true : null;

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var surface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, surfaceName, OpenMode.ForWrite);
      // C97: build options on a surface without data (before pasting into it); nothing to rebuild then.
      var empty = SurfaceCommands.GeneralPropertiesOrNull(surface) == null;
      try
      {
        var options = surface.BuildOptions;
        if (copyDeleted != null) options.CopyDeletedDependentObjects = copyDeleted.Value;
        if (maxLength != null) options.MaximumTriangleLength = maxLength.Value;
        if (useMaxLength != null) options.UseMaximumTriangleLength = useMaxLength.Value;
        if (!empty) surface.Rebuild();
      }
      catch (InvalidOperationException ex) when (empty)
      {
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"'{surface.Name}' has no data yet and Civil 3D does not take build options for it ({ex.Message}); add data first (paste, points, breaklines). Nothing was changed.");
      }

      var readBack = surface.BuildOptions;
      if ((copyDeleted != null && readBack.CopyDeletedDependentObjects != copyDeleted.Value)
        || (useMaxLength != null && readBack.UseMaximumTriangleLength != useMaxLength.Value)
        || (maxLength != null && Math.Abs(readBack.MaximumTriangleLength - maxLength.Value) > 1e-9))
      {
        throw new JsonRpcDispatchException(
          "CIVIL3D.TRANSACTION_FAILED",
          $"Build options for '{surface.Name}' did not persist (read back differs from the request).");
      }

      return new Dictionary<string, object?>
      {
        ["surfaceName"] = surface.Name,
        ["copyDeletedDependentObjects"] = readBack.CopyDeletedDependentObjects,
        ["useMaximumTriangleLength"] = readBack.UseMaximumTriangleLength,
        ["maximumTriangleLength"] = readBack.MaximumTriangleLength,
        ["numberOfTriangles"] = SurfaceCommands.TriangleCount(surface),
        ["empty"] = empty,
      };
    });
  }

  public static Task<object?> FindSurfaceGapsAsync(JsonObject? parameters)
  {
    var surfaceName = PluginRuntime.GetRequiredString(parameters, "surfaceName");
    var minEdge = PluginRuntime.GetOptionalDouble(parameters, "minEdgeLength") ?? 10.0;
    var maxClusters = PluginRuntime.GetOptionalInt(parameters, "maxClusters") ?? 50;
    if (minEdge <= 0 || maxClusters <= 0)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "minEdgeLength and maxClusters must be greater than zero.");
    }

    var region = ParseOptionalRing(parameters, "region");

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var tin = RequireTin(CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, surfaceName, OpenMode.ForRead));
      var scanned = 0;
      var longOnes = new List<GapMath.Triangle>();
      foreach (TinSurfaceTriangle triangle in tin.GetTriangles(false))
      {
        scanned++;
        var a = triangle.Vertex1.Location;
        var b = triangle.Vertex2.Location;
        var c = triangle.Vertex3.Location;
        var t = new GapMath.Triangle((a.X, a.Y), (b.X, b.Y), (c.X, c.Y));
        if (GapMath.MaxEdge(t) >= minEdge)
        {
          longOnes.Add(t);
        }
      }

      var all = GapMath.FindGaps(longOnes, minEdge, region, int.MaxValue);
      return new Dictionary<string, object?>
      {
        ["surfaceName"] = tin.Name,
        ["minEdgeLength"] = minEdge,
        ["trianglesScanned"] = scanned,
        ["longTriangles"] = all.Sum(g => g.TriangleCount),
        ["gapCount"] = all.Count,
        ["gaps"] = all.Take(maxClusters).Select(g => new Dictionary<string, object?>
        {
          ["triangleCount"] = g.TriangleCount,
          ["area"] = Math.Round(g.Area, 1),
          ["maxEdge"] = Math.Round(g.MaxEdge, 2),
          ["minX"] = Math.Round(g.MinX, 2),
          ["minY"] = Math.Round(g.MinY, 2),
          ["maxX"] = Math.Round(g.MaxX, 2),
          ["maxY"] = Math.Round(g.MaxY, 2),
          ["centroidX"] = Math.Round(g.CentroidX, 2),
          ["centroidY"] = Math.Round(g.CentroidY, 2),
        }).ToList(),
      };
    });
  }

  private static TinSurface RequireTin(Autodesk.Civil.DatabaseServices.Surface surface) =>
    surface as TinSurface ?? throw new JsonRpcDispatchException(
      "CIVIL3D.INVALID_INPUT",
      $"Surface '{surface.Name}' is a {surface.GetType().Name}; this action needs a TIN surface.");

  private static List<(double X, double Y)>? ParseOptionalRing(JsonObject? parameters, string name)
  {
    if (PluginRuntime.GetParameter(parameters, name) is not JsonArray array)
    {
      return null;
    }

    var ring = new List<(double X, double Y)>();
    foreach (var node in array)
    {
      if (node is not JsonObject point)
      {
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{name} must be a list of {{x, y}} points.");
      }

      ring.Add((
        point["x"]?.GetValue<double>() ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Point is missing x."),
        point["y"]?.GetValue<double>() ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Point is missing y.")));
    }

    if (ring.Count < 3)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{name} needs at least three points.");
    }

    return ring;
  }

  private static Point3dCollection ToClosedPoint3dCollection(IReadOnlyList<(double X, double Y)> ring)
  {
    var points = new Point3dCollection();
    foreach (var (x, y) in ring)
    {
      points.Add(new Point3d(x, y, 0));
    }

    if (ring[0] != ring[^1])
    {
      points.Add(new Point3d(ring[0].X, ring[0].Y, 0));
    }

    return points;
  }
}
