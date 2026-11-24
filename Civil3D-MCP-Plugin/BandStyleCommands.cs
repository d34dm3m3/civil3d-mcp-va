using System.Text;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices.Styles;

namespace Civil3DMcpPlugin;

/// <summary>
/// Band styles (CivilDocument.Styles.BandStyles) per band type, named as in view_band_edit's bandType: list them,
/// read one (title, label groups), copy one (StyleBase.CopyAsSibling) and change one (title text, band height,
/// title box width, and per label group the visibility of labels and ticks and the text of its label).
/// The label of a group is not a shared label style: each band style owns its own label styles (the "Compose
/// Label" buttons of Band Details), which Civil 3D names with a GUID, so a group's label is read and changed as
/// the text components of that embedded label style. Typed API only.
/// </summary>
public static class BandStyleCommands
{
  private static readonly string[] Kinds =
  {
    "ProfileData", "VerticalGeometry", "HorizontalGeometry", "SuperelevationData", "SectionalData", "PipeNetwork",
    "SectionData", "SectionSegment",
  };

  private static BandStyleCollection Collection(BandStylesRoot root, string kind) => kind switch
  {
    "ProfileData" => root.ProfileViewProfileDataBandStyles,
    "VerticalGeometry" => root.ProfileViewVerticalGeometryBandStyles,
    "HorizontalGeometry" => root.ProfileViewHorizontalGeometryBandStyles,
    "SuperelevationData" => root.ProfileViewSuperElevationBandStyles,
    "SectionalData" => root.ProfileViewSectionalDataBandStyles,
    "PipeNetwork" => root.ProfileViewPipeNetworkBandStyles,
    "SectionData" => root.SectionViewSectionDataBandStyles,
    "SectionSegment" => root.SectionViewSegmentsBandStyles,
    _ => throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"bandType '{kind}' is unknown."),
  };

  private static string Kind(string? text)
  {
    var kind = Kinds.FirstOrDefault(k => string.Equals(k, text, StringComparison.OrdinalIgnoreCase));
    return kind ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"bandType '{text}' is unknown. Use {string.Join(", ", Kinds)}.");
  }

  private static T? Safe<T>(Func<T> read)
  {
    try
    {
      return read();
    }
    catch (Exception)
    {
      return default;
    }
  }

  public static Task<object?> ListBandStylesAsync(JsonObject? parameters)
  {
    var only = PluginRuntime.GetOptionalString(parameters, "bandType");
    var kinds = string.IsNullOrWhiteSpace(only) ? Kinds : new[] { Kind(only) };
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var root = ((CivilDocument)civilDoc).Styles.BandStyles;
      var result = new Dictionary<string, object?>();
      foreach (var kind in kinds)
      {
        var styles = new List<Dictionary<string, object?>>();
        foreach (ObjectId id in Collection(root, kind))
        {
          if (transaction.GetObject(id, OpenMode.ForRead) is not BandStyle style) continue;
          styles.Add(new Dictionary<string, object?>
          {
            ["name"] = style.Name,
            ["description"] = Safe(() => style.Description),
            ["bandHeight"] = Safe(() => Math.Round(style.BandHeight, 4)),
          });
        }

        result[kind] = styles;
      }

      return new Dictionary<string, object?> { ["bandStyles"] = result, ["view"] = "SectionData and SectionSegment are section view bands; the rest profile view bands." };
    });
  }

  /// <summary>One label group of a band style: its embedded label style and the display components (Display tab)
  /// of its labels and ticks. Groups that share a display component (the two tangent groups of a vertical
  /// geometry band) change together.</summary>
  private sealed record Group(string Location, Func<ObjectId> LabelStyle, Func<DisplayStyle>? Labels, Func<DisplayStyle>? Ticks);

  /// <summary>C56: the tick style (Band Details) of a ProfileData group: full height ticks or small ticks at the
  /// top/middle/bottom of the band. Null for other band types and groups without ticks.</summary>
  private static BandTickStyle? TickStyle(BandStyle style, string location) => style is not ProfileDataBandStyle s ? null : location switch
  {
    "majorStations" => s.MajorIncrementTickStyle,
    "minorStations" => s.MinorIncrementTickStyle,
    "horizontalGeometryPoints" => s.HGPTickStyle,
    "verticalGeometryPoints" => s.VGPTickStyle,
    "stationEquations" => s.StationEquationTickStyle,
    _ => null,
  };

  private static Dictionary<string, object?>? DescribeTicks(BandTickStyle? t) => t == null ? null : new Dictionary<string, object?>
  {
    ["fullHeight"] = Safe<object?>(() => t.IncrementTicksToFullHeight),
    ["atTop"] = Safe<object?>(() => t.IncrementSmallTicksAtTop),
    ["atMiddle"] = Safe<object?>(() => t.IncrementSmallTicksAtMiddle),
    ["atBottom"] = Safe<object?>(() => t.IncrementSmallTicksAtBottom),
    ["topSize"] = Safe<object?>(() => t.SmallTicksAtTopSize),
    ["middleSize"] = Safe<object?>(() => t.SmallTicksAtMiddleSize),
    ["bottomSize"] = Safe<object?>(() => t.SmallTicksAtBottomSize),
  };

  private static List<Group> Groups(BandStyle style) => style switch
  {
    ProfileDataBandStyle s => new List<Group>
    {
      new("majorStations", () => s.MajorIncrementLabelStyleId, () => s.GetDisplayStylePlan(ProfileDataDisplayStyleType.MajorStationLabel), () => s.GetDisplayStylePlan(ProfileDataDisplayStyleType.MajorTicks)),
      new("minorStations", () => s.MinorIncrementLabelStyleId, () => s.GetDisplayStylePlan(ProfileDataDisplayStyleType.MinorStationLabel), () => s.GetDisplayStylePlan(ProfileDataDisplayStyleType.MinorTicks)),
      new("horizontalGeometryPoints", () => s.HGPLabelStyleId, () => s.GetDisplayStylePlan(ProfileDataDisplayStyleType.LabelsAtHGP), () => s.GetDisplayStylePlan(ProfileDataDisplayStyleType.TicksAtHGP)),
      new("verticalGeometryPoints", () => s.VGPLabelStyleId, () => s.GetDisplayStylePlan(ProfileDataDisplayStyleType.LabelsAtVGP), () => s.GetDisplayStylePlan(ProfileDataDisplayStyleType.TicksAtVGP)),
      new("stationEquations", () => s.StationEquationLabelStyleId, () => s.GetDisplayStylePlan(ProfileDataDisplayStyleType.LabelsAtStationEquations), () => s.GetDisplayStylePlan(ProfileDataDisplayStyleType.TicksAtStationEquations)),
      new("incrementalDistance", () => s.IncrementalDistanceLabelStyleId, () => s.GetDisplayStylePlan(ProfileDataDisplayStyleType.LabelsAtIncrementalDistance), null),
    },
    VerticalGeometryBandStyle s => new List<Group>
    {
      new("uphillTangent", () => s.UphillTangentLabelStyleId, () => s.GetDisplayStylePlan(VerticalGeometryDisplayStyleType.TangentLabels), () => s.GetDisplayStylePlan(VerticalGeometryDisplayStyleType.Ticks)),
      new("downhillTangent", () => s.DownhillTangentLabelStyleId, () => s.GetDisplayStylePlan(VerticalGeometryDisplayStyleType.TangentLabels), () => s.GetDisplayStylePlan(VerticalGeometryDisplayStyleType.Ticks)),
      new("crestCurve", () => s.CrestCurveLabelStyleId, () => s.GetDisplayStylePlan(VerticalGeometryDisplayStyleType.CurveLabels), () => s.GetDisplayStylePlan(VerticalGeometryDisplayStyleType.Ticks)),
      new("sagCurve", () => s.SagCurveLabelStyleId, () => s.GetDisplayStylePlan(VerticalGeometryDisplayStyleType.CurveLabels), () => s.GetDisplayStylePlan(VerticalGeometryDisplayStyleType.Ticks)),
    },
    HorizontalGeometryBandStyle s => new List<Group>
    {
      new("tangent", () => s.TangentLabelStyleId, () => s.GetDisplayStylePlan(HorizontalGeometryDisplayStyleType.TangentLabels), () => s.GetDisplayStylePlan(HorizontalGeometryDisplayStyleType.Ticks)),
      new("curve", () => s.CurveLabelStyleId, () => s.GetDisplayStylePlan(HorizontalGeometryDisplayStyleType.CurveLabels), () => s.GetDisplayStylePlan(HorizontalGeometryDisplayStyleType.Ticks)),
      new("spiral", () => s.SpiralLabelStyleId, () => s.GetDisplayStylePlan(HorizontalGeometryDisplayStyleType.SpiralLabels), () => s.GetDisplayStylePlan(HorizontalGeometryDisplayStyleType.Ticks)),
      new("pointOfIntersection", () => s.PointOfIntersectionLabelStyleId, () => s.GetDisplayStylePlan(HorizontalGeometryDisplayStyleType.PILabels), () => s.GetDisplayStylePlan(HorizontalGeometryDisplayStyleType.TicksAtPI)),
    },
    PipeNetworkBandStyle s => new List<Group>
    {
      new("pipe", () => s.PipeLabelStyleId, () => s.GetDisplayStylePlan(PipeNetworkDisplayStyleType.LabelsForPipe), () => s.GetDisplayStylePlan(PipeNetworkDisplayStyleType.TicksAtPipeEnds)),
      new("structure", () => s.StructureLabelStyleId, () => s.GetDisplayStylePlan(PipeNetworkDisplayStyleType.LabelsAtStructureLocation), () => s.GetDisplayStylePlan(PipeNetworkDisplayStyleType.TicksAtStructureLocation)),
    },
    SuperelevationDataBandStyle s => new List<Group>
    {
      new("normalCrown", () => s.NormalCrownLabelStyleId, () => s.GetDisplayStylePlan(SuperelevationDataDisplayStyleType.LabelsAtNormalCrown), () => s.GetDisplayStylePlan(SuperelevationDataDisplayStyleType.TicksAtNormalCrown)),
      new("levelCrown", () => s.LevelCrownLabelStyleId, () => s.GetDisplayStylePlan(SuperelevationDataDisplayStyleType.LabelsAtLevelCrown), () => s.GetDisplayStylePlan(SuperelevationDataDisplayStyleType.TicksAtLevelCrown)),
      new("reverseCrown", () => s.ReverseCrownLabelStyleId, () => s.GetDisplayStylePlan(SuperelevationDataDisplayStyleType.LabelsAtReverseCrown), () => s.GetDisplayStylePlan(SuperelevationDataDisplayStyleType.TicksAtReverseCrown)),
      new("fullSuper", () => s.FullSuperLabelStyleId, () => s.GetDisplayStylePlan(SuperelevationDataDisplayStyleType.LabelsAtFullSuper), () => s.GetDisplayStylePlan(SuperelevationDataDisplayStyleType.TicksAtFullSuper)),
      new("shoulderCriticalPoints", () => s.ShoulderCriticalPointsLabelStyleId, () => s.GetDisplayStylePlan(SuperelevationDataDisplayStyleType.LabelsAtShoulderBreakOver), () => s.GetDisplayStylePlan(SuperelevationDataDisplayStyleType.TicksAtShoulderBreakOver)),
      new("slopeTransitionRegion", () => s.SlopeTransitionRegionLabelStyleId, () => s.GetDisplayStylePlan(SuperelevationDataDisplayStyleType.LabelsAlongTransitionSegment), null),
    },
    SectionalDataBandStyle s => new List<Group>
    {
      new("sampleLineStation", () => s.SampleLineStationLabelStyleId, () => s.GetDisplayStylePlan(SectionalDataDisplayStyleType.SampleLineStationLabels), () => s.GetDisplayStylePlan(SectionalDataDisplayStyleType.TicksAtSampleLineStation)),
      new("incrementalSectionData", () => s.IncrementalSectionDataLabelStyleId, () => s.GetDisplayStylePlan(SectionalDataDisplayStyleType.IncrementalStationRegionLabels), null),
    },
    _ => new List<Group>(),
  };

  private static (BandStyle Style, List<string> Names) FindStyle(CivilDocument civilDoc, Transaction transaction, string kind, string name, OpenMode mode)
  {
    BandStyle? style = null;
    var names = new List<string>();
    foreach (ObjectId id in Collection(civilDoc.Styles.BandStyles, kind))
    {
      if (transaction.GetObject(id, OpenMode.ForRead) is not BandStyle candidate) continue;
      names.Add(candidate.Name);
      if (style == null && string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase)) style = candidate;
    }

    if (style == null)
      throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"{kind} band style '{name}' was not found. Styles: {string.Join(", ", names)}.");
    if (mode == OpenMode.ForWrite) style.UpgradeOpen();
    return (style, names);
  }

  private static List<Dictionary<string, object?>> LabelTexts(ObjectId labelStyleId, Transaction transaction)
  {
    var texts = new List<Dictionary<string, object?>>();
    if (labelStyleId.IsNull || transaction.GetObject(labelStyleId, OpenMode.ForRead) is not LabelStyle labelStyle) return texts;
    foreach (ObjectId id in labelStyle.GetComponents(LabelStyleComponentType.Text))
    {
      if (transaction.GetObject(id, OpenMode.ForRead) is not LabelStyleTextComponent text) continue;
      texts.Add(new Dictionary<string, object?>
      {
        ["component"] = Safe(() => text.General.Name),
        ["contents"] = Safe(() => text.Text.Contents.Value),
        ["visible"] = Safe<object?>(() => text.General.Visible.Value),
        // C54: placement (Band Details > Compose Label): anchor point on the band, text attachment, angle, offsets
        ["anchorComponent"] = Safe(() => text.General.AnchorComponent.Value),
        ["anchorPoint"] = Safe(() => text.General.AnchorLocation.Value.ToString()),
        ["attachment"] = Safe(() => text.Text.Attachment.Value.ToString()),
        ["angleDegrees"] = Safe<object?>(() => Math.Round(text.Text.Angle.Value * 180.0 / Math.PI, 3)),
        ["xOffset"] = Safe<object?>(() => text.Text.XOffset.Value),
        ["yOffset"] = Safe<object?>(() => text.Text.YOffset.Value),
        ["height"] = Safe<object?>(() => text.Text.Height.Value),
      });
    }

    return texts;
  }

  private static Dictionary<string, object?> Describe(BandStyle style, string kind, Transaction transaction)
  {
    string? StyleName(ObjectId id) => id.IsNull ? null : (transaction.GetObject(id, OpenMode.ForRead) as StyleBase)?.Name;
    var groups = Groups(style).Select(g => new Dictionary<string, object?>
    {
      ["location"] = g.Location,
      ["labelsVisible"] = g.Labels == null ? null : Safe(() => (bool?)g.Labels().Visible),
      ["ticksVisible"] = g.Ticks == null ? null : Safe(() => (bool?)g.Ticks().Visible),
      // The group's own (embedded) label style: Civil 3D names it with a GUID; its text components show what is labelled.
      ["embeddedLabelStyle"] = Safe(() => StyleName(g.LabelStyle())),
      ["labelTexts"] = Safe(() => LabelTexts(g.LabelStyle(), transaction)),
      ["tickStyle"] = Safe(() => DescribeTicks(TickStyle(style, g.Location))),
    }).ToList();

    return new Dictionary<string, object?>
    {
      ["name"] = style.Name,
      ["bandType"] = kind,
      ["description"] = Safe(() => style.Description),
      ["bandHeight"] = Safe(() => style.BandHeight),
      ["titleText"] = Safe(() => style.Text),
      ["titleTextPlain"] = Safe(() => SplitTitle(style.Text).Text),
      ["titleTextHeight"] = Safe(() => style.TextHeight),
      ["titleTextStyle"] = Safe(() => style.TextStyle),
      ["titleLocation"] = Safe(() => style.TextLocation.ToString()),
      ["titleBoxPosition"] = Safe(() => style.TextBoxPosition.ToString()),
      ["titleBoxWidth"] = Safe(() => style.TextBoxWidth),
      ["titleLabelStyle"] = Safe(() => StyleName(style.TitleTextLabelStyleId)),
      // Civil 3D 2026 draws the band title from this embedded label style, not from style.Text.
      ["titleLabelTexts"] = Safe(() => LabelTexts(style.TitleTextLabelStyleId, transaction)),
      ["labelOnlyTangents"] = style is VerticalGeometryBandStyle v ? Safe(() => (bool?)v.LabelOnlyTangents) : null,
      ["groups"] = groups,
    };
  }

  public static Task<object?> GetBandStyleAsync(JsonObject? parameters)
  {
    var kind = Kind(PluginRuntime.GetRequiredString(parameters, "bandType"));
    var name = PluginRuntime.GetRequiredString(parameters, "style");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
      Describe(FindStyle((CivilDocument)civilDoc, transaction, kind, name, OpenMode.ForRead).Style, kind, transaction));
  }

  /// <summary>Copies a band style under a new name (StyleBase.CopyAsSibling). The response tells whether the
  /// copy got its own embedded label styles (labelStylesSharedWithSource lists the groups that did not).</summary>
  public static Task<object?> CopyBandStyleAsync(JsonObject? parameters)
  {
    var kind = Kind(PluginRuntime.GetRequiredString(parameters, "bandType"));
    var name = PluginRuntime.GetRequiredString(parameters, "style");
    var newName = PluginRuntime.GetRequiredString(parameters, "newName").Trim();
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var (source, names) = FindStyle((CivilDocument)civilDoc, transaction, kind, name, OpenMode.ForWrite);
      if (names.Contains(newName, StringComparer.OrdinalIgnoreCase))
        throw new JsonRpcDispatchException("CIVIL3D.CONFLICT", $"A {kind} band style named '{newName}' already exists. Nothing was copied.");
      var copyId = source.CopyAsSibling(newName);
      var copy = (BandStyle)transaction.GetObject(copyId, OpenMode.ForRead);
      var sourceLabels = Groups(source).ToDictionary(g => g.Location, g => Safe(() => g.LabelStyle()));
      var shared = Groups(copy).Where(g => !Safe(() => g.LabelStyle()).IsNull && Safe(() => g.LabelStyle()) == sourceLabels.GetValueOrDefault(g.Location))
        .Select(g => g.Location).ToList();
      var result = Describe(copy, kind, transaction);
      result["copiedFrom"] = source.Name;
      result["labelStylesSharedWithSource"] = shared;
      return result;
    });
  }

  private sealed record GroupUpdate(string Location, bool? LabelsVisible, bool? TicksVisible, string? LabelText, string? LabelComponent, bool? LabelVisible,
    Autodesk.Civil.AnchorPointType? LabelAnchor = null, Autodesk.Civil.LabelTextAttachmentType? LabelAttachment = null, double? LabelAngle = null,
    double? LabelXOffset = null, double? LabelYOffset = null, double? LabelHeight = null,
    bool? TickFullHeight = null, bool? TickAtTop = null, bool? TickAtMiddle = null, bool? TickAtBottom = null)
  {
    public bool ChangesTicks => TickFullHeight != null || TickAtTop != null || TickAtMiddle != null || TickAtBottom != null;
    public bool ChangesText => LabelText != null || LabelVisible != null || LabelAnchor != null || LabelAttachment != null
      || LabelAngle != null || LabelXOffset != null || LabelYOffset != null || LabelHeight != null;
  }

  private static T? ParseEnum<T>(JsonNode? node, string field) where T : struct, Enum
  {
    if (node == null) return null;
    var text = node.GetValue<string>();
    if (Enum.TryParse<T>(text, true, out var value)) return value;
    throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{field} '{text}' is unknown. Use {string.Join(", ", Enum.GetNames<T>())}. Nothing was changed.");
  }

  /// <summary>Changes a band style: titleText (plain text, "\P" or a newline for a line break; the MText format
  /// codes in front of the current title are kept), bandHeight, titleBoxWidth, and per group labelsVisible,
  /// ticksVisible, labelText (contents of a text component of the group's embedded label style; labelComponent
  /// names it when there are several) and labelVisible (that text component shown or hidden). Everything is checked before anything changes; the style is read back.</summary>
  public static Task<object?> SetBandStyleAsync(JsonObject? parameters)
  {
    var kind = Kind(PluginRuntime.GetRequiredString(parameters, "bandType"));
    var name = PluginRuntime.GetRequiredString(parameters, "style");
    var titleText = PluginRuntime.GetOptionalString(parameters, "titleText");
    var bandHeight = PluginRuntime.GetOptionalDouble(parameters, "bandHeight");
    var titleBoxWidth = PluginRuntime.GetOptionalDouble(parameters, "titleBoxWidth");
    // C182: the title label's text offset (drawing units, as the label style stores them). Use case: the
    // rotated group titles (DAGV, SPILLV) sat centred on the line between two band rows and ran into the row above.
    var titleLabelXOffset = PluginRuntime.GetOptionalDouble(parameters, "titleLabelXOffset");
    var titleLabelYOffset = PluginRuntime.GetOptionalDouble(parameters, "titleLabelYOffset");
    var updates = new List<GroupUpdate>();
    if (PluginRuntime.GetParameter(parameters, "groups") is JsonArray groupsNode)
    {
      foreach (var node in groupsNode)
      {
        if (node is not JsonObject g || string.IsNullOrWhiteSpace(g["location"]?.GetValue<string>()))
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Each group needs a location. Nothing was changed.");
        updates.Add(new GroupUpdate(g["location"]!.GetValue<string>(), g["labelsVisible"]?.GetValue<bool>(), g["ticksVisible"]?.GetValue<bool>(),
          g["labelText"]?.GetValue<string>(), g["labelComponent"]?.GetValue<string>(), g["labelVisible"]?.GetValue<bool>(),
          ParseEnum<Autodesk.Civil.AnchorPointType>(g["labelAnchor"], "labelAnchor"),
          ParseEnum<Autodesk.Civil.LabelTextAttachmentType>(g["labelAttachment"], "labelAttachment"),
          g["labelAngleDegrees"]?.GetValue<double>(), g["labelXOffset"]?.GetValue<double>(), g["labelYOffset"]?.GetValue<double>(),
          g["labelHeight"]?.GetValue<double>(), g["tickFullHeight"]?.GetValue<bool>(), g["tickAtTop"]?.GetValue<bool>(),
          g["tickAtMiddle"]?.GetValue<bool>(), g["tickAtBottom"]?.GetValue<bool>()));
      }
    }

    if (bandHeight is <= 0 || titleBoxWidth is < 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "bandHeight must be > 0 and titleBoxWidth >= 0. Nothing was changed.");
    if (titleText == null && bandHeight == null && titleBoxWidth == null && updates.Count == 0 && titleLabelXOffset == null && titleLabelYOffset == null)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Give titleText, bandHeight, titleBoxWidth, titleLabelXOffset, titleLabelYOffset and/or groups. Nothing was changed.");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var cdoc = (CivilDocument)civilDoc;
      var (style, _) = FindStyle(cdoc, transaction, kind, name, OpenMode.ForWrite);
      var groups = Groups(style);

      // Check everything first.
      var plan = new List<(GroupUpdate Update, Group Group, LabelStyleTextComponent? Text)>();
      foreach (var update in updates)
      {
        var group = groups.FirstOrDefault(g => string.Equals(g.Location, update.Location, StringComparison.OrdinalIgnoreCase))
          ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
            $"{kind} band styles have no group '{update.Location}'. Groups: {string.Join(", ", groups.Select(g => g.Location))}. Nothing was changed.");
        if (update.LabelsVisible != null && group.Labels == null)
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Group '{group.Location}' has no labels to show or hide. Nothing was changed.");
        if (update.TicksVisible != null && group.Ticks == null)
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Group '{group.Location}' has no ticks to show or hide. Nothing was changed.");
        if (update.ChangesTicks && TickStyle(style, group.Location) == null)
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Group '{group.Location}' of a {kind} band style has no tick style to change (ProfileData groups only). Nothing was changed.");

        LabelStyleTextComponent? text = null;
        if (update.ChangesText)
        {
          var labelStyleId = group.LabelStyle();
          if (labelStyleId.IsNull || transaction.GetObject(labelStyleId, OpenMode.ForRead) is not LabelStyle labelStyle)
            throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Group '{group.Location}' has no label style. Nothing was changed.");
          var sharedWith = OtherStylesUsing(cdoc, transaction, kind, style.ObjectId, labelStyleId);
          if (sharedWith.Count > 0)
            throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
              $"The label of '{group.Location}' is shared with band style(s) {string.Join(", ", sharedWith)}; changing it would change them too. Nothing was changed.");
          var texts = labelStyle.GetComponents(LabelStyleComponentType.Text).Cast<ObjectId>()
            .Select(id => (LabelStyleTextComponent)transaction.GetObject(id, OpenMode.ForRead)).ToList();
          text = update.LabelComponent != null
            ? texts.FirstOrDefault(t => string.Equals(Safe(() => t.General.Name), update.LabelComponent, StringComparison.OrdinalIgnoreCase))
            : texts.Count == 1 ? texts[0] : null;
          if (text == null)
            throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
              $"Give labelComponent for '{group.Location}': its text components are {string.Join(", ", texts.Select(t => Safe(() => t.General.Name)))}. Nothing was changed.");
        }

        plan.Add((update, group, text));
      }

      // The drawn title comes from the title label style: change its text component(s) too. One component is
      // changed as it is; with several, those whose text equals the current title.
      var titleTexts = new List<LabelStyleTextComponent>();
      if ((titleText != null || titleLabelXOffset != null || titleLabelYOffset != null) && !style.TitleTextLabelStyleId.IsNull
          && transaction.GetObject(style.TitleTextLabelStyleId, OpenMode.ForRead) is LabelStyle titleLabelStyle)
      {
        var sharedTitle = OtherStylesUsingTitle(cdoc, transaction, kind, style.ObjectId, style.TitleTextLabelStyleId);
        if (sharedTitle.Count > 0)
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
            $"The title label of '{name}' is shared with band style(s) {string.Join(", ", sharedTitle)}; changing it would change them too. Nothing was changed.");
        var components = titleLabelStyle.GetComponents(LabelStyleComponentType.Text).Cast<ObjectId>()
          .Select(id => (LabelStyleTextComponent)transaction.GetObject(id, OpenMode.ForRead)).ToList();
        var current = SplitTitle(style.Text).Text;
        titleTexts = components.Count == 1 || titleText == null
          ? components
          : components.Where(t => string.Equals(Safe(() => SplitTitle(t.Text.Contents.Value).Text), current, StringComparison.OrdinalIgnoreCase)).ToList();
      }

      var before = Describe(style, kind, transaction);
      if (titleText != null) style.Text = ReplaceTitle(style.Text, titleText);
      foreach (var text in titleTexts)
      {
        text.UpgradeOpen();
        if (titleText != null) text.Text.Contents.Value = ReplaceTitle(text.Text.Contents.Value, titleText);
        if (titleLabelXOffset != null) text.Text.XOffset.Value = titleLabelXOffset.Value;
        if (titleLabelYOffset != null) text.Text.YOffset.Value = titleLabelYOffset.Value;
      }
      if (bandHeight != null) style.BandHeight = bandHeight.Value;
      if (titleBoxWidth != null) style.TextBoxWidth = titleBoxWidth.Value;
      foreach (var (update, group, text) in plan)
      {
        if (update.LabelsVisible != null) group.Labels!().Visible = update.LabelsVisible.Value;
        if (update.TicksVisible != null) group.Ticks!().Visible = update.TicksVisible.Value;
        if (update.ChangesTicks)
        {
          var tick = TickStyle(style, group.Location)!;
          if (update.TickFullHeight != null) tick.IncrementTicksToFullHeight = update.TickFullHeight.Value;
          if (update.TickAtTop != null) tick.IncrementSmallTicksAtTop = update.TickAtTop.Value;
          if (update.TickAtMiddle != null) tick.IncrementSmallTicksAtMiddle = update.TickAtMiddle.Value;
          if (update.TickAtBottom != null) tick.IncrementSmallTicksAtBottom = update.TickAtBottom.Value;
        }
        if (text != null)
        {
          text.UpgradeOpen();
          if (update.LabelText != null) text.Text.Contents.Value = update.LabelText;
          if (update.LabelVisible != null) text.General.Visible.Value = update.LabelVisible.Value;
          if (update.LabelAnchor != null) text.General.AnchorLocation.Value = update.LabelAnchor.Value;
          if (update.LabelAttachment != null) text.Text.Attachment.Value = update.LabelAttachment.Value;
          if (update.LabelAngle != null) text.Text.Angle.Value = update.LabelAngle.Value * Math.PI / 180.0;
          if (update.LabelXOffset != null) text.Text.XOffset.Value = update.LabelXOffset.Value;
          if (update.LabelYOffset != null) text.Text.YOffset.Value = update.LabelYOffset.Value;
          if (update.LabelHeight != null) text.Text.Height.Value = update.LabelHeight.Value;
        }
      }

      var result = Describe(style, kind, transaction);
      result["before"] = before;
      if (titleText != null || titleLabelXOffset != null || titleLabelYOffset != null) result["titleLabelComponentsChanged"] = titleTexts.Count;
      return result;
    });
  }

  /// <summary>
  /// C90: find/replace in the label texts of band styles: the text components of each group's embedded label style, in
  /// one band style (style) or in every band style of the type, optionally only one group (location) and one component.
  /// Need: stations as 0/000 in every band (C90's station field); set_band_style changes one group's whole label text
  /// per call and has no find/replace, so the field code could not be swapped over 20 band styles while keeping the rest
  /// of each label (format codes, other fields). An embedded label style shared by several band styles is changed once;
  /// with one style given, a change that would also reach other band styles is refused, as set_band_style does.
  /// dryRun lists the changes without making them.
  /// </summary>
  public static Task<object?> SetBandStyleTextsAsync(JsonObject? parameters)
  {
    var kind = Kind(PluginRuntime.GetRequiredString(parameters, "bandType"));
    var name = PluginRuntime.GetOptionalString(parameters, "style")?.Trim();
    var location = PluginRuntime.GetOptionalString(parameters, "group")?.Trim();
    var componentName = PluginRuntime.GetOptionalString(parameters, "component")?.Trim();
    var dryRun = PluginRuntime.GetOptionalBool(parameters, "dryRun") ?? false;
    var rewrite = LabelStyleTextCommands.TextRewrite.Parse(parameters, allowContents: false);
    if (name is "") name = null;
    if (location is "") location = null;
    if (componentName is "") componentName = null;

    object? Body(Autodesk.AutoCAD.ApplicationServices.Document doc, CivilDocument cdoc, Database database, Transaction transaction)
    {
      var styles = new List<BandStyle>();
      if (name != null) styles.Add(FindStyle(cdoc, transaction, kind, name, OpenMode.ForRead).Style);
      else
        foreach (ObjectId id in Collection(cdoc.Styles.BandStyles, kind))
          if (transaction.GetObject(id, OpenMode.ForRead) is BandStyle style) styles.Add(style);
      if (location != null && styles.Count > 0 && !Groups(styles[0]).Any(g => string.Equals(g.Location, location, StringComparison.OrdinalIgnoreCase)))
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
          $"{kind} band styles have no group '{location}'. Groups: {string.Join(", ", Groups(styles[0]).Select(g => g.Location))}. Nothing was changed.");

      // Each embedded label style once, with the band styles and groups that use it.
      var users = new Dictionary<ObjectId, List<string>>();
      var order = new List<(ObjectId LabelStyle, string Group)>();
      foreach (var style in styles)
      {
        foreach (var group in Groups(style))
        {
          if (location != null && !string.Equals(group.Location, location, StringComparison.OrdinalIgnoreCase)) continue;
          var labelStyleId = Safe(() => group.LabelStyle());
          if (labelStyleId.IsNull) continue;
          if (!users.TryGetValue(labelStyleId, out var list))
          {
            users[labelStyleId] = list = new List<string>();
            order.Add((labelStyleId, group.Location));
          }
          list.Add($"{style.Name} ({group.Location})");
        }
      }

      var changes = new List<(ObjectId LabelStyle, string Group, LabelStyleTextCommands.TextPart Part, string After)>();
      var unchanged = 0;
      foreach (var (labelStyleId, group) in order)
      {
        if (transaction.GetObject(labelStyleId, OpenMode.ForRead) is not LabelStyle labelStyle) continue;
        foreach (var part in LabelStyleTextCommands.TextParts(labelStyle, transaction))
        {
          if (part.Type != "Text" || part.Contents == null) continue;
          if (componentName != null && !string.Equals(part.Name, componentName, StringComparison.OrdinalIgnoreCase)) continue;
          var after = rewrite.Apply(part.Contents);
          if (after == part.Contents) unchanged++;
          else changes.Add((labelStyleId, group, part, after));
        }
      }

      if (name != null)
      {
        var style = styles[0];
        foreach (var labelStyleId in changes.Select(c => c.LabelStyle).Distinct())
        {
          var sharedWith = OtherStylesUsing(cdoc, transaction, kind, style.ObjectId, labelStyleId);
          if (sharedWith.Count > 0)
            throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
              $"A label of '{style.Name}' is shared with band style(s) {string.Join(", ", sharedWith)}; changing it would change them too (leave out style to change every {kind} band style). Nothing was changed.");
        }
      }

      var changed = new List<Dictionary<string, object?>>();
      foreach (var (labelStyleId, group, part, after) in changes)
      {
        var readBack = dryRun ? after : LabelStyleTextCommands.WriteContents(transaction, part.Id, after);
        changed.Add(new Dictionary<string, object?>
        {
          ["usedBy"] = users[labelStyleId], ["group"] = group, ["component"] = part.Name, ["before"] = part.Contents, ["after"] = readBack,
        });
      }

      return new Dictionary<string, object?>
      {
        ["bandType"] = kind,
        ["dryRun"] = dryRun,
        ["bandStyles"] = styles.Count,
        ["labelStyles"] = order.Count,
        ["changed"] = changed,
        ["unchanged"] = unchanged,
        ["note"] = dryRun ? "Dry run: nothing was changed." : changed.Count == 0 ? "Nothing matched; nothing was changed." : null,
      };
    }

    return dryRun
      ? CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) => Body(doc, (CivilDocument)civilDoc, database, transaction))
      : CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) => Body(doc, (CivilDocument)civilDoc, database, transaction));
  }

  /// <summary>Other band styles of the same type whose title uses the given (embedded) label style.</summary>
  private static List<string> OtherStylesUsingTitle(CivilDocument civilDoc, Transaction transaction, string kind, ObjectId styleId, ObjectId labelStyleId)
  {
    var names = new List<string>();
    foreach (ObjectId id in Collection(civilDoc.Styles.BandStyles, kind))
    {
      if (id == styleId || transaction.GetObject(id, OpenMode.ForRead) is not BandStyle other) continue;
      if (Safe(() => other.TitleTextLabelStyleId) == labelStyleId) names.Add(other.Name);
    }

    return names;
  }

  /// <summary>Other band styles of the same type whose groups use the given (embedded) label style.</summary>
  private static List<string> OtherStylesUsing(CivilDocument civilDoc, Transaction transaction, string kind, ObjectId styleId, ObjectId labelStyleId)
  {
    var names = new List<string>();
    foreach (ObjectId id in Collection(civilDoc.Styles.BandStyles, kind))
    {
      if (id == styleId || transaction.GetObject(id, OpenMode.ForRead) is not BandStyle other) continue;
      if (Groups(other).Any(g => Safe(() => g.LabelStyle()) == labelStyleId)) names.Add(other.Name);
    }

    return names;
  }

  /// <summary>Splits a band title into its leading MText format codes ("{" and \F...; \C...; \H...; etc.), the
  /// text, and the closing braces.</summary>
  internal static (string Prefix, string Text, string Suffix) SplitTitle(string? title)
  {
    title ??= "";
    var i = 0;
    var braces = 0;
    while (i < title.Length)
    {
      if (title[i] == '{') { braces++; i++; continue; }
      if (title[i] == '\\' && i + 1 < title.Length && "FfCcHhWwQqTtAap".IndexOf(title[i + 1]) >= 0)
      {
        var semicolon = title.IndexOf(';', i);
        if (semicolon < 0) break;
        i = semicolon + 1;
        continue;
      }

      break;
    }

    var end = title.Length;
    var closing = 0;
    while (closing < braces && end > i && title[end - 1] == '}') { end--; closing++; }
    return (title[..i], title[i..end], new string('}', braces));
  }

  /// <summary>New title: the current format prefix, the plain text (newline or "\P" = line break; braces and other
  /// backslashes escaped) and the closing braces.</summary>
  internal static string ReplaceTitle(string? current, string plain)
  {
    var (prefix, _, suffix) = SplitTitle(current);
    var text = new StringBuilder();
    plain = plain.Replace("\r\n", "\n");
    for (var i = 0; i < plain.Length; i++)
    {
      var c = plain[i];
      if (c == '\n') text.Append("\\P");
      else if (c == '\\' && i + 1 < plain.Length && plain[i + 1] == 'P') { text.Append("\\P"); i++; }
      else if (c == '\\') text.Append("\\\\");
      else if (c == '{' || c == '}') text.Append('\\').Append(c);
      else text.Append(c);
    }

    return prefix + text + suffix;
  }
}
