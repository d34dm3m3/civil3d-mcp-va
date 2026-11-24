using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using Autodesk.Civil.DatabaseServices.Styles;

namespace Civil3DMcpPlugin;

/// <summary>
/// Pipes and structures shown in a profile view (the Pipe Networks tab of the profile view properties):
/// whether each part is drawn and its style override in that view only, so a part can be hidden or drawn
/// differently in the profile without touching its plan display. Also the profile view style (vertical
/// exaggeration), which sets the vertical scale for a given horizontal scale.
/// </summary>
public static class ProfileViewPartCommands
{
  private sealed record ViewPart(string Kind, GraphOverride Override, ObjectId PartId, string Name, string Network);

  private static string? StyleName(Transaction transaction, ObjectId id) =>
    id.IsNull ? null : (transaction.GetObject(id, OpenMode.ForRead) as StyleBase)?.Name;

  // The network is looked up by part name over all networks: Part.NetworkName on the override's id came back
  // empty for every part live (2026-10-06), so the network filter matched nothing.
  private static List<ViewPart> Parts(ProfileView view, Transaction transaction, CivilDocument civilDoc)
  {
    var pipeNetwork = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    var structureNetwork = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (ObjectId networkId in civilDoc.GetPipeNetworkIds())
    {
      if (transaction.GetObject(networkId, OpenMode.ForRead) is not Network network) continue;
      foreach (ObjectId id in network.GetPipeIds())
        if (transaction.GetObject(id, OpenMode.ForRead) is Part p && p.Name != null) pipeNetwork[p.Name] = network.Name;
      foreach (ObjectId id in network.GetStructureIds())
        if (transaction.GetObject(id, OpenMode.ForRead) is Part p && p.Name != null) structureNetwork[p.Name] = network.Name;
    }

    var parts = new List<ViewPart>();
    foreach (PipeOverride o in view.PipeOverrides)
      parts.Add(new ViewPart("pipe", o, o.PipeId, o.PipeName, pipeNetwork.GetValueOrDefault(o.PipeName ?? "", "")));
    foreach (StructureOverride o in view.StructureOverrides)
      parts.Add(new ViewPart("structure", o, o.StructId, o.StructName, structureNetwork.GetValueOrDefault(o.StructName ?? "", "")));
    return parts;
  }

  private static List<ViewPart> Select(List<ViewPart> parts, string? network, string? prefix, string kind) =>
    parts.Where(p => (kind == "all" || p.Kind == kind)
        && (string.IsNullOrWhiteSpace(network) || string.Equals(p.Network, network, StringComparison.OrdinalIgnoreCase))
        && (string.IsNullOrWhiteSpace(prefix) || p.Name.StartsWith(prefix + "-", StringComparison.OrdinalIgnoreCase)))
      .ToList();

  private static string ReadKind(JsonObject? parameters)
  {
    var kind = (PluginRuntime.GetOptionalString(parameters, "partKind") ?? "all").Trim().ToLowerInvariant();
    return kind is "pipe" or "structure" or "all"
      ? kind
      : throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"partKind must be 'pipe', 'structure' or 'all', not '{kind}'.");
  }

  /// <summary>Summary per network and kind (count, drawn, style override), and the parts themselves when
  /// filtered to one network or prefix.</summary>
  private static Dictionary<string, object?> Describe(ProfileView view, Transaction transaction, List<ViewPart> parts, bool listParts)
  {
    object? Entry(ViewPart p) => new Dictionary<string, object?>
    {
      ["name"] = p.Name,
      ["kind"] = p.Kind,
      ["network"] = p.Network,
      ["draw"] = p.Override.Draw,
      ["overrideStyle"] = p.Override.UseOverrideStyle ? StyleName(transaction, p.Override.OverrideStyleId) : null,
    };

    var styleId = Civil3DCompatibility.GetPropertyValue<ObjectId>(view, "StyleId");
    var style = styleId.IsNull ? null : transaction.GetObject(styleId, OpenMode.ForRead) as ProfileViewStyle;
    return new Dictionary<string, object?>
    {
      ["profileViewName"] = view.Name,
      ["style"] = style?.Name,
      ["verticalExaggeration"] = style?.GraphStyle.VerticalExaggeration,
      ["groups"] = parts.GroupBy(p => (p.Network, p.Kind)).Select(g => new Dictionary<string, object?>
      {
        ["network"] = g.Key.Network,
        ["kind"] = g.Key.Kind,
        ["count"] = g.Count(),
        ["drawn"] = g.Count(p => p.Override.Draw),
        ["overrideStyles"] = g.Where(p => p.Override.UseOverrideStyle)
          .Select(p => StyleName(transaction, p.Override.OverrideStyleId)).Distinct().ToList(),
      }).ToList(),
      ["parts"] = listParts ? parts.Select(Entry).ToList() : null,
    };
  }

  public static Task<object?> GetProfileViewPartsAsync(JsonObject? parameters)
  {
    var viewName = PluginRuntime.GetRequiredString(parameters, "profileViewName");
    var network = PluginRuntime.GetOptionalString(parameters, "network");
    var prefix = PluginRuntime.GetOptionalString(parameters, "namePrefix");
    var kind = ReadKind(parameters);
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var view = ProfileEditCommands.FindProfileViewByName((CivilDocument)civilDoc, transaction, viewName);
      var parts = Select(Parts(view, transaction, (CivilDocument)civilDoc), network, prefix, kind);
      return Describe(view, transaction, parts, listParts: !string.IsNullOrWhiteSpace(network) || !string.IsNullOrWhiteSpace(prefix));
    });
  }

  /// <summary>Draw on/off and/or a style override (empty string clears it) for the selected parts of a
  /// profile view. At least one of drawParts and overrideStyle is required; no matching part is an error.</summary>
  public static Task<object?> SetProfileViewPartsAsync(JsonObject? parameters)
  {
    var viewName = PluginRuntime.GetRequiredString(parameters, "profileViewName");
    var network = PluginRuntime.GetOptionalString(parameters, "network");
    var prefix = PluginRuntime.GetOptionalString(parameters, "namePrefix");
    var kind = ReadKind(parameters);
    var draw = PluginRuntime.GetOptionalBool(parameters, "drawParts");
    var overrideStyle = PluginRuntime.GetOptionalString(parameters, "overrideStyle");
    // C98i: exact part names (namePrefix only selects whole runs, prefix + "-"); live 2026-10-10: the pump station inlet of
    // TS (five pipes and structures off the alignment) was to be hidden in the profile view. Every name must be in the view.
    var names = (PluginRuntime.GetParameter(parameters, "names") as JsonArray)?.Select(n => n?.GetValue<string>()).OfType<string>()
      .ToHashSet(StringComparer.OrdinalIgnoreCase);
    if (draw == null && overrideStyle == null)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Give drawParts and/or overrideStyle (empty string clears the override).");
    if (overrideStyle is { Length: > 0 } && kind == "all")
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "A style override needs partKind 'pipe' or 'structure' (pipe and structure styles differ).");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var cdoc = (CivilDocument)civilDoc;
      var found = ProfileEditCommands.FindProfileViewByName(cdoc, transaction, viewName);
      var view = CivilObjectUtils.GetRequiredObject<ProfileView>(transaction, found.ObjectId, OpenMode.ForWrite);
      var parts = Select(Parts(view, transaction, (CivilDocument)civilDoc), network, prefix, kind);
      if (names is { Count: > 0 })
      {
        var missing = names.Where(n => !parts.Any(p => string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase))).ToList();
        if (missing.Count > 0)
          throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND",
            $"Not in profile view '{view.Name}' (with the other filters): {string.Join(", ", missing)}. Nothing was changed.");
        parts = parts.Where(p => names.Contains(p.Name)).ToList();
      }
      if (parts.Count == 0)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
          $"No {(kind == "all" ? "part" : kind)} in profile view '{view.Name}' matches network '{network}' and prefix '{prefix}'. Nothing was changed.");

      var styleId = ObjectId.Null;
      if (overrideStyle is { Length: > 0 })
        styleId = kind == "pipe"
          ? LookupUtils.GetStyleIdStrict(cdoc.Styles.PipeStyles, transaction, overrideStyle, "Pipe style")
          : LookupUtils.GetStyleIdStrict(cdoc.Styles.StructureStyles, transaction, overrideStyle, "Structure style");

      foreach (var p in parts)
      {
        if (draw != null) p.Override.Draw = draw.Value;
        if (overrideStyle == null) continue;
        if (styleId.IsNull)
        {
          p.Override.UseOverrideStyle = false;
        }
        else
        {
          p.Override.OverrideStyleId = styleId;
          p.Override.UseOverrideStyle = true;
        }
      }

      var readBack = Select(Parts(view, transaction, (CivilDocument)civilDoc), network, prefix, kind);
      if (names is { Count: > 0 }) readBack = readBack.Where(p => names.Contains(p.Name)).ToList();   // C98i: only the named parts
      var notApplied = readBack.Count(p => (draw != null && p.Override.Draw != draw.Value)
        || (overrideStyle != null && p.Override.UseOverrideStyle != !styleId.IsNull));
      if (notApplied > 0)
        throw new JsonRpcDispatchException("CIVIL3D.TRANSACTION_FAILED", $"{notApplied} part(s) in profile view '{view.Name}' did not take the change.");

      var result = Describe(view, transaction, readBack, listParts: false);
      result["changed"] = parts.Count;
      return result;
    });
  }

  public static Task<object?> ListProfileViewStylesAsync(JsonObject? parameters)
  {
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var styles = new List<Dictionary<string, object?>>();
      foreach (ObjectId id in ((CivilDocument)civilDoc).Styles.ProfileViewStyles)
      {
        if (transaction.GetObject(id, OpenMode.ForRead) is not ProfileViewStyle style) continue;
        styles.Add(new Dictionary<string, object?>
        {
          ["name"] = style.Name,
          ["verticalExaggeration"] = style.GraphStyle.VerticalExaggeration,
          ["direction"] = style.GraphStyle.Direction.ToString(),
          ["axisOffsets"] = AxisOffsets(style),
          ["description"] = style.Description,
        });
      }

      return new Dictionary<string, object?> { ["styles"] = styles };
    });
  }

  /// <summary>
  /// C99a: a profile view style's graph settings: direction (GraphStyle.Direction, LeftToRight or RightToLeft) and vertical
  /// exaggeration, optionally on a copy (copyAs, StyleBase.CopyAsSibling). Use case (pilot project, 2026-10-10):
  /// the stationing runs east to west, so a profile drawn left to right cannot stand under a readable plan; drawn right to
  /// left (stations growing to the left) it stands in line with a plan that has east on the right. Values are checked
  /// before the first write and read back; a value Civil 3D does not keep undoes the call.
  /// </summary>
  public static Task<object?> SetProfileViewStyleGraphAsync(JsonObject? parameters)
  {
    var styleName = PluginRuntime.GetRequiredString(parameters, "style").Trim();
    var copyAs = PluginRuntime.GetOptionalString(parameters, "copyAs")?.Trim();
    var directionText = PluginRuntime.GetOptionalString(parameters, "direction")?.Trim();
    var exaggeration = PluginRuntime.GetOptionalDouble(parameters, "verticalExaggeration");
    // C122b: the graph title text (GraphStyle.TitleStyle.Text, with field codes). Use case (a project, 2026-10-12): the title's
    // scale fields followed the drawing scale (1:200) while the sheet viewport is 1:400, so the title said H 1:200 V 1:50.
    var titleText = PluginRuntime.GetOptionalString(parameters, "titleText");
    // C99c: axis offsets (GridStyle.AxisOffsetLeft/Right/Bottom/Above, plotted metres): the gap between graph and axis that
    // the grid lines bridge (requested 2026-10-10: shorter elevation ticks give a more compact profile)
    var offsets = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
    if (PluginRuntime.GetParameter(parameters, "axisOffsets") is JsonObject offsetNode)
      foreach (var (key, value) in offsetNode)
      {
        if (key.ToLowerInvariant() is not ("left" or "right" or "bottom" or "above"))
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"axisOffsets takes left, right, bottom and above, not '{key}'. Nothing was changed.");
        var v = value?.GetValue<double>() ?? -1;
        if (v < 0) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "An axis offset must be >= 0 (plotted metres). Nothing was changed.");
        offsets[key] = v;
      }
    GraphDirectionType? direction = null;
    if (!string.IsNullOrEmpty(directionText))
    {
      if (!Enum.TryParse<GraphDirectionType>(directionText, true, out var parsed))
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "direction must be LeftToRight or RightToLeft. Nothing was changed.");
      direction = parsed;
    }
    if (exaggeration is <= 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "verticalExaggeration must be > 0. Nothing was changed.");
    if (direction == null && exaggeration == null && offsets.Count == 0 && titleText == null && string.IsNullOrEmpty(copyAs))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Give direction, verticalExaggeration, axisOffsets, titleText and/or copyAs. Nothing was changed.");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var styles = ((CivilDocument)civilDoc).Styles.ProfileViewStyles;
      var sourceId = LookupUtils.GetStyleIdStrict(styles, transaction, styleName, "Profile view style");
      var targetId = sourceId;
      var copied = false;
      if (!string.IsNullOrEmpty(copyAs))
      {
        var existing = ObjectId.Null;
        foreach (ObjectId id in styles)
          if (transaction.GetObject(id, OpenMode.ForRead) is ProfileViewStyle s && string.Equals(s.Name, copyAs, StringComparison.OrdinalIgnoreCase))
            existing = id;
        if (!existing.IsNull)
        {
          targetId = existing;   // re-run: the copy exists and is changed again
        }
        else
        {
          targetId = ((ProfileViewStyle)transaction.GetObject(sourceId, OpenMode.ForWrite)).CopyAsSibling(copyAs);
          copied = true;
        }
      }

      var style = (ProfileViewStyle)transaction.GetObject(targetId, OpenMode.ForWrite);
      var before = new Dictionary<string, object?>
      {
        ["direction"] = style.GraphStyle.Direction.ToString(),
        ["verticalExaggeration"] = style.GraphStyle.VerticalExaggeration,
        ["axisOffsets"] = AxisOffsets(style),
        ["titleText"] = style.GraphStyle.TitleStyle.Text,
      };
      if (titleText != null)
      {
        style.GraphStyle.TitleStyle.Text = titleText;
        if (style.GraphStyle.TitleStyle.Text != titleText)
          throw new JsonRpcDispatchException("CIVIL3D.TRANSACTION_FAILED", $"Profile view style '{style.Name}' did not keep the title text. Nothing was changed.");
      }
      if (direction != null) style.GraphStyle.Direction = direction.Value;
      var grid = style.GridStyle;
      foreach (var (key, v) in offsets)
        switch (key.ToLowerInvariant())
        {
          case "left": grid.AxisOffsetLeft = v; break;
          case "right": grid.AxisOffsetRight = v; break;
          case "bottom": grid.AxisOffsetBottom = v; break;
          case "above": grid.AxisOffsetAbove = v; break;
        }
      var kept = AxisOffsets(style);
      foreach (var (key, v) in offsets)
        if (Math.Abs(kept[key.ToLowerInvariant()] - v) > 1e-9)
          throw new JsonRpcDispatchException("CIVIL3D.TRANSACTION_FAILED", $"Profile view style '{style.Name}' did not keep axis offset {key}. Nothing was changed.");
      if (exaggeration != null) style.GraphStyle.VerticalExaggeration = exaggeration.Value;
      if ((direction != null && style.GraphStyle.Direction != direction.Value)
          || (exaggeration != null && Math.Abs(style.GraphStyle.VerticalExaggeration - exaggeration.Value) > 1e-9))
        throw new JsonRpcDispatchException("CIVIL3D.TRANSACTION_FAILED", $"Profile view style '{style.Name}' did not keep the graph settings. Nothing was changed.");

      return new Dictionary<string, object?>
      {
        ["style"] = style.Name,
        ["copiedFrom"] = copied ? styleName : null,
        ["direction"] = style.GraphStyle.Direction.ToString(),
        ["verticalExaggeration"] = style.GraphStyle.VerticalExaggeration,
        ["axisOffsets"] = AxisOffsets(style),
        ["titleText"] = style.GraphStyle.TitleStyle.Text,
        ["before"] = copied ? null : before,
      };
    });
  }

  private static Dictionary<string, double> AxisOffsets(ProfileViewStyle style) => new()
  {
    ["left"] = style.GridStyle.AxisOffsetLeft,
    ["right"] = style.GridStyle.AxisOffsetRight,
    ["bottom"] = style.GridStyle.AxisOffsetBottom,
    ["above"] = style.GridStyle.AxisOffsetAbove,
  };

  /// <summary>
  /// C99b: model coordinates of station/elevation points in a profile view and back (ProfileView.FindXYAtStationAndElevation,
  /// FindStationAndElevationAtXY). points: [{station, elevation}] or [{x, y}]. Use case (pilot project, 2026-10-10): text and
  /// water-level lines drawn into a profile view drawn right to left landed 1 m off after the station range changed, because
  /// Civil keeps its own origin for such a view; the view itself is the only reliable source. A point outside the view's
  /// range is reported with ok false.
  /// </summary>
  public static Task<object?> ProfileViewStationToXyAsync(JsonObject? parameters)
  {
    var viewName = PluginRuntime.GetRequiredString(parameters, "profileViewName");
    if (PluginRuntime.GetParameter(parameters, "points") is not JsonArray points || points.Count == 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "points must list at least one {station, elevation} or {x, y}.");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var view = ProfileEditCommands.FindProfileViewByName((CivilDocument)civilDoc, transaction, viewName);
      var result = new List<Dictionary<string, object?>>();
      foreach (var node in points)
      {
        if (node is not JsonObject p) continue;
        if (p["station"] != null)
        {
          double s = p["station"]!.GetValue<double>(), e = p["elevation"]?.GetValue<double>() ?? view.ElevationMin;
          double x = 0, y = 0;
          var ok = view.FindXYAtStationAndElevation(s, e, ref x, ref y);
          result.Add(new Dictionary<string, object?> { ["station"] = s, ["elevation"] = e, ["x"] = ok ? x : null, ["y"] = ok ? y : null, ["ok"] = ok });
        }
        else
        {
          double x = p["x"]?.GetValue<double>() ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "A point needs station or x and y."),
                 y = p["y"]?.GetValue<double>() ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "A point needs station or x and y.");
          double s = 0, e = 0;
          var ok = view.FindStationAndElevationAtXY(x, y, ref s, ref e);
          result.Add(new Dictionary<string, object?> { ["x"] = x, ["y"] = y, ["station"] = ok ? s : null, ["elevation"] = ok ? e : null, ["ok"] = ok });
        }
      }
      return new Dictionary<string, object?>
      {
        ["profileViewName"] = view.Name,
        ["stationStart"] = view.StationStart,
        ["stationEnd"] = view.StationEnd,
        ["elevationMin"] = view.ElevationMin,
        ["elevationMax"] = view.ElevationMax,
        // C113: the graph's insertion point (lower corner at the start station), for drawing in the bands below it (V3)
        ["location"] = new[] { view.Location.X, view.Location.Y },
        ["points"] = result,
      };
    });
  }

  /// <summary>
  /// C99e: moves a profile view (Graph.Location, the insertion point) to x, y or by dx, dy. Use case (pilot
  /// project, 2026-10-11): two sheet profile views were placed next to an existing one and its end showed in the sheet's viewport;
  /// there was no way to move or delete a profile view. Returns the location before and after and the view's station
  /// range, which does not change.
  /// </summary>
  /// <summary>C106: erase a profile view (with the labels, bands and drawn pipe network parts that belong to it; the
  /// alignment, profiles and networks stay). dryRun reports without erasing. Need (lesson 20, 2026-10-11): profile views
  /// could neither be moved nor erased; C99e moved them.</summary>
  public static Task<object?> DeleteProfileViewAsync(JsonObject? parameters)
  {
    var viewName = PluginRuntime.GetRequiredString(parameters, "profileViewName");
    var dryRun = PluginRuntime.GetOptionalBool(parameters, "dryRun") ?? false;
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var found = ProfileEditCommands.FindProfileViewByName((CivilDocument)civilDoc, transaction, viewName);
      var view = CivilObjectUtils.GetRequiredObject<ProfileView>(transaction, found.ObjectId, OpenMode.ForWrite);
      var alignment = view.AlignmentId.IsNull ? null : (transaction.GetObject(view.AlignmentId, OpenMode.ForRead) as Alignment)?.Name;
      var result = new Dictionary<string, object?>
      {
        ["profileViewName"] = view.Name,
        ["alignmentName"] = alignment,
        ["location"] = new[] { view.Location.X, view.Location.Y },
        ["stationStart"] = view.StationStart,
        ["stationEnd"] = view.StationEnd,
        ["dryRun"] = dryRun,
      };
      if (!dryRun) view.Erase();
      result["erased"] = !dryRun;
      return result;
    });
  }

  public static Task<object?> MoveProfileViewAsync(JsonObject? parameters)
  {
    var viewName = PluginRuntime.GetRequiredString(parameters, "profileViewName");
    var x = PluginRuntime.GetOptionalDouble(parameters, "x");
    var y = PluginRuntime.GetOptionalDouble(parameters, "y");
    var dx = PluginRuntime.GetOptionalDouble(parameters, "dx");
    var dy = PluginRuntime.GetOptionalDouble(parameters, "dy");
    if ((x == null) != (y == null))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Give both x and y, or dx and/or dy. Nothing was changed.");
    if (x == null && dx == null && dy == null)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Give x and y, or dx and/or dy. Nothing was changed.");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var found = ProfileEditCommands.FindProfileViewByName((CivilDocument)civilDoc, transaction, viewName);
      var view = CivilObjectUtils.GetRequiredObject<ProfileView>(transaction, found.ObjectId, OpenMode.ForWrite);
      var before = view.Location;
      var target = x != null
        ? new Autodesk.AutoCAD.Geometry.Point3d(x.Value, y!.Value, before.Z)
        : new Autodesk.AutoCAD.Geometry.Point3d(before.X + (dx ?? 0), before.Y + (dy ?? 0), before.Z);
      view.Location = target;
      return new Dictionary<string, object?>
      {
        ["profileViewName"] = view.Name,
        ["before"] = new[] { before.X, before.Y },
        ["after"] = new[] { view.Location.X, view.Location.Y },
        ["stationStart"] = view.StationStart,
        ["stationEnd"] = view.StationEnd,
      };
    });
  }

  public static Task<object?> SetProfileViewStyleAsync(JsonObject? parameters)
  {
    var viewName = PluginRuntime.GetRequiredString(parameters, "profileViewName");
    var styleName = PluginRuntime.GetRequiredString(parameters, "style");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var cdoc = (CivilDocument)civilDoc;
      var styleId = LookupUtils.GetStyleIdStrict(cdoc.Styles.ProfileViewStyles, transaction, styleName, "Profile view style");
      var found = ProfileEditCommands.FindProfileViewByName(cdoc, transaction, viewName);
      var view = CivilObjectUtils.GetRequiredObject<ProfileView>(transaction, found.ObjectId, OpenMode.ForWrite);
      view.StyleId = styleId;
      var style = (ProfileViewStyle)transaction.GetObject(styleId, OpenMode.ForRead);
      return new Dictionary<string, object?>
      {
        ["profileViewName"] = view.Name,
        ["style"] = style.Name,
        ["verticalExaggeration"] = style.GraphStyle.VerticalExaggeration,
      };
    });
  }
}
