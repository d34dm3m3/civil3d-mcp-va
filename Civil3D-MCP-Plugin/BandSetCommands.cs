using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices.Styles;

namespace Civil3DMcpPlugin;

/// <summary>
/// Profile view band sets (C85, E5 read part): every band set style with its top and bottom bands in order (band type,
/// band style, gap, labels at start/end, intervals). Use case: step 3 of the company template (profile and bands), where the
/// VA band sets carry soil, ground improvement and compaction bands the user wants out, and band sets for
/// gravity, pressure and pipe packages are missing.
/// </summary>
public static class BandSetCommands
{
  public static Task<object?> ListBandSetsAsync(JsonObject? parameters)
  {
    var contains = PluginRuntime.GetOptionalString(parameters, "contains");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var sets = new List<Dictionary<string, object?>>();
      foreach (ObjectId id in ((CivilDocument)civilDoc).Styles.ProfileViewBandSetStyles)
      {
        if (id.IsNull || id.IsErased) continue;
        var set = (ProfileViewBandSetStyle)transaction.GetObject(id, OpenMode.ForRead);
        if (contains != null && !set.Name.Contains(contains, StringComparison.OrdinalIgnoreCase)) continue;
        sets.Add(new Dictionary<string, object?>
        {
          ["name"] = set.Name,
          ["top"] = Items(set.GetTopBandSetItems(), transaction),
          ["bottom"] = Items(set.GetBottomBandSetItems(), transaction),
        });
      }
      return new Dictionary<string, object?> { ["count"] = sets.Count, ["bandSets"] = sets };
    });
  }

  /// <summary>
  /// C86 (E5 write): create a band set (from a bottom band list, or as a copy of another) or change one: remove bands by band style name
  /// (top and bottom), replace the bottom bands with a given list, or append bands at the bottom. A band is
  /// {bandType, style, gap?}; band types as Civil's BandType (ProfileData, PipeNetwork, VerticalGeometry …).
  /// Everything is looked up before the band set is touched.
  /// </summary>
  public static Task<object?> SetBandSetAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var copyFrom = PluginRuntime.GetOptionalString(parameters, "copyFrom");
    var remove = (PluginRuntime.GetParameter(parameters, "removeStyles") as JsonArray)?.Select(n => n?.GetValue<string>()).OfType<string>()
      .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var bottom = Bands(PluginRuntime.GetParameter(parameters, "bottom") as JsonArray, "bottom");
    var append = Bands(PluginRuntime.GetParameter(parameters, "appendBottom") as JsonArray, "appendBottom");
    if (bottom != null && append != null)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Give bottom (replace) or appendBottom, not both. Nothing was changed.");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var styles = ((CivilDocument)civilDoc).Styles;
      // Exact name first: the company template has band sets that differ only in case ("… VA-Profil" and
      // "… VA-profil"); a case-insensitive match alone changed the wrong one (live 2026-10-09).
      ObjectId Find(string n)
      {
        var loose = ObjectId.Null;
        foreach (ObjectId id in styles.ProfileViewBandSetStyles)
        {
          if (id.IsNull || id.IsErased) continue;
          var existing = ((ProfileViewBandSetStyle)transaction.GetObject(id, OpenMode.ForRead)).Name;
          if (string.Equals(existing, n, StringComparison.Ordinal)) return id;
          if (loose.IsNull && string.Equals(existing, n, StringComparison.OrdinalIgnoreCase)) loose = id;
        }
        return loose;
      }

      // Check every band style before anything is created or changed.
      foreach (var band in (bottom ?? new()).Concat(append ?? new()))
        if (!BandStyleExists(styles, transaction, band.Type, band.Style))
          throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"No {band.Type} band style '{band.Style}'. Nothing was changed.");

      var targetId = Find(name);
      var created = false;
      if (targetId.IsNull)
      {
        if (copyFrom != null)
        {
          var sourceId = Find(copyFrom);
          if (sourceId.IsNull) throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Band set '{copyFrom}' was not found. Nothing was changed.");
          targetId = ((ProfileViewBandSetStyle)transaction.GetObject(sourceId, OpenMode.ForWrite)).CopyAsSibling(name);
        }
        else
        {
          // Civil cannot add a band to an empty band list: a band set from ProfileViewBandSetStyles.Add has none, and a
          // list emptied by RemoveAll has no native vector (NullReferenceException in Add, live 2026-10-09). A new band
          // set is therefore a copy of one with bottom bands and no top bands; its bands are replaced below.
          if (bottom == null || bottom.Count == 0)
            throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Band set '{name}' does not exist: give copyFrom, or bottom with at least one band. Nothing was changed.");
          var seedId = Seed(styles, transaction) ?? throw new JsonRpcDispatchException("CIVIL3D.OPERATION_FAILED",
            "No band set with bottom bands and no top bands to start a new one from; give copyFrom. Nothing was changed.");
          targetId = ((ProfileViewBandSetStyle)transaction.GetObject(seedId, OpenMode.ForWrite)).CopyAsSibling(name);
        }
        created = true;
      }
      else if (copyFrom != null)
      {
        throw new JsonRpcDispatchException("CIVIL3D.CONFLICT", $"Band set '{name}' already exists; copyFrom only creates new ones. Nothing was changed.");
      }

      var set = (ProfileViewBandSetStyle)transaction.GetObject(targetId, OpenMode.ForWrite);
      var removed = new List<string>();
      var top = set.GetTopBandSetItems();
      if (top != null)
        for (var i = top.Count - 1; i >= 0; i--)
        {
          var styleName = StyleName(top[i], transaction);
          if (styleName != null && remove.Contains(styleName)) { top.RemoveAt(i); removed.Add(styleName); }
        }

      // New bands go in first and the old ones come out after, so the list is never empty while bands are added.
      var bottomItems = set.GetBottomBandSetItems();
      var original = bottomItems?.Count ?? 0;
      var adding = bottom ?? append ?? new();
      if (adding.Count > 0 && original == 0)
        throw new JsonRpcDispatchException("CIVIL3D.OPERATION_FAILED",
          $"Band set '{name}' has no bottom bands and Civil cannot add to an empty band list; create it again with copyFrom. Nothing was changed.");
      foreach (var band in adding)
      {
        bottomItems!.Add(database, band.Type, band.Style);
        var added = bottomItems[bottomItems.Count - 1];
        if (band.Gap != null) added.Gap = band.Gap.Value;
        // Not every property is valid for every band type (InvalidOperationException, as when they are read): a value
        // that does not apply is refused with the band named, and the transaction is not committed.
        void Apply(string what, Action set)
        {
          try { set(); }
          catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
          {
            throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Band '{band.Style}' ({band.Type}) does not take {what}: {ex.Message} Nothing was changed.");
          }
        }
        if (band.ShowLabels != null) Apply("showLabels", () => added.ShowLabels = band.ShowLabels.Value);
        if (band.LabelAtStart != null) Apply("labelAtStart", () => added.LabelAtStartStation = band.LabelAtStart.Value);
        if (band.LabelAtEnd != null) Apply("labelAtEnd", () => added.LabelAtEndStation = band.LabelAtEnd.Value);
        if (band.MajorInterval != null) Apply("majorInterval", () => added.MajorInterval = band.MajorInterval.Value);
        if (band.MinorInterval != null) Apply("minorInterval", () => added.MinorInterval = band.MinorInterval.Value);
      }
      var replaced = 0;
      for (var i = original - 1; i >= 0; i--)
      {
        var styleName = StyleName(bottomItems![i], transaction);
        if (bottom != null) { bottomItems.RemoveAt(i); replaced++; }
        else if (styleName != null && remove.Contains(styleName)) { bottomItems.RemoveAt(i); removed.Add(styleName); }
      }
      if (top != null) set.SetTopBandSetItems(top);
      if (bottomItems != null) set.SetBottomBandSetItems(bottomItems);

      return new Dictionary<string, object?>
      {
        ["name"] = name,
        ["created"] = created,
        ["copiedFrom"] = created ? copyFrom : null,
        ["removed"] = removed,
        ["replacedBands"] = bottom != null && !(created && copyFrom == null) ? replaced : null,
        ["top"] = Items(set.GetTopBandSetItems(), transaction),
        ["bottom"] = Items(set.GetBottomBandSetItems(), transaction),
      };
    });
  }

  private sealed record Band(Autodesk.Civil.BandType Type, string Style, double? Gap, bool? ShowLabels = null, bool? LabelAtStart = null,
    bool? LabelAtEnd = null, double? MajorInterval = null, double? MinorInterval = null);

  /// <summary>A band set to copy for a new one: bottom bands (so bands can be added) and no top bands.</summary>
  private static ObjectId? Seed(StylesRoot styles, Transaction transaction)
  {
    foreach (ObjectId id in styles.ProfileViewBandSetStyles)
    {
      if (id.IsNull || id.IsErased) continue;
      var set = (ProfileViewBandSetStyle)transaction.GetObject(id, OpenMode.ForRead);
      if ((set.GetTopBandSetItems()?.Count ?? 0) == 0 && (set.GetBottomBandSetItems()?.Count ?? 0) > 0) return id;
    }
    return null;
  }

  private static List<Band>? Bands(JsonArray? array, string key)
  {
    if (array == null) return null;
    return array.Select((n, i) =>
    {
      if (n is not JsonObject o) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{key}[{i}] must be {{bandType, style, gap?}}. Nothing was changed.");
      var typeText = o["bandType"]?.GetValue<string>() ?? "";
      if (!Enum.TryParse<Autodesk.Civil.BandType>(typeText, true, out var type))
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{key}[{i}]: band type '{typeText}' is not one of {string.Join(", ", Enum.GetNames<Autodesk.Civil.BandType>())}. Nothing was changed.");
      var style = o["style"]?.GetValue<string>();
      if (string.IsNullOrWhiteSpace(style)) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{key}[{i}] needs a style. Nothing was changed.");
      double? gap = o["gap"] is JsonValue g ? g.GetValue<double>() : null;
      // C98f: a new item gets Civil's defaults (labels hidden), so a replaced list lost every label (live 2026-10-10,
      // VA tryck rewritten with {bandType, style, gap}); the item settings listBandSets reports can now be given back.
      bool? Flag(string k) => o[k] is JsonValue v ? v.GetValue<bool>() : null;
      double? Num(string k) => o[k] is JsonValue v ? v.GetValue<double>() : null;
      return new Band(type, style!, gap, Flag("showLabels"), Flag("labelAtStart"), Flag("labelAtEnd"), Num("majorInterval"), Num("minorInterval"));
    }).ToList();
  }

  private static bool BandStyleExists(StylesRoot styles, Transaction transaction, Autodesk.Civil.BandType type, string name)
  {
    var root = styles.BandStyles;
    StyleCollectionBase? collection = type switch
    {
      Autodesk.Civil.BandType.ProfileData => root.ProfileViewProfileDataBandStyles,
      Autodesk.Civil.BandType.PipeNetwork => root.ProfileViewPipeNetworkBandStyles,
      Autodesk.Civil.BandType.VerticalGeometry => root.ProfileViewVerticalGeometryBandStyles,
      Autodesk.Civil.BandType.HorizontalGeometry => root.ProfileViewHorizontalGeometryBandStyles,
      Autodesk.Civil.BandType.SuperelevationData => root.ProfileViewSuperElevationBandStyles,
      Autodesk.Civil.BandType.SectionalData => root.ProfileViewSectionalDataBandStyles,
      _ => null,
    };
    if (collection == null) return true;   // other types: AutoCAD checks the name when the band is added
    foreach (ObjectId id in collection)
      if (!id.IsNull && transaction.GetObject(id, OpenMode.ForRead) is StyleBase s && string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)) return true;
    return false;
  }

  private static string? StyleName(ProfileViewBandSetItem item, Transaction transaction)
  {
    try
    {
      var id = ((BandSetItem)item).BandStyleId;
      return id.IsNull ? null : (transaction.GetObject(id, OpenMode.ForRead) as StyleBase)?.Name;
    }
    catch (Exception ex) when (ex is Autodesk.AutoCAD.Runtime.Exception or InvalidOperationException)
    {
      return null;
    }
  }

  private static List<Dictionary<string, object?>> Items(ProfileViewBandSetItemCollection items, Transaction transaction)
  {
    var list = new List<Dictionary<string, object?>>();
    foreach (ProfileViewBandSetItem item in items)
    {
      string? style = null;
      try
      {
        var styleId = ((BandSetItem)item).BandStyleId;
        style = styleId.IsNull ? null : (transaction.GetObject(styleId, OpenMode.ForRead) as StyleBase)?.Name;
      }
      catch (Exception ex) when (ex is Autodesk.AutoCAD.Runtime.Exception or InvalidOperationException)
      {
        // a band whose style cannot be read: listed without one
      }
      // Some properties are invalid for some band types (InvalidOperationException, live 2026-10-09): read each safely.
      list.Add(new Dictionary<string, object?>
      {
        ["bandType"] = Safe(() => item.BandType.ToString()),
        ["style"] = style,
        ["gap"] = Safe(() => (object)Math.Round(item.Gap, 4)),
        ["showLabels"] = Safe(() => (object)item.ShowLabels),
        ["labelAtStart"] = Safe(() => (object)item.LabelAtStartStation),
        ["labelAtEnd"] = Safe(() => (object)item.LabelAtEndStation),
        ["majorInterval"] = Safe(() => (object)item.MajorInterval),
        ["minorInterval"] = Safe(() => (object)item.MinorInterval),
      });
    }
    return list;
  }

  private static object? Safe(Func<object?> read)
  {
    try
    {
      return read();
    }
    catch (Exception ex) when (ex is Autodesk.AutoCAD.Runtime.Exception or InvalidOperationException)
    {
      return null;
    }
  }
}
