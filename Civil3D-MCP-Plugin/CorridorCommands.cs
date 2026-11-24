using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;

namespace Civil3DMcpPlugin;

public static class CorridorCommands
{
  public static Task<object?> ListCorridorsAsync()
  {
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var corridors = new List<Dictionary<string, object?>>();
      foreach (ObjectId objectId in civilDoc.CorridorCollection)
      {
        var corridor = CivilObjectUtils.GetRequiredObject<Corridor>(transaction, objectId, OpenMode.ForRead);
        corridors.Add(ToCorridorSummary(corridor));
      }

      return new Dictionary<string, object?>
      {
        ["corridors"] = corridors,
      };
    });
  }

  public static Task<object?> GetCorridorAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var corridor = CivilObjectUtils.FindCorridorByName(civilDoc, transaction, name, OpenMode.ForRead);
      var baselines = new List<Dictionary<string, object?>>();

      foreach (Baseline baseline in corridor.Baselines)
      {
        baselines.Add(ToBaselineInfo(baseline, transaction));
      }

      var surfaces = ReadCorridorSurfaces(corridor, transaction);
      var featureLines = ReadCorridorFeatureLines(corridor);

      return new Dictionary<string, object?>
      {
        ["name"] = corridor.Name,
        ["handle"] = CivilObjectUtils.GetHandle(corridor),
        ["style"] = CivilObjectUtils.GetName(transaction.GetObject(corridor.StyleId, OpenMode.ForRead)) ?? string.Empty,
        ["layer"] = corridor.Layer,
        ["baselines"] = baselines,
        ["surfaces"] = surfaces,
        ["featureLineCount"] = featureLines.Count,
        ["state"] = GetCorridorState(corridor),
      };
    });
  }

  public static Task<object?> RebuildCorridorAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");

    // Create the job up front so the caller gets a jobId immediately — they
    // poll civil3d_job(status, jobId) until the background rebuild finishes.
    var requestId = PluginRuntime.GetCurrentRequestId();
    var job = JobRegistry.Create(
      $"Rebuilding corridor {name}",
      "corridor_rebuild",
      requestId,
      PluginRuntime.GetActiveDrawingIdentity());
    job.CancellationSource = new CancellationTokenSource();
    var cancellationToken = job.CancellationSource.Token;

    // Dispatch the rebuild to the Civil 3D command thread via
    // CivilExecution.WriteAsync, but do NOT await it here so the JSON-RPC
    // handler returns immediately and the HTTP/TCP command timeout does not
    // apply to long-running rebuilds.
    _ = Task.Run(async () =>
    {
      try
      {
        await PluginRuntime.RunWithRequestContextAsync(
          "rebuildCorridor",
          $"{requestId ?? "job"}:job:{job.JobId}",
          cancellationToken,
          job.DrawingIdentity,
          async () =>
          {
            cancellationToken.ThrowIfCancellationRequested();
            JobRegistry.Progress(job.JobId, 10, $"Scheduling rebuild for corridor {name}", null);

            var result = await CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
            {
              cancellationToken.ThrowIfCancellationRequested();

              var corridor = CivilObjectUtils.FindCorridorByName(civilDoc, transaction, name, OpenMode.ForWrite);

              // Corridor.Rebuild() is synchronous and exposes no cancellation
              // handle. Abort before entering and after it returns.
              JobRegistry.Progress(job.JobId, 40, $"Rebuilding corridor {name}", null);
              corridor.Rebuild();
              cancellationToken.ThrowIfCancellationRequested();

              return new Dictionary<string, object?>
              {
                ["corridorName"] = name,
                ["state"] = GetCorridorState(corridor),
              };
            });

            JobRegistry.Complete(job.JobId, result);
            PluginLog.Info("Corridor", $"Rebuild completed for corridor '{name}' (job {job.JobId}).");
            return result;
          });
      }
      catch (OperationCanceledException)
      {
        JobRegistry.AcknowledgeCancellation(job.JobId);
        PluginLog.Info("Corridor", $"Rebuild cancelled for corridor '{name}' (job {job.JobId}).");
      }
      catch (Exception ex)
      {
        PluginLog.Error("Corridor", $"Rebuild failed for corridor '{name}' (job {job.JobId}).", ex);
        try
        {
          JobRegistry.Fail(job.JobId, ex.Message);
        }
        catch (Exception failEx)
        {
          PluginLog.Error("Corridor", $"Unable to mark job {job.JobId} as failed.", failEx);
        }
        job.CancellationSource = null;
      }
      finally
      {
        // Release the cancellation source once the worker has exited.
        try
        {
          job.CancellationSource?.Dispose();
        }
        catch (ObjectDisposedException)
        {
          // Already disposed (e.g. by Cancel/Dispose race); safe to ignore.
        }
      }
    }, CancellationToken.None);

    return Task.FromResult<object?>(new Dictionary<string, object?>
    {
      ["jobId"] = job.JobId,
      ["state"] = "running",
      ["message"] = $"Corridor '{name}' rebuild queued. Poll civil3d_job with action='status' to track progress.",
    });
  }

  public static Task<object?> GetCorridorSurfacesAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var corridor = CivilObjectUtils.FindCorridorByName(civilDoc, transaction, name, OpenMode.ForRead);
      return new Dictionary<string, object?>
      {
        ["corridorName"] = corridor.Name,
        ["surfaces"] = ReadCorridorSurfaces(corridor, transaction),
      };
    });
  }

  /// <summary>Feature line codes of a corridor; with includeGeometry (C60, 2026-10-08) also each feature line's 3D
  /// points (main baselines) per code, side by the sign of the median offset, for machine control lines such as the
  /// trench bottom edges and daylight.</summary>
  public static Task<object?> GetCorridorFeatureLinesAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var includeGeometry = PluginRuntime.GetOptionalBool(parameters, "includeGeometry") ?? false;
    var codes = (PluginRuntime.GetParameter(parameters, "codes") as JsonArray)?.Select(n => n?.GetValue<string>()).OfType<string>()
      .ToHashSet(StringComparer.OrdinalIgnoreCase);
    var decimals = PluginRuntime.GetOptionalInt(parameters, "decimals") ?? 3;
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var corridor = CivilObjectUtils.FindCorridorByName(civilDoc, transaction, name, OpenMode.ForRead);
      var result = new Dictionary<string, object?>
      {
        ["corridorName"] = corridor.Name,
        ["featureLines"] = ReadCorridorFeatureLines(corridor),
      };
      if (!includeGeometry)
        return result;

      var lines = new List<Dictionary<string, object?>>();
      var diagnostics = new List<Dictionary<string, object?>>();
      foreach (Baseline baseline in corridor.Baselines)
      {
        var map = baseline.MainBaselineFeatureLines.FeatureLineCollectionMap;
        // per code through CodeNames() and the indexer (enumerating the map gave nothing live, 2026-10-08)
        foreach (var code in map.CodeNames())
        {
          if (codes != null && !codes.Contains(code))
            continue;
          var seen = 0;
          var pointsSeen = 0;
          foreach (CorridorFeatureLine line in map[code])
          {
            seen++;
            var points = new List<double[]>();
            var offsets = new List<double>();
            var stations = new List<double>();
            foreach (FeatureLinePoint point in line.FeatureLinePoints)
            {
              points.Add(new[] { Math.Round(point.XYZ.X, decimals), Math.Round(point.XYZ.Y, decimals), Math.Round(point.XYZ.Z, decimals) });
              offsets.Add(point.Offset);
              stations.Add(point.Station);
            }

            pointsSeen += points.Count;
            if (points.Count == 0)
              continue;
            var median = offsets.OrderBy(o => o).ElementAt(offsets.Count / 2);
            lines.Add(new Dictionary<string, object?>
            {
              ["baseline"] = baseline.Name,
              ["code"] = line.CodeName,
              ["side"] = median < 0 ? "left" : median > 0 ? "right" : "center",
              ["medianOffset"] = Math.Round(median, 3),
              ["startStation"] = Math.Round(stations.Min(), 3),
              ["endStation"] = Math.Round(stations.Max(), 3),
              ["points"] = points,
            });
          }

          diagnostics.Add(new Dictionary<string, object?>
          {
            ["baseline"] = baseline.Name, ["code"] = code, ["featureLines"] = seen, ["points"] = pointsSeen,
          });
        }
      }

      result["diagnostics"] = diagnostics;
      result["lines"] = lines;
      return result;
    });
  }

  public static Task<object?> ComputeCorridorVolumesAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var corridorSurfaceName = PluginRuntime.GetRequiredString(parameters, "corridorSurface");
    var referenceSurfaceName = PluginRuntime.GetRequiredString(parameters, "referenceSurface");

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var corridor = CivilObjectUtils.FindCorridorByName(civilDoc, transaction, name, OpenMode.ForRead);
      var corridorSurface = FindCorridorSurfaceByName(corridor, corridorSurfaceName, transaction);
      var referenceSurface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, referenceSurfaceName, OpenMode.ForRead);
      return ComputeVolumeBetweenSurfaces(database, transaction, corridorSurface, referenceSurface);
    });
  }

  public static Task<object?> CreateCorridorAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var profileName = PluginRuntime.GetRequiredString(parameters, "profileName");
    var assemblyName = PluginRuntime.GetRequiredString(parameters, "assemblyName");
    var baselineName = PluginRuntime.GetOptionalString(parameters, "baselineName") ?? "Baseline (1)";
    var regionName = PluginRuntime.GetOptionalString(parameters, "regionName") ?? "Region (1)";
    var startStation = PluginRuntime.GetOptionalDouble(parameters, "startStation");
    var endStation = PluginRuntime.GetOptionalDouble(parameters, "endStation");
    var frequency = CorridorEditingCommands.ReadFrequency(parameters);

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      foreach (ObjectId existingId in civilDoc.CorridorCollection)
      {
        var existing = CivilObjectUtils.GetRequiredObject<Corridor>(transaction, existingId, OpenMode.ForRead);
        if (string.Equals(existing.Name, name, StringComparison.OrdinalIgnoreCase))
          throw new JsonRpcDispatchException("CIVIL3D.CONFLICT", $"A corridor named '{name}' already exists.");
      }

      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var profileId = FindProfileId(alignment, transaction, profileName);
      var assembly = AssemblyCreationCommands.FindAssemblyByName(civilDoc, transaction, assemblyName, OpenMode.ForRead);

      var corridorId = civilDoc.CorridorCollection.Add(name, baselineName, alignment.ObjectId, profileId, regionName, assembly.ObjectId);
      var corridor = CivilObjectUtils.GetRequiredObject<Corridor>(transaction, corridorId, OpenMode.ForWrite);
      if (corridor.Baselines.Count == 0 || corridor.Baselines[0].BaselineRegions.Count == 0)
        throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", "CorridorCollection.Add did not create a baseline region. The transaction was not committed.");

      var region = corridor.Baselines[0].BaselineRegions[0];
      if (startStation.HasValue || endStation.HasValue)
      {
        CorridorEditingCommands.ApplyStations(region, startStation, endStation);
        CorridorEditingCommands.VerifyStations(region, startStation ?? region.StartStation, endStation ?? region.EndStation);
      }
      CorridorEditingCommands.ApplyFrequency(region, frequency);

      // Not rebuilt here: targets are usually assigned next (target_mapping_set).
      return new Dictionary<string, object?>
      {
        ["name"] = corridor.Name,
        ["handle"] = CivilObjectUtils.GetHandle(corridor),
        ["baselines"] = corridor.Baselines.Cast<Baseline>().Select(baseline => ToBaselineInfo(baseline, transaction)).ToList(),
        ["state"] = GetCorridorState(corridor),
        ["created"] = true,
      };
    });
  }

  public static Task<object?> CreateCorridorSurfaceAsync(JsonObject? parameters)
  {
    var corridorName = PluginRuntime.GetRequiredString(parameters, "corridorName");
    var surfaceName = PluginRuntime.GetRequiredString(parameters, "surfaceName");
    var linkCodes = (PluginRuntime.GetParameter(parameters, "linkCodes") as JsonArray)?
      .Select(node => node?.GetValue<string>())
      .Where(code => !string.IsNullOrWhiteSpace(code))
      .Cast<string>()
      .ToList() ?? new List<string>();
    var addAsBreakline = PluginRuntime.GetOptionalBool(parameters, "addAsBreakline") ?? true;
    var boundaryFromExtents = PluginRuntime.GetOptionalBool(parameters, "boundaryFromExtents") ?? true;
    var overhangText = PluginRuntime.GetOptionalString(parameters, "overhangCorrection");
    if (linkCodes.Count == 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "linkCodes must contain at least one code.");
    OverhangCorrectionType? overhang = overhangText?.ToLowerInvariant() switch
    {
      null => null,
      "none" => OverhangCorrectionType.None,
      "top_links" => OverhangCorrectionType.TopLinks,
      "bottom_links" => OverhangCorrectionType.BottomLinks,
      _ => throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "overhangCorrection must be none, top_links, or bottom_links."),
    };

    return CivilExecution.WriteInCommandAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var corridor = CivilObjectUtils.FindCorridorByName(civilDoc, transaction, corridorName, OpenMode.ForWrite);
      if (corridor.CorridorSurfaces.SurfaceNames().Contains(surfaceName, StringComparer.OrdinalIgnoreCase))
        throw new JsonRpcDispatchException("CIVIL3D.CONFLICT", $"Corridor '{corridorName}' already has a surface named '{surfaceName}'.");

      var availableCodes = corridor.GetLinkCodes();
      if (availableCodes.Length == 0)
        throw new JsonRpcDispatchException("CIVIL3D.CONFLICT", $"Corridor '{corridorName}' has no link codes yet; assign targets and rebuild first.");
      var unknown = linkCodes.Where(code => !availableCodes.Contains(code, StringComparer.Ordinal)).ToList();
      if (unknown.Count > 0)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
          $"Unknown link code(s) {string.Join(", ", unknown)}. Available: {string.Join(", ", availableCodes)}.");

      var corridorSurface = corridor.CorridorSurfaces.Add(surfaceName);
      foreach (var code in linkCodes)
        corridorSurface.AddLinkCode(code, addAsBreakline);
      if (overhang.HasValue)
        corridorSurface.OverhangCorrection = overhang.Value;
      if (boundaryFromExtents)
        corridorSurface.Boundaries.AddCorridorExtentsBoundary($"{surfaceName}_extents");

      // No explicit Rebuild: Civil 3D builds the new surface when the command ends
      // (live 2026-10-02); the corridor rebuild job can be run afterwards if needed.
      var missingCodes = linkCodes.Except(corridorSurface.LinkCodes(), StringComparer.Ordinal).ToList();
      if (missingCodes.Count > 0)
        throw new JsonRpcDispatchException("CIVIL3D.API_ERROR",
          $"Link code(s) {string.Join(", ", missingCodes)} did not persist on '{surfaceName}'. The transaction was not committed.");

      return new Dictionary<string, object?>
      {
        ["corridorName"] = corridor.Name,
        ["surfaceName"] = corridorSurface.Name,
        ["surfaceHandle"] = corridorSurface.SurfaceId.IsNull ? null : corridorSurface.SurfaceId.Handle.ToString(),
        ["linkCodes"] = corridorSurface.LinkCodes(),
        ["overhangCorrection"] = corridorSurface.OverhangCorrection.ToString(),
        ["boundaries"] = corridorSurface.Boundaries.BoundaryNames(),
        ["state"] = GetCorridorState(corridor),
      };
    }, $"createCorridorSurface '{surfaceName}'", TimeSpan.FromSeconds(100));
  }

  /// <summary>
  /// Erases a corridor; its corridor surfaces go with it and are listed in the result.
  /// Runs in the deferred command like the other corridor-surface writes (see C5).
  /// </summary>
  public static Task<object?> DeleteCorridorAsync(JsonObject? parameters)
  {
    var corridorName = PluginRuntime.GetRequiredString(parameters, "corridorName");

    return CivilExecution.WriteInCommandAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var corridor = CivilObjectUtils.FindCorridorByName(civilDoc, transaction, corridorName, OpenMode.ForWrite);
      var surfaces = corridor.CorridorSurfaces.SurfaceNames();
      corridor.Erase();
      if (!corridor.IsErased)
        throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"Corridor '{corridorName}' was not erased. The transaction was not committed.");

      return new Dictionary<string, object?>
      {
        ["corridorName"] = corridorName,
        ["deleted"] = true,
        ["removedSurfaces"] = surfaces,
      };
    }, $"deleteCorridor '{corridorName}'", TimeSpan.FromSeconds(100));
  }

  /// <summary>
  /// Adds a polygon boundary to a corridor surface. Used when the corridor extents
  /// boundary is rejected (Civil 3D: "crossing polygon" at baseline angle points, live
  /// 2026-10-02); the polygon is typically the union of the section footprints.
  /// </summary>
  public static Task<object?> AddCorridorSurfaceBoundaryAsync(JsonObject? parameters)
  {
    var corridorName = PluginRuntime.GetRequiredString(parameters, "corridorName");
    var surfaceName = PluginRuntime.GetRequiredString(parameters, "surfaceName");
    var boundaryName = PluginRuntime.GetRequiredString(parameters, "boundaryName");
    var replace = PluginRuntime.GetOptionalBool(parameters, "replace") ?? false;
    var boundaryType = (PluginRuntime.GetOptionalString(parameters, "boundaryType") ?? "outside").ToLowerInvariant() switch
    {
      "outside" => CorridorSurfaceBoundaryType.OutsideBoundary,
      "inside" => CorridorSurfaceBoundaryType.InsideBoundary,
      _ => throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "boundaryType must be outside or inside."),
    };
    var points = ReadPolygon(PluginRuntime.GetParameter(parameters, "points") as JsonArray);

    return CivilExecution.WriteInCommandAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var corridor = CivilObjectUtils.FindCorridorByName(civilDoc, transaction, corridorName, OpenMode.ForWrite);
      var surface = corridor.CorridorSurfaces.Cast<CorridorSurface>()
        .FirstOrDefault(candidate => string.Equals(candidate.Name, surfaceName, StringComparison.OrdinalIgnoreCase))
        ?? throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Corridor surface '{surfaceName}' was not found on corridor '{corridorName}'.");

      if (surface.Boundaries.BoundaryNames().Contains(boundaryName, StringComparer.OrdinalIgnoreCase))
      {
        if (!replace)
          throw new JsonRpcDispatchException("CIVIL3D.CONFLICT", $"Corridor surface '{surfaceName}' already has a boundary named '{boundaryName}'. Pass replace=true to replace it.");
        surface.Boundaries.Remove(boundaryName);
      }

      var collection = new Point3dCollection();
      foreach (var (x, y) in points) collection.Add(new Point3d(x, y, 0));
      var boundary = surface.Boundaries.Add(boundaryName, collection);
      boundary.BoundaryType = boundaryType;

      if (!surface.Boundaries.BoundaryNames().Contains(boundaryName, StringComparer.OrdinalIgnoreCase))
        throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"Boundary '{boundaryName}' did not persist on '{surfaceName}'. The transaction was not committed.");

      return new Dictionary<string, object?>
      {
        ["corridorName"] = corridor.Name,
        ["surfaceName"] = surface.Name,
        ["boundaryName"] = boundaryName,
        ["boundaryType"] = boundary.BoundaryType.ToString(),
        ["pointCount"] = points.Count,
        ["boundaries"] = surface.Boundaries.BoundaryNames(),
        ["replaced"] = replace,
      };
    }, $"addCorridorSurfaceBoundary '{surfaceName}/{boundaryName}'", TimeSpan.FromSeconds(100));
  }

  /// <summary>Reads at least three distinct XY points; a repeated closing point is dropped.</summary>
  private static List<(double X, double Y)> ReadPolygon(JsonArray? array)
  {
    var points = new List<(double X, double Y)>();
    foreach (var node in array ?? new JsonArray())
    {
      if (node is not JsonObject point || point["x"] is null || point["y"] is null)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Each boundary point must be an object with x and y.");
      points.Add((point["x"]!.GetValue<double>(), point["y"]!.GetValue<double>()));
    }
    if (points.Count > 3 && points[0] == points[^1]) points.RemoveAt(points.Count - 1);
    if (points.Distinct().Count() < 3)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "A boundary needs at least three distinct points.");
    return points;
  }

  private static ObjectId FindProfileId(Alignment alignment, Transaction transaction, string profileName)
  {
    foreach (ObjectId profileId in alignment.GetProfileIds())
    {
      var profile = CivilObjectUtils.GetRequiredObject<Profile>(transaction, profileId, OpenMode.ForRead);
      if (string.Equals(profile.Name, profileName, StringComparison.OrdinalIgnoreCase))
        return profileId;
    }
    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Profile '{profileName}' was not found on alignment '{alignment.Name}'.");
  }

  private static Dictionary<string, object?> ToBaselineInfo(Baseline baseline, Transaction transaction)
  {
    return new Dictionary<string, object?>
    {
      ["name"] = baseline.Name,
      ["alignmentName"] = CivilObjectUtils.GetName(transaction.GetObject(baseline.AlignmentId, OpenMode.ForRead)) ?? string.Empty,
      ["profileName"] = CivilObjectUtils.GetName(transaction.GetObject(baseline.ProfileId, OpenMode.ForRead)) ?? string.Empty,
      ["regions"] = CorridorEditingCommands.ReadRegions(baseline, transaction),
    };
  }

  private static Dictionary<string, object?> ToCorridorSummary(Corridor corridor)
  {
    return new Dictionary<string, object?>
    {
      ["name"] = corridor.Name,
      ["handle"] = CivilObjectUtils.GetHandle(corridor),
      ["baselineCount"] = corridor.Baselines.Count,
      ["regionCount"] = corridor.Baselines.Cast<Baseline>().Sum(baseline => baseline.BaselineRegions.Count),
      ["surfaceCount"] = ReadCorridorSurfaces(corridor, null).Count,
      ["state"] = GetCorridorState(corridor),
      ["lastBuildTime"] = CivilObjectUtils.GetPropertyValue<DateTime?>(corridor, "RebuildDate")?.ToString("O"),
    };
  }

  private static List<Dictionary<string, object?>> ReadCorridorSurfaces(Corridor corridor, Transaction? transaction)
  {
    var corridorSurfaces = corridor.CorridorSurfaces;
    return corridorSurfaces.Cast<CorridorSurface>()
      .Select(surface => new Dictionary<string, object?>
      {
        ["name"] = surface.Name,
        ["surfaceId"] = surface.SurfaceId == ObjectId.Null ? null : surface.SurfaceId.Handle.ToString(),
        ["boundaries"] = surface.Boundaries.Cast<CorridorSurfaceBoundary>().Select(boundary => boundary.Name).ToList(),
      })
      .ToList();
  }

  private static List<Dictionary<string, object?>> ReadCorridorFeatureLines(Corridor corridor)
  {
    var featureLines = corridor.FeatureLineCodeInfos;
    return featureLines.Cast<FeatureLineCodeInfo>()
      .Select(feature => new Dictionary<string, object?>
      {
        ["name"] = feature.CodeName,
      })
      .ToList();
  }

  private static string GetCorridorState(Corridor corridor) => corridor.IsOutOfDate ? "out_of_date" : "built";

  private static Autodesk.Civil.DatabaseServices.Surface FindCorridorSurfaceByName(Corridor corridor, string name, Transaction transaction)
  {
    var definition = corridor.CorridorSurfaces.Cast<CorridorSurface>()
      .FirstOrDefault(surface => string.Equals(surface.Name, name, StringComparison.OrdinalIgnoreCase))
      ?? throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Corridor surface '{name}' was not found on corridor '{corridor.Name}'.");

    if (definition.SurfaceId == ObjectId.Null)
    {
      throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"Corridor surface '{name}' has no built Civil 3D surface ObjectId.");
    }

    return CivilObjectUtils.GetRequiredObject<Autodesk.Civil.DatabaseServices.Surface>(transaction, definition.SurfaceId, OpenMode.ForRead);
  }

  private static Dictionary<string, object?> ComputeVolumeBetweenSurfaces(
    Database database,
    Transaction transaction,
    Autodesk.Civil.DatabaseServices.Surface baseSurface,
    Autodesk.Civil.DatabaseServices.Surface comparisonSurface)
  {
    var temporaryName = $"MCP_TMP_CORRIDOR_VOLUME_{Guid.NewGuid():N}";
    var volumeSurfaceId = TinVolumeSurface.Create(temporaryName, baseSurface.ObjectId, comparisonSurface.ObjectId);
    var volumeSurface = CivilObjectUtils.GetRequiredObject<TinVolumeSurface>(transaction, volumeSurfaceId, OpenMode.ForRead);
    var properties = volumeSurface.GetVolumeProperties();
    return new Dictionary<string, object?>
    {
      ["cutVolume"] = properties.UnadjustedCutVolume,
      ["fillVolume"] = properties.UnadjustedFillVolume,
      ["netVolume"] = properties.UnadjustedNetVolume,
      ["units"] = new Dictionary<string, object?>
      {
        ["volume"] = CivilObjectUtils.VolumeUnits(database),
      },
      ["source"] = "TinVolumeSurface.GetVolumeProperties",
    };
  }
}
