using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.DatabaseServices;

namespace Civil3DMcpPlugin;

/// <summary>
/// Editing commands for Civil 3D vertical profiles and profile views:
/// add_pvi, delete_pvi, add_curve, set_grade, get_elevation,
/// check_k_values, profile_view_create, profile_view_band_set.
/// </summary>
public static class ProfileEditCommands
{
  // ─── profileAddPvi ────────────────────────────────────────────────────────

  public static Task<object?> AddPviAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var profileName = PluginRuntime.GetRequiredString(parameters, "profileName");
    // C63 (2026-10-08): many PVIs in one transaction. Every committed profile edit makes Civil 3D update the
    // profile views on the alignment (live: about 100 s per commit with nine views and their pipe networks),
    // so 82 single add_pvi calls took hours. pvis = [{station, elevation}]; replaceAll removes the profile's PVIs
    // first, in the same transaction, so the profile object (and corridors built on it) stays.
    var list = PluginRuntime.GetParameter(parameters, "pvis") as JsonArray;
    var replaceAll = PluginRuntime.GetOptionalBool(parameters, "replaceAll") ?? false;
    List<(double Station, double Elevation)> pvis;
    if (list != null)
    {
      pvis = list.Select(n => n is JsonObject o
          ? (o["station"]?.GetValue<double>() ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Each pvi needs station."),
             o["elevation"]?.GetValue<double>() ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Each pvi needs elevation."))
          : throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Each pvi must be an object {station, elevation}."))
        .ToList();
      if (pvis.Count == 0)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "pvis is empty. Nothing was changed.");
      if (pvis.Select(p => Math.Round(p.Station, 6)).Distinct().Count() != pvis.Count)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "pvis has the same station twice. Nothing was changed.");
    }
    else
    {
      if (replaceAll)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "replaceAll needs pvis. Nothing was changed.");
      pvis = new List<(double, double)> { (PluginRuntime.GetRequiredDouble(parameters, "station"), PluginRuntime.GetRequiredDouble(parameters, "elevation")) };
    }

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var profile = CivilObjectUtils.FindProfileByName(alignment, transaction, profileName, OpenMode.ForWrite);

      var removed = 0;
      var ordered = pvis.OrderBy(p => p.Station).ToList();
      if (replaceAll && profile.PVIs.Count > 0)
      {
        // Civil 3D will not remove the first or the last PVI (live 2026-10-08), and moving an end PVI's station
        // failed the same way. So: an end at the same station (1 mm) only gets the new elevation, otherwise the new
        // end is added first (the old end becomes an inner PVI); then every inner PVI is removed by index.
        var first = ordered[0];
        var last = ordered[^1];
        var oldFirst = profile.PVIs[0].RawStation;
        var oldLast = profile.PVIs[profile.PVIs.Count - 1].RawStation;
        try
        {
          foreach (var end in ordered.Count > 1 ? new[] { first, last } : new[] { first })
          {
            ProfilePVI? same = null;
            for (var i = 0; i < profile.PVIs.Count; i++)
              if (Math.Abs(profile.PVIs[i].RawStation - end.Station) < 0.001) same = profile.PVIs[i];
            if (same != null) same.Elevation = end.Elevation;
            else profile.PVIs.AddPVI(end.Station, end.Elevation);
          }

          for (var i = profile.PVIs.Count - 2; i >= 1; i--)
          {
            profile.PVIs.RemoveAt(i);
            removed++;
          }
        }
        catch (InvalidOperationException ex)
        {
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
            $"replaceAll on '{profile.Name}': {ex.Message} (old PVIs {oldFirst:F4}-{oldLast:F4}, new {first.Station:F4}-{last.Station:F4}; "
            + "an old end outside the new range cannot be removed). Nothing was changed.");
        }

        if (profile.PVIs.Count != Math.Min(2, ordered.Count)
            || Math.Abs(profile.PVIs[0].RawStation - first.Station) > 0.001
            || Math.Abs(profile.PVIs[profile.PVIs.Count - 1].RawStation - last.Station) > 0.001)
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
            $"replaceAll on '{profile.Name}': the old ends ({oldFirst:F4}-{oldLast:F4}) lie outside the new ones ({first.Station:F4}-{last.Station:F4}) and cannot be removed. Nothing was changed.");
        ordered = ordered.Count > 1 ? ordered.Skip(1).Take(ordered.Count - 2).ToList() : new List<(double, double)>();
      }

      // C165: a PVI already at the station (1 mm) is a conflict; Civil threw "Value does not fall within the expected
      // range" (ArgumentException, reported as an internal error; coverage sweep 2026-10-21).
      foreach (var (station, _) in ordered)
        for (var i = 0; i < profile.PVIs.Count; i++)
          if (Math.Abs(profile.PVIs[i].RawStation - station) < 0.001)
            throw new JsonRpcDispatchException("CIVIL3D.CONFLICT",
              $"'{profile.Name}' already has a PVI at station {station:F3}; change its elevation, delete it first, or give replaceAll. Nothing was changed.");
      foreach (var (station, elevation) in ordered)
      {
        try
        {
          profile.PVIs.AddPVI(station, elevation);
        }
        catch (ArgumentException)
        {
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
            $"Civil 3D refused a PVI at station {station:F3} on '{profile.Name}' (alignment '{alignment.Name}' runs {alignment.StartingStation:F3}-{alignment.EndingStation:F3}, " +
            $"profile {profile.StartingStation:F3}-{profile.EndingStation:F3}). Nothing was changed.");
        }
      }

      if (list == null)
      {
        return new Dictionary<string, object?>
        {
          ["alignmentName"] = alignment.Name,
          ["profileName"] = profile.Name,
          ["station"] = pvis[0].Station,
          ["elevation"] = pvis[0].Elevation,
          ["success"] = true,
        };
      }

      return new Dictionary<string, object?>
      {
        ["alignmentName"] = alignment.Name,
        ["profileName"] = profile.Name,
        ["removed"] = removed,
        ["added"] = pvis.Count,
        ["pviCount"] = profile.PVIs.Count,
        ["success"] = true,
      };
    });
  }

  // ─── profileDeletePvi ─────────────────────────────────────────────────────

  public static Task<object?> DeletePviAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var profileName = PluginRuntime.GetRequiredString(parameters, "profileName");
    var station = PluginRuntime.GetRequiredDouble(parameters, "station");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var profile = CivilObjectUtils.FindProfileByName(alignment, transaction, profileName, OpenMode.ForWrite);

      // The nearest PVI used to be removed whatever the distance, so a mistyped station
      // silently deleted another PVI. Require a match within the tolerance.
      var tolerance = PluginRuntime.GetOptionalDouble(parameters, "tolerance") ?? 0.01;
      var targetPvi = FindPviNearStation(profile.PVIs, station);
      if (targetPvi == null || Math.Abs(targetPvi.RawStation - station) > tolerance)
      {
        throw new JsonRpcDispatchException(
          "CIVIL3D.OBJECT_NOT_FOUND",
          $"No PVI within {tolerance} m of station {station} in profile '{profileName}'"
          + (targetPvi == null ? "." : $" (nearest at {targetPvi.RawStation:F3}). Nothing was deleted."));
      }

      var removedStation = targetPvi.RawStation;
      var removedElevation = targetPvi.Elevation;
      profile.PVIs.RemoveAt(removedStation, removedElevation);

      return new Dictionary<string, object?>
      {
        ["alignmentName"] = alignment.Name,
        ["profileName"] = profile.Name,
        ["station"] = station,
        ["removedStation"] = removedStation,
        ["removedElevation"] = removedElevation,
        ["success"] = true,
      };
    });
  }

  // ─── profileAddCurve ──────────────────────────────────────────────────────

  public static Task<object?> AddCurveAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var profileName = PluginRuntime.GetRequiredString(parameters, "profileName");
    var pviStation = PluginRuntime.GetRequiredDouble(parameters, "pviStation");
    var length = PluginRuntime.GetRequiredDouble(parameters, "length");
    var curveType = PluginRuntime.GetOptionalString(parameters, "curveType") ?? "symmetric_parabola";

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var profile = CivilObjectUtils.FindProfileByName(alignment, transaction, profileName, OpenMode.ForWrite);

      if (!string.Equals(curveType, "symmetric_parabola", StringComparison.OrdinalIgnoreCase))
      {
        throw new JsonRpcDispatchException(
          "CIVIL3D.API_ERROR",
          $"Curve type '{curveType}' is not implemented. Civil 3D's typed API path currently supports symmetric_parabola only.");
      }

      var targetPvi = FindPviNearStation(profile.PVIs, pviStation);
      if (targetPvi == null)
      {
        throw new JsonRpcDispatchException(
          "CIVIL3D.OBJECT_NOT_FOUND",
          $"No PVI found near station {pviStation} in profile '{profileName}'.");
      }

      profile.Entities.AddFreeSymmetricParabolaByPVIAndCurveLength(targetPvi, length);

      return new Dictionary<string, object?>
      {
        ["alignmentName"] = alignment.Name,
        ["profileName"] = profile.Name,
        ["pviStation"] = pviStation,
        ["curveLength"] = length,
        ["curveType"] = curveType,
        ["success"] = true,
      };
    });
  }

  // ─── profileSetGrade ──────────────────────────────────────────────────────

  public static Task<object?> SetGradeAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var profileName = PluginRuntime.GetRequiredString(parameters, "profileName");
    var entityIndex = (int)(PluginRuntime.GetRequiredDouble(parameters, "entityIndex"));
    var grade = PluginRuntime.GetRequiredDouble(parameters, "grade");

    throw new JsonRpcDispatchException(
      "CIVIL3D.API_ERROR",
      $"Cannot set grade {grade} on profile entity {entityIndex} in '{profileName}': ProfileTangent.Grade is read-only in the Civil 3D 2026 .NET API. " +
      "Edit the adjoining PVIs instead.");
  }

  // ─── profileGetElevation ──────────────────────────────────────────────────

  /// <summary>
  /// Delegates to the same underlying implementation as
  /// ProfileCommands.GetProfileElevationAsync but is exposed as a
  /// dedicated tool per JFS-10 requirements.
  /// </summary>
  public static Task<object?> GetElevationAsync(JsonObject? parameters)
  {
    return ProfileCommands.GetProfileElevationAsync(parameters);
  }

  // ─── profileCheckKValues ──────────────────────────────────────────────────

  public static Task<object?> CheckKValuesAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var profileName = PluginRuntime.GetRequiredString(parameters, "profileName");
    var designSpeed = PluginRuntime.GetRequiredDouble(parameters, "designSpeed");

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var profile = CivilObjectUtils.FindProfileByName(alignment, transaction, profileName, OpenMode.ForRead);

      var entities = CivilObjectUtils.GetPropertyValue<object>(profile, "Entities");
      if (entities == null)
      {
        throw new JsonRpcDispatchException(
          "CIVIL3D.TRANSACTION_FAILED",
          $"Profile '{profileName}' does not expose an Entities collection.");
      }

      // AASHTO minimum K values table (metric km/h → K_sag, K_crest)
      // Source: AASHTO Green Book 2011 Table 3-36 / 3-37
      var kTable = BuildAashtoKTable();
      var (kSagMin, kCrestMin) = LookupKValues(kTable, designSpeed);

      var results = new List<Dictionary<string, object?>>();
      var index = 0;
      foreach (var entity in (System.Collections.IEnumerable)entities)
      {
        var entityType = entity?.GetType().Name ?? string.Empty;
        var isCurve = entityType.ToLowerInvariant().Contains("parabola")
          || entityType.ToLowerInvariant().Contains("curve");
        if (!isCurve)
        {
          index++;
          continue;
        }

        var curveLength = CivilObjectUtils.GetPropertyValue<double?>(entity, "Length") ?? 0;
        var gradeIn = CivilObjectUtils.GetPropertyValue<double?>(entity, "GradeIn")
          ?? CivilObjectUtils.GetPropertyValue<double?>(entity, "StartGrade") ?? 0;
        var gradeOut = CivilObjectUtils.GetPropertyValue<double?>(entity, "GradeOut")
          ?? CivilObjectUtils.GetPropertyValue<double?>(entity, "EndGrade") ?? 0;
        var algebraicDiff = Math.Abs(gradeOut - gradeIn);
        var kValue = algebraicDiff > 1e-10 ? curveLength / algebraicDiff : double.PositiveInfinity;

        var isSag = gradeOut > gradeIn;
        var requiredK = isSag ? kSagMin : kCrestMin;
        var passes = kValue >= requiredK || double.IsPositiveInfinity(kValue);

        results.Add(new Dictionary<string, object?>
        {
          ["entityIndex"] = index,
          ["curveType"] = isSag ? "sag" : "crest",
          ["curveLength"] = curveLength,
          ["gradeIn"] = gradeIn,
          ["gradeOut"] = gradeOut,
          ["algebraicDifference"] = algebraicDiff,
          ["kValue"] = double.IsPositiveInfinity(kValue) ? null : (object?)kValue,
          ["requiredK"] = requiredK,
          ["passes"] = passes,
        });
        index++;
      }

      var allPass = results.All(r => (bool)(r["passes"] ?? false));
      return new Dictionary<string, object?>
      {
        ["alignmentName"] = alignment.Name,
        ["profileName"] = profile.Name,
        ["designSpeed"] = designSpeed,
        ["kSagMinimum"] = kSagMin,
        ["kCrestMinimum"] = kCrestMin,
        ["curves"] = results,
        ["allPass"] = allPass,
        ["summary"] = allPass
          ? $"All {results.Count} vertical curve(s) meet minimum K values for {designSpeed} design speed."
          : $"{results.Count(r => !(bool)(r["passes"] ?? false))} of {results.Count} curve(s) fail minimum K value requirements.",
      };
    });
  }

  // ─── profileViewCreate ────────────────────────────────────────────────────

  public static Task<object?> ProfileViewCreateAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var profileViewName = PluginRuntime.GetRequiredString(parameters, "profileViewName");
    var insertX = PluginRuntime.GetRequiredDouble(parameters, "insertX");
    var insertY = PluginRuntime.GetRequiredDouble(parameters, "insertY");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var insertionPoint = new Point3d(insertX, insertY, 0);

      if (TryFindProfileViewByName(civilDoc, transaction, profileViewName) != null)
      {
        throw new JsonRpcDispatchException(
          "CIVIL3D.CONFLICT",
          $"A profile view named '{profileViewName}' already exists. Nothing was created.");
      }

      var styleId = LookupUtils.GetProfileViewStyleId(
        civilDoc, transaction, PluginRuntime.GetOptionalString(parameters, "style"));
      var bandSetId = LookupUtils.GetProfileViewBandSetId(
        civilDoc, transaction, PluginRuntime.GetOptionalString(parameters, "bandSet"));

      // AeccDbMgd 2026: Create(alignmentId, insertPosition, profileViewName, bandSetId, styleId).
      // The previous reflective calls used argument orders that do not exist and always returned null.
      var pvId = ProfileView.Create(alignment.ObjectId, insertionPoint, profileViewName, bandSetId, styleId);
      if (pvId.IsNull)
      {
        throw new JsonRpcDispatchException(
          "CIVIL3D.TRANSACTION_FAILED",
          $"ProfileView.Create returned a null id for alignment '{alignment.Name}'.");
      }

      var profileView = CivilObjectUtils.GetRequiredObject<ProfileView>(
        transaction, pvId, OpenMode.ForRead);

      return new Dictionary<string, object?>
      {
        ["profileViewName"] = profileView.Name,
        ["handle"] = CivilObjectUtils.GetHandle(profileView),
        ["alignmentName"] = alignment.Name,
        ["insertX"] = insertX,
        ["insertY"] = insertY,
        ["style"] = CivilObjectUtils.GetName(transaction.GetObject(styleId, OpenMode.ForRead)),
        ["bandSet"] = CivilObjectUtils.GetName(transaction.GetObject(bandSetId, OpenMode.ForRead)),
        ["success"] = true,
      };
    });
  }

  // ─── profileViewBandSet ───────────────────────────────────────────────────

  public static Task<object?> ProfileViewBandSetAsync(JsonObject? parameters)
  {
    var profileViewName = PluginRuntime.GetRequiredString(parameters, "profileViewName");
    var bandSetName = PluginRuntime.GetRequiredString(parameters, "bandSetName");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var profileView = FindProfileViewByName(civilDoc, transaction, profileViewName);
      var writeView = CivilObjectUtils.GetRequiredObject<ProfileView>(
        transaction, profileView.ObjectId, OpenMode.ForWrite);

      var bandSetId = LookupUtils.GetProfileViewBandSetId(civilDoc, transaction, bandSetName);

      writeView.Bands.ImportBandSetStyle(bandSetId);

      return new Dictionary<string, object?>
      {
        ["profileViewName"] = profileView.Name,
        ["bandSetName"] = bandSetName,
        ["success"] = true,
      };
    });
  }

  // ─── profile view: which profiles are drawn ──────────────────────────────

  public static Task<object?> GetProfileViewProfilesAsync(JsonObject? parameters)
  {
    var profileViewName = PluginRuntime.GetRequiredString(parameters, "profileViewName");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var profileView = FindProfileViewByName(civilDoc, transaction, profileViewName);
      return new Dictionary<string, object?>
      {
        ["profileViewName"] = profileView.Name,
        ["profiles"] = ListDrawnProfiles(profileView),
      };
    });
  }

  public static Task<object?> SetProfileViewProfilesAsync(JsonObject? parameters)
  {
    var profileViewName = PluginRuntime.GetRequiredString(parameters, "profileViewName");
    if (PluginRuntime.GetParameter(parameters, "draw") is not JsonObject drawNode || drawNode.Count == 0)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "draw must map at least one profile name to true or false.");
    }

    var draw = drawNode.ToDictionary(
      kv => kv.Key,
      kv => kv.Value?.GetValue<bool>() ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"draw['{kv.Key}'] must be true or false."),
      StringComparer.OrdinalIgnoreCase);

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var found = FindProfileViewByName(civilDoc, transaction, profileViewName);
      var profileView = CivilObjectUtils.GetRequiredObject<ProfileView>(transaction, found.ObjectId, OpenMode.ForWrite);
      var available = profileView.GraphOverrides.Select(o => o.ProfileName).ToList();
      var unknown = draw.Keys.Where(k => !available.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList();
      if (unknown.Count > 0)
      {
        throw new JsonRpcDispatchException(
          "CIVIL3D.INVALID_INPUT",
          $"Profile(s) {string.Join(", ", unknown)} are not in profile view '{profileView.Name}'. Available: {string.Join(", ", available)}. Nothing was changed.");
      }

      foreach (var profileOverride in profileView.GraphOverrides)
      {
        if (draw.TryGetValue(profileOverride.ProfileName, out var value))
        {
          profileOverride.Draw = value;
        }
      }

      var readBack = ListDrawnProfiles(profileView);
      var notApplied = readBack
        .Where(p => draw.TryGetValue((string)p["profileName"]!, out var wanted) && (bool)p["draw"]! != wanted)
        .Select(p => (string)p["profileName"]!)
        .ToList();
      if (notApplied.Count > 0)
      {
        throw new JsonRpcDispatchException(
          "CIVIL3D.TRANSACTION_FAILED",
          $"Draw did not persist for {string.Join(", ", notApplied)} in profile view '{profileView.Name}'.");
      }

      return new Dictionary<string, object?>
      {
        ["profileViewName"] = profileView.Name,
        ["profiles"] = readBack,
      };
    });
  }

  private static List<Dictionary<string, object?>> ListDrawnProfiles(ProfileView profileView) =>
    profileView.GraphOverrides
      .Select(o => new Dictionary<string, object?> { ["profileName"] = o.ProfileName, ["draw"] = o.Draw })
      .ToList();

  // ─── Private helpers ─────────────────────────────────────────────────────

  internal static ProfileView FindProfileViewByName(
    Autodesk.Civil.ApplicationServices.CivilDocument civilDoc,
    Transaction transaction,
    string name)
  {
    return TryFindProfileViewByName(civilDoc, transaction, name)
      ?? throw new JsonRpcDispatchException(
        "CIVIL3D.OBJECT_NOT_FOUND",
        $"Profile view '{name}' was not found in model space.");
  }

  private static ProfileView? TryFindProfileViewByName(
    Autodesk.Civil.ApplicationServices.CivilDocument civilDoc,
    Transaction transaction,
    string name)
  {
    // Profile views live in model space; enumerate all ProfileView objects
    var database = CivilObjectUtils.GetDatabase(civilDoc);
    var blockTable = CivilObjectUtils.GetRequiredObject<BlockTable>(
      transaction, database.BlockTableId, OpenMode.ForRead);
    var modelSpace = CivilObjectUtils.GetRequiredObject<BlockTableRecord>(
      transaction, blockTable[BlockTableRecord.ModelSpace], OpenMode.ForRead);

    foreach (ObjectId objectId in modelSpace)
    {
      var obj = transaction.GetObject(objectId, OpenMode.ForRead);
      if (obj is ProfileView pv
        && string.Equals(pv.Name, name, StringComparison.OrdinalIgnoreCase))
      {
        return pv;
      }
    }

    return null;
  }

  private static ProfilePVI? FindPviNearStation(ProfilePVICollection pvis, double targetStation)
  {
    ProfilePVI? closest = null;
    var minDist = double.MaxValue;

    foreach (ProfilePVI pvi in pvis)
    {
      var dist = Math.Abs(pvi.RawStation - targetStation);
      if (dist < minDist)
      {
        minDist = dist;
        closest = pvi;
      }
    }

    return closest;
  }

  /// <summary>
  /// AASHTO minimum K values (metric, km/h).
  /// Returns (K_sag_min, K_crest_min).
  /// Source: AASHTO A Policy on Geometric Design of Highways and Streets, 2011.
  /// </summary>
  private static List<(double speed, double kSag, double kCrest)> BuildAashtoKTable() =>
  [
    (30, 3, 1),
    (40, 7, 2),
    (50, 9, 4),
    (60, 11, 6),
    (70, 14, 10),
    (80, 19, 17),
    (90, 24, 29),
    (100, 30, 44),
    (110, 37, 60),
    (120, 46, 84),
    (130, 57, 114),
  ];

  private static (double kSag, double kCrest) LookupKValues(
    List<(double speed, double kSag, double kCrest)> table,
    double designSpeed)
  {
    // Find exact match first
    var exact = table.FirstOrDefault(t => Math.Abs(t.speed - designSpeed) < 0.5);
    if (exact != default)
    {
      return (exact.kSag, exact.kCrest);
    }

    // Interpolate between nearest values
    var lower = table.LastOrDefault(t => t.speed <= designSpeed);
    var upper = table.FirstOrDefault(t => t.speed > designSpeed);

    if (lower == default)
    {
      return (table[0].kSag, table[0].kCrest);
    }

    if (upper == default)
    {
      return (table[^1].kSag, table[^1].kCrest);
    }

    var ratio = (designSpeed - lower.speed) / (upper.speed - lower.speed);
    return (
      lower.kSag + ratio * (upper.kSag - lower.kSag),
      lower.kCrest + ratio * (upper.kCrest - lower.kCrest));
  }
}
