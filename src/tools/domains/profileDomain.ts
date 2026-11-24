import { z } from "zod";
import { withApplicationConnection } from "../../utils/ConnectionManager.js";
import type { DomainToolDefinition } from "../domainRuntime.js";

// ─── Shared schemas ───────────────────────────────────────────────────────────

const GenericProfileResponseSchema = z.object({}).passthrough();

const ProfileSummarySchema = z.object({
  name: z.string(),
  handle: z.string(),
  type: z.enum(["surface", "layout", "superimposed", "corridor", "file"]),
  style: z.string(),
  startStation: z.number(),
  endStation: z.number(),
  minElevation: z.number(),
  maxElevation: z.number(),
});

const ProfileListResponseSchema = z.object({
  alignmentName: z.string(),
  profiles: z.array(ProfileSummarySchema),
});

const ProfileEntitySchema = z.object({
  index: z.number(),
  type: z.enum(["tangent", "circular_curve", "parabola", "asymmetric_parabola"]),
  startStation: z.number(),
  endStation: z.number(),
  startElevation: z.number(),
  endElevation: z.number(),
  grade: z.number().nullable(),
  length: z.number(),
});

const ProfileDetailResponseSchema = z.object({
  name: z.string(),
  handle: z.string(),
  type: z.string(),
  style: z.string(),
  layer: z.string(),
  startStation: z.number(),
  endStation: z.number(),
  minElevation: z.number(),
  maxElevation: z.number(),
  entityCount: z.number(),
  entities: z.array(ProfileEntitySchema),
  pviCount: z.number(),
  units: z.object({
    horizontal: z.string(),
    vertical: z.string(),
  }),
});

const ProfileElevationResponseSchema = z.object({
  station: z.number(),
  elevation: z.number(),
  grade: z.number(),
  units: z.string(),
});

const ProfileSamplePointSchema = z.object({
  station: z.number(),
  elevation: z.number(),
  grade: z.number().nullable(),
});

const ProfileSampleElevationsResponseSchema = z.object({
  alignmentName: z.string(),
  profileName: z.string(),
  startStation: z.number(),
  endStation: z.number(),
  interval: z.number(),
  samples: z.array(ProfileSamplePointSchema),
  units: z.string(),
});

const ProfileReportResponseSchema = z.object({
  profile: ProfileDetailResponseSchema,
  samples: ProfileSampleElevationsResponseSchema,
  summary: z.object({
    sampledStationCount: z.number(),
    startStation: z.number(),
    endStation: z.number(),
    interval: z.number(),
    minimumElevation: z.number(),
    maximumElevation: z.number(),
    elevationRange: z.number(),
    averageGrade: z.number().nullable(),
    totalAscendingLength: z.number(),
    totalDescendingLength: z.number(),
    tangentCount: z.number(),
    curveCount: z.number(),
    pviCount: z.number(),
  }),
});

// ─── Per-action input schemas ─────────────────────────────────────────────────

const ProfileListArgsSchema = z.object({
  action: z.literal("list"),
  alignmentName: z.string(),
});

const ProfileGetArgsSchema = z.object({
  action: z.literal("get"),
  alignmentName: z.string(),
  profileName: z.string(),
});

const ProfileGetElevationArgsSchema = z.object({
  action: z.literal("get_elevation"),
  alignmentName: z.string(),
  profileName: z.string(),
  station: z.number(),
});

const ProfileSampleElevationsArgsSchema = z.object({
  action: z.literal("sample_elevations"),
  alignmentName: z.string(),
  profileName: z.string(),
  interval: z.number(),
  startStation: z.number().optional(),
  endStation: z.number().optional(),
});

const ProfileCreateFromSurfaceArgsSchema = z.object({
  action: z.literal("create_from_surface"),
  alignmentName: z.string(),
  profileName: z.string(),
  surfaceName: z.string(),
  style: z.string().optional(),
  layer: z.string().optional(),
  labelSet: z.string().optional(),
  // C102: new profiles are not drawn in existing profile views unless true (all) or listed
  drawInProfileViews: z.union([z.boolean(), z.array(z.string()).min(1)]).optional(),
});

const ProfileCreateLayoutArgsSchema = z.object({
  action: z.literal("create_layout"),
  alignmentName: z.string(),
  profileName: z.string(),
  style: z.string().optional(),
  layer: z.string().optional(),
  labelSet: z.string().optional(),
  // C102: new profiles are not drawn in existing profile views unless true (all) or listed
  drawInProfileViews: z.union([z.boolean(), z.array(z.string()).min(1)]).optional(),
});

const ProfileDeleteArgsSchema = z.object({
  action: z.literal("delete"),
  alignmentName: z.string(),
  profileName: z.string(),
});

const ProfileReportArgsSchema = z.object({
  action: z.literal("report"),
  alignmentName: z.string(),
  profileName: z.string(),
  interval: z.number().positive().optional(),
  startStation: z.number().optional(),
  endStation: z.number().optional(),
  maximumSamples: z.number().int().positive().max(400).optional(),
});

// C63: many PVIs in one transaction (pvis), optionally replacing all existing PVIs (replaceAll)
const ProfileAddPviArgsSchema = z.object({
  action: z.literal("add_pvi"),
  alignmentName: z.string(),
  profileName: z.string(),
  station: z.number().optional(),
  elevation: z.number().optional(),
  pvis: z.array(z.object({ station: z.number(), elevation: z.number() })).min(1).optional(),
  replaceAll: z.boolean().optional(),
}).refine((a) => (a.pvis !== undefined) !== (a.station !== undefined && a.elevation !== undefined), { message: "Give station and elevation, or pvis." })
  .refine((a) => !a.replaceAll || a.pvis !== undefined, { message: "replaceAll needs pvis." });

const ProfileDeletePviArgsSchema = z.object({
  action: z.literal("delete_pvi"),
  alignmentName: z.string(),
  profileName: z.string(),
  station: z.number(),
});

const ProfileAddCurveArgsSchema = z.object({
  action: z.literal("add_curve"),
  alignmentName: z.string(),
  profileName: z.string(),
  pviStation: z.number(),
  length: z.number().positive(),
  curveType: z.enum(["symmetric_parabola", "asymmetric_parabola"]).optional().default("symmetric_parabola"),
});

const ProfileSetGradeArgsSchema = z.object({
  action: z.literal("set_grade"),
  alignmentName: z.string(),
  profileName: z.string(),
  entityIndex: z.number().int().min(0),
  grade: z.number(),
});

const ProfileCheckKValuesArgsSchema = z.object({
  action: z.literal("check_k_values"),
  alignmentName: z.string(),
  profileName: z.string(),
  designSpeed: z.number().positive(),
});

const ProfileViewCreateArgsSchema = z.object({
  action: z.literal("view_create"),
  alignmentName: z.string(),
  profileViewName: z.string(),
  insertX: z.number(),
  insertY: z.number(),
  style: z.string().optional(),
  bandSet: z.string().optional(),
});

const ProfileViewBandSetArgsSchema = z.object({
  action: z.literal("view_band_set"),
  profileViewName: z.string(),
  bandSetName: z.string(),
});

const ProfileViewProfilesGetArgsSchema = z.object({
  action: z.literal("view_profiles_get"),
  profileViewName: z.string(),
});

const BandUpdateSchema = z.object({
  location: z.enum(["bottom", "top"]),
  index: z.number().int().min(0),
  profile1: z.string().optional(),
  profile2: z.string().optional(),
  network: z.string().optional(),
  majorInterval: z.number().positive().optional(),
  minorInterval: z.number().positive().optional(),
  // C53: "Geometry Points to Label in Band" (ProfilePointType / AlignmentPointType names, or ["all"]) and label switches
  showLabels: z.boolean().optional(),
  labelAtStartStation: z.boolean().optional(),
  labelAtEndStation: z.boolean().optional(),
  gap: z.number().min(0).optional(),
  weeding: z.number().min(0).optional(),
  verticalGeometryPoints: z.array(z.string().min(1)).optional(),
  horizontalGeometryPoints: z.array(z.string().min(1)).optional(),
});

const ProfileViewBandsGetArgsSchema = z.object({
  action: z.literal("view_bands_get"),
  profileViewName: z.string(),
});

const ProfileViewBandsSetArgsSchema = z.object({
  action: z.literal("view_bands_set"),
  profileViewName: z.string(),
  bands: z.array(BandUpdateSchema).min(1).optional(),
  // C55: off = the bands keep their own major/minor intervals instead of the grid's
  matchIncrementToGridIntervals: z.boolean().optional(),
}).refine((a) => a.bands !== undefined || a.matchIncrementToGridIntervals !== undefined,
  { message: "Give bands and/or matchIncrementToGridIntervals." });

const ProfileViewLabelsClearArgsSchema = z.object({
  action: z.literal("view_profile_labels_clear"),
  profileViewName: z.string(),
  profiles: z.array(z.string().min(1)).min(1).optional(),
});

const ProfileViewBandEditArgsSchema = z.object({
  action: z.literal("view_band_edit"),
  profileViewName: z.string(),
  op: z.enum(["add", "remove", "swap"]),
  location: z.enum(["bottom", "top"]).optional(),
  bandType: z.string().optional(),
  style: z.string().optional(),
  network: z.string().optional(),
  profile1: z.string().optional(),
  profile2: z.string().optional(),
  majorInterval: z.number().positive().optional(),
  minorInterval: z.number().positive().optional(),
  position: z.number().int().min(0).optional(),
  index: z.number().int().min(0).optional(),
  index2: z.number().int().min(0).optional(),
}).refine((a) => a.op !== "add" || Boolean(a.bandType && a.style), { message: "add needs bandType and style" })
  .refine((a) => a.op === "add" || a.index !== undefined, { message: "remove/swap need index" })
  .refine((a) => a.op !== "swap" || a.index2 !== undefined, { message: "swap needs index2" });

const ProfileLabelStylesListArgsSchema = z.object({
  action: z.literal("label_styles_list"),
  labelKind: z.enum(["line", "grade_break", "curve", "major_station", "minor_station", "geometry_point", "pipe", "pipe_crossing", "pipe_section", "structure"]),
});

const ProfileLineLabelsAddArgsSchema = z.object({
  action: z.literal("line_labels_add"),
  profileViewName: z.string(),
  profileName: z.string(),
  style: z.string(),
  replace: z.boolean().optional(),
});

const ProfileLabelKindSchema = z.enum(["line", "grade_break", "curve", "major_station", "minor_station", "geometry_point", "pipe", "pipe_crossing", "pipe_section", "structure"]);

const ProfileLabelStyleGetArgsSchema = z.object({
  action: z.literal("label_style_get"),
  labelKind: ProfileLabelKindSchema,
  style: z.string().min(1),
});

const ProfileLabelStyleCopyArgsSchema = z.object({
  action: z.literal("label_style_copy"),
  labelKind: ProfileLabelKindSchema,
  style: z.string().min(1),
  newStyle: z.string().min(1),
}).refine((a) => a.newStyle !== a.style, { message: "newStyle must differ from style." });

const LabelTextAttachmentSchema = z.enum([
  "TopLeft", "TopCenter", "TopRight", "MiddleLeft", "MiddleCenter", "MiddleRight", "BottomLeft", "BottomCenter", "BottomRight",
]);

const LabelStyleTextSchema = z.object({
  component: z.string().min(1),
  contents: z.string().optional(),
  visible: z.boolean().optional(),
  like: z.string().optional(),
  attachment: LabelTextAttachmentSchema.optional(),
  xOffset: z.number().optional(),
  yOffset: z.number().optional(),
}).refine((t) => Object.keys(t).some((k) => k !== "component"), { message: "Give at least one setting besides the component name." });

const ProfileLabelStyleTextSetArgsSchema = z.object({
  action: z.literal("label_style_text_set"),
  labelKind: ProfileLabelKindSchema,
  style: z.string().min(1),
  text: LabelStyleTextSchema,
});

const ProfileViewProfilesSetArgsSchema = z.object({
  action: z.literal("view_profiles_set"),
  profileViewName: z.string(),
  draw: z.record(z.string(), z.boolean()).refine((m) => Object.keys(m).length > 0, { message: "Give at least one profile." }),
});

// Pipes and structures in a profile view: drawn or not, style override in that view only (plan untouched).
const ProfileViewPartKindSchema = z.enum(["pipe", "structure", "all"]);
const ProfileViewPartsGetArgsSchema = z.object({
  action: z.literal("view_parts_get"),
  profileViewName: z.string(),
  network: z.string().optional(),
  namePrefix: z.string().optional(),
  partKind: ProfileViewPartKindSchema.optional(),
});
const ProfileViewPartsSetArgsSchema = z.object({
  action: z.literal("view_parts_set"),
  profileViewName: z.string(),
  network: z.string().optional(),
  namePrefix: z.string().optional(),
  partKind: ProfileViewPartKindSchema.optional(),
  drawParts: z.boolean().optional(),
  overrideStyle: z.string().optional(),
}).refine((a) => a.drawParts !== undefined || a.overrideStyle !== undefined, { message: "Give drawParts and/or overrideStyle (empty string clears the override)." })
  .refine((a) => !a.overrideStyle || (a.partKind !== undefined && a.partKind !== "all"), { message: "A style override needs partKind 'pipe' or 'structure'." });
const ProfileViewStylesListArgsSchema = z.object({ action: z.literal("view_styles_list") });
const ProfileViewStyleSetArgsSchema = z.object({
  action: z.literal("view_style_set"),
  profileViewName: z.string(),
  style: z.string().min(1),
});

// Station and elevation range of a profile view: each range either user specified (both ends) or automatic.
const ProfileViewRangeSetArgsSchema = z.object({
  action: z.literal("view_range_set"),
  profileViewName: z.string().min(1),
  startStation: z.number().optional(),
  endStation: z.number().optional(),
  automaticStation: z.boolean().optional(),
  minElevation: z.number().optional(),
  maxElevation: z.number().optional(),
  automaticElevation: z.boolean().optional(),
}).refine((a) => (a.startStation === undefined) === (a.endStation === undefined), { message: "Give both startStation and endStation or neither." })
  .refine((a) => (a.minElevation === undefined) === (a.maxElevation === undefined), { message: "Give both minElevation and maxElevation or neither." })
  .refine((a) => !(a.startStation !== undefined && a.automaticStation), { message: "Give a station range or automaticStation, not both." })
  .refine((a) => !(a.minElevation !== undefined && a.automaticElevation), { message: "Give an elevation range or automaticElevation, not both." })
  .refine((a) => a.startStation === undefined || a.endStation === undefined || a.startStation < a.endStation, { message: "startStation must be less than endStation." })
  .refine((a) => a.minElevation === undefined || a.maxElevation === undefined || a.minElevation < a.maxElevation, { message: "minElevation must be less than maxElevation." })
  .refine((a) => a.startStation !== undefined || a.automaticStation || a.minElevation !== undefined || a.automaticElevation,
    { message: "Give a station range, automaticStation, an elevation range and/or automaticElevation." });

// Band styles per band type (names as view_band_edit's bandType) and one style's label groups.
const BAND_STYLE_TYPES = ["ProfileData", "VerticalGeometry", "HorizontalGeometry", "SuperelevationData", "SectionalData", "PipeNetwork", "SectionData", "SectionSegment"] as const;
const BandStyleType = z.preprocess(
  (v) => (typeof v === "string" ? BAND_STYLE_TYPES.find((t) => t.toLowerCase() === v.toLowerCase()) ?? v : v),
  z.enum(BAND_STYLE_TYPES),
);
const ProfileBandStylesListArgsSchema = z.object({ action: z.literal("band_styles_list"), bandType: BandStyleType.optional() });
const ProfileBandStyleGetArgsSchema = z.object({ action: z.literal("band_style_get"), bandType: BandStyleType, style: z.string().min(1) });
const ProfileBandStyleCopyArgsSchema = z.object({ action: z.literal("band_style_copy"), bandType: BandStyleType, style: z.string().min(1), newName: z.string().min(1) });
// A label group as band_style_get returns it (location); labelText sets a text component of the group's own
// (embedded) label style, labelComponent names the component when there are several.
const BandGroupUpdateObject = z.object({
  location: z.string().min(1),
  labelsVisible: z.boolean().optional(),
  ticksVisible: z.boolean().optional(),
  labelText: z.string().optional(),
  labelComponent: z.string().min(1).optional(),
  // C52: show or hide that text component (e.g. the elevation of a vertical geometry point)
  labelVisible: z.boolean().optional(),
  // C54: placement of that text component: anchor point on the band (AnchorPointType, e.g. BandMiddle), text
  // attachment (e.g. MiddleCenter), angle in degrees, offsets and height (paper metres as Civil 3D stores them)
  labelAnchor: z.string().min(1).optional(),
  labelAttachment: z.string().min(1).optional(),
  labelAngleDegrees: z.number().optional(),
  labelXOffset: z.number().optional(),
  labelYOffset: z.number().optional(),
  labelHeight: z.number().positive().optional(),
  // C56: tick style of a ProfileData group (Band Details): through the full band height, or small ticks
  tickFullHeight: z.boolean().optional(),
  tickAtTop: z.boolean().optional(),
  tickAtMiddle: z.boolean().optional(),
  tickAtBottom: z.boolean().optional(),
});
const GROUP_CHANGES = ["labelsVisible", "ticksVisible", "labelText", "labelVisible", "labelAnchor", "labelAttachment",
  "labelAngleDegrees", "labelXOffset", "labelYOffset", "labelHeight", "tickFullHeight", "tickAtTop", "tickAtMiddle", "tickAtBottom"] as const;
const BandGroupUpdateSchema = BandGroupUpdateObject.refine(
  (g) => GROUP_CHANGES.some((k) => (g as Record<string, unknown>)[k] !== undefined),
  { message: "Give labelsVisible, ticksVisible, labelText, labelVisible and/or a label placement for each group." },
);
const ProfileBandStyleSetArgsSchema = z.object({
  action: z.literal("band_style_set"),
  bandType: BandStyleType,
  style: z.string().min(1),
  titleText: z.string().optional(),
  bandHeight: z.number().positive().optional(),
  titleBoxWidth: z.number().min(0).optional(),
  groups: z.array(BandGroupUpdateSchema).min(1).optional(),
}).refine((a) => a.titleText !== undefined || a.bandHeight !== undefined || a.titleBoxWidth !== undefined || a.groups !== undefined,
  { message: "Give titleText, bandHeight, titleBoxWidth and/or groups." });

// C103: a layout profile from a pipe network's pipe ends at the pipes' top (crown), invert, outside bottom or
// centre, stations at the nearest point on the alignment. Ends farther than maxOffset or beyond the alignment are left out;
// minSpacing thins the vertices (ends and fixedStations kept), endSnap puts ends near the alignment's ends on them. An
// existing profile is replaced only with replace (its bands and profile views get the new one); dryRun only reports.
const DrawInProfileViewsSchema = z.union([z.boolean(), z.array(z.string()).min(1)]);
const ProfileCreateFromNetworkArgsSchema = z.object({
  action: z.literal("create_from_network"),
  networkName: z.string().min(1),
  alignmentName: z.string().min(1),
  profileName: z.string().min(1),
  reference: z.enum(["top", "invert", "bottom", "centre", "center"]).optional(),
  minSpacing: z.number().min(0).optional(),
  maxOffset: z.number().positive().optional(),
  endSnap: z.number().min(0).optional(),
  fromStation: z.number().optional(),
  toStation: z.number().optional(),
  fixedStations: z.array(z.number()).optional(),
  pipeNames: z.array(z.string().min(1)).min(1).optional(),
  replace: z.boolean().optional(),
  dryRun: z.boolean().optional(),
  style: z.string().optional(),
  layer: z.string().optional(),
  labelSet: z.string().optional(),
  drawInProfileViews: DrawInProfileViewsSchema.optional(),
}).refine((a) => a.fromStation === undefined || a.toStation === undefined || a.fromStation < a.toStation,
  { message: "fromStation must be less than toStation." });

// C104: a band set onto a profile view band by band (mode add, default; import = ImportBandSetStyle) with the band set's
// intervals, gaps and label switches; grid matching off unless asked. Sources per band (index, or style with occurrence
// for a style used twice) and as defaults: profile1 for every profile data band, network for every pipe network band.
const BandLabelOptions = {
  showLabels: z.boolean().optional(),
  labelAtStartStation: z.boolean().optional(),
  labelAtEndStation: z.boolean().optional(),
  verticalGeometryPoints: z.array(z.string().min(1)).optional(),
  horizontalGeometryPoints: z.array(z.string().min(1)).optional(),
};
const BandSetApplyBandSchema = z.object({
  index: z.number().int().min(0).optional(),
  style: z.string().min(1).optional(),
  occurrence: z.number().int().min(1).optional(),
  location: z.enum(["bottom", "top"]).optional(),
  profile1: z.string().min(1).optional(),
  profile2: z.string().min(1).optional(),
  network: z.string().min(1).optional(),
  ...BandLabelOptions,
}).refine((b) => (b.index === undefined) !== (b.style === undefined), { message: "Give each band by index or by style, not both." })
  .refine((b) => b.occurrence === undefined || b.style !== undefined, { message: "occurrence goes with style." });
const ProfileViewBandSetApplyArgsSchema = z.object({
  action: z.literal("view_band_set_apply"),
  profileViewName: z.string().min(1),
  bandSet: z.string().min(1),
  mode: z.enum(["add", "import"]).optional(),
  profile1: z.string().min(1).optional(),
  network: z.string().min(1).optional(),
  matchIncrementToGridIntervals: z.boolean().optional(),
  bands: z.array(BandSetApplyBandSchema).optional(),
  labels: z.object(BandLabelOptions).optional(),
});

// C106: erase a profile view with its labels, bands and drawn parts (alignment, profiles and networks stay); dryRun only reports.
const ProfileViewDeleteArgsSchema = z.object({
  action: z.literal("view_delete"),
  profileViewName: z.string().min(1),
  dryRun: z.boolean().optional(),
});

// ─── Canonical input shape (union of all action fields) ───────────────────────

const canonicalProfileInputShape = {
  action: z.enum([
    "list",
    "get",
    "get_elevation",
    "sample_elevations",
    "create_from_surface",
    "create_layout",
    "create_from_network",
    "delete",
    "report",
    "add_pvi",
    "delete_pvi",
    "add_curve",
    "set_grade",
    "check_k_values",
    "view_create",
    "view_delete",
    "view_band_set",
    "view_band_set_apply",
    "view_profiles_get",
    "view_profiles_set",
    "view_parts_get",
    "view_parts_set",
    "view_styles_list",
    "view_style_set",
    "view_range_set",
    "band_styles_list",
    "band_style_get",
    "band_style_copy",
    "band_style_set",
    "view_bands_get",
    "view_bands_set",
    "view_band_edit",
    "view_profile_labels_clear",
    "label_styles_list",
    "line_labels_add",
    "label_style_get",
    "label_style_copy",
    "label_style_text_set",
  ]),
  alignmentName: z.string().optional(),
  // view_bands_set items ({location, index, ...}) and view_band_set_apply items ({index | style, occurrence?, ...}) both
  // survive this shape; each action checks its own items.
  bands: z.array(BandUpdateSchema.partial().extend({
    style: z.string().optional(),
    occurrence: z.number().int().optional(),
  })).optional().describe("view_bands_set: [{location, index, profile1?, ...}]; view_band_set_apply: [{index | style, occurrence?, location? (default bottom), profile1?, profile2?, network?, showLabels?, labelAtStartStation?, labelAtEndStation?, verticalGeometryPoints?, horizontalGeometryPoints?}]."),
  matchIncrementToGridIntervals: z.boolean().optional(),
  newStyle: z.string().optional(),
  text: LabelStyleTextSchema.optional(),
  labelKind: z.enum(["line", "grade_break", "curve", "major_station", "minor_station", "geometry_point", "pipe", "pipe_crossing", "pipe_section", "structure"]).optional(),
  replace: z.boolean().optional(),
  draw: z.record(z.string(), z.boolean()).optional(),
  profileName: z.string().optional(),
  profileViewName: z.string().optional(),
  station: z.number().optional(),
  elevation: z.number().optional(),
  pvis: z.array(z.object({ station: z.number(), elevation: z.number() })).optional(),
  replaceAll: z.boolean().optional(),
  startStation: z.number().optional(),
  endStation: z.number().optional(),
  interval: z.number().optional(),
  maximumSamples: z.number().int().positive().max(400).optional(),
  surfaceName: z.string().optional(),
  style: z.string().optional(),
  layer: z.string().optional(),
  labelSet: z.string().optional(),
  pviStation: z.number().optional(),
  length: z.number().positive().optional(),
  curveType: z.enum(["symmetric_parabola", "asymmetric_parabola"]).optional(),
  entityIndex: z.number().int().min(0).optional(),
  grade: z.number().optional(),
  designSpeed: z.number().positive().optional(),
  insertX: z.number().optional(),
  insertY: z.number().optional(),
  bandSet: z.string().optional(),
  bandSetName: z.string().optional(),
  op: z.enum(["add", "remove", "swap"]).optional(),
  location: z.enum(["bottom", "top"]).optional(),
  bandType: z.string().optional(),
  network: z.string().optional(),
  profile1: z.string().optional(),
  profile2: z.string().optional(),
  majorInterval: z.number().optional(),
  minorInterval: z.number().optional(),
  position: z.number().int().min(0).optional(),
  index: z.number().int().min(0).optional(),
  index2: z.number().int().min(0).optional(),
  profiles: z.array(z.string()).optional(),
  namePrefix: z.string().optional(),
  partKind: z.enum(["pipe", "structure", "all"]).optional(),
  drawParts: z.boolean().optional(),
  overrideStyle: z.string().optional(),
  automaticStation: z.boolean().optional(),
  minElevation: z.number().optional(),
  maxElevation: z.number().optional(),
  automaticElevation: z.boolean().optional(),
  newName: z.string().optional(),
  titleText: z.string().optional().describe("Band title as plain text; \\P or a newline breaks the line; the MText format codes in front are kept."),
  bandHeight: z.number().optional(),
  titleBoxWidth: z.number().optional(),
  groups: z.array(BandGroupUpdateObject).optional(),
  drawInProfileViews: DrawInProfileViewsSchema.optional().describe("create_from_surface/create_layout/create_from_network: draw the new profile in the alignment's existing profile views: true = all, or a list of names (default none)."),
  networkName: z.string().optional().describe("create_from_network: pipe network whose pipe ends give the vertices."),
  reference: z.enum(["top", "invert", "bottom", "centre", "center"]).optional().describe("create_from_network: top (crown, centre + OD/2, default), invert (centre - ID/2), bottom (outside bottom, centre - OD/2) or centre."),
  minSpacing: z.number().optional().describe("create_from_network: thin the vertices while two lie closer than this (station distance); ends and fixedStations stay."),
  maxOffset: z.number().optional().describe("create_from_network: leave out pipe ends farther than this from the alignment."),
  endSnap: z.number().optional().describe("create_from_network: pipe ends within this distance of the alignment's start or end point get its station."),
  fromStation: z.number().optional().describe("create_from_network: leave out pipe ends before this station."),
  toStation: z.number().optional().describe("create_from_network: leave out pipe ends after this station."),
  fixedStations: z.array(z.number()).optional().describe("create_from_network: vertices at these stations (within 0.1) are never thinned out."),
  pipeNames: z.array(z.string()).optional().describe("create_from_network: only these pipes of the network."),
  dryRun: z.boolean().optional().describe("create_from_network/view_delete: report without changing the drawing."),
  mode: z.enum(["add", "import"]).optional().describe("view_band_set_apply: add (default; the band set's bands added one by one) or import (ImportBandSetStyle)."),
  labels: z.object(BandLabelOptions).optional().describe("view_band_set_apply: label defaults for every band {showLabels? (default true), labelAtStartStation?, labelAtEndStation?, verticalGeometryPoints?, horizontalGeometryPoints? (point type names or [\"all\"])}."),
};

// ─── Report helper ────────────────────────────────────────────────────────────

function estimateSampleCount(startStation: number, endStation: number, interval: number) {
  const length = Math.max(0, endStation - startStation);
  return Math.floor(length / interval) + 2;
}

// ─── Domain definition ────────────────────────────────────────────────────────

export const PROFILE_DOMAIN_DEFINITION: DomainToolDefinition = {
  domain: "profile",
  actions: {
    list: {
      action: "list",
      inputSchema: ProfileListArgsSchema,
      responseSchema: ProfileListResponseSchema,
      capabilities: ["query"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["listProfiles"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("listProfiles", {
          alignmentName: args.alignmentName,
        }),
      ),
    },
    get: {
      action: "get",
      inputSchema: ProfileGetArgsSchema,
      responseSchema: ProfileDetailResponseSchema,
      capabilities: ["query", "inspect"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["getProfile"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("getProfile", {
          alignmentName: args.alignmentName,
          profileName: args.profileName,
        }),
      ),
    },
    get_elevation: {
      action: "get_elevation",
      inputSchema: ProfileGetElevationArgsSchema,
      responseSchema: ProfileElevationResponseSchema,
      capabilities: ["query", "analyze"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["getProfileElevation"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("getProfileElevation", {
          alignmentName: args.alignmentName,
          profileName: args.profileName,
          station: args.station,
        }),
      ),
    },
    sample_elevations: {
      action: "sample_elevations",
      inputSchema: ProfileSampleElevationsArgsSchema,
      responseSchema: ProfileSampleElevationsResponseSchema,
      capabilities: ["query", "analyze"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["sampleProfileElevations"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("sampleProfileElevations", {
          alignmentName: args.alignmentName,
          profileName: args.profileName,
          startStation: args.startStation,
          endStation: args.endStation,
          interval: args.interval,
        }),
      ),
    },
    create_from_surface: {
      action: "create_from_surface",
      inputSchema: ProfileCreateFromSurfaceArgsSchema,
      responseSchema: GenericProfileResponseSchema,
      capabilities: ["create"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["createProfileFromSurface"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("createProfileFromSurface", {
          alignmentName: args.alignmentName,
          profileName: args.profileName,
          surfaceName: args.surfaceName,
          style: args.style,
          layer: args.layer,
          labelSet: args.labelSet,
          drawInProfileViews: args.drawInProfileViews,
        }),
      ),
    },
    create_layout: {
      action: "create_layout",
      inputSchema: ProfileCreateLayoutArgsSchema,
      responseSchema: GenericProfileResponseSchema,
      capabilities: ["create"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["createLayoutProfile"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("createLayoutProfile", {
          alignmentName: args.alignmentName,
          profileName: args.profileName,
          style: args.style,
          layer: args.layer,
          labelSet: args.labelSet,
          drawInProfileViews: args.drawInProfileViews,
        }),
      ),
    },
    create_from_network: {
      action: "create_from_network",
      inputSchema: ProfileCreateFromNetworkArgsSchema,
      responseSchema: GenericProfileResponseSchema,
      capabilities: ["create", "edit"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["profileFromNetwork"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("profileFromNetwork", {
          networkName: args.networkName, alignmentName: args.alignmentName, profileName: args.profileName,
          reference: args.reference, minSpacing: args.minSpacing, maxOffset: args.maxOffset, endSnap: args.endSnap,
          fromStation: args.fromStation, toStation: args.toStation, fixedStations: args.fixedStations, pipeNames: args.pipeNames,
          replace: args.replace, dryRun: args.dryRun, style: args.style, layer: args.layer, labelSet: args.labelSet,
          drawInProfileViews: args.drawInProfileViews,
        }),
      ),
    },
    delete: {
      action: "delete",
      inputSchema: ProfileDeleteArgsSchema,
      responseSchema: GenericProfileResponseSchema,
      capabilities: ["delete"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["deleteProfile"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("deleteProfile", {
          alignmentName: args.alignmentName,
          profileName: args.profileName,
        }),
      ),
    },
    report: {
      action: "report",
      inputSchema: ProfileReportArgsSchema,
      responseSchema: ProfileReportResponseSchema,
      capabilities: ["query", "analyze", "generate"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["getProfile", "sampleProfileElevations"],
      execute: async (args) => await withApplicationConnection(async (appClient) => {
        const profile = ProfileDetailResponseSchema.parse(
          await appClient.sendCommand("getProfile", {
            alignmentName: args.alignmentName,
            profileName: args.profileName,
          }),
        );

        const startStation = Number(args.startStation ?? profile.startStation);
        const endStation = Number(args.endStation ?? profile.endStation);
        const interval = Number(args.interval ?? Math.max((endStation - startStation) / 20, 25));
        const maximumSamples = Number(args.maximumSamples ?? 150);
        const sampleCount = estimateSampleCount(startStation, endStation, interval);

        if (sampleCount > maximumSamples) {
          throw new Error(
            `Profile report would require ${sampleCount} samples, which exceeds the maximum of ${maximumSamples}. ` +
            "Increase the interval or maximumSamples.",
          );
        }

        const samples = ProfileSampleElevationsResponseSchema.parse(
          await appClient.sendCommand("sampleProfileElevations", {
            alignmentName: args.alignmentName,
            profileName: args.profileName,
            startStation,
            endStation,
            interval,
          }),
        );

        const elevations = samples.samples.map((s) => s.elevation);
        const grades = samples.samples
          .map((s) => s.grade)
          .filter((g): g is number => g !== null);

        const totalAscendingLength = profile.entities
          .filter((e) => (e.grade ?? 0) > 0)
          .reduce((sum, e) => sum + e.length, 0);
        const totalDescendingLength = profile.entities
          .filter((e) => (e.grade ?? 0) < 0)
          .reduce((sum, e) => sum + e.length, 0);
        const tangentCount = profile.entities.filter((e) => e.type === "tangent").length;
        const curveCount = profile.entities.filter((e) => e.type !== "tangent").length;
        const averageGrade = grades.length > 0
          ? grades.reduce((sum, g) => sum + g, 0) / grades.length
          : null;

        return ProfileReportResponseSchema.parse({
          profile,
          samples,
          summary: {
            sampledStationCount: samples.samples.length,
            startStation: samples.startStation,
            endStation: samples.endStation,
            interval: samples.interval,
            minimumElevation: Math.min(...elevations),
            maximumElevation: Math.max(...elevations),
            elevationRange: Math.max(...elevations) - Math.min(...elevations),
            averageGrade,
            totalAscendingLength,
            totalDescendingLength,
            tangentCount,
            curveCount,
            pviCount: profile.pviCount,
          },
        });
      }),
    },
    add_pvi: {
      action: "add_pvi",
      inputSchema: ProfileAddPviArgsSchema,
      responseSchema: GenericProfileResponseSchema,
      capabilities: ["create", "edit"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["profileAddPvi"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("profileAddPvi", {
          alignmentName: args.alignmentName,
          profileName: args.profileName,
          station: args.station,
          elevation: args.elevation,
          pvis: args.pvis,
          replaceAll: args.replaceAll,
        }),
      ),
    },
    delete_pvi: {
      action: "delete_pvi",
      inputSchema: ProfileDeletePviArgsSchema,
      responseSchema: GenericProfileResponseSchema,
      capabilities: ["delete", "edit"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["profileDeletePvi"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("profileDeletePvi", {
          alignmentName: args.alignmentName,
          profileName: args.profileName,
          station: args.station,
        }),
      ),
    },
    add_curve: {
      action: "add_curve",
      inputSchema: ProfileAddCurveArgsSchema,
      responseSchema: GenericProfileResponseSchema,
      capabilities: ["create", "edit"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["profileAddCurve"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("profileAddCurve", {
          alignmentName: args.alignmentName,
          profileName: args.profileName,
          pviStation: args.pviStation,
          length: args.length,
          curveType: args.curveType,
        }),
      ),
    },
    set_grade: {
      action: "set_grade",
      inputSchema: ProfileSetGradeArgsSchema,
      responseSchema: GenericProfileResponseSchema,
      capabilities: ["edit"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["profileSetGrade"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("profileSetGrade", {
          alignmentName: args.alignmentName,
          profileName: args.profileName,
          entityIndex: args.entityIndex,
          grade: args.grade,
        }),
      ),
    },
    check_k_values: {
      action: "check_k_values",
      inputSchema: ProfileCheckKValuesArgsSchema,
      responseSchema: GenericProfileResponseSchema,
      capabilities: ["analyze", "inspect"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["profileCheckKValues"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("profileCheckKValues", {
          alignmentName: args.alignmentName,
          profileName: args.profileName,
          designSpeed: args.designSpeed,
        }),
      ),
    },
    view_create: {
      action: "view_create",
      inputSchema: ProfileViewCreateArgsSchema,
      responseSchema: GenericProfileResponseSchema,
      capabilities: ["create"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["profileViewCreate"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("profileViewCreate", {
          alignmentName: args.alignmentName,
          profileViewName: args.profileViewName,
          insertX: args.insertX,
          insertY: args.insertY,
          style: args.style,
          bandSet: args.bandSet,
        }),
      ),
    },
    view_delete: {
      action: "view_delete",
      inputSchema: ProfileViewDeleteArgsSchema,
      responseSchema: GenericProfileResponseSchema,
      capabilities: ["delete"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["profileViewDelete"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("profileViewDelete", { profileViewName: args.profileViewName, dryRun: args.dryRun }),
      ),
    },
    view_band_set: {
      action: "view_band_set",
      inputSchema: ProfileViewBandSetArgsSchema,
      responseSchema: GenericProfileResponseSchema,
      capabilities: ["edit", "manage"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["profileViewBandSet"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("profileViewBandSet", {
          profileViewName: args.profileViewName,
          bandSetName: args.bandSetName,
        }),
      ),
    },
    view_band_set_apply: {
      action: "view_band_set_apply",
      inputSchema: ProfileViewBandSetApplyArgsSchema,
      responseSchema: GenericProfileResponseSchema,
      capabilities: ["edit"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["profileViewApplyBandSet"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("profileViewApplyBandSet", {
          profileViewName: args.profileViewName, bandSet: args.bandSet, mode: args.mode, profile1: args.profile1, network: args.network,
          matchIncrementToGridIntervals: args.matchIncrementToGridIntervals, bands: args.bands, labels: args.labels,
        }),
      ),
    },
    view_profiles_get: {
      action: "view_profiles_get",
      inputSchema: ProfileViewProfilesGetArgsSchema,
      responseSchema: GenericProfileResponseSchema,
      capabilities: ["query", "inspect"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["getProfileViewProfiles"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("getProfileViewProfiles", {
          profileViewName: args.profileViewName,
        }),
      ),
    },
    view_profiles_set: {
      action: "view_profiles_set",
      inputSchema: ProfileViewProfilesSetArgsSchema,
      responseSchema: GenericProfileResponseSchema,
      capabilities: ["edit"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["setProfileViewProfiles"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("setProfileViewProfiles", {
          profileViewName: args.profileViewName,
          draw: args.draw,
        }),
      ),
    },
    view_parts_get: {
      action: "view_parts_get",
      inputSchema: ProfileViewPartsGetArgsSchema,
      responseSchema: GenericProfileResponseSchema,
      capabilities: ["query", "inspect"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["getProfileViewParts"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("getProfileViewParts", {
          profileViewName: args.profileViewName, network: args.network, namePrefix: args.namePrefix, partKind: args.partKind,
        }),
      ),
    },
    view_parts_set: {
      action: "view_parts_set",
      inputSchema: ProfileViewPartsSetArgsSchema,
      responseSchema: GenericProfileResponseSchema,
      capabilities: ["edit"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["setProfileViewParts"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("setProfileViewParts", {
          profileViewName: args.profileViewName, network: args.network, namePrefix: args.namePrefix, partKind: args.partKind,
          drawParts: args.drawParts, overrideStyle: args.overrideStyle,
        }),
      ),
    },
    view_styles_list: {
      action: "view_styles_list",
      inputSchema: ProfileViewStylesListArgsSchema,
      responseSchema: GenericProfileResponseSchema,
      capabilities: ["query", "inspect"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["listProfileViewStyles"],
      execute: async () => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("listProfileViewStyles", {}),
      ),
    },
    view_style_set: {
      action: "view_style_set",
      inputSchema: ProfileViewStyleSetArgsSchema,
      responseSchema: GenericProfileResponseSchema,
      capabilities: ["edit"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["setProfileViewStyle"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("setProfileViewStyle", { profileViewName: args.profileViewName, style: args.style }),
      ),
    },
    view_range_set: {
      action: "view_range_set",
      inputSchema: ProfileViewRangeSetArgsSchema,
      responseSchema: GenericProfileResponseSchema,
      capabilities: ["edit"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["setProfileViewRange"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("setProfileViewRange", {
          profileViewName: args.profileViewName, startStation: args.startStation, endStation: args.endStation,
          automaticStation: args.automaticStation, minElevation: args.minElevation, maxElevation: args.maxElevation,
          automaticElevation: args.automaticElevation,
        }),
      ),
    },
    band_styles_list: {
      action: "band_styles_list",
      inputSchema: ProfileBandStylesListArgsSchema,
      responseSchema: GenericProfileResponseSchema,
      capabilities: ["query", "inspect"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["listBandStyles"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("listBandStyles", { bandType: args.bandType }),
      ),
    },
    band_style_get: {
      action: "band_style_get",
      inputSchema: ProfileBandStyleGetArgsSchema,
      responseSchema: GenericProfileResponseSchema,
      capabilities: ["query", "inspect"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["getBandStyle"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("getBandStyle", { bandType: args.bandType, style: args.style }),
      ),
    },
    band_style_copy: {
      action: "band_style_copy",
      inputSchema: ProfileBandStyleCopyArgsSchema,
      responseSchema: GenericProfileResponseSchema,
      capabilities: ["create"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["copyBandStyle"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("copyBandStyle", { bandType: args.bandType, style: args.style, newName: args.newName }),
      ),
    },
    band_style_set: {
      action: "band_style_set",
      inputSchema: ProfileBandStyleSetArgsSchema,
      responseSchema: GenericProfileResponseSchema,
      capabilities: ["edit"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["setBandStyle"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("setBandStyle", {
          bandType: args.bandType, style: args.style, titleText: args.titleText, bandHeight: args.bandHeight,
          titleBoxWidth: args.titleBoxWidth, groups: args.groups,
        }),
      ),
    },
    view_bands_get: {
      action: "view_bands_get",
      inputSchema: ProfileViewBandsGetArgsSchema,
      responseSchema: GenericProfileResponseSchema,
      capabilities: ["query", "inspect"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["getProfileViewBands"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("getProfileViewBands", { profileViewName: args.profileViewName }),
      ),
    },
    view_bands_set: {
      action: "view_bands_set",
      inputSchema: ProfileViewBandsSetArgsSchema,
      responseSchema: GenericProfileResponseSchema,
      capabilities: ["edit"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["setProfileViewBands"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("setProfileViewBands", {
          profileViewName: args.profileViewName, bands: args.bands, matchIncrementToGridIntervals: args.matchIncrementToGridIntervals,
        }),
      ),
    },
    view_profile_labels_clear: {
      action: "view_profile_labels_clear",
      inputSchema: ProfileViewLabelsClearArgsSchema,
      responseSchema: GenericProfileResponseSchema,
      capabilities: ["delete"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["clearProfileViewLabels"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("clearProfileViewLabels", { profileViewName: args.profileViewName, profiles: args.profiles }),
      ),
    },
    view_band_edit: {
      action: "view_band_edit",
      inputSchema: ProfileViewBandEditArgsSchema,
      responseSchema: GenericProfileResponseSchema,
      capabilities: ["edit"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["editProfileViewBands"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("editProfileViewBands", {
          profileViewName: args.profileViewName, op: args.op, location: args.location ?? "bottom", bandType: args.bandType,
          style: args.style, network: args.network, profile1: args.profile1, profile2: args.profile2,
          majorInterval: args.majorInterval, minorInterval: args.minorInterval, position: args.position, index: args.index, index2: args.index2,
        }),
      ),
    },
    label_styles_list: {
      action: "label_styles_list",
      inputSchema: ProfileLabelStylesListArgsSchema,
      responseSchema: GenericProfileResponseSchema,
      capabilities: ["query", "inspect"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["listProfileLabelStyles"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("listProfileLabelStyles", { labelKind: args.labelKind }),
      ),
    },
    line_labels_add: {
      action: "line_labels_add",
      inputSchema: ProfileLineLabelsAddArgsSchema,
      responseSchema: GenericProfileResponseSchema,
      capabilities: ["create", "edit"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["addProfileLineLabels"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("addProfileLineLabels", {
          profileViewName: args.profileViewName,
          profileName: args.profileName,
          style: args.style,
          replace: args.replace ?? false,
        }),
      ),
    },
    label_style_get: {
      action: "label_style_get",
      inputSchema: ProfileLabelStyleGetArgsSchema,
      responseSchema: GenericProfileResponseSchema,
      capabilities: ["query", "inspect"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["getProfileLabelStyle"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("getProfileLabelStyle", { labelKind: args.labelKind, style: args.style }),
      ),
    },
    label_style_copy: {
      action: "label_style_copy",
      inputSchema: ProfileLabelStyleCopyArgsSchema,
      responseSchema: GenericProfileResponseSchema,
      capabilities: ["create"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["copyProfileLabelStyle"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("copyProfileLabelStyle", {
          labelKind: args.labelKind,
          style: args.style,
          newStyle: args.newStyle,
        }),
      ),
    },
    label_style_text_set: {
      action: "label_style_text_set",
      inputSchema: ProfileLabelStyleTextSetArgsSchema,
      responseSchema: GenericProfileResponseSchema,
      capabilities: ["edit"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["setProfileLabelStyleText"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("setProfileLabelStyleText", {
          labelKind: args.labelKind,
          style: args.style,
          text: args.text,
        }),
      ),
    },
  },
  exposures: [
    {
      toolName: "civil3d_profile",
      displayName: "Civil 3D Profile",
      description: "Reads Civil 3D vertical profiles, reports geometry, and manages creation, editing, and deletion through a single domain tool.",
      inputShape: canonicalProfileInputShape,
      supportedActions: [
        "list",
        "get",
        "get_elevation",
        "sample_elevations",
        "create_from_surface",
        "create_layout",
        "create_from_network",
        "delete",
        "report",
        "add_pvi",
        "delete_pvi",
        "add_curve",
        "set_grade",
        "check_k_values",
        "view_create",
        "view_delete",
        "view_band_set",
        "view_band_set_apply",
        "view_profiles_get",
        "view_profiles_set",
        "view_parts_get", "view_parts_set", "view_styles_list", "view_style_set",
        "view_range_set", "band_styles_list", "band_style_get", "band_style_copy", "band_style_set",
        "view_bands_get",
        "view_bands_set", "view_band_edit", "view_profile_labels_clear",
        "label_styles_list",
        "line_labels_add",
        "label_style_get",
        "label_style_copy",
        "label_style_text_set",
      ],
      resolveAction: (rawArgs) => ({
        action: String(rawArgs.action ?? ""),
        args: rawArgs,
      }),
    },
    {
      toolName: "civil3d_profile_report",
      displayName: "Civil 3D Profile Report",
      description: "Builds a structured profile report by fetching profile detail and sampling elevations along the profile range.",
      inputShape: {
        alignmentName: z.string(),
        profileName: z.string(),
        interval: z.number().positive().optional(),
        startStation: z.number().optional(),
        endStation: z.number().optional(),
        maximumSamples: z.number().int().positive().max(400).optional(),
      },
      supportedActions: ["report"],
      resolveAction: (rawArgs) => ({
        action: "report",
        args: {
          action: "report",
          alignmentName: rawArgs.alignmentName,
          profileName: rawArgs.profileName,
          interval: rawArgs.interval,
          startStation: rawArgs.startStation,
          endStation: rawArgs.endStation,
          maximumSamples: rawArgs.maximumSamples,
        },
      }),
    },
    {
      toolName: "civil3d_profile_add_pvi",
      displayName: "Civil 3D Profile Add PVI",
      description: "Adds a PVI (Point of Vertical Intersection) to a Civil 3D layout profile at the specified station and elevation.",
      inputShape: {
        alignmentName: z.string(),
        profileName: z.string(),
        station: z.number(),
        elevation: z.number(),
      },
      supportedActions: ["add_pvi"],
      resolveAction: (rawArgs) => ({
        action: "add_pvi",
        args: {
          action: "add_pvi",
          alignmentName: rawArgs.alignmentName,
          profileName: rawArgs.profileName,
          station: rawArgs.station,
          elevation: rawArgs.elevation,
        },
      }),
    },
    {
      toolName: "civil3d_profile_delete_pvi",
      displayName: "Civil 3D Profile Delete PVI",
      description: "Deletes the PVI nearest to the specified station from a Civil 3D layout profile.",
      inputShape: {
        alignmentName: z.string(),
        profileName: z.string(),
        station: z.number(),
      },
      supportedActions: ["delete_pvi"],
      resolveAction: (rawArgs) => ({
        action: "delete_pvi",
        args: {
          action: "delete_pvi",
          alignmentName: rawArgs.alignmentName,
          profileName: rawArgs.profileName,
          station: rawArgs.station,
        },
      }),
    },
    {
      toolName: "civil3d_profile_add_curve",
      displayName: "Civil 3D Profile Add Curve",
      description: "Adds a symmetric or asymmetric parabolic vertical curve at an existing PVI in a Civil 3D layout profile.",
      inputShape: {
        alignmentName: z.string(),
        profileName: z.string(),
        pviStation: z.number(),
        length: z.number().positive(),
        curveType: z.enum(["symmetric_parabola", "asymmetric_parabola"]).optional().default("symmetric_parabola"),
      },
      supportedActions: ["add_curve"],
      resolveAction: (rawArgs) => ({
        action: "add_curve",
        args: {
          action: "add_curve",
          alignmentName: rawArgs.alignmentName,
          profileName: rawArgs.profileName,
          pviStation: rawArgs.pviStation,
          length: rawArgs.length,
          curveType: rawArgs.curveType,
        },
      }),
    },
    {
      toolName: "civil3d_profile_set_grade",
      displayName: "Civil 3D Profile Set Grade",
      description: "Sets the grade (slope) of a tangent entity in a Civil 3D layout profile. Grade is expressed as a decimal fraction (0.02 = 2%).",
      inputShape: {
        alignmentName: z.string(),
        profileName: z.string(),
        entityIndex: z.number().int().min(0),
        grade: z.number(),
      },
      supportedActions: ["set_grade"],
      resolveAction: (rawArgs) => ({
        action: "set_grade",
        args: {
          action: "set_grade",
          alignmentName: rawArgs.alignmentName,
          profileName: rawArgs.profileName,
          entityIndex: rawArgs.entityIndex,
          grade: rawArgs.grade,
        },
      }),
    },
    {
      toolName: "civil3d_profile_get_elevation",
      displayName: "Civil 3D Profile Get Elevation",
      description: "Samples the elevation and instantaneous grade of a Civil 3D profile at a given station.",
      inputShape: {
        alignmentName: z.string(),
        profileName: z.string(),
        station: z.number(),
      },
      supportedActions: ["get_elevation"],
      resolveAction: (rawArgs) => ({
        action: "get_elevation",
        args: {
          action: "get_elevation",
          alignmentName: rawArgs.alignmentName,
          profileName: rawArgs.profileName,
          station: rawArgs.station,
        },
      }),
    },
    {
      toolName: "civil3d_profile_check_k_values",
      displayName: "Civil 3D Profile Check K Values",
      description: "Validates the K value of every vertical curve in a Civil 3D layout profile against AASHTO minimum K values for the specified design speed. Returns a pass/fail report per curve.",
      inputShape: {
        alignmentName: z.string(),
        profileName: z.string(),
        designSpeed: z.number().positive(),
      },
      supportedActions: ["check_k_values"],
      resolveAction: (rawArgs) => ({
        action: "check_k_values",
        args: {
          action: "check_k_values",
          alignmentName: rawArgs.alignmentName,
          profileName: rawArgs.profileName,
          designSpeed: rawArgs.designSpeed,
        },
      }),
    },
    {
      toolName: "civil3d_profile_view_create",
      displayName: "Civil 3D Profile View Create",
      description: "Creates a Civil 3D profile view at the specified insertion point in model space. Optionally applies a style and band set.",
      inputShape: {
        alignmentName: z.string(),
        profileViewName: z.string(),
        insertX: z.number(),
        insertY: z.number(),
        style: z.string().optional(),
        bandSet: z.string().optional(),
      },
      supportedActions: ["view_create"],
      resolveAction: (rawArgs) => ({
        action: "view_create",
        args: {
          action: "view_create",
          alignmentName: rawArgs.alignmentName,
          profileViewName: rawArgs.profileViewName,
          insertX: rawArgs.insertX,
          insertY: rawArgs.insertY,
          style: rawArgs.style,
          bandSet: rawArgs.bandSet,
        },
      }),
    },
    {
      toolName: "civil3d_profile_view_band_set",
      displayName: "Civil 3D Profile View Band Set",
      description: "Applies a band set style to an existing Civil 3D profile view, updating the data bands displayed above and below the grid.",
      inputShape: {
        profileViewName: z.string(),
        bandSetName: z.string(),
      },
      supportedActions: ["view_band_set"],
      resolveAction: (rawArgs) => ({
        action: "view_band_set",
        args: {
          action: "view_band_set",
          profileViewName: rawArgs.profileViewName,
          bandSetName: rawArgs.bandSetName,
        },
      }),
    },
  ],
};
