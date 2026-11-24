using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.DatabaseServices;
using Autodesk.Civil.DatabaseServices.Styles;

namespace Civil3DMcpPlugin;

/// <summary>
/// C104: a band set onto an existing profile view band by band, with the band set's own settings and a source per band.
/// Need (lesson 18, T6 2026-10-11): ImportBandSetStyle gave the bands but not the band set's intervals (a
/// station band set to 50/10 came out 100/20, the profile view matching the bands to its grid), and every band without a
/// source drew nothing; the pilots set the sources one band at a time afterwards (PAKETBAND, BORR_BAND).
/// Steps: the band set's items are read, its bands are added one by one (mode add, default; import = ImportBandSetStyle), the grid matching is turned off (unless asked), and
/// each band gets its item's major and minor interval, gap, weeding, labels at start and end and show labels; then the
/// sources: per band (index, or style with occurrence for a style used twice) profile1, profile2 and network, and as
/// defaults profile1 for every profile data band and network for every pipe network band without its own. Everything
/// is resolved before the first change. Labels: showLabels true unless given, and labelAtStartStation, labelAtEndStation and
/// the geometry points to label (vertical, horizontal; ["all"]) as defaults in labels or per band. Bands without a source
/// are reported, since they draw nothing.
/// </summary>
public static class ProfileViewBandSetApplyCommands
{
  private sealed record Labels(bool? ShowLabels, bool? LabelAtStart, bool? LabelAtEnd, List<Autodesk.Civil.ProfilePointType>? Vertical,
    List<Autodesk.Civil.AlignmentPointType>? Horizontal);

  private sealed record Source(int? Index, string? Style, int Occurrence, string? Profile1, string? Profile2, string? Network, string Location, Labels Labels);

  private sealed record ItemSettings(string? Style, string? BandType, double? Major, double? Minor, double? Gap, double? Weeding,
    bool? ShowLabels, bool? LabelAtStart, bool? LabelAtEnd);

  public static Task<object?> ApplyBandSetAsync(JsonObject? parameters)
  {
    var viewName = PluginRuntime.GetRequiredString(parameters, "profileViewName");
    var bandSetName = PluginRuntime.GetRequiredString(parameters, "bandSet");
    var defaultProfile1 = PluginRuntime.GetOptionalString(parameters, "profile1");
    var defaultNetwork = PluginRuntime.GetOptionalString(parameters, "network");
    var matchGrid = PluginRuntime.GetOptionalBool(parameters, "matchIncrementToGridIntervals") ?? false;
    var mode = (PluginRuntime.GetOptionalString(parameters, "mode") ?? "add").ToLowerInvariant();
    if (mode is not ("add" or "import"))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "mode is add (default: the band set's bands added one by one) or import (ImportBandSetStyle). Nothing was changed.");
    var sources = (PluginRuntime.GetParameter(parameters, "bands") as JsonArray ?? new JsonArray()).Select((n, i) =>
    {
      if (n is not JsonObject o)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"bands[{i}] must be {{index | style, occurrence?, location?, profile1?, profile2?, network?}}. Nothing was changed.");
      var index = o["index"]?.GetValue<int>();
      var style = o["style"]?.GetValue<string>();
      if ((index == null) == (style == null))
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"bands[{i}]: give index or style. Nothing was changed.");
      var location = (o["location"]?.GetValue<string>() ?? "bottom").ToLowerInvariant();
      if (location is not ("bottom" or "top"))
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"bands[{i}]: location is bottom or top. Nothing was changed.");
      return new Source(index, style, o["occurrence"]?.GetValue<int>() ?? 1, o["profile1"]?.GetValue<string>(), o["profile2"]?.GetValue<string>(),
        o["network"]?.GetValue<string>(), location, ParseLabels(o, $"bands[{i}]"));
    }).ToList();
    // labels: defaults for every band (showLabels true unless given; the band sets made through the API store no
    // labels and no geometry points, so imported bands drew nothing, C53).
    var labelDefaults = PluginRuntime.GetParameter(parameters, "labels") is JsonObject lo ? ParseLabels(lo, "labels") : new Labels(null, null, null, null, null);
    labelDefaults = labelDefaults with { ShowLabels = labelDefaults.ShowLabels ?? true };

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var found = ProfileEditCommands.FindProfileViewByName(civilDoc, transaction, viewName);
      var view = CivilObjectUtils.GetRequiredObject<ProfileView>(transaction, found.ObjectId, OpenMode.ForWrite);
      var alignment = CivilObjectUtils.GetRequiredObject<Alignment>(transaction, view.AlignmentId, OpenMode.ForRead);
      var setId = LookupUtils.GetProfileViewBandSetId(civilDoc, transaction, bandSetName);
      if (setId.IsNull)
        throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Band set '{bandSetName}' was not found. Nothing was changed.");
      var set = (ProfileViewBandSetStyle)transaction.GetObject(setId, OpenMode.ForRead);
      var definition = new Dictionary<string, List<ItemSettings>>
      {
        ["bottom"] = Read(set.GetBottomBandSetItems(), transaction),
        ["top"] = Read(set.GetTopBandSetItems(), transaction),
      };

      // Resolve every source before the first change.
      ObjectId Profile(string name) => CivilObjectUtils.FindProfileByName(alignment, transaction, name, OpenMode.ForRead).ObjectId;
      ObjectId NetworkId(string name)
      {
        foreach (ObjectId id in civilDoc.GetPipeNetworkIds())
          if (string.Equals(CivilObjectUtils.GetName(transaction.GetObject(id, OpenMode.ForRead)), name, StringComparison.OrdinalIgnoreCase)) return id;
        throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Pipe network '{name}' was not found. Nothing was changed.");
      }
      var plan = new Dictionary<(string Location, int Index), (ObjectId? P1, ObjectId? P2, ObjectId? Net, Labels? Labels)>();
      foreach (var s in sources)
      {
        var items = definition[s.Location];
        int index;
        if (s.Index != null)
        {
          index = s.Index.Value;
          if (index < 0 || index >= items.Count)
            throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Band set '{bandSetName}' has {items.Count} {s.Location} band(s); index {index} does not exist. Nothing was changed.");
        }
        else
        {
          var matches = items.Select((it, i) => (it, i)).Where(x => string.Equals(x.it.Style, s.Style, StringComparison.OrdinalIgnoreCase)).Select(x => x.i).ToList();
          if (s.Occurrence < 1 || s.Occurrence > matches.Count)
            throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
              $"Band set '{bandSetName}' has {matches.Count} {s.Location} band(s) with style '{s.Style}'; occurrence {s.Occurrence} does not exist. Nothing was changed.");
          index = matches[s.Occurrence - 1];
        }
        plan[(s.Location, index)] = (s.Profile1 != null ? Profile(s.Profile1) : null, s.Profile2 != null ? Profile(s.Profile2) : null,
          s.Network != null ? NetworkId(s.Network) : null, s.Labels);
      }
      ObjectId? p1Default = defaultProfile1 != null ? Profile(defaultProfile1) : null;
      ObjectId? netDefault = defaultNetwork != null ? NetworkId(defaultNetwork) : null;

      if (mode == "import")
      {
        view.Bands.ImportBandSetStyle(setId);
      }
      else
      {
        // The band set's bands added one by one with their type and style, then the old bands removed, as the pilots did
        // (editProfileViewBands add/remove): live in T6 2026-10-11 imported bands drew the station labels half a band too
        // low and no crown elevations at the PVIs, while added bands draw as the pilot's sheets.
        foreach (var location in new[] { "bottom", "top" })
        {
          var items = location == "bottom" ? view.Bands.GetBottomBandItems() : view.Bands.GetTopBandItems();
          var old = items.Count;
          foreach (var st in definition[location])
          {
            if (st.Style == null || !Enum.TryParse<Autodesk.Civil.BandType>(st.BandType, true, out var bandType))
              throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Band set '{bandSetName}': a {location} band has no readable type or style. Nothing was changed.");
            items.Add(bandType, st.Style);
          }
          for (var i = 0; i < old; i++) items.RemoveAt(0);
          if (location == "bottom") view.Bands.SetBottomBandItems(items);
          else view.Bands.SetTopBandItems(items);
        }
      }
      view.Bands.MatchIncrementToGridIntervals = matchGrid;
      var warnings = new List<string>();
      foreach (var location in new[] { "bottom", "top" })
      {
        var items = location == "bottom" ? view.Bands.GetBottomBandItems() : view.Bands.GetTopBandItems();
        var list = items.Cast<ProfileViewBandItem>().ToList();
        var settings = definition[location];
        if (list.Count != settings.Count)
          warnings.Add($"{location}: the band set has {settings.Count} band(s), the profile view got {list.Count}; settings were copied by position.");
        for (var i = 0; i < list.Count; i++)
        {
          var item = list[i];
          BandSetItem baseItem = item;
          var st = i < settings.Count ? settings[i] : null;
          if (st != null && !matchGrid)
          {
            // C185 (2026-11-11): a band set item can store 0 for "not set" (a ground-type band in a gravity-sewer band set);
            // Civil rejects a non-positive interval with an ArgumentException that aborted the whole apply. Skip those.
            Try(() => { if (st.Major is > 0) { baseItem.MajorInterval = st.Major.Value; item.MajorInterval = st.Major.Value; } });
            Try(() => { if (st.Minor is > 0) { baseItem.MinorInterval = st.Minor.Value; item.MinorInterval = st.Minor.Value; } });
          }
          if (st != null)
          {
            Try(() => { if (st.Gap != null) baseItem.Gap = st.Gap.Value; });
            Try(() => { if (st.Weeding != null) baseItem.Weeding = st.Weeding.Value; });
            Try(() => { if (st.ShowLabels != null) baseItem.ShowLabels = st.ShowLabels.Value; });
            Try(() => { if (st.LabelAtStart != null) item.LabelAtStartStation = st.LabelAtStart.Value; });
            Try(() => { if (st.LabelAtEnd != null) item.LabelAtEndStation = st.LabelAtEnd.Value; });
          }
          var type = Safe(() => baseItem.BandType.ToString()) ?? "";
          plan.TryGetValue((location, i), out var own);
          var p1 = own.P1 ?? (type == "ProfileData" ? p1Default : null);
          var net = own.Net ?? (type == "PipeNetwork" ? netDefault : null);
          if (p1 != null) item.Profile1Id = p1.Value;
          if (own.P2 != null) item.Profile2Id = own.P2.Value;
          if (net != null) item.DataSourceId = net.Value;
          // labels: the band's own, else the defaults
          var lb = own.Labels;
          var show = lb?.ShowLabels ?? labelDefaults.ShowLabels;
          var atStart = lb?.LabelAtStart ?? labelDefaults.LabelAtStart;
          var atEnd = lb?.LabelAtEnd ?? labelDefaults.LabelAtEnd;
          var vertical = lb?.Vertical ?? labelDefaults.Vertical;
          var horizontal = lb?.Horizontal ?? labelDefaults.Horizontal;
          Try(() => { if (show != null) baseItem.ShowLabels = show.Value; });
          Try(() => { if (atStart != null) item.LabelAtStartStation = atStart.Value; });
          Try(() => { if (atEnd != null) item.LabelAtEndStation = atEnd.Value; });
          if (type == "ProfileData")
          {
            Try(() => { if (vertical != null) item.SetVerticalGeometryPointsOptions(ProfileViewBandLabelCommands.Select(item.GetVerticalGeometryPointsOptions(), vertical)); });
            Try(() => { if (horizontal != null) item.SetHorizontalGeometryPointsOptions(ProfileViewBandLabelCommands.Select(item.GetHorizontalGeometryPointsOptions(), horizontal)); });
          }
        }
        if (location == "bottom") view.Bands.SetBottomBandItems(items);
        else view.Bands.SetTopBandItems(items);
      }

      // Read back: bands without a source draw nothing.
      var result = new Dictionary<string, object?> { ["profileViewName"] = view.Name, ["bandSet"] = set.Name, ["mode"] = mode, ["matchIncrementToGridIntervals"] = matchGrid };
      foreach (var location in new[] { "bottom", "top" })
      {
        var bands = new List<Dictionary<string, object?>>();
        var i = 0;
        foreach (ProfileViewBandItem item in location == "bottom" ? view.Bands.GetBottomBandItems() : view.Bands.GetTopBandItems())
        {
          BandSetItem baseItem = item;
          var type = Safe(() => baseItem.BandType.ToString());
          var style = Safe(() => Name(baseItem.BandStyleId, transaction));
          var p1 = Safe(() => Name(item.Profile1Id, transaction));
          var source = Safe(() => Name(item.DataSourceId, transaction));
          if (type == "ProfileData" && p1 == null) warnings.Add($"{location} band {i} ({style}) has no profile1 and draws nothing.");
          if (type == "PipeNetwork" && source == null) warnings.Add($"{location} band {i} ({style}) has no pipe network and draws nothing.");
          bands.Add(new Dictionary<string, object?>
          {
            ["index"] = i++, ["bandType"] = type, ["style"] = style, ["profile1"] = p1, ["profile2"] = Safe(() => Name(item.Profile2Id, transaction)),
            ["dataSource"] = source, ["majorInterval"] = Safe<object?>(() => baseItem.MajorInterval), ["minorInterval"] = Safe<object?>(() => baseItem.MinorInterval),
            ["gap"] = Safe<object?>(() => baseItem.Gap), ["showLabels"] = Safe<object?>(() => baseItem.ShowLabels),
            ["labelAtStartStation"] = Safe<object?>(() => item.LabelAtStartStation),
            ["verticalGeometryPoints"] = Safe<object?>(() => Selected(item.GetVerticalGeometryPointsOptions())),
            ["horizontalGeometryPoints"] = Safe<object?>(() => Selected(item.GetHorizontalGeometryPointsOptions())),
          });
        }
        result[location] = bands;
      }
      result["warnings"] = warnings;
      return result;
    });
  }

  private static Labels ParseLabels(JsonObject o, string label) => new(o["showLabels"]?.GetValue<bool>(), o["labelAtStartStation"]?.GetValue<bool>(),
    o["labelAtEndStation"]?.GetValue<bool>(),
    ProfileViewBandLabelCommands.ParsePointTypes<Autodesk.Civil.ProfilePointType>(o["verticalGeometryPoints"], $"{label}.verticalGeometryPoints"),
    ProfileViewBandLabelCommands.ParsePointTypes<Autodesk.Civil.AlignmentPointType>(o["horizontalGeometryPoints"], $"{label}.horizontalGeometryPoints"));

  private static List<string> Selected<T>(Autodesk.Civil.GeometryPointSelector<T> selector) where T : struct, Enum =>
    Enum.GetValues<T>().Where(t => selector[t].Selected).Select(t => t.ToString()).ToList();

  private static List<ItemSettings> Read(ProfileViewBandSetItemCollection? items, Transaction transaction)
  {
    var list = new List<ItemSettings>();
    if (items == null) return list;
    foreach (ProfileViewBandSetItem item in items)
    {
      BandSetItem baseItem = item;
      list.Add(new ItemSettings(Safe(() => Name(baseItem.BandStyleId, transaction)), Safe(() => item.BandType.ToString()),
        Safe<double?>(() => item.MajorInterval), Safe<double?>(() => item.MinorInterval), Safe<double?>(() => item.Gap),
        Safe<double?>(() => baseItem.Weeding), Safe<bool?>(() => item.ShowLabels), Safe<bool?>(() => item.LabelAtStartStation),
        Safe<bool?>(() => item.LabelAtEndStation)));
    }
    return list;
  }

  private static void Try(Action write)
  {
    try
    {
      write();
    }
    catch (Exception ex) when (ex is InvalidOperationException or Autodesk.AutoCAD.Runtime.Exception)
    {
      // a setting the band type does not take
    }
  }

  private static T? Safe<T>(Func<T> read)
  {
    try
    {
      return read();
    }
    catch (Exception ex) when (ex is InvalidOperationException or Autodesk.AutoCAD.Runtime.Exception)
    {
      return default;
    }
  }

  private static string? Name(ObjectId id, Transaction transaction) =>
    id.IsNull || !id.IsValid || id.IsErased ? null : CivilObjectUtils.GetName(transaction.GetObject(id, OpenMode.ForRead));
}
