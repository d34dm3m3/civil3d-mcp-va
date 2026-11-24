using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices.Styles;

namespace Civil3DMcpPlugin;

/// <summary>
/// Label style texts and profile view axes (C90). Use case: stations are to be shown as 0/000 everywhere (alignment
/// station labels, structure labels with a station, the profile view's axis, the bands), and Civil's station format
/// cannot give a slash (C80: the ambient station delimiter has PlusSign, MinusSign, Automatic, Underscore, None), so the
/// field code itself is rewritten to two station fields, kilometres and metres, with a slash between. That text lives in
/// the label styles' text components and in the profile view style's axis tick labels, which had no tool outside the
/// profile label kinds (ProfileLabelStyleCommands) and the band styles (BandStyleCommands).
/// A label style collection is named by its property path under CivilDocument.Styles.LabelStyles
/// ("AlignmentLabelStyles.MajorStationLabelStyles", "PipeLabelStyles.PlanProfileLabelStyles",
/// "StructureLabelStyles.LabelStyles"); text components are the Text, ReferenceText and TextForEach components, named by
/// StyleGeneral.Name (StyleBase.Name has only a setter). Profile view axes: ProfileViewStyle.BottomAxis/TopAxis/LeftAxis/
/// RightAxis (AxisStyle) with MajorTickStyle/MinorTickStyle (AxisTickStyle.LabelText, Interval). Every style, component,
/// pattern and value is checked before the first change ("Nothing was changed.").
/// </summary>
public static class LabelStyleTextCommands
{
  private const int MaxDepth = 3;
  private static readonly string[] Axes = { "Bottom", "Top", "Left", "Right" };
  private static readonly string[] Ticks = { "Major", "Minor" };

  // ---------------------------------------------------------------- text rewrite shared with band_style_text_set

  /// <summary>How a text component's contents change: replaced whole (contents) or by find/replaceWith, plain ordinal
  /// or as a .NET regular expression (regex: true; replaceWith may use $1, a literal $ is $$).</summary>
  internal sealed class TextRewrite
  {
    public string? Contents { get; private init; }
    public string? Find { get; private init; }
    public string ReplaceWith { get; private init; } = "";
    public Regex? Pattern { get; private init; }

    public string Apply(string before)
    {
      if (Contents != null) return Contents;
      if (Pattern == null) return before.Replace(Find!, ReplaceWith, StringComparison.Ordinal);
      try
      {
        return Pattern.Replace(before, ReplaceWith);
      }
      catch (RegexMatchTimeoutException)
      {
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "find (regex) took too long on a label text; simplify it. Nothing was changed.");
      }
    }

    internal static TextRewrite Parse(JsonObject? parameters, bool allowContents)
    {
      var contents = allowContents ? PluginRuntime.GetOptionalString(parameters, "contents") : null;
      var find = PluginRuntime.GetOptionalString(parameters, "find");
      var replaceWith = PluginRuntime.GetOptionalString(parameters, "replaceWith");
      var regex = PluginRuntime.GetOptionalBool(parameters, "regex") ?? false;
      if (contents != null)
      {
        if (find != null || replaceWith != null || regex)
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Give contents (the whole text) or find/replaceWith, not both. Nothing was changed.");
        return new TextRewrite { Contents = contents };
      }

      if (string.IsNullOrEmpty(find) || replaceWith == null)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
          (allowContents ? "Give contents, or " : "Give ") + "find (not empty) and replaceWith (may be empty). Nothing was changed.");
      if (!regex) return new TextRewrite { Find = find, ReplaceWith = replaceWith };
      try
      {
        return new TextRewrite { Find = find, ReplaceWith = replaceWith, Pattern = new Regex(find, RegexOptions.None, TimeSpan.FromSeconds(1)) };
      }
      catch (ArgumentException ex)
      {
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"find is not a valid regular expression: {ex.Message}. Nothing was changed.");
      }
    }
  }

  /// <summary>A text-bearing component of a label style: Text, ReferenceText or TextForEach (whose StyleGeneral and
  /// StyleText derive from the Text component's).</summary>
  internal sealed record TextPart(ObjectId Id, string Type, string? Name, bool? Visible, string? Contents);

  internal static List<TextPart> TextParts(LabelStyle style, Transaction transaction)
  {
    var parts = new List<TextPart>();
    foreach (var id in style.GetComponentsDrawOrder())
    {
      var component = transaction.GetObject(id, OpenMode.ForRead);
      var (type, general, text) = Split(component);
      if (general == null || text == null) continue;
      parts.Add(new TextPart(id, type, Safe(() => general.Name), Safe<bool?>(() => general.Visible.Value), Safe(() => text.Contents.Value)));
    }
    return parts;
  }

  private static (string Type, LabelStyleTextComponent.StyleGeneral? General, LabelStyleTextComponent.StyleText? Text) Split(DBObject component) => component switch
  {
    LabelStyleTextComponent t => ("Text", t.General, t.Text),
    LabelStyleReferenceTextComponent r => ("ReferenceText", r.General, r.Text),
    LabelStyleTextForEachComponent f => ("TextForEach", f.General, f.Text),
    _ => (component.GetType().Name, null, null),
  };

  /// <summary>A text component's contents as they are now (after earlier writes in this call).</summary>
  internal static string? ReadContents(Transaction transaction, ObjectId componentId)
  {
    var (_, _, text) = Split(transaction.GetObject(componentId, OpenMode.ForRead));
    return text == null ? null : Safe(() => text.Contents.Value);
  }

  /// <summary>Writes a text component's contents (opened for write here) and returns what Civil reads back.</summary>
  internal static string? WriteContents(Transaction transaction, ObjectId componentId, string contents)
  {
    var (_, _, text) = Split(transaction.GetObject(componentId, OpenMode.ForWrite));
    text!.Contents.Value = contents;
    return Safe(() => text.Contents.Value);
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

  // ---------------------------------------------------------------- label style collections

  /// <summary>C90: the label styles of one collection (path under CivilDocument.Styles.LabelStyles), with their child
  /// styles; without collection the collection paths with style counts.</summary>
  public static Task<object?> ListLabelStylesAsync(JsonObject? parameters)
  {
    var path = PluginRuntime.GetOptionalString(parameters, "collection");
    var contains = PluginRuntime.GetOptionalString(parameters, "contains");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      if (string.IsNullOrWhiteSpace(path))
      {
        return new Dictionary<string, object?>
        {
          ["collections"] = Collections(civilDoc)
            .Where(c => contains == null || c.Path.Contains(contains, StringComparison.OrdinalIgnoreCase))
            .Select(c => new Dictionary<string, object?> { ["path"] = c.Path, ["count"] = Safe(() => c.Collection.Count) }).ToList(),
          ["note"] = "Give collection (a path above) to list its styles.",
        };
      }

      var (collection, resolved) = ResolveCollection(civilDoc, path);
      var top = TopLevelIds(collection);
      var styles = new List<Dictionary<string, object?>>();
      foreach (var id in top)
      {
        if (transaction.GetObject(id, OpenMode.ForRead) is not LabelStyle style) continue;
        if (contains != null && !style.Name.Contains(contains, StringComparison.OrdinalIgnoreCase)) continue;
        var children = Safe(() => style.GetDescendantIds())?.Cast<ObjectId>().Where(c => !c.IsNull && !c.IsErased)
          .Select(c => (transaction.GetObject(c, OpenMode.ForRead) as LabelStyle)?.Name).OfType<string>().ToList();
        styles.Add(new Dictionary<string, object?> { ["name"] = style.Name, ["childStyles"] = children is { Count: > 0 } ? children : null });
      }
      return new Dictionary<string, object?> { ["collection"] = resolved, ["count"] = styles.Count, ["styles"] = styles };
    });
  }

  /// <summary>C90: a label style's text components (name, type, visibility, contents with field codes).</summary>
  public static Task<object?> GetLabelStyleTextAsync(JsonObject? parameters)
  {
    var path = PluginRuntime.GetRequiredString(parameters, "collection");
    var styleName = PluginRuntime.GetRequiredString(parameters, "style");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var (collection, resolved) = ResolveCollection(civilDoc, path);
      var style = (LabelStyle)transaction.GetObject(FindStyleId(collection, resolved, styleName, transaction), OpenMode.ForRead);
      return Describe(resolved, style, transaction);
    });
  }

  /// <summary>
  /// C90: changes the contents of one text component (component) or of all text components of a label style: contents
  /// replaces the whole text, find/replaceWith replaces a string (regex: true for a regular expression). Without style,
  /// find/replaceWith runs over every style of the collection (child styles included); styles without the named
  /// component are listed as skipped. dryRun lists the changes without making them.
  /// </summary>
  public static Task<object?> SetLabelStyleTextAsync(JsonObject? parameters)
  {
    var path = PluginRuntime.GetRequiredString(parameters, "collection");
    var styleName = PluginRuntime.GetOptionalString(parameters, "style")?.Trim();
    var componentName = PluginRuntime.GetOptionalString(parameters, "component")?.Trim();
    var dryRun = PluginRuntime.GetOptionalBool(parameters, "dryRun") ?? false;
    var rewrite = TextRewrite.Parse(parameters, allowContents: true);
    if (rewrite.Contents != null && string.IsNullOrEmpty(styleName))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "contents replaces whole texts; give style (find/replaceWith may run over the whole collection). Nothing was changed.");
    if (componentName is "") componentName = null;

    object? Body(Autodesk.AutoCAD.ApplicationServices.Document doc, CivilDocument civilDoc, Database database, Transaction transaction)
    {
      var (collection, resolved) = ResolveCollection(civilDoc, path);
      var styleIds = string.IsNullOrEmpty(styleName)
        ? AllStyleIds(collection)
        : new List<ObjectId> { FindStyleId(collection, resolved, styleName, transaction) };
      var changes = new List<(string Style, TextPart Part, string After)>();
      var skipped = new List<string>();
      var unchanged = 0;
      foreach (var id in styleIds)
      {
        if (transaction.GetObject(id, OpenMode.ForRead) is not LabelStyle style) continue;
        var parts = TextParts(style, transaction);
        var targets = componentName == null ? parts : parts.Where(p => string.Equals(p.Name, componentName, StringComparison.OrdinalIgnoreCase)).ToList();
        // One text for every component would wipe a style with several texts: contents without component needs a style
        // with exactly one text component.
        if (rewrite.Contents != null && componentName == null && parts.Count > 1)
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
            $"Label style '{style.Name}' has {parts.Count} text components ({string.Join(", ", parts.Select(p => p.Name))}); give component with contents. Nothing was changed.");
        if (targets.Count == 0)
        {
          if (!string.IsNullOrEmpty(styleName))
            throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", componentName == null
              ? $"Label style '{style.Name}' has no text components. Nothing was changed."
              : $"Label style '{style.Name}' has no text component '{componentName}'. Text components: {string.Join(", ", parts.Select(p => p.Name))}. Nothing was changed.");
          skipped.Add(style.Name);
          continue;
        }

        foreach (var part in targets)
        {
          if (part.Contents == null)
            throw new JsonRpcDispatchException("CIVIL3D.OPERATION_FAILED", $"The text of component '{part.Name}' in '{style.Name}' cannot be read. Nothing was changed.");
          var after = rewrite.Apply(part.Contents);
          if (after == part.Contents) unchanged++;
          else changes.Add((style.Name, part, after));
        }
      }

      // Parents come before their child styles (AllStyleIds); a child component that already shows the new text after its
      // parent was written follows the parent and is not written, so it stays inherited rather than becoming an override.
      var changed = new List<Dictionary<string, object?>>();
      var followedParent = 0;
      foreach (var (style, part, after) in changes)
      {
        if (!dryRun && ReadContents(transaction, part.Id) == after)
        {
          followedParent++;
          continue;
        }
        var readBack = dryRun ? after : WriteContents(transaction, part.Id, after);
        changed.Add(new Dictionary<string, object?>
        {
          ["style"] = style, ["component"] = part.Name, ["type"] = part.Type, ["before"] = part.Contents, ["after"] = readBack,
        });
      }

      return new Dictionary<string, object?>
      {
        ["collection"] = resolved,
        ["dryRun"] = dryRun,
        ["styles"] = styleIds.Count,
        ["changed"] = changed,
        ["unchanged"] = unchanged,
        ["followedParent"] = followedParent > 0 ? followedParent : null,
        ["skippedWithoutComponent"] = skipped.Count > 0 ? skipped : null,
        ["note"] = dryRun ? "Dry run: nothing was changed." : changed.Count == 0 ? "Nothing matched; nothing was changed." : null,
      };
    }

    return dryRun ? CivilExecution.ReadAsync<object?>(Body) : CivilExecution.WriteAsync<object?>(Body);
  }

  /// <summary>C90: copies a label style in its collection under a new name (StyleBase.CopyAsSibling), e.g. a structure
  /// label style for the Hv/Vv text (C92).</summary>
  public static Task<object?> CopyLabelStyleAsync(JsonObject? parameters)
  {
    var path = PluginRuntime.GetRequiredString(parameters, "collection");
    var styleName = PluginRuntime.GetRequiredString(parameters, "style");
    var newName = PluginRuntime.GetRequiredString(parameters, "newName").Trim();
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      // C93b (live 2026-10-10): "Brunn Hv/Vv" made CopyAsSibling throw ArgumentException, reported as an internal
      // error. Style names follow the AutoCAD symbol name rules (no < > / \ " : ; ? * | , = `); checked here, on the
      // main thread, before anything is opened.
      try
      {
        SymbolUtilityServices.ValidateSymbolName(newName, false);
      }
      catch (Autodesk.AutoCAD.Runtime.Exception)
      {
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"'{newName}' is not a valid style name (none of < > / \\ \" : ; ? * | , = `). Nothing was changed.");
      }

      var (collection, resolved) = ResolveCollection(civilDoc, path);
      var sourceId = FindStyleId(collection, resolved, styleName, transaction);
      if (AllStyleIds(collection).Any(id => string.Equals((transaction.GetObject(id, OpenMode.ForRead) as LabelStyle)?.Name, newName, StringComparison.OrdinalIgnoreCase)))
        throw new JsonRpcDispatchException("CIVIL3D.CONFLICT", $"{resolved} already has a style named '{newName}'. Nothing was changed.");
      var source = (LabelStyle)transaction.GetObject(sourceId, OpenMode.ForWrite);
      ObjectId copyId;
      try
      {
        copyId = source.CopyAsSibling(newName);
      }
      catch (ArgumentException ex)
      {
        // Need: Civil may refuse a name the AutoCAD rules allow; the transaction is aborted, nothing stays.
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Civil 3D refused the style name '{newName}': {ex.Message} Nothing was changed.");
      }
      var copy = (LabelStyle)transaction.GetObject(copyId, OpenMode.ForRead);
      var result = Describe(resolved, copy, transaction);
      result["copiedFrom"] = source.Name;
      return result;
    });
  }

  private static Dictionary<string, object?> Describe(string collection, LabelStyle style, Transaction transaction) => new()
  {
    ["collection"] = collection,
    ["style"] = style.Name,
    ["components"] = TextParts(style, transaction).Select(p => new Dictionary<string, object?>
    {
      ["name"] = p.Name, ["type"] = p.Type, ["visible"] = p.Visible, ["contents"] = p.Contents,
    }).ToList(),
  };

  /// <summary>A label style collection by its property path under CivilDocument.Styles.LabelStyles (a leading "Styles."
  /// or "LabelStyles." is accepted, as styles_list_external names them).</summary>
  internal static (LabelStyleCollection Collection, string Path) ResolveCollection(CivilDocument civilDoc, string raw)
  {
    var parts = raw.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    if (parts.Count > 0 && string.Equals(parts[0], "Styles", StringComparison.OrdinalIgnoreCase)) parts.RemoveAt(0);
    if (parts.Count > 0 && string.Equals(parts[0], "LabelStyles", StringComparison.OrdinalIgnoreCase)) parts.RemoveAt(0);
    if (parts.Count == 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"'{raw}' names no label style collection; label_styles_list without collection lists them. Nothing was changed.");
    object current = civilDoc.Styles.LabelStyles;
    var walked = new List<string>();
    foreach (var part in parts)
    {
      object? value;
      bool found;
      try
      {
        (found, value) = Civil3DCompatibility.GetPropertyIgnoreCase(current, part);
      }
      catch (Exception ex) when (ex is not JsonRpcDispatchException)
      {
        throw new JsonRpcDispatchException("CIVIL3D.OPERATION_FAILED", $"'{part}' cannot be read in this drawing ({ex.Message}). Nothing was changed.");
      }
      if (!found || value == null || (value is not LabelStyleCollection && value is not LabelStylesNode))
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
          $"'{part}' is not a label style collection under {(walked.Count == 0 ? "LabelStyles" : string.Join(".", walked))}. Under it: "
          + $"{string.Join(", ", Civil3DCompatibility.GetCivilReferencePropertyNames(current))}. Nothing was changed.");
      walked.Add(part);
      current = value;
    }

    if (current is not LabelStyleCollection collection)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
        $"'{string.Join(".", walked)}' is a group of collections: {string.Join(", ", Civil3DCompatibility.GetCivilReferencePropertyNames(current))}. Nothing was changed.");
    return (collection, string.Join(".", walked));
  }

  /// <summary>Every label style collection under CivilDocument.Styles.LabelStyles with its property path (C98b: also to
  /// find the collection that holds a given style).</summary>
  internal static List<(string Path, LabelStyleCollection Collection)> Collections(CivilDocument civilDoc)
  {
    var found = new List<(string Path, LabelStyleCollection Collection)>();
    Walk(civilDoc.Styles.LabelStyles, "", 0, found);
    return found;
  }

  private static void Walk(object node, string prefix, int depth, List<(string Path, LabelStyleCollection Collection)> found)
  {
    if (depth > MaxDepth) return;
    foreach (var name in Civil3DCompatibility.GetCivilReferencePropertyNames(node))
    {
      object? value;
      try
      {
        value = Civil3DCompatibility.GetPropertyIgnoreCase(node, name).Value;
      }
      catch (Exception)
      {
        continue;   // a feature this drawing does not have
      }
      var path = prefix.Length == 0 ? name : $"{prefix}.{name}";
      if (value is LabelStyleCollection collection) found.Add((path, collection));
      else if (value is LabelStylesNode) Walk(value, path, depth + 1, found);
    }
  }

  private static List<ObjectId> TopLevelIds(LabelStyleCollection collection)
  {
    var ids = new List<ObjectId>();
    foreach (ObjectId id in collection)
      if (!id.IsNull && !id.IsErased) ids.Add(id);
    return ids;
  }

  /// <summary>Top-level styles and their child styles (LabelStyleCollection.GetDescendantIds), each once.</summary>
  internal static List<ObjectId> AllStyleIds(LabelStyleCollection collection)
  {
    var ids = TopLevelIds(collection);
    var descendants = Safe(() => collection.GetDescendantIds());
    if (descendants != null)
      foreach (ObjectId id in descendants)
        if (!id.IsNull && !id.IsErased && !ids.Contains(id)) ids.Add(id);
    return ids;
  }

  /// <summary>A style by name: exact first, then without case (C86b: names may differ only in case); child styles too.</summary>
  internal static ObjectId FindStyleId(LabelStyleCollection collection, string path, string name, Transaction transaction)
  {
    var loose = ObjectId.Null;
    var names = new List<string>();
    foreach (var id in AllStyleIds(collection))
    {
      if (transaction.GetObject(id, OpenMode.ForRead) is not LabelStyle style) continue;
      names.Add(style.Name);
      if (string.Equals(style.Name, name, StringComparison.Ordinal)) return id;
      if (loose.IsNull && string.Equals(style.Name, name, StringComparison.OrdinalIgnoreCase)) loose = id;
    }
    if (!loose.IsNull) return loose;
    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"{path} has no label style '{name}'. Styles: {string.Join(", ", names)}. Nothing was changed.");
  }

  // ---------------------------------------------------------------- profile view axes

  /// <summary>C90: a profile view style's axes (all four, or axis): shown or not, title, and the major and minor tick
  /// styles (label text with field codes, interval, tick size, text height and style, offsets, rotation, justification).</summary>
  public static Task<object?> GetProfileViewAxesAsync(JsonObject? parameters)
  {
    var styleName = PluginRuntime.GetRequiredString(parameters, "style");
    var axisName = AxisName(PluginRuntime.GetOptionalString(parameters, "axis"), required: false);
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var style = FindProfileViewStyle(civilDoc, transaction, styleName, OpenMode.ForRead);
      var axes = (axisName == null ? Axes : new[] { axisName }).ToDictionary(a => a, a => (object?)DescribeAxis(Axis(style, a)));
      return new Dictionary<string, object?> { ["style"] = style.Name, ["axes"] = axes };
    });
  }

  /// <summary>C90: changes one axis tick style of a profile view style: labelText (contents with field codes, e.g. the
  /// station as 0/000) and/or interval (stations along Top/Bottom, elevations along Left/Right, in drawing units).</summary>
  public static Task<object?> SetProfileViewAxisAsync(JsonObject? parameters)
  {
    var styleName = PluginRuntime.GetRequiredString(parameters, "style");
    var axisName = AxisName(PluginRuntime.GetOptionalString(parameters, "axis"), required: true)!;
    var tickText = PluginRuntime.GetOptionalString(parameters, "tick");
    var tickName = Ticks.FirstOrDefault(t => string.Equals(t, tickText?.Trim(), StringComparison.OrdinalIgnoreCase));
    var labelText = PluginRuntime.GetOptionalString(parameters, "labelText");
    var interval = PluginRuntime.GetOptionalDouble(parameters, "interval");
    // C99d: tick size and label offsets (plotted metres). Use case: the 25 mm elevation ticks on the left and
    // right axes made the profile wide; shorter ticks with the label next to them give a more compact profile.
    var tickSize = PluginRuntime.GetOptionalDouble(parameters, "tickSize");
    var offsetX = PluginRuntime.GetOptionalDouble(parameters, "offsetX");
    var offsetY = PluginRuntime.GetOptionalDouble(parameters, "offsetY");
    if (labelText == null && interval == null && tickSize == null && offsetX == null && offsetY == null)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Give labelText, interval, tickSize, offsetX and/or offsetY. Nothing was changed.");
    if (tickSize != null && !(tickSize >= 0 && double.IsFinite(tickSize.Value)))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "tickSize must be >= 0 (plotted metres). Nothing was changed.");
    if (tickName == null)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"tick must be {string.Join(" or ", Ticks)}, not '{tickText}'. Nothing was changed.");
    if (interval != null && !(interval > 0 && double.IsFinite(interval.Value)))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "interval must be > 0. Nothing was changed.");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var style = FindProfileViewStyle(civilDoc, transaction, styleName, OpenMode.ForWrite);
      var axis = Axis(style, axisName);
      var tick = tickName == "Major" ? axis.MajorTickStyle : axis.MinorTickStyle;
      var before = DescribeTick(tick);
      if (labelText != null) tick.LabelText = labelText;
      if (interval != null) tick.Interval = interval.Value;
      if (tickSize != null) tick.Size = tickSize.Value;
      if (offsetX != null) tick.OffsetX = offsetX.Value;
      if (offsetY != null) tick.OffsetY = offsetY.Value;
      return new Dictionary<string, object?>
      {
        ["style"] = style.Name, ["axis"] = axisName, ["tick"] = tickName, ["before"] = before, ["after"] = DescribeTick(tick),
      };
    });
  }

  private static string? AxisName(string? text, bool required)
  {
    if (string.IsNullOrWhiteSpace(text))
    {
      if (required) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Give axis: {string.Join(", ", Axes)}. Nothing was changed.");
      return null;
    }
    return Axes.FirstOrDefault(a => string.Equals(a, text.Trim(), StringComparison.OrdinalIgnoreCase))
      ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"axis must be {string.Join(", ", Axes)}, not '{text}'. Nothing was changed.");
  }

  private static AxisStyle Axis(ProfileViewStyle style, string axis) => axis switch
  {
    "Bottom" => style.BottomAxis,
    "Top" => style.TopAxis,
    "Left" => style.LeftAxis,
    _ => style.RightAxis,
  };

  private static Dictionary<string, object?> DescribeAxis(AxisStyle axis) => new()
  {
    ["showTickAndLabel"] = Safe<object?>(() => axis.ShowTickAndLabel),
    ["title"] = Safe(() => new Dictionary<string, object?>
    {
      ["text"] = axis.TitleStyle.Text, ["textHeight"] = axis.TitleStyle.TextHeight, ["textStyle"] = axis.TitleStyle.TextStyle,
      ["location"] = axis.TitleStyle.Location.ToString(),
    }),
    ["major"] = Safe(() => DescribeTick(axis.MajorTickStyle)),
    ["minor"] = Safe(() => DescribeTick(axis.MinorTickStyle)),
  };

  private static Dictionary<string, object?> DescribeTick(AxisTickStyle tick) => new()
  {
    ["labelText"] = Safe(() => tick.LabelText),
    ["interval"] = Safe<object?>(() => tick.Interval),
    ["tickSize"] = Safe<object?>(() => tick.Size),
    ["textHeight"] = Safe<object?>(() => tick.TextHeight),
    ["textStyle"] = Safe(() => tick.TextStyle),
    ["offsetX"] = Safe<object?>(() => tick.OffsetX),
    ["offsetY"] = Safe<object?>(() => tick.OffsetY),
    ["rotation"] = Safe<object?>(() => tick.Rotation),
    ["justification"] = Safe(() => tick.Justification.ToString()),
  };

  /// <summary>A profile view style by name, exact first, then without case.</summary>
  private static ProfileViewStyle FindProfileViewStyle(CivilDocument civilDoc, Transaction transaction, string name, OpenMode mode)
  {
    ProfileViewStyle? loose = null;
    var names = new List<string>();
    foreach (ObjectId id in civilDoc.Styles.ProfileViewStyles)
    {
      if (id.IsNull || id.IsErased || transaction.GetObject(id, OpenMode.ForRead) is not ProfileViewStyle style) continue;
      names.Add(style.Name);
      if (string.Equals(style.Name, name, StringComparison.Ordinal)) { loose = style; break; }
      if (loose == null && string.Equals(style.Name, name, StringComparison.OrdinalIgnoreCase)) loose = style;
    }
    if (loose == null)
      throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Profile view style '{name}' was not found. Styles: {string.Join(", ", names)}. Nothing was changed.");
    if (mode == OpenMode.ForWrite) loose.UpgradeOpen();
    return loose;
  }
}
