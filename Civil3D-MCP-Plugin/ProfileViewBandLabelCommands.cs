using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.DatabaseServices;
using Autodesk.Civil.DatabaseServices.Styles;

namespace Civil3DMcpPlugin;

/// <summary>
/// Profile view bands (data sources and intervals) and profile line labels (length and grade).
/// Band style ids are read through BandSetItem: ProfileViewBandSetItem overrides only the setter
/// of BandStyleId (same shape as C12b), so the typed base getter is used.
/// </summary>
public static class ProfileViewBandLabelCommands
{
  public static Task<object?> GetProfileViewBandsAsync(JsonObject? parameters)
  {
    var profileViewName = PluginRuntime.GetRequiredString(parameters, "profileViewName");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var profileView = ProfileEditCommands.FindProfileViewByName(civilDoc, transaction, profileViewName);
      return DescribeView(profileView, transaction);
    });
  }

  /// <summary>C122: the profile views of every alignment (or one, alignmentName): name, handle, alignment, style,
  /// insertion point, station and elevation range, and the number of bottom/top bands. Use case (a project, 2026-10-12):
  /// the band and view commands take a profile view name and nothing listed them.</summary>
  public static Task<object?> ListProfileViewsAsync(JsonObject? parameters)
  {
    var only = PluginRuntime.GetOptionalString(parameters, "alignmentName");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var views = new List<Dictionary<string, object?>>();
      foreach (ObjectId aid in civilDoc.GetAlignmentIds())
      {
        var alignment = (Alignment)transaction.GetObject(aid, OpenMode.ForRead);
        if (only != null && !string.Equals(alignment.Name, only, StringComparison.OrdinalIgnoreCase)) continue;
        foreach (ObjectId vid in alignment.GetProfileViewIds())
        {
          if (transaction.GetObject(vid, OpenMode.ForRead) is not ProfileView view) continue;
          string? style = null;
          try { style = ((Autodesk.Civil.DatabaseServices.Styles.ProfileViewStyle)transaction.GetObject(view.StyleId, OpenMode.ForRead)).Name; } catch (System.Exception) { }
          views.Add(new Dictionary<string, object?>
          {
            ["name"] = view.Name,
            ["handle"] = view.Handle.ToString(),
            ["alignment"] = alignment.Name,
            ["style"] = style,
            ["insertion"] = new[] { Math.Round(view.Location.X, 3), Math.Round(view.Location.Y, 3) },
            ["stationStart"] = Math.Round(view.StationStart, 3),
            ["stationEnd"] = Math.Round(view.StationEnd, 3),
            ["elevationMin"] = Math.Round(view.ElevationMin, 3),
            ["elevationMax"] = Math.Round(view.ElevationMax, 3),
            ["bottomBands"] = view.Bands.GetBottomBandItems().Count,
            ["topBands"] = view.Bands.GetTopBandItems().Count,
          });
        }
      }
      return new Dictionary<string, object?> { ["profileViews"] = views, ["count"] = views.Count };
    });
  }

  public static Task<object?> SetProfileViewBandsAsync(JsonObject? parameters)
  {
    var profileViewName = PluginRuntime.GetRequiredString(parameters, "profileViewName");
    var matchGrid = PluginRuntime.GetOptionalBool(parameters, "matchIncrementToGridIntervals");
    var bandsNode = PluginRuntime.GetParameter(parameters, "bands") as JsonArray;
    if ((bandsNode == null || bandsNode.Count == 0) && matchGrid == null)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "bands must contain at least one band update (or give matchIncrementToGridIntervals).");
    }

    var updates = (bandsNode ?? new JsonArray()).Select(ParseUpdate).ToList();

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var found = ProfileEditCommands.FindProfileViewByName(civilDoc, transaction, profileViewName);
      var profileView = CivilObjectUtils.GetRequiredObject<ProfileView>(transaction, found.ObjectId, OpenMode.ForWrite);
      var alignment = CivilObjectUtils.GetRequiredObject<Alignment>(transaction, profileView.AlignmentId, OpenMode.ForRead);
      // C54 evidence: live a major interval of 50 read back 100. Record where it is lost: on the item itself, or when
      // the band items are written back.
      var intervalTrace = new List<Dictionary<string, object?>>();
      // Off before the intervals are set, so that they are kept.
      if (matchGrid != null) profileView.Bands.MatchIncrementToGridIntervals = matchGrid.Value;

      foreach (var location in new[] { "bottom", "top" })
      {
        var forLocation = updates.Where(u => u.Location == location).ToList();
        if (forLocation.Count == 0)
        {
          continue;
        }

        var items = location == "bottom" ? profileView.Bands.GetBottomBandItems() : profileView.Bands.GetTopBandItems();
        var list = items.Cast<ProfileViewBandItem>().ToList();
        foreach (var u in forLocation)
        {
          if (u.Index >= list.Count)
          {
            throw new JsonRpcDispatchException(
              "CIVIL3D.INVALID_INPUT",
              $"Profile view '{profileView.Name}' has {list.Count} {location} band(s); index {u.Index} does not exist. Nothing was changed.");
          }

          var item = list[u.Index];
          try
          {
            if (u.Profile1 != null) item.Profile1Id = CivilObjectUtils.FindProfileByName(alignment, transaction, u.Profile1, OpenMode.ForRead).ObjectId;
            if (u.Profile2 != null) item.Profile2Id = CivilObjectUtils.FindProfileByName(alignment, transaction, u.Profile2, OpenMode.ForRead).ObjectId;
            if (u.Network != null) item.DataSourceId = FindNetworkId(civilDoc, transaction, u.Network);
            if (u.MajorInterval != null) SetMajor(item, u.MajorInterval.Value);
            if (u.MinorInterval != null) SetMinor(item, u.MinorInterval.Value);
            if (u.MajorInterval != null || u.MinorInterval != null)
            {
              BandSetItem traced = item;
              intervalTrace.Add(new Dictionary<string, object?>
              {
                ["location"] = location, ["index"] = u.Index, ["requestedMajor"] = u.MajorInterval, ["requestedMinor"] = u.MinorInterval,
                ["onItemMajor"] = Safe<object?>(() => item.MajorInterval), ["onItemBaseMajor"] = Safe<object?>(() => traced.MajorInterval),
                ["onItemMinor"] = Safe<object?>(() => item.MinorInterval),
              });
            }
            // C53: which geometry points the band labels ("Geometry Points to Label in Band") and whether it shows
            // labels at all; a band added through the API had none selected, so a vertical geometry band stayed empty.
            BandSetItem baseItem = item;
            if (u.ShowLabels != null) baseItem.ShowLabels = u.ShowLabels.Value;
            // Gap: space above the band (paper units as the band style stores them, e.g. 0 = bands touch).
            if (u.Gap != null) baseItem.Gap = u.Gap.Value;
            // C55 Weeding: smallest distance between two labels in the band (paper units); closer ones are skipped.
            if (u.Weeding != null) baseItem.Weeding = u.Weeding.Value;
            if (u.LabelAtStartStation != null) item.LabelAtStartStation = u.LabelAtStartStation.Value;
            if (u.LabelAtEndStation != null) item.LabelAtEndStation = u.LabelAtEndStation.Value;
            if (u.VerticalGeometryPoints != null)
              item.SetVerticalGeometryPointsOptions(Select(item.GetVerticalGeometryPointsOptions(), u.VerticalGeometryPoints));
            if (u.HorizontalGeometryPoints != null)
              item.SetHorizontalGeometryPointsOptions(Select(item.GetHorizontalGeometryPointsOptions(), u.HorizontalGeometryPoints));
          }
          catch (InvalidOperationException ex)
          {
            throw new JsonRpcDispatchException(
              "CIVIL3D.INVALID_INPUT",
              $"{location} band {u.Index} ({Safe(() => ((BandSetItem)item).BandType.ToString())}) does not accept that setting: {ex.Message} Nothing was changed.");
          }
        }

        if (location == "bottom") profileView.Bands.SetBottomBandItems(items);
        else profileView.Bands.SetTopBandItems(items);
      }

      var result = DescribeView(profileView, transaction);
      if (intervalTrace.Count > 0) result["intervalTrace"] = intervalTrace;
      return result;
    });
  }

  /// <summary>
  /// C160 (2026-10-21): makes pipe network bands follow the network again. A band does not recompute after the network's
  /// geometry is changed through the API until its band settings are written (C125, 2026-10-12; REGENALL is
  /// not enough), so every band item of the chosen profile views is written back unchanged, with its data source set
  /// again. Profile views: the named ones, those whose bands show networkName, or all.
  /// </summary>
  public static Task<object?> RefreshProfileViewBandsAsync(JsonObject? parameters)
  {
    var names = (PluginRuntime.GetParameter(parameters, "profileViewNames") as JsonArray)?.Select(n => n?.ToString()).OfType<string>()
      .ToHashSet(StringComparer.OrdinalIgnoreCase);
    var networkName = PluginRuntime.GetOptionalString(parameters, "networkName");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var network = networkName == null ? ObjectId.Null : FindNetworkId(civilDoc, transaction, networkName);
      var refreshed = new List<Dictionary<string, object?>>();
      foreach (ObjectId aid in civilDoc.GetAlignmentIds())
      {
        var alignment = (Alignment)transaction.GetObject(aid, OpenMode.ForRead);
        foreach (ObjectId vid in alignment.GetProfileViewIds())
        {
          var view = (ProfileView)transaction.GetObject(vid, OpenMode.ForRead);
          if (names != null && !names.Contains(view.Name)) continue;
          var bottom = view.Bands.GetBottomBandItems();
          var top = view.Bands.GetTopBandItems();
          var items = bottom.Cast<ProfileViewBandItem>().Concat(top.Cast<ProfileViewBandItem>()).ToList();
          var networkBands = items.Where(item => Safe(() => ((BandSetItem)item).BandType) == Autodesk.Civil.BandType.PipeNetwork).ToList();
          if (network != ObjectId.Null && !networkBands.Any(item => Safe(() => item.DataSourceId) == network)) continue;
          view.UpgradeOpen();
          foreach (var item in networkBands)
          {
            var source = Safe(() => item.DataSourceId);
            if (source != ObjectId.Null) item.DataSourceId = source;
          }
          view.Bands.SetBottomBandItems(bottom);
          view.Bands.SetTopBandItems(top);
          refreshed.Add(new Dictionary<string, object?> { ["profileView"] = view.Name, ["bands"] = items.Count, ["networkBands"] = networkBands.Count });
        }
      }

      if (names != null)
      {
        var missing = names.Where(n => !refreshed.Any(r => string.Equals((string?)r["profileView"], n, StringComparison.OrdinalIgnoreCase))).ToList();
        if (missing.Count > 0 && network == ObjectId.Null)
          throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Profile view(s) not found: {string.Join(", ", missing)}. Nothing was changed.");
      }
      return new Dictionary<string, object?> { ["refreshed"] = refreshed, ["count"] = refreshed.Count };
    });
  }

  /// <summary>C160: runs a command that changes a pipe network's geometry, then recomputes the bands that show the network
  /// (not for dryRun); the answer gets bandsRefreshed (profile views). Used by setPipeEndElevations, setStructureRims and
  /// erasePipeNetworkParts.</summary>
  internal static async Task<object?> ThenRefreshBandsAsync(Task<object?> change, JsonObject? parameters)
  {
    var result = await change;
    var networkName = PluginRuntime.GetOptionalString(parameters, "networkName");
    if (networkName == null || (PluginRuntime.GetOptionalBool(parameters, "dryRun") ?? false)) return result;
    var refresh = await RefreshProfileViewBandsAsync(new JsonObject { ["networkName"] = networkName }) as Dictionary<string, object?>;
    if (result is Dictionary<string, object?> answer && refresh != null) answer["bandsRefreshed"] = refresh["refreshed"];
    return result;
  }

  /// <summary>Adds a band (type + band style; optionally its network, profiles, intervals and position),
  /// removes one, or swaps two, at the bottom or top of a profile view.</summary>
  public static Task<object?> EditProfileViewBandsAsync(JsonObject? parameters)
  {
    var profileViewName = PluginRuntime.GetRequiredString(parameters, "profileViewName");
    var op = PluginRuntime.GetRequiredString(parameters, "op").Trim().ToLowerInvariant();
    var location = (PluginRuntime.GetOptionalString(parameters, "location") ?? "bottom").Trim().ToLowerInvariant();
    if (location is not ("bottom" or "top"))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "location must be 'bottom' or 'top'.");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var found = ProfileEditCommands.FindProfileViewByName(civilDoc, transaction, profileViewName);
      var profileView = CivilObjectUtils.GetRequiredObject<ProfileView>(transaction, found.ObjectId, OpenMode.ForWrite);
      var alignment = CivilObjectUtils.GetRequiredObject<Alignment>(transaction, profileView.AlignmentId, OpenMode.ForRead);
      var items = location == "bottom" ? profileView.Bands.GetBottomBandItems() : profileView.Bands.GetTopBandItems();
      int CheckIndex(string name)
      {
        var index = PluginRuntime.GetOptionalInt(parameters, name)
          ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{name} is required for op '{op}'.");
        if (index < 0 || index >= items.Count)
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{location} band {index} does not exist ({items.Count} bands). Nothing was changed.");
        return index;
      }

      switch (op)
      {
        case "add":
        {
          var typeText = PluginRuntime.GetRequiredString(parameters, "bandType");
          if (!Enum.TryParse<Autodesk.Civil.BandType>(typeText, true, out var bandType))
            throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"bandType '{typeText}' is unknown. Use {string.Join(", ", Enum.GetNames<Autodesk.Civil.BandType>())}.");
          var styleName = PluginRuntime.GetRequiredString(parameters, "style");
          items.Add(bandType, styleName);
          var item = items.Cast<ProfileViewBandItem>().Last();
          var network = PluginRuntime.GetOptionalString(parameters, "network");
          var profile1 = PluginRuntime.GetOptionalString(parameters, "profile1");
          var profile2 = PluginRuntime.GetOptionalString(parameters, "profile2");
          if (profile1 != null) item.Profile1Id = CivilObjectUtils.FindProfileByName(alignment, transaction, profile1, OpenMode.ForRead).ObjectId;
          if (profile2 != null) item.Profile2Id = CivilObjectUtils.FindProfileByName(alignment, transaction, profile2, OpenMode.ForRead).ObjectId;
          if (network != null) item.DataSourceId = FindNetworkId(civilDoc, transaction, network);
          if (PluginRuntime.GetOptionalDouble(parameters, "majorInterval") is double major) SetMajor(item, major);
          if (PluginRuntime.GetOptionalDouble(parameters, "minorInterval") is double minor) SetMinor(item, minor);
          if (PluginRuntime.GetOptionalInt(parameters, "position") is int position)
          {
            if (position < 0 || position >= items.Count)
              throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"position {position} is outside 0..{items.Count - 1}. Nothing was changed.");
            for (var i = items.Count - 1; i > position; i--) items.Swap(i, i - 1);
          }

          break;
        }
        case "remove":
          items.RemoveAt(CheckIndex("index"));
          break;
        case "swap":
          items.Swap(CheckIndex("index"), CheckIndex("index2"));
          break;
        default:
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"op must be add, remove or swap, not '{op}'.");
      }

      if (location == "bottom") profileView.Bands.SetBottomBandItems(items);
      else profileView.Bands.SetTopBandItems(items);
      return DescribeView(profileView, transaction);
    });
  }

  /// <summary>Erases every label group (lines, grade breaks, curves, stations, geometry points) on the
  /// given profiles (all when none given) in a profile view, so the profiles are drawn as plain lines.</summary>
  public static Task<object?> ClearProfileViewLabelsAsync(JsonObject? parameters)
  {
    var profileViewName = PluginRuntime.GetRequiredString(parameters, "profileViewName");
    var names = (PluginRuntime.GetParameter(parameters, "profiles") as JsonArray)?.Select(n => n?.GetValue<string>()).OfType<string>().ToList();

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var found = ProfileEditCommands.FindProfileViewByName(civilDoc, transaction, profileViewName);
      var profileView = CivilObjectUtils.GetRequiredObject<ProfileView>(transaction, found.ObjectId, OpenMode.ForRead);
      var erased = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
      foreach (var profileOverride in profileView.GraphOverrides)
      {
        var name = CivilObjectUtils.GetName(transaction.GetObject(profileOverride.ProfileId, OpenMode.ForRead)) ?? "";
        if (names != null && !names.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
        foreach (ObjectId id in profileOverride.GetProfileLabelGroupIds())
        {
          if (id.IsErased) continue;
          var group = transaction.GetObject(id, OpenMode.ForWrite);
          var kind = group.GetType().Name;
          group.Erase();
          erased[kind] = erased.TryGetValue(kind, out var n) ? n + 1 : 1;
        }
      }

      return new Dictionary<string, object?> { ["profileViewName"] = profileViewName, ["profiles"] = names, ["erasedLabelGroups"] = erased };
    });
  }

  public static Task<object?> ListProfileLabelStylesAsync(JsonObject? parameters)
  {
    var kind = PluginRuntime.GetRequiredString(parameters, "labelKind");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var names = new List<string?>();
      foreach (ObjectId id in LabelStyles(civilDoc, kind))
      {
        names.Add(CivilObjectUtils.GetName(transaction.GetObject(id, OpenMode.ForRead)));
      }

      return new Dictionary<string, object?> { ["labelKind"] = kind, ["styles"] = names };
    });
  }

  public static Task<object?> AddProfileLineLabelsAsync(JsonObject? parameters)
  {
    var profileViewName = PluginRuntime.GetRequiredString(parameters, "profileViewName");
    var profileName = PluginRuntime.GetRequiredString(parameters, "profileName");
    var styleName = PluginRuntime.GetRequiredString(parameters, "style");
    var replace = PluginRuntime.GetOptionalBool(parameters, "replace") ?? false;

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var profileView = ProfileEditCommands.FindProfileViewByName(civilDoc, transaction, profileViewName);
      var alignment = CivilObjectUtils.GetRequiredObject<Alignment>(transaction, profileView.AlignmentId, OpenMode.ForRead);
      var profile = CivilObjectUtils.FindProfileByName(alignment, transaction, profileName, OpenMode.ForRead);

      ObjectId styleId = ObjectId.Null;
      var available = new List<string>();
      foreach (ObjectId id in LabelStyles(civilDoc, "line"))
      {
        var name = CivilObjectUtils.GetName(transaction.GetObject(id, OpenMode.ForRead)) ?? "";
        available.Add(name);
        if (string.Equals(name, styleName, StringComparison.OrdinalIgnoreCase)) styleId = id;
      }

      if (styleId.IsNull)
      {
        throw new JsonRpcDispatchException(
          "CIVIL3D.INVALID_INPUT",
          $"Profile line label style '{styleName}' was not found. Available: {string.Join(", ", available)}. Nothing was created.");
      }

      var existing = ExistingLineLabelGroups(profileView, profile.ObjectId, transaction);
      if (existing.Count > 0 && !replace)
      {
        throw new JsonRpcDispatchException(
          "CIVIL3D.CONFLICT",
          $"Profile '{profile.Name}' already has {existing.Count} line label group(s) in '{profileView.Name}'. Pass replace=true to replace them. Nothing was created.");
      }

      foreach (var id in existing)
      {
        transaction.GetObject(id, OpenMode.ForWrite).Erase();
      }

      var groupId = ProfileLineLabelGroup.Create(profileView.ObjectId, profile.ObjectId, styleId);
      var group = CivilObjectUtils.GetRequiredObject<ProfileLineLabelGroup>(transaction, groupId, OpenMode.ForRead);
      return new Dictionary<string, object?>
      {
        ["profileViewName"] = profileView.Name,
        ["profileName"] = profile.Name,
        ["style"] = styleName,
        ["handle"] = CivilObjectUtils.GetHandle(group),
        ["labelCount"] = (int)group.SubEntityCount,
        ["replacedGroups"] = existing.Count,
      };
    });
  }

  private sealed record BandUpdate(string Location, int Index, string? Profile1, string? Profile2, string? Network, double? MajorInterval, double? MinorInterval,
    bool? ShowLabels = null, bool? LabelAtStartStation = null, bool? LabelAtEndStation = null, double? Gap = null, double? Weeding = null,
    List<Autodesk.Civil.ProfilePointType>? VerticalGeometryPoints = null, List<Autodesk.Civil.AlignmentPointType>? HorizontalGeometryPoints = null);

  /// <summary>ProfileViewBandSetItem declares MajorInterval/MinorInterval again (as with BandStyleId, C12b); live
  /// 2026-10-07 a value set through it read back unchanged (100/20), so the BandSetItem accessors are set too.</summary>
  private static void SetMajor(ProfileViewBandItem item, double value)
  {
    BandSetItem baseItem = item;
    baseItem.MajorInterval = value;
    item.MajorInterval = value;
  }

  private static void SetMinor(ProfileViewBandItem item, double value)
  {
    BandSetItem baseItem = item;
    baseItem.MinorInterval = value;
    item.MinorInterval = value;
  }

  /// <summary>Point type names for "Geometry Points to Label in Band": a list of names, or ["all"].</summary>
  internal static List<T>? ParsePointTypes<T>(JsonNode? node, string field) where T : struct, Enum
  {
    if (node == null) return null;
    if (node is not JsonArray array)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{field} must be a list of point types (or [\"all\"]).");
    var names = array.Select(n => n?.GetValue<string>() ?? "").ToList();
    if (names.Count == 1 && string.Equals(names[0], "all", StringComparison.OrdinalIgnoreCase)) return Enum.GetValues<T>().ToList();
    var result = new List<T>();
    foreach (var name in names)
    {
      if (!Enum.TryParse<T>(name, true, out var value))
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{field}: '{name}' is unknown. Use {string.Join(", ", Enum.GetNames<T>())} or \"all\".");
      result.Add(value);
    }

    return result;
  }

  private static List<string> SelectedPoints<T>(Autodesk.Civil.GeometryPointSelector<T> selector) where T : struct, Enum =>
    Enum.GetValues<T>().Where(t => selector[t].Selected).Select(t => t.ToString()).ToList();

  internal static Autodesk.Civil.GeometryPointSelector<T> Select<T>(Autodesk.Civil.GeometryPointSelector<T> selector, List<T> types) where T : struct, Enum
  {
    selector.UnSelectAll();
    foreach (var t in types) selector[t].Selected = true;
    return selector;
  }

  private static BandUpdate ParseUpdate(JsonNode? node)
  {
    if (node is not JsonObject o)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Each band update must be an object.");
    }

    var location = o["location"]?.GetValue<string>() ?? "bottom";
    if (location != "bottom" && location != "top")
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Band location '{location}' must be bottom or top.");
    }

    var index = o["index"]?.GetValue<int>() ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Band update needs an index.");
    if (index < 0)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Band index must be zero or more.");
    }

    return new BandUpdate(location, index, o["profile1"]?.GetValue<string>(), o["profile2"]?.GetValue<string>(), o["network"]?.GetValue<string>(),
      o["majorInterval"]?.GetValue<double>(), o["minorInterval"]?.GetValue<double>(),
      o["showLabels"]?.GetValue<bool>(), o["labelAtStartStation"]?.GetValue<bool>(), o["labelAtEndStation"]?.GetValue<bool>(), o["gap"]?.GetValue<double>(), o["weeding"]?.GetValue<double>(),
      ParsePointTypes<Autodesk.Civil.ProfilePointType>(o["verticalGeometryPoints"], "verticalGeometryPoints"),
      ParsePointTypes<Autodesk.Civil.AlignmentPointType>(o["horizontalGeometryPoints"], "horizontalGeometryPoints"));
  }

  private static Dictionary<string, object?> DescribeView(ProfileView profileView, Transaction transaction) => new()
  {
    ["profileViewName"] = profileView.Name,
    // C55: when on, every band takes its major/minor increments from the profile view's grid, and the bands' own
    // intervals are overwritten (live 2026-10-07: 50 on the item, 100 after SetBottomBandItems).
    ["matchIncrementToGridIntervals"] = Safe<object?>(() => profileView.Bands.MatchIncrementToGridIntervals),
    ["bottom"] = DescribeBands(profileView.Bands.GetBottomBandItems(), transaction),
    ["top"] = DescribeBands(profileView.Bands.GetTopBandItems(), transaction),
  };

  private static List<Dictionary<string, object?>> DescribeBands(ProfileViewBandItemCollection items, Transaction transaction)
  {
    var result = new List<Dictionary<string, object?>>();
    var index = 0;
    foreach (ProfileViewBandItem item in items)
    {
      BandSetItem baseItem = item;
      // Some getters throw "Operation is not valid due to the current state of the object" for band
      // types without that data (live 2026-10-05); report them as null instead of failing the read.
      result.Add(new Dictionary<string, object?>
      {
        ["index"] = index++,
        ["bandType"] = Safe(() => baseItem.BandType.ToString()),
        ["style"] = Safe(() => NameOf(baseItem.BandStyleId, transaction)),
        ["profile1"] = Safe(() => NameOf(item.Profile1Id, transaction)),
        ["profile2"] = Safe(() => NameOf(item.Profile2Id, transaction)),
        ["dataSource"] = Safe(() => NameOf(item.DataSourceId, transaction)),
        ["majorInterval"] = Safe<object?>(() => baseItem.MajorInterval),
        ["minorInterval"] = Safe<object?>(() => baseItem.MinorInterval),
        ["showLabels"] = Safe<object?>(() => baseItem.ShowLabels),
        ["gap"] = Safe<object?>(() => baseItem.Gap),
        ["weeding"] = Safe<object?>(() => baseItem.Weeding),
        ["labelAtStartStation"] = Safe<object?>(() => item.LabelAtStartStation),
        ["labelAtEndStation"] = Safe<object?>(() => item.LabelAtEndStation),
        ["verticalGeometryPoints"] = Safe<object?>(() => SelectedPoints(item.GetVerticalGeometryPointsOptions())),
        ["horizontalGeometryPoints"] = Safe<object?>(() => SelectedPoints(item.GetHorizontalGeometryPointsOptions())),
      });
    }

    return result;
  }

  private static ObjectId FindNetworkId(Autodesk.Civil.ApplicationServices.CivilDocument civilDoc, Transaction transaction, string name)
  {
    var available = new List<string>();
    foreach (ObjectId id in civilDoc.GetPipeNetworkIds())
    {
      var networkName = CivilObjectUtils.GetName(transaction.GetObject(id, OpenMode.ForRead)) ?? "";
      if (string.Equals(networkName, name, StringComparison.OrdinalIgnoreCase)) return id;
      available.Add(networkName);
    }

    throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Pipe network '{name}' was not found. Available: {string.Join(", ", available)}. Nothing was changed.");
  }

  private static T? Safe<T>(Func<T> read)
  {
    try
    {
      return read();
    }
    catch (InvalidOperationException)
    {
      return default;
    }
  }

  private static string? NameOf(ObjectId id, Transaction transaction) =>
    id.IsNull || !id.IsValid || id.IsErased ? null : CivilObjectUtils.GetName(transaction.GetObject(id, OpenMode.ForRead));

  internal static LabelStyleCollection LabelStyles(Autodesk.Civil.ApplicationServices.CivilDocument civilDoc, string kind)
  {
    var root = civilDoc.Styles.LabelStyles.ProfileLabelStyles;
    var pipes = civilDoc.Styles.LabelStyles.PipeLabelStyles;
    return kind switch
    {
      "pipe" => pipes.PlanProfileLabelStyles,
      "pipe_crossing" => pipes.CrossProfileLabelStyles,
      "pipe_section" => pipes.CrossSectionLabelStyles,
      "structure" => civilDoc.Styles.LabelStyles.StructureLabelStyles.LabelStyles,
      "line" => root.LineLabelStyles,
      "grade_break" => root.GradeBreakLabelStyles,
      "curve" => root.CurveLabelStyles,
      "major_station" => root.MajorStationLabelStyles,
      "minor_station" => root.MinorStationLabelStyles,
      "geometry_point" => root.HorizontalGeometryPointLabelStyles,
      _ => throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Unknown profile label kind '{kind}'."),
    };
  }

  private static List<ObjectId> ExistingLineLabelGroups(ProfileView profileView, ObjectId profileId, Transaction transaction)
  {
    var result = new List<ObjectId>();
    foreach (var profileOverride in profileView.GraphOverrides)
    {
      if (profileOverride.ProfileId != profileId)
      {
        continue;
      }

      foreach (ObjectId id in profileOverride.GetProfileLabelGroupIds())
      {
        if (!id.IsErased && transaction.GetObject(id, OpenMode.ForRead) is ProfileLineLabelGroup)
        {
          result.Add(id);
        }
      }
    }

    return result;
  }
}
