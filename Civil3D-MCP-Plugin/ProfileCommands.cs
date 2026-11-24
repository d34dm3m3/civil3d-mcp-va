using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.DatabaseServices;

namespace Civil3DMcpPlugin;

public static class ProfileCommands
{
  public static Task<object?> ListProfilesAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var profiles = alignment.GetProfileIds()
        .Cast<ObjectId>()
        .Select(id => CivilObjectUtils.GetRequiredObject<Profile>(transaction, id, OpenMode.ForRead))
        .Select(ToProfileSummary)
        .ToList();

      return new Dictionary<string, object?>
      {
        ["alignmentName"] = alignment.Name,
        ["profiles"] = profiles,
      };
    });
  }

  public static Task<object?> GetProfileAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var profileName = PluginRuntime.GetRequiredString(parameters, "profileName");

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var profile = CivilObjectUtils.FindProfileByName(alignment, transaction, profileName, OpenMode.ForRead);
      var entities = ReadProfileEntities(profile);

      return new Dictionary<string, object?>
      {
        ["name"] = profile.Name,
        ["handle"] = CivilObjectUtils.GetHandle(profile),
        ["type"] = MapProfileType(profile.ProfileType.ToString()),
        ["style"] = CivilObjectUtils.GetName(transaction.GetObject(profile.StyleId, OpenMode.ForRead)) ?? string.Empty,
        ["layer"] = profile.Layer,
        ["startStation"] = profile.StartingStation,
        ["endStation"] = profile.EndingStation,
        ["minElevation"] = GetElevationExtents(profile).Min,
        ["maxElevation"] = GetElevationExtents(profile).Max,
        ["entityCount"] = entities.Count,
        ["entities"] = entities,
        ["pviCount"] = CountPvis(profile),
        // C157: the PVIs themselves (C14: only the count was returned)
        ["pvis"] = profile.PVIs.Cast<ProfilePVI>().Select(v => new Dictionary<string, object?>
        {
          ["station"] = Math.Round(v.RawStation, 4),
          ["elevation"] = Math.Round(v.Elevation, 4),
          ["type"] = v.PVIType.ToString(),
        }).ToList(),
        ["units"] = new Dictionary<string, object?>
        {
          ["horizontal"] = CivilObjectUtils.LinearUnits(database),
          ["vertical"] = CivilObjectUtils.LinearUnits(database),
        },
      };
    });
  }

  public static Task<object?> GetProfileElevationAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var profileName = PluginRuntime.GetRequiredString(parameters, "profileName");
    var station = PluginRuntime.GetRequiredDouble(parameters, "station");

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var profile = CivilObjectUtils.FindProfileByName(alignment, transaction, profileName, OpenMode.ForRead);
      var elevation = ReadProfileElevation(profile, station);
      var grade = ReadProfileGrade(profile, station);

      return new Dictionary<string, object?>
      {
        ["station"] = station,
        ["elevation"] = elevation,
        ["grade"] = grade,
        ["units"] = CivilObjectUtils.LinearUnits(database),
      };
    });
  }

  public static Task<object?> SampleProfileElevationsAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var profileName = PluginRuntime.GetRequiredString(parameters, "profileName");
    var interval = PluginRuntime.GetRequiredDouble(parameters, "interval");
    var startStation = PluginRuntime.GetOptionalDouble(parameters, "startStation");
    var endStation = PluginRuntime.GetOptionalDouble(parameters, "endStation");

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var profile = CivilObjectUtils.FindProfileByName(alignment, transaction, profileName, OpenMode.ForRead);
      var from = startStation ?? profile.StartingStation;
      var to = endStation ?? profile.EndingStation;
      var samples = new List<Dictionary<string, object?>>();

      for (var station = from; station <= to; station += interval)
      {
        samples.Add(new Dictionary<string, object?>
        {
          ["station"] = station,
          ["elevation"] = ReadProfileElevation(profile, station),
          ["grade"] = ReadProfileGrade(profile, station),
        });
      }

      return new Dictionary<string, object?>
      {
        ["alignmentName"] = alignment.Name,
        ["profileName"] = profile.Name,
        ["startStation"] = from,
        ["endStation"] = to,
        ["interval"] = interval,
        ["samples"] = samples,
        ["units"] = CivilObjectUtils.LinearUnits(database),
      };
    });
  }

  public static Task<object?> CreateProfileFromSurfaceAsync(JsonObject? parameters)
  {
    var drawIn = ParseDrawIn(parameters);
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var profileName = PluginRuntime.GetRequiredString(parameters, "profileName");
    var surfaceName = PluginRuntime.GetRequiredString(parameters, "surfaceName");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var surface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, surfaceName, OpenMode.ForRead);
      var layerId = LookupUtils.GetLayerId(database, transaction, PluginRuntime.GetOptionalString(parameters, "layer"));
      var styleId = LookupUtils.GetProfileStyleId(civilDoc, transaction, PluginRuntime.GetOptionalString(parameters, "style"));
      var labelSetId = LookupUtils.GetProfileLabelSetId(civilDoc, transaction, PluginRuntime.GetOptionalString(parameters, "labelSet"));
      var profileId = Profile.CreateFromSurface(profileName, alignment.ObjectId, surface.ObjectId, layerId, styleId, labelSetId);
      var profile = CivilObjectUtils.GetRequiredObject<Profile>(transaction, profileId, OpenMode.ForRead);

      return new Dictionary<string, object?>
      {
        ["alignmentName"] = alignment.Name,
        ["profileName"] = profile.Name,
        ["created"] = true,
        ["profileViews"] = DrawInProfileViews(transaction, alignment, profile.Name, drawIn),
      };
    });
  }

  public static Task<object?> CreateLayoutProfileAsync(JsonObject? parameters)
  {
    var drawIn = ParseDrawIn(parameters);
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var profileName = PluginRuntime.GetRequiredString(parameters, "profileName");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var layerId = LookupUtils.GetLayerId(database, transaction, PluginRuntime.GetOptionalString(parameters, "layer"));
      var styleId = LookupUtils.GetProfileStyleId(civilDoc, transaction, PluginRuntime.GetOptionalString(parameters, "style"));
      var labelSetId = LookupUtils.GetProfileLabelSetId(civilDoc, transaction, PluginRuntime.GetOptionalString(parameters, "labelSet"));
      var profileId = Profile.CreateByLayout(profileName, alignment.ObjectId, layerId, styleId, labelSetId);
      var profile = CivilObjectUtils.GetRequiredObject<Profile>(transaction, profileId, OpenMode.ForRead);

      return new Dictionary<string, object?>
      {
        ["alignmentName"] = alignment.Name,
        ["profileName"] = profile.Name,
        ["created"] = true,
        ["profileViews"] = DrawInProfileViews(transaction, alignment, profile.Name, drawIn),
      };
    });
  }

  // ─── C102: new profiles are not drawn in existing profile views unless asked ─────

  /// <summary>drawInProfileViews: absent or false = none (C102 default), true = all (Civil's own behaviour), or a list of
  /// profile view names. Live 2026-10-11 every profile a script made or remade (excavation bottom, bore profiles) was drawn
  /// in every profile view of the alignment ("black lines"), and the scripts had to hide it again in each view.</summary>
  internal static (bool All, HashSet<string> Names) ParseDrawIn(JsonObject? parameters)
  {
    var node = PluginRuntime.GetParameter(parameters, "drawInProfileViews");
    if (node == null) return (false, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    if (node is JsonValue value && value.TryGetValue<bool>(out var all))
      return (all, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    if (node is JsonArray list)
      return (false, list.Select(n => n?.GetValue<string>()?.Trim() ?? "").Where(n => n.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase));
    throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "drawInProfileViews must be true, false or a list of profile view names.");
  }

  internal static List<Dictionary<string, object?>> DrawInProfileViews(Transaction transaction, Alignment alignment, string profileName, (bool All, HashSet<string> Names) drawIn)
  {
    var result = new List<Dictionary<string, object?>>();
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (ObjectId id in alignment.GetProfileViewIds())
    {
      if (transaction.GetObject(id, OpenMode.ForRead) is not ProfileView view) continue;
      seen.Add(view.Name);
      var draw = drawIn.All || drawIn.Names.Contains(view.Name);
      var found = false;
      view.UpgradeOpen();
      foreach (var graphOverride in view.GraphOverrides)
      {
        if (!string.Equals(graphOverride.ProfileName, profileName, StringComparison.OrdinalIgnoreCase)) continue;
        graphOverride.Draw = draw;
        found = true;
      }
      result.Add(new Dictionary<string, object?> { ["profileViewName"] = view.Name, ["draw"] = found ? draw : null });
    }
    var missing = drawIn.Names.Where(n => !seen.Contains(n)).ToList();
    if (missing.Count > 0)
      throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND",
        $"Profile view(s) {string.Join(", ", missing)} are not views of alignment '{alignment.Name}'. Nothing was changed.");
    return result;
  }

  public static Task<object?> DeleteProfileAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var profileName = PluginRuntime.GetRequiredString(parameters, "profileName");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var profile = CivilObjectUtils.FindProfileByName(alignment, transaction, profileName, OpenMode.ForWrite);
      profile.Erase();

      return new Dictionary<string, object?>
      {
        ["alignmentName"] = alignment.Name,
        ["profileName"] = profileName,
        ["deleted"] = true,
      };
    });
  }

  private static Dictionary<string, object?> ToProfileSummary(Profile profile)
  {
    var extents = GetElevationExtents(profile);
    return new Dictionary<string, object?>
    {
      ["name"] = profile.Name,
      ["handle"] = CivilObjectUtils.GetHandle(profile),
      ["type"] = MapProfileType(profile.ProfileType.ToString()),
      ["style"] = string.Empty,
      ["startStation"] = profile.StartingStation,
      ["endStation"] = profile.EndingStation,
      ["minElevation"] = extents.Min,
      ["maxElevation"] = extents.Max,
    };
  }

  private static List<Dictionary<string, object?>> ReadProfileEntities(Profile profile)
  {
    var entities = new List<Dictionary<string, object?>>();
    var index = 0;
    foreach (ProfileEntity entity in profile.Entities)
    {
      entities.Add(new Dictionary<string, object?>
      {
        ["index"] = index++,
        ["type"] = MapProfileEntityType(entity.EntityType.ToString()),
        ["startStation"] = entity.StartStation,
        ["endStation"] = entity.EndStation,
        ["startElevation"] = entity.StartElevation,
        ["endElevation"] = entity.EndElevation,
        ["grade"] = entity is ProfileTangent tangent ? tangent.Grade : null,
        ["length"] = entity.Length,
      });
    }

    return entities;
  }

  private static (double Min, double Max) GetElevationExtents(Profile profile)
  {
    // Civil 3D calculates profile-wide extents, including interior vertical
    // curve extrema and the numeric fallback required for empty layouts.
    return (profile.ElevationMin, profile.ElevationMax);
  }

  private static int CountPvis(Profile profile)
  {
    return profile.PVIs.Count;
  }

  private static double ReadProfileElevation(Profile profile, double station)
  {
    try
    {
      return profile.ElevationAt(station);
    }
    catch (Exception exception)
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.API_ERROR",
        $"Could not read elevation on profile '{profile.Name}' at station {station}: {exception.Message}");
    }
  }

  private static double ReadProfileGrade(Profile profile, double station)
  {
    try
    {
      return profile.GradeAt(station);
    }
    catch (Exception exception)
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.API_ERROR",
        $"Could not read grade on profile '{profile.Name}' at station {station}: {exception.Message}");
    }
  }

  private static string MapProfileType(string? value)
  {
    // Civil 3D ProfileType enum: EG (surface), FG (layout), Superimposed, CorridorFeature, File.
    var text = value?.ToLowerInvariant() ?? string.Empty;
    return text switch
    {
      "eg" => "surface",
      "fg" => "layout",
      "corridorfeature" => "corridor",
      "file" => "file",
      _ when text.Contains("surface") => "surface",
      _ when text.Contains("super") => "superimposed",
      _ => "layout",
    };
  }

  private static string MapProfileEntityType(string value)
  {
    var text = value.ToLowerInvariant();
    if (text.Contains("asymmetric"))
    {
      return "asymmetric_parabola";
    }

    if (text.Contains("parabola"))
    {
      return "parabola";
    }

    if (text.Contains("curve"))
    {
      return "circular_curve";
    }

    return "tangent";
  }
}
