using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;

namespace Civil3DMcpPlugin;

/// <summary>
/// Corridor region and target editing through the typed Civil 3D 2026 API:
///   BaselineRegionCollection.Add / RemoveAt, BaselineRegion.Split,
///   BaselineRegion.AppliedAssemblySetting (frequencies),
///   GetTargets() / SubassemblyTargetInfo.TargetIds / SetTargets() on
///   BaselineRegion, Baseline and Corridor.
/// Every write is read back; a mismatch throws so the transaction is not committed.
/// </summary>
public static class CorridorEditingCommands
{
  private const double StationTolerance = 1e-6;

  // -------------------------------------------------------------------------
  // Target mappings
  // -------------------------------------------------------------------------

  public static Task<object?> GetCorridorTargetMappingsAsync(JsonObject? parameters)
  {
    var corridorName = PluginRuntime.GetRequiredString(parameters, "corridorName");
    var regionIndex = PluginRuntime.GetOptionalInt(parameters, "regionIndex");
    var baselineIndex = PluginRuntime.GetOptionalInt(parameters, "baselineIndex") ?? 0;

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var corridor = CivilObjectUtils.FindCorridorByName(civilDoc, transaction, corridorName, OpenMode.ForRead);
      var baseline = GetBaseline(corridor, baselineIndex);
      var results = new List<Dictionary<string, object?>>();

      for (var ri = 0; ri < baseline.BaselineRegions.Count; ri++)
      {
        if (regionIndex.HasValue && ri != regionIndex.Value) continue;
        var region = baseline.BaselineRegions[ri];
        using var targets = region.GetTargets();
        results.Add(new Dictionary<string, object?>
        {
          ["regionIndex"] = ri,
          ["regionName"] = region.Name,
          ["startStation"] = region.StartStation,
          ["endStation"] = region.EndStation,
          ["targets"] = targets.Cast<SubassemblyTargetInfo>().Select(info => ToTargetInfo(info, transaction)).ToList(),
        });
      }

      return new Dictionary<string, object?>
      {
        ["corridorName"] = corridor.Name,
        ["baselineIndex"] = baselineIndex,
        ["regions"] = results,
      };
    });
  }

  public static Task<object?> SetCorridorTargetMappingsAsync(JsonObject? parameters)
  {
    var corridorName = PluginRuntime.GetRequiredString(parameters, "corridorName");
    var scope = (PluginRuntime.GetOptionalString(parameters, "scope") ?? "region").ToLowerInvariant();
    var baselineIndex = PluginRuntime.GetOptionalInt(parameters, "baselineIndex") ?? 0;
    var regionIndex = PluginRuntime.GetOptionalInt(parameters, "regionIndex") ?? 0;
    var rebuild = PluginRuntime.GetOptionalBool(parameters, "rebuild") ?? true;
    var rows = ReadTargetRows(PluginRuntime.GetParameter(parameters, "targets") as JsonArray);
    if (scope is not ("region" or "baseline" or "corridor"))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "scope must be region, baseline, or corridor.");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var corridor = CivilObjectUtils.FindCorridorByName(civilDoc, transaction, corridorName, OpenMode.ForWrite);
      var baseline = scope == "corridor" ? null : GetBaseline(corridor, baselineIndex);
      var region = scope == "region" ? GetRegion(baseline!, regionIndex) : null;

      var resolved = rows
        .Select(row => (Row: row, TargetId: ResolveTargetObjectId(civilDoc, database, transaction, row.TargetType, row.TargetName)))
        .ToList();

      var applied = new List<Dictionary<string, object?>>();
      var expected = new List<(string Subassembly, string LogicalName, ObjectId TargetId)>();
      using (var targets = GetTargets(corridor, baseline, region))
      {
        foreach (var (row, targetId) in resolved)
        {
          // Index access mirrors Autodesk's sample (targets[i].TargetIds = ...; SetTargets(targets)).
          var matches = Enumerable.Range(0, targets.Count)
            .Select(i => targets[i])
            .Where(info => Matches(row, info))
            .ToList();
          if (matches.Count == 0)
          {
            var available = targets.Cast<SubassemblyTargetInfo>()
              .Select(info => $"{info.SubassemblyName}: {info.LogicalName} ('{info.DisplayName}', {info.TargetType})")
              .Distinct();
            throw new JsonRpcDispatchException(
              "CIVIL3D.INVALID_INPUT",
              $"No subassembly target matches {Describe(row)}. Available: {string.Join("; ", available)}. The transaction was not committed.");
          }

          foreach (var info in matches)
          {
            if (!IsCompatible(row.TargetType, info.TargetType))
            {
              throw new JsonRpcDispatchException(
                "CIVIL3D.INVALID_INPUT",
                $"Target '{info.LogicalName}' on '{info.SubassemblyName}' expects {info.TargetType}; a {row.TargetType} was supplied. The transaction was not committed.");
            }
            info.TargetIds = new ObjectIdCollection { targetId };
            expected.Add((info.SubassemblyName, info.LogicalName, targetId));
            applied.Add(new Dictionary<string, object?>
            {
              ["subassemblyName"] = info.SubassemblyName,
              ["logicalName"] = info.LogicalName,
              ["displayName"] = info.DisplayName,
              ["targetType"] = info.TargetType.ToString(),
              ["targetName"] = row.TargetName,
            });
          }
        }

        SetTargets(corridor, baseline, region, targets);
      }

      // Read back: every assigned target must now reference the requested object.
      using (var check = GetTargets(corridor, baseline, region))
      {
        foreach (var (subassembly, logicalName, targetId) in expected)
        {
          var info = check.Cast<SubassemblyTargetInfo>().FirstOrDefault(candidate =>
            candidate.SubassemblyName == subassembly && candidate.LogicalName == logicalName);
          if (info == null || !info.TargetIds.Cast<ObjectId>().Contains(targetId))
          {
            throw new JsonRpcDispatchException(
              "CIVIL3D.API_ERROR",
              $"Target '{logicalName}' on '{subassembly}' did not persist after SetTargets. The transaction was not committed.");
          }
        }
      }

      if (rebuild) corridor.Rebuild();

      return new Dictionary<string, object?>
      {
        ["corridorName"] = corridor.Name,
        ["scope"] = scope,
        ["baselineIndex"] = scope == "corridor" ? null : baselineIndex,
        ["regionIndex"] = scope == "region" ? regionIndex : null,
        ["applied"] = applied,
        ["rebuilt"] = rebuild,
        ["state"] = corridor.IsOutOfDate ? "out_of_date" : "built",
      };
    });
  }

  // -------------------------------------------------------------------------
  // Regions
  // -------------------------------------------------------------------------

  public static Task<object?> AddCorridorRegionAsync(JsonObject? parameters)
  {
    var corridorName = PluginRuntime.GetRequiredString(parameters, "corridorName");
    var baselineIndex = PluginRuntime.GetOptionalInt(parameters, "baselineIndex") ?? 0;
    var assemblyName = PluginRuntime.GetRequiredString(parameters, "assemblyName");
    var regionName = PluginRuntime.GetOptionalString(parameters, "regionName");
    var startStation = PluginRuntime.GetRequiredDouble(parameters, "startStation");
    var endStation = PluginRuntime.GetRequiredDouble(parameters, "endStation");
    var frequency = ReadFrequency(parameters);
    var rebuild = PluginRuntime.GetOptionalBool(parameters, "rebuild") ?? true;
    if (startStation >= endStation)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "startStation must be less than endStation.");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var corridor = CivilObjectUtils.FindCorridorByName(civilDoc, transaction, corridorName, OpenMode.ForWrite);
      var baseline = GetBaseline(corridor, baselineIndex);
      var assembly = AssemblyCreationCommands.FindAssemblyByName(civilDoc, transaction, assemblyName, OpenMode.ForRead);
      var name = regionName ?? $"Region ({baseline.BaselineRegions.Count + 1})";

      var region = baseline.BaselineRegions.Add(name, assembly.ObjectId, startStation, endStation);
      ApplyFrequency(region, frequency);
      VerifyStations(region, startStation, endStation);

      if (rebuild) corridor.Rebuild();
      return RegionResult(corridor, baseline, baselineIndex, baseline.BaselineRegions.IndexOf(region), transaction, rebuild);
    });
  }

  public static Task<object?> EditCorridorRegionAsync(JsonObject? parameters)
  {
    var corridorName = PluginRuntime.GetRequiredString(parameters, "corridorName");
    var baselineIndex = PluginRuntime.GetOptionalInt(parameters, "baselineIndex") ?? 0;
    var regionIndex = PluginRuntime.GetRequiredInt(parameters, "regionIndex");
    var regionName = PluginRuntime.GetOptionalString(parameters, "regionName");
    var assemblyName = PluginRuntime.GetOptionalString(parameters, "assemblyName");
    var startStation = PluginRuntime.GetOptionalDouble(parameters, "startStation");
    var endStation = PluginRuntime.GetOptionalDouble(parameters, "endStation");
    var frequency = ReadFrequency(parameters);
    var rebuild = PluginRuntime.GetOptionalBool(parameters, "rebuild") ?? true;

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var corridor = CivilObjectUtils.FindCorridorByName(civilDoc, transaction, corridorName, OpenMode.ForWrite);
      var baseline = GetBaseline(corridor, baselineIndex);
      var region = GetRegion(baseline, regionIndex);

      if (regionName != null) region.Name = regionName;
      if (assemblyName != null)
        region.AssemblyId = AssemblyCreationCommands.FindAssemblyByName(civilDoc, transaction, assemblyName, OpenMode.ForRead).ObjectId;
      ApplyStations(region, startStation, endStation);
      ApplyFrequency(region, frequency);
      VerifyStations(region, startStation ?? region.StartStation, endStation ?? region.EndStation);

      if (rebuild) corridor.Rebuild();
      return RegionResult(corridor, baseline, baselineIndex, regionIndex, transaction, rebuild);
    });
  }

  public static Task<object?> SplitCorridorRegionAsync(JsonObject? parameters)
  {
    var corridorName = PluginRuntime.GetRequiredString(parameters, "corridorName");
    var baselineIndex = PluginRuntime.GetOptionalInt(parameters, "baselineIndex") ?? 0;
    var regionIndex = PluginRuntime.GetRequiredInt(parameters, "regionIndex");
    var station = PluginRuntime.GetRequiredDouble(parameters, "station");
    var rebuild = PluginRuntime.GetOptionalBool(parameters, "rebuild") ?? true;

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var corridor = CivilObjectUtils.FindCorridorByName(civilDoc, transaction, corridorName, OpenMode.ForWrite);
      var baseline = GetBaseline(corridor, baselineIndex);
      var region = GetRegion(baseline, regionIndex);
      if (station <= region.StartStation + StationTolerance || station >= region.EndStation - StationTolerance)
      {
        throw new JsonRpcDispatchException(
          "CIVIL3D.INVALID_INPUT",
          $"Split station {station:F3} must lie inside region {regionIndex} ({region.StartStation:F3}–{region.EndStation:F3}).");
      }

      var countBefore = baseline.BaselineRegions.Count;
      region.Split(station);
      if (baseline.BaselineRegions.Count != countBefore + 1)
        throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", "BaselineRegion.Split did not add a region. The transaction was not committed.");

      if (rebuild) corridor.Rebuild();
      return new Dictionary<string, object?>
      {
        ["corridorName"] = corridor.Name,
        ["baselineIndex"] = baselineIndex,
        ["regions"] = ReadRegions(baseline, transaction),
        ["rebuilt"] = rebuild,
      };
    });
  }

  public static Task<object?> DeleteCorridorRegionAsync(JsonObject? parameters)
  {
    var corridorName = PluginRuntime.GetRequiredString(parameters, "corridorName");
    var baselineIndex = PluginRuntime.GetOptionalInt(parameters, "baselineIndex") ?? 0;
    var regionIndex = PluginRuntime.GetRequiredInt(parameters, "regionIndex");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var corridor = CivilObjectUtils.FindCorridorByName(civilDoc, transaction, corridorName, OpenMode.ForWrite);
      var baseline = GetBaseline(corridor, baselineIndex);
      var region = GetRegion(baseline, regionIndex);
      var deleted = new Dictionary<string, object?>
      {
        ["name"] = region.Name,
        ["startStation"] = region.StartStation,
        ["endStation"] = region.EndStation,
      };

      var countBefore = baseline.BaselineRegions.Count;
      baseline.BaselineRegions.RemoveAt(regionIndex);
      if (baseline.BaselineRegions.Count != countBefore - 1)
        throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", "BaselineRegions.RemoveAt did not remove the region. The transaction was not committed.");

      corridor.Rebuild();
      return new Dictionary<string, object?>
      {
        ["corridorName"] = corridor.Name,
        ["baselineIndex"] = baselineIndex,
        ["deletedRegionIndex"] = regionIndex,
        ["deletedRegion"] = deleted,
        ["regions"] = ReadRegions(baseline, transaction),
      };
    });
  }

  // -------------------------------------------------------------------------
  // Shared helpers (also used by CorridorCommands)
  // -------------------------------------------------------------------------

  internal sealed record FrequencySettings(
    double? AlongTangents,
    double? AlongCurves,
    double? AlongProfileCurves,
    bool? AtProfileGeometryPoints);

  internal static FrequencySettings ReadFrequency(JsonObject? parameters) => new(
    PluginRuntime.GetOptionalDouble(parameters, "frequency"),
    PluginRuntime.GetOptionalDouble(parameters, "frequencyAtCurves"),
    PluginRuntime.GetOptionalDouble(parameters, "frequencyAlongProfileCurves"),
    PluginRuntime.GetOptionalBool(parameters, "appliedAtProfileGeometryPoints"));

  internal static void ApplyFrequency(BaselineRegion region, FrequencySettings frequency)
  {
    foreach (var value in new[] { frequency.AlongTangents, frequency.AlongCurves, frequency.AlongProfileCurves })
    {
      if (value is <= 0)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Frequencies must be positive.");
    }

    var setting = region.AppliedAssemblySetting;
    if (frequency.AlongTangents is double tangents) setting.FrequencyAlongTangents = tangents;
    if (frequency.AlongCurves is double curves)
    {
      setting.FrequencyAlongCurves = curves;
      setting.FrequencyAlongSpirals = curves;
    }
    if (frequency.AlongProfileCurves is double profileCurves) setting.FrequencyAlongProfileCurves = profileCurves;
    if (frequency.AtProfileGeometryPoints is bool atPoints) setting.AppliedAtProfileGeometryPoints = atPoints;

    // Read back through a fresh accessor; the setting object must write through.
    var check = region.AppliedAssemblySetting;
    if ((frequency.AlongTangents is double t && Math.Abs(check.FrequencyAlongTangents - t) > 1e-9) ||
        (frequency.AlongCurves is double c && Math.Abs(check.FrequencyAlongCurves - c) > 1e-9) ||
        (frequency.AlongProfileCurves is double p && Math.Abs(check.FrequencyAlongProfileCurves - p) > 1e-9) ||
        (frequency.AtProfileGeometryPoints is bool a && check.AppliedAtProfileGeometryPoints != a))
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.API_ERROR",
        "AppliedAssemblySetting did not persist the requested frequencies. The transaction was not committed.");
    }
  }

  internal static void ApplyStations(BaselineRegion region, double? startStation, double? endStation)
  {
    var start = startStation ?? region.StartStation;
    var end = endStation ?? region.EndStation;
    if (start >= end)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"startStation ({start:F3}) must be less than endStation ({end:F3}).");

    // Order the writes so the region never becomes inverted in between.
    if (start >= region.EndStation)
    {
      region.EndStation = end;
      region.StartStation = start;
    }
    else
    {
      region.StartStation = start;
      region.EndStation = end;
    }
  }

  internal static void VerifyStations(BaselineRegion region, double startStation, double endStation)
  {
    if (Math.Abs(region.StartStation - startStation) > StationTolerance ||
        Math.Abs(region.EndStation - endStation) > StationTolerance)
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.INVALID_INPUT",
        $"Civil 3D set region '{region.Name}' to {region.StartStation:F3}–{region.EndStation:F3} instead of {startStation:F3}–{endStation:F3} (outside the baseline or overlapping another region?). The transaction was not committed.");
    }
  }

  internal static Dictionary<string, object?> ToRegionInfo(BaselineRegion region, int index, Transaction transaction)
  {
    var setting = region.AppliedAssemblySetting;
    return new Dictionary<string, object?>
    {
      ["index"] = index,
      ["name"] = region.Name,
      ["assemblyName"] = region.AssemblyId.IsNull
        ? string.Empty
        : CivilObjectUtils.GetName(transaction.GetObject(region.AssemblyId, OpenMode.ForRead)) ?? string.Empty,
      ["startStation"] = region.StartStation,
      ["endStation"] = region.EndStation,
      ["frequency"] = setting.FrequencyAlongTangents,
      ["frequencyAtCurves"] = setting.FrequencyAlongCurves,
      ["frequencyAlongProfileCurves"] = setting.FrequencyAlongProfileCurves,
      ["appliedAtProfileGeometryPoints"] = setting.AppliedAtProfileGeometryPoints,
    };
  }

  internal static List<Dictionary<string, object?>> ReadRegions(Baseline baseline, Transaction transaction)
  {
    var regions = new List<Dictionary<string, object?>>();
    for (var i = 0; i < baseline.BaselineRegions.Count; i++)
      regions.Add(ToRegionInfo(baseline.BaselineRegions[i], i, transaction));
    return regions;
  }

  internal static Baseline GetBaseline(Corridor corridor, int index)
  {
    if (index < 0 || index >= corridor.Baselines.Count)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
        $"Baseline index {index} is out of range. Corridor '{corridor.Name}' has {corridor.Baselines.Count} baseline(s).");
    return corridor.Baselines[index];
  }

  private static BaselineRegion GetRegion(Baseline baseline, int index)
  {
    if (index < 0 || index >= baseline.BaselineRegions.Count)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
        $"Region index {index} is out of range. Baseline '{baseline.Name}' has {baseline.BaselineRegions.Count} region(s).");
    return baseline.BaselineRegions[index];
  }

  private static Dictionary<string, object?> RegionResult(
    Corridor corridor, Baseline baseline, int baselineIndex, int regionIndex, Transaction transaction, bool rebuilt)
  {
    return new Dictionary<string, object?>
    {
      ["corridorName"] = corridor.Name,
      ["baselineIndex"] = baselineIndex,
      ["regionIndex"] = regionIndex,
      ["region"] = ToRegionInfo(baseline.BaselineRegions[regionIndex], regionIndex, transaction),
      ["regions"] = ReadRegions(baseline, transaction),
      ["rebuilt"] = rebuilt,
    };
  }

  // -------------------------------------------------------------------------
  // Target helpers
  // -------------------------------------------------------------------------

  private sealed record TargetRow(string? LogicalName, string? DisplayName, string? SubassemblyName, string TargetType, string TargetName);

  private static List<TargetRow> ReadTargetRows(JsonArray? targets)
  {
    if (targets == null || targets.Count == 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "targets must contain at least one row.");

    var rows = new List<TargetRow>();
    foreach (var node in targets)
    {
      if (node is not JsonObject row)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Each target must be an object.");
      var logicalName = row["logicalName"]?.GetValue<string>();
      var displayName = row["displayName"]?.GetValue<string>();
      var targetType = row["targetType"]?.GetValue<string>();
      var targetName = row["targetName"]?.GetValue<string>();
      if (string.IsNullOrWhiteSpace(logicalName) && string.IsNullOrWhiteSpace(displayName))
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Each target needs logicalName or displayName.");
      if (string.IsNullOrWhiteSpace(targetType) || string.IsNullOrWhiteSpace(targetName))
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Each target needs targetType and targetName.");
      rows.Add(new TargetRow(logicalName, displayName, row["subassemblyName"]?.GetValue<string>(), targetType!, targetName!));
    }
    return rows;
  }

  private static bool Matches(TargetRow row, SubassemblyTargetInfo info)
  {
    var nameMatches =
      (row.LogicalName != null && string.Equals(row.LogicalName, info.LogicalName, StringComparison.OrdinalIgnoreCase)) ||
      (row.DisplayName != null && string.Equals(row.DisplayName, info.DisplayName, StringComparison.OrdinalIgnoreCase));
    var subassemblyMatches = row.SubassemblyName == null ||
      string.Equals(row.SubassemblyName, info.SubassemblyName, StringComparison.OrdinalIgnoreCase);
    return nameMatches && subassemblyMatches;
  }

  private static string Describe(TargetRow row) =>
    $"logicalName '{row.LogicalName}' / displayName '{row.DisplayName}'" +
    (row.SubassemblyName == null ? string.Empty : $" on '{row.SubassemblyName}'");

  private static bool IsCompatible(string suppliedType, SubassemblyLogicalNameType expected) =>
    suppliedType.ToLowerInvariant() switch
    {
      "surface" => expected == SubassemblyLogicalNameType.Surface,
      "alignment" => expected is SubassemblyLogicalNameType.Alignment or SubassemblyLogicalNameType.Offset,
      "profile" => expected is SubassemblyLogicalNameType.Profile or SubassemblyLogicalNameType.Elevation,
      "polyline" => expected is SubassemblyLogicalNameType.Offset or SubassemblyLogicalNameType.Elevation,
      _ => false,
    };

  private static Dictionary<string, object?> ToTargetInfo(SubassemblyTargetInfo info, Transaction transaction)
  {
    // TargetToOption and UseSameSideTarget throw "The count of TargetIds should be greater
    // or equal to 2" below two targets (AeccDbMgd 2026; live 2026-10-02), so they are only
    // read when they apply.
    var multipleTargets = info.TargetIds.Count >= 2;
    return new Dictionary<string, object?>
    {
      ["subassemblyName"] = info.SubassemblyName,
      ["assemblyGroupName"] = info.AssemblyGroupName,
      ["logicalName"] = info.LogicalName,
      ["displayName"] = info.DisplayName,
      ["targetType"] = info.TargetType.ToString(),
      ["targetToOption"] = multipleTargets ? info.TargetToOption.ToString() : null,
      ["useSameSideTarget"] = multipleTargets ? info.UseSameSideTarget : null,
      ["targetNames"] = info.TargetIds.Cast<ObjectId>()
        .Where(id => !id.IsNull && !id.IsErased)
        .Select(id => CivilObjectUtils.GetName(transaction.GetObject(id, OpenMode.ForRead)) ?? id.Handle.ToString())
        .ToList(),
    };
  }

  private static SubassemblyTargetInfoCollection GetTargets(Corridor corridor, Baseline? baseline, BaselineRegion? region) =>
    region?.GetTargets() ?? baseline?.GetTargets() ?? corridor.GetTargets();

  private static void SetTargets(Corridor corridor, Baseline? baseline, BaselineRegion? region, SubassemblyTargetInfoCollection targets)
  {
    if (region != null) region.SetTargets(targets);
    else if (baseline != null) baseline.SetTargets(targets);
    else corridor.SetTargets(targets);
  }

  private static ObjectId ResolveTargetObjectId(
    CivilDocument civilDoc, Database database, Transaction transaction, string targetType, string targetName)
  {
    switch (targetType.ToLowerInvariant())
    {
      case "surface":
        foreach (ObjectId id in civilDoc.GetSurfaceIds())
        {
          var surface = CivilObjectUtils.GetRequiredObject<CivilSurface>(transaction, id, OpenMode.ForRead);
          if (string.Equals(surface.Name, targetName, StringComparison.OrdinalIgnoreCase)) return id;
        }
        break;
      case "alignment":
        return CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, targetName).ObjectId;
      case "profile":
        foreach (ObjectId alignmentId in civilDoc.GetAlignmentIds())
        {
          var alignment = CivilObjectUtils.GetRequiredObject<Alignment>(transaction, alignmentId, OpenMode.ForRead);
          foreach (ObjectId profileId in alignment.GetProfileIds())
          {
            var profile = CivilObjectUtils.GetRequiredObject<Profile>(transaction, profileId, OpenMode.ForRead);
            if (string.Equals(profile.Name, targetName, StringComparison.OrdinalIgnoreCase)) return profileId;
          }
        }
        break;
      case "polyline":
        // Polylines have no name; targetName is the entity handle (hex).
        if (long.TryParse(targetName, System.Globalization.NumberStyles.HexNumber, null, out var handleValue) &&
            database.TryGetObjectId(new Handle(handleValue), out var polylineId) &&
            transaction.GetObject(polylineId, OpenMode.ForRead) is Polyline)
        {
          return polylineId;
        }
        break;
    }

    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Target {targetType} '{targetName}' was not found.");
  }
}
