using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;

namespace Civil3DMcpPlugin;

/// <summary>
/// Station and elevation range of a profile view (Profile View Properties, Stations / Elevations tabs):
/// ProfileView.StationRangeMode + StationStart/StationEnd and ElevationRangeMode + ElevationMin/ElevationMax.
/// Each range is either user specified (both ends given) or automatic; what Civil 3D applied is read back.
/// </summary>
public static class ProfileViewRangeCommands
{
  public static Task<object?> SetProfileViewRangeAsync(JsonObject? parameters)
  {
    var viewName = PluginRuntime.GetRequiredString(parameters, "profileViewName");
    var startStation = PluginRuntime.GetOptionalDouble(parameters, "startStation");
    var endStation = PluginRuntime.GetOptionalDouble(parameters, "endStation");
    var automaticStation = PluginRuntime.GetOptionalBool(parameters, "automaticStation") ?? false;
    var minElevation = PluginRuntime.GetOptionalDouble(parameters, "minElevation");
    var maxElevation = PluginRuntime.GetOptionalDouble(parameters, "maxElevation");
    var automaticElevation = PluginRuntime.GetOptionalBool(parameters, "automaticElevation") ?? false;

    if ((startStation == null) != (endStation == null))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Give both startStation and endStation or neither. Nothing was changed.");
    if ((minElevation == null) != (maxElevation == null))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Give both minElevation and maxElevation or neither. Nothing was changed.");
    if (startStation != null && automaticStation)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Give a station range or automaticStation, not both. Nothing was changed.");
    if (minElevation != null && automaticElevation)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Give an elevation range or automaticElevation, not both. Nothing was changed.");
    if (startStation >= endStation)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "startStation must be less than endStation. Nothing was changed.");
    if (minElevation >= maxElevation)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "minElevation must be less than maxElevation. Nothing was changed.");
    if (startStation == null && !automaticStation && minElevation == null && !automaticElevation)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Give a station range, automaticStation, an elevation range and/or automaticElevation. Nothing was changed.");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var found = ProfileEditCommands.FindProfileViewByName((CivilDocument)civilDoc, transaction, viewName);
      var view = CivilObjectUtils.GetRequiredObject<ProfileView>(transaction, found.ObjectId, OpenMode.ForWrite);
      var before = Describe(view);
      try
      {
        if (automaticStation)
        {
          view.StationRangeMode = StationRangeType.Automatic;
        }
        else if (startStation != null)
        {
          view.StationRangeMode = StationRangeType.UserSpecified;
          // Move the end first when the new range lies beyond the current end, so start < end at every step.
          if (startStation.Value >= view.StationEnd) { view.StationEnd = endStation!.Value; view.StationStart = startStation.Value; }
          else { view.StationStart = startStation.Value; view.StationEnd = endStation!.Value; }
        }

        if (automaticElevation)
        {
          view.ElevationRangeMode = ElevationRangeType.Automatic;
        }
        else if (minElevation != null)
        {
          view.ElevationRangeMode = ElevationRangeType.UserSpecified;
          if (minElevation.Value >= view.ElevationMax) { view.ElevationMax = maxElevation!.Value; view.ElevationMin = minElevation.Value; }
          else { view.ElevationMin = minElevation.Value; view.ElevationMax = maxElevation!.Value; }
        }
      }
      catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Autodesk.AutoCAD.Runtime.Exception)
      {
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Civil 3D rejected the range for '{view.Name}': {ex.Message} Nothing was changed.");
      }

      var result = Describe(view);
      result["profileViewName"] = view.Name;
      result["before"] = before;
      return result;
    });
  }

  private static Dictionary<string, object?> Describe(ProfileView view) => new()
  {
    ["stationRangeMode"] = view.StationRangeMode.ToString(),
    ["startStation"] = Math.Round(view.StationStart, 4),
    ["endStation"] = Math.Round(view.StationEnd, 4),
    ["elevationRangeMode"] = view.ElevationRangeMode.ToString(),
    ["minElevation"] = Math.Round(view.ElevationMin, 4),
    ["maxElevation"] = Math.Round(view.ElevationMax, 4),
  };
}
