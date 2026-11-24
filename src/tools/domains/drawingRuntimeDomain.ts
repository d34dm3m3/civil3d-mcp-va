import { z } from "zod";
import { withApplicationConnection } from "../../utils/ConnectionManager.js";
import type { DomainToolDefinition } from "../domainRuntime.js";

// C93c: filePath null for a drawing never saved (templatePath: the file AutoCAD reports for it, its template); activeDocument is the
// drawing calls run in, contextDocument the document whose command context ran this one.
const DrawingInfoResponseSchema = z.object({ fileName: z.string().optional(), filePath: z.string().nullable().optional(), templatePath: z.string().nullable().optional(), activeDocument: z.string().nullable().optional(), contextDocument: z.string().nullable().optional(), coordinateSystem: z.string().nullable().optional(), linearUnits: z.enum(["feet", "meters", "other"]).optional(), angularUnits: z.enum(["degrees", "radians", "grads"]).optional(), unsavedChanges: z.boolean().optional(), objectCounts: z.object({ surfaces: z.number().optional(), alignments: z.number().optional(), profiles: z.number().optional(), corridors: z.number().optional(), pipeNetworks: z.number().optional(), points: z.number().optional(), parcels: z.number().optional() }).optional(), drawingName: z.string().optional(), projectName: z.string().nullable().optional(), units: z.string().optional() });
const DrawingSettingsResponseSchema = z.object({ coordinateSystem: z.string().nullable().optional(), coordinateZone: z.string().nullable().optional(), datum: z.string().nullable().optional(), scaleFactor: z.number().optional(), elevationReference: z.string().nullable().optional(), defaultLayer: z.string().nullable().optional(), defaultStyles: z.object({ surface: z.string().nullable().optional(), alignment: z.string().nullable().optional(), profile: z.string().nullable().optional(), corridor: z.string().nullable().optional(), pipeNetwork: z.string().nullable().optional() }).optional() });
// Labels and other unnamed objects come back with name null (live 2026-10-06: SpanningPipeProfileLabel).
const SelectedCivilObjectsResponseSchema = z.array(z.object({ handle: z.string(), objectType: z.string(), name: z.string().nullable().optional(), layer: z.string().nullable().optional(), description: z.string().nullable().optional() }));
const CivilObjectTypesResponseSchema = z.array(z.string());
const GenericResponseSchema = z.object({}).passthrough();

const DrawingInfoArgs = z.object({ action: z.literal("info") });
// PNG of a model-space window or a profile view through the plot engine (the user's view is not touched).
const DrawingViewPngArgs = z.object({ action: z.literal("view_png"), output: z.string().min(1), overwrite: z.boolean().optional(), profileViewName: z.string().optional(), x: z.number().optional(), y: z.number().optional(), width: z.number().positive().optional(), height: z.number().positive().optional(), margin: z.number().min(0).optional(), media: z.string().optional(), plotStyleTable: z.string().optional() })
  .refine((a) => Boolean(a.profileViewName) !== (a.x !== undefined && a.y !== undefined && a.width !== undefined && a.height !== undefined), { message: "Give either profileViewName or a window (x, y, width, height)." });
const DrawingNewArgs = z.object({ action: z.literal("new"), templatePath: z.string().optional() });
const DrawingSaveArgs = z.object({ action: z.literal("save"), saveAs: z.string().optional(), overwrite: z.boolean().optional() });
const DrawingUndoArgs = z.object({ action: z.literal("undo"), steps: z.number().int().min(1).max(10).optional() });
const DrawingRedoArgs = z.object({ action: z.literal("redo"), steps: z.number().int().min(1).max(10).optional() });
const DrawingSettingsArgs = z.object({ action: z.literal("settings") });
const SelectedObjectsArgs = z.object({ action: z.literal("selected_objects_info"), limit: z.number().optional() });
const ObjectTypesArgs = z.object({ action: z.literal("list_object_types") });
const LayerStatsArgs = z.object({ action: z.literal("layer_stats"), limit: z.number().int().min(1).max(1000).optional() });
const StyleObjectType = z.enum(["surface", "corridor", "alignment", "profile", "pipe_network"]);
const StyleSetArgs = z.object({
  action: z.literal("style_set"),
  objectType: StyleObjectType,
  name: z.string().min(1),
  style: z.string().min(1).optional(),
  alignmentName: z.string().min(1).optional(),
  pipeStyle: z.string().min(1).optional(),
  structureStyle: z.string().min(1).optional(),
  namePrefix: z.string().min(1).optional(),
  codeSetStyle: z.string().min(1).optional(),
  partFamily: z.string().min(1).optional(),
  layer: z.string().min(1).optional(),
  createLayer: z.boolean().optional(),
}).refine((a) => a.objectType === "pipe_network" ? Boolean(a.pipeStyle || a.structureStyle || a.layer)
  : a.objectType === "corridor" ? Boolean(a.style || a.codeSetStyle) : Boolean(a.style), {
  message: "style is required; for pipe_network give pipeStyle, structureStyle and/or layer, for corridor style and/or codeSetStyle",
}).refine((a) => a.objectType !== "profile" || Boolean(a.alignmentName), { message: "alignmentName is required for a profile" });
const LayerListArgs = z.object({ action: z.literal("layer_list"), contains: z.string().optional() });
const EntityType = z.enum(["line", "polyline", "polyline3d", "circle", "arc", "text", "insert", "point", "image", "hatch", "mleader"]);
const EntitiesListArgs = z.object({
  action: z.literal("entities_list"),
  layer: z.string().min(1).optional(),
  layerContains: z.string().min(1).optional(),
  types: z.array(EntityType).min(1).optional(),
  limit: z.number().int().min(1).max(5000).optional(),
  maxVertices: z.number().int().min(2).max(100000).optional(),
  // C49: largest distance between a polyline arc and the chords that replace it (model units); also returns
  // densified points that follow the arcs (every vertex included, at most 20 000 per polyline).
  densifyArcs: z.number().min(0.001).optional(),
  // C65: only entities whose extents touch this area (model units)
  bbox: z.object({ minX: z.number(), minY: z.number(), maxX: z.number(), maxY: z.number() })
    .refine((b) => b.minX < b.maxX && b.minY < b.maxY, { message: "bbox needs minX < maxX and minY < maxY" }).optional(),
  // C52: a paper layout's entities instead of model space (title block, north arrow, scale bar)
  layout: z.string().min(1).optional(),
  // C105: only objects whose script id (script-id XData) starts with idPrefix ("" = any id); id and idData come back
  idPrefix: z.string().optional(),
  // C111: skip the first offset matches, to page through more than limit objects
  offset: z.number().int().min(0).optional(),
});
const ScaleGetArgs = z.object({ action: z.literal("scale_get") });
const ScaleSetArgs = z.object({ action: z.literal("scale_set"), scale: z.string().min(3) });
const CorridorFeatureLinesSetArgs = z.object({
  action: z.literal("corridor_feature_lines_set"),
  name: z.string().min(1),
  connected: z.boolean(),
  codes: z.array(z.string().min(1)).min(1).optional(),
  rebuild: z.boolean().optional(),
});
const LayoutListArgs = z.object({ action: z.literal("layout_list"), layout: z.string().min(1).optional() });
const BlockAttributesSetArgs = z.object({
  action: z.literal("block_attributes_set"),
  layout: z.string().min(1).optional(),
  blockName: z.string().min(1),
  values: z.record(z.string().min(1), z.string()).optional(),
  // C52: move attributes into their fields ([x, y] in the layout's units) and set their text height
  positions: z.record(z.string().min(1), z.tuple([z.number(), z.number()])).optional(),
  heights: z.record(z.string().min(1), z.number().positive()).optional(),
}).refine((a) => [a.values, a.positions, a.heights].some((m) => m !== undefined && Object.keys(m).length > 0),
  { message: "values, positions and/or heights must have at least one tag" });
const BlockAttributesGetArgs = z.object({
  action: z.literal("block_attributes_get"),
  layout: z.string().min(1).optional(),
  blockName: z.string().min(1),
});
const LayoutTextAddArgs = z.object({
  action: z.literal("layout_text_add"),
  layout: z.string().min(1),
  text: z.string().min(1),
  x: z.number(),
  y: z.number(),
  height: z.number().positive().optional(),
  width: z.number().min(0).optional(),
  layer: z.string().min(1).optional(),
  // C77: rotation, text style; AutoCAD field codes (%<\AcVar Filename>%) in text become live fields.
  rotationDegrees: z.number().optional(),
  textStyle: z.string().min(1).optional(),
});
const EntitiesLayerSetArgs = z.object({
  action: z.literal("entities_layer_set"),
  handles: z.array(z.string().regex(/^[0-9A-Fa-f]+$/)).min(1),
  layer: z.string().min(1),
  createLayer: z.boolean().optional(),
});
// Standard lineweights in hundredths of a mm (e.g. 35 = 0.35 mm).
const Lineweight = z.number().int().min(0).max(211);
const LayerSetArgs = z.object({
  action: z.literal("layer_set"),
  layers: z.array(z.string().min(1)).min(1),
  on: z.boolean().optional(),
  frozen: z.boolean().optional(),
  color: z.number().int().min(1).max(255).optional(),
  linetype: z.string().min(1).optional(),
  lineweight: z.union([Lineweight, z.literal("Default")]).optional(),
  plot: z.boolean().optional(),
}).refine((a) => [a.on, a.frozen, a.color, a.linetype, a.lineweight, a.plot].some((v) => v !== undefined),
  { message: "give on, frozen, color, linetype, lineweight and/or plot" });
// Display properties of plain AutoCAD entities (same entities and all-or-nothing rule as entities_erase).
const EntitiesStyleSetArgs = z.object({
  action: z.literal("entities_style_set"),
  handles: z.array(z.string().regex(/^[0-9A-Fa-f]+$/)).min(1),
  color: z.union([z.number().int().min(0).max(256), z.enum(["ByLayer", "ByBlock"])]).optional(),
  linetype: z.string().min(1).optional(),
  lineweight: z.union([Lineweight, z.enum(["ByLayer", "ByBlock", "Default"])]).optional(),
  linetypeScale: z.number().positive().optional(),
}).refine((a) => [a.color, a.linetype, a.lineweight, a.linetypeScale].some((v) => v !== undefined),
  { message: "give color, linetype, lineweight and/or linetypeScale" });
const LinetypesListArgs = z.object({ action: z.literal("linetypes_list"), contains: z.string().optional() });
// C67 text styles: font by file (.shx/.ttf) or TrueType typeface; height 0 = variable height.
// C68 block definitions: read geometry, texts and attribute definitions; change attribute definitions (tag, position
// in block units, height, style, justification, prompt, default), replace text and sync the block references.
const AttachmentPointName = z.enum(["BaseLeft", "BaseCenter", "BaseRight", "BaseAlign", "BaseMid", "BaseFit", "BottomLeft", "BottomCenter",
  "BottomRight", "MiddleLeft", "MiddleCenter", "MiddleRight", "TopLeft", "TopCenter", "TopRight"]);
const AttributeDefinitionSpec = z.object({
  tag: z.string().min(1), newTag: z.string().min(1).optional(), create: z.boolean().optional(),
  x: z.number().optional(), y: z.number().optional(), height: z.number().positive().optional(), widthFactor: z.number().positive().optional(),
  textStyle: z.string().min(1).optional(), justify: AttachmentPointName.optional(), prompt: z.string().optional(),
  defaultValue: z.string().optional(), invisible: z.boolean().optional(),
  color: z.union([z.number().int().min(0).max(256), z.enum(["ByLayer", "ByBlock"])]).optional(),
  layer: z.string().min(1).optional(),
  // C116: multiline makes a multiline (MText) attribute and can only be turned on; width wraps it (block units, 0 = no wrapping).
  multiline: z.boolean().optional(), width: z.number().min(0).optional(),
}).refine((a) => !a.create || (a.x !== undefined && a.y !== undefined && a.height !== undefined), { message: "create needs x, y and height" })
  .refine((a) => (a.x === undefined) === (a.y === undefined), { message: "give x and y together" })
  .refine((a) => a.multiline !== false, { message: "multiline can only be turned on; recreate the attribute to make it single-line" });
const BlockDefinitionGetArgs = z.object({ action: z.literal("block_definition_get"), blockName: z.string().min(1), includeGeometry: z.boolean().optional(), maxEntities: z.number().int().positive().optional() });
const BlockDefinitionSetArgs = z.object({
  action: z.literal("block_definition_set"), blockName: z.string().min(1),
  attributeDefinitions: z.array(AttributeDefinitionSpec).optional(), removeAttributes: z.array(z.string().min(1)).optional(),
  texts: z.array(z.object({ find: z.string().min(1), replace: z.string() })).optional(), syncReferences: z.boolean().optional(),
  // C68b: full ATTSYNC: every reference gets exactly the definition's attributes (stale tags erased, missing added,
  // position/height/style from the definition, values of existing tags kept).
  attsync: z.boolean().optional(),
}).refine((a) => (a.attributeDefinitions?.length ?? 0) + (a.removeAttributes?.length ?? 0) + (a.texts?.length ?? 0) > 0 || a.attsync === true,
  { message: "give attributeDefinitions, removeAttributes, texts and/or attsync: true" });
// C69 page setups: named PlotSettings per paper size and pen table, full or reduced scale (scaleDenominator 2 = 1:2),
// applied to layouts. Media by canonical/locale name or by paper size in mm.
const PaperSize = { widthMm: z.number().positive().optional(), heightMm: z.number().positive().optional() };
const PlotMediaListArgs = z.object({ action: z.literal("plot_media_list"), device: z.string().min(1).optional(), contains: z.string().optional(), ...PaperSize })
  .refine((a) => (a.widthMm === undefined) === (a.heightMm === undefined), { message: "give widthMm and heightMm together" });
const PageSetupsListArgs = z.object({ action: z.literal("page_setups_list") });
const PageSetupSetArgs = z.object({
  action: z.literal("page_setup_set"), name: z.string().min(1), device: z.string().min(1).optional(), media: z.string().min(1).optional(), ...PaperSize,
  plotStyleTable: z.string().min(1).optional(), scaleDenominator: z.number().positive().optional(),
  rotationDegrees: z.union([z.literal(0), z.literal(90), z.literal(180), z.literal(270)]).optional(),
  plotStyles: z.boolean().optional(), lineweights: z.boolean().optional(), scaleLineweights: z.boolean().optional(),
  // C93b: plot transparency (layer transparency such as the grey base map shows in the PDF only with it).
  plotTransparency: z.boolean().optional(),
  // C69b: plot offset from the printable area's corner in mm (e.g. half the A1 margins for an A1 sheet on full-bleed A3).
  plotOriginMm: z.tuple([z.number(), z.number()]).optional(),
}).refine((a) => (a.widthMm === undefined) === (a.heightMm === undefined), { message: "give widthMm and heightMm together" })
  .refine((a) => !(a.media !== undefined && a.widthMm !== undefined), { message: "give media or widthMm/heightMm, not both" });
const PageSetupApplyArgs = z.object({ action: z.literal("page_setup_apply"), name: z.string().min(1), layoutNames: z.array(z.string().min(1)).min(1) });
// C70: reload the plugin's newest build in bin/Release without restarting Civil 3D (needs the C70 loader).
const PluginReloadArgs = z.object({ action: z.literal("plugin_reload") });
// C73: save diagnostics. Object classes, loaded modules and side-database save tests (read only; temp files only),
// and replacing 3D solids that carry history (the cause of a company template's failing first save, eExcessiveItemCount).
const ObjectClassesListArgs = z.object({ action: z.literal("object_classes_list"), sourcePath: z.string().min(1).optional() });
const ModulesListArgs = z.object({ action: z.literal("modules_list"), contains: z.string().optional() });
const SaveTestArgs = z.object({
  action: z.literal("save_test"), sourcePath: z.string().min(1), attempts: z.number().int().min(1).max(5).optional(),
  format: z.enum(["dwg", "dwt", "dxf"]).optional(), version: z.enum(["current", "2018", "2013", "2010", "2007"]).optional(),
  eraseDictionaryEntries: z.array(z.string().min(1)).optional(), eraseClasses: z.array(z.string().min(1)).optional(),
  eraseBlocks: z.array(z.string().min(1)).optional(), replaceSolidsInBlocks: z.array(z.string()).optional(), clearSolidHistory: z.boolean().optional(),
});
const SolidsHistoryRemoveArgs = z.object({ action: z.literal("solids_history_remove"), blocks: z.array(z.string().min(1)).optional() });
const TextStylesListArgs = z.object({ action: z.literal("textstyles_list"), contains: z.string().optional() });
const TextStyleSetArgs = z.object({
  action: z.literal("textstyle_set"),
  name: z.string().min(1),
  fileName: z.string().regex(/\.(shx|ttf)$/i, "fileName must be a .shx or .ttf font file").optional(),
  typeface: z.string().min(1).optional(),
  bigFontFileName: z.string().optional(),
  height: z.number().min(0).optional(),
  widthFactor: z.number().positive().optional(),
  obliquingDegrees: z.number().min(-85).max(85).optional(),
  create: z.boolean().optional(),
  makeCurrent: z.boolean().optional(),
}).refine((a) => [a.fileName, a.typeface, a.bigFontFileName, a.height, a.widthFactor, a.obliquingDegrees, a.makeCurrent].some((v) => v !== undefined),
  { message: "give fileName, typeface, bigFontFileName, height, widthFactor, obliquingDegrees and/or makeCurrent" });

const Handle = z.string().regex(/^[0-9A-Fa-f]+$/);
// C75: block references by handle: insertion point, scale, rotation and dynamic block properties.
const BlockReferenceGetArgs = z.object({ action: z.literal("block_reference_get"), handle: Handle });
const BlockReferenceSetArgs = z.object({
  action: z.literal("block_reference_set"), handle: Handle,
  x: z.number().optional(), y: z.number().optional(), scaleX: z.number().optional(), scaleY: z.number().optional(),
  rotationDegrees: z.number().optional(), dynamicProperties: z.record(z.string(), z.union([z.string(), z.number(), z.boolean()])).optional(),
}).refine((a) => (a.x === undefined) === (a.y === undefined), { message: "give x and y together" })
  .refine((a) => [a.x, a.scaleX, a.scaleY, a.rotationDegrees].some((v) => v !== undefined) || Object.keys(a.dynamicProperties ?? {}).length > 0,
    { message: "give x/y, scaleX, scaleY, rotationDegrees or dynamicProperties" });
// Multileader in model space: arrowhead at (x, y), MText at (textX, textY). textHeight/arrowSize are model units
// (the leader is made non-annotative); rotationDegrees turns the text (WCS, CCW from +X; -twistDegrees of the
// sheet viewport reads horizontally); attachment = side of the text the leader meets (default: the side facing the arrow).
const MLeaderAddArgs = z.object({
  action: z.literal("mleader_add"),
  x: z.number(),
  y: z.number(),
  textX: z.number(),
  textY: z.number(),
  text: z.string().min(1),
  textHeight: z.number().positive(),
  rotationDegrees: z.number().optional(),
  layer: z.string().min(1).optional(),
  createLayer: z.boolean().optional(),
  frame: z.boolean().optional(),
  // 0 = a text mask in the background colour (C99g); 1-255 a filled box. min(1) refused the mask (found 2026-10-11).
  backgroundColor: z.number().int().min(0).max(255).optional(),
  arrowSize: z.number().positive().optional(),
  landing: z.boolean().optional(),
  attachment: z.enum(["left", "right"]).optional(),
});
// Plain AutoCAD entities only (texts, multileaders, lines, polylines, circles, arcs, hatches, inserts, leaders, dimensions).
const EntitiesEraseArgs = z.object({ action: z.literal("entities_erase"), handles: z.array(Handle).min(1) });
const DwgPath = z.string().min(1).regex(/\.(dwg|dwt)$/i, { message: "sourcePath must be a .dwg or .dwt file" });
const BlockListExternalArgs = z.object({ action: z.literal("block_list_external"), sourcePath: DwgPath });
// C78: styles between drawings. A collection is a property path under CivilDocument.Styles ("PipeStyles",
// "PartsListSet", "LabelStyles.PipeLabelStyles.PlanProfileLabelStyles"); without collection the paths are listed.
const StylesListExternalArgs = z.object({ action: z.literal("styles_list_external"), sourcePath: DwgPath.optional(), collection: z.string().min(1).optional(), contains: z.string().optional() });
// C80: ambient settings (Edit Drawing Settings → Ambient Settings), read by group and set by "Group.Property".
const SettingsAmbientGetArgs = z.object({ action: z.literal("settings_ambient_get"), group: z.string().min(1).optional() });
const SettingsAmbientSetArgs = z.object({
  action: z.literal("settings_ambient_set"),
  values: z.record(z.string().regex(/^[A-Za-z]+\.[A-Za-z]+$/, "keys are Group.Property, e.g. Grade.Format"), z.string()).refine((v) => Object.keys(v).length > 0, { message: "give at least one value" }),
});
// C91: Civil feature and command settings (GetSettings<T>, types in Autodesk.Civil.Settings, e.g. SettingsCmdCreateProfileView,
// SettingsPipeNetwork, SettingsCmdCreateNetwork) by "Group.Property" (or "Property" on the type); a style id by the style's
// name, or {collection, name} with the collection path under CivilDocument.Styles; name templates as strings.
const CivilSettingsListArgs = z.object({ action: z.literal("civil_settings_list"), contains: z.string().optional(), includeAmbient: z.boolean().optional() });
const CivilSettingsGetArgs = z.object({ action: z.literal("civil_settings_get"), settingsType: z.string().min(1), group: z.string().min(1).optional(), includeAmbient: z.boolean().optional() });
const StyleByCollection = z.object({ collection: z.string().min(1), name: z.string().min(1) }).strict();
const CivilSettingsSetArgs = z.object({
  action: z.literal("civil_settings_set"), settingsType: z.string().min(1),
  values: z.record(z.string().regex(/^[A-Za-z]+(\.[A-Za-z]+)?$/, "keys are Group.Property (or Property), e.g. Styles.ProfileViewStyleId"), z.union([z.string(), StyleByCollection]))
    .refine((v) => Object.keys(v).length > 0, { message: "give at least one value" }),
});
// C92: Hv/Vv in each structure with exactly two pipes (horizontal deviation from straight through, change of inclination);
// write: true puts "Hv 30°"/"Vv 5°" (decimals, minimum angles) in the structure's Description, keepExisting after " | ".
const StructureBendAnglesArgs = z.object({
  action: z.literal("structure_bend_angles"), networkName: z.string().min(1), structureNames: z.array(z.string().min(1)).min(1).optional(),
  write: z.boolean().optional(), dryRun: z.boolean().optional(), decimals: z.number().int().min(0).max(3).optional(),
  minHvDeg: z.number().min(0).max(180).optional(), minVvDeg: z.number().min(0).max(180).optional(), keepExisting: z.boolean().optional(),
});
// C93: the open drawings. List; open a local .dwg/.dwt (the open document comes back when the file is open already);
// make one active (later calls run in it); close one with save (over its own file) or discard (unsaved changes needed it),
// never the only open drawing, closed in Civil 3D only after the answer has been written.
const DocumentPath = z.string().min(1).regex(/\.(dwg|dwt)$/i, { message: "path must be a .dwg or .dwt file" });
const oneDocument = (a: { name?: string; path?: string }) => (a.name === undefined) !== (a.path === undefined);
const DocumentsListArgs = z.object({ action: z.literal("documents_list") });
const DocumentOpenArgs = z.object({ action: z.literal("document_open"), path: DocumentPath, readOnly: z.boolean().optional(), activate: z.boolean().optional() });
const DocumentActivateArgs = z.object({ action: z.literal("document_activate"), name: z.string().min(1).optional(), path: DocumentPath.optional() })
  .refine(oneDocument, { message: "give the document by name or by path (one of them)" });
const DocumentCloseArgs = z.object({ action: z.literal("document_close"), name: z.string().min(1).optional(), path: DocumentPath.optional(), save: z.boolean().optional(), discard: z.boolean().optional() })
  .refine(oneDocument, { message: "give the document by name or by path (one of them)" })
  .refine((a) => !(a.save && a.discard), { message: "give save or discard, not both" });
// C94: a copy of a drawing in other plane coordinates (e.g. SWEREF 99 15 00 -> SWEREF 99 13 30 for a municipal base map). The caller fits the
// parameters over the project area (pyproj); the source is read in a side database, every model space entity is moved with
// TransformBy, and a new DWG is written (never over the source); no coordinate system is set.
const HelmertTransform = z.object({ a: z.number(), b: z.number(), tx: z.number(), ty: z.number() }).strict()
  .describe("2D similarity: x' = a*x - b*y + tx, y' = b*x + a*y + ty (model X = east, Y = north).");
const AffineTransform = z.object({ m11: z.number(), m12: z.number(), m13: z.number(), m21: z.number(), m22: z.number(), m23: z.number() }).strict()
  .describe("Affine: x' = m11*x + m12*y + m13, y' = m21*x + m22*y + m23.");
const HeightTransform = z.object({ scale: z.number().positive().optional(), offset: z.number().optional() }).strict()
  .describe("Heights: z' = scale*z + offset (default unchanged).");
const DwgFile = (name: string) => z.string().min(1).regex(/\.dwg$/i, { message: `${name} must be a .dwg file` });
const samePath = (a: string, b: string) => a.replace(/\//g, "\\").toLowerCase() === b.replace(/\//g, "\\").toLowerCase();
const DrawingTransformArgs = z.object({
  action: z.literal("drawing_transform"), sourcePath: DwgFile("sourcePath"), outputPath: DwgFile("outputPath"), overwrite: z.boolean().optional(),
  transform: z.union([HelmertTransform, AffineTransform]), z: HeightTransform.optional(), dryRun: z.boolean().optional(),
  // Civil 3D objects (AECC_*) are skipped unless asked for: Civil has not opened the side database.
  includeCivilObjects: z.boolean().optional(),
}).refine((a) => !samePath(a.sourcePath, a.outputPath), { message: "outputPath must not be the source drawing" });
// C95: drawing references in the active drawing. List (name, saved path, file found, loaded/unloaded/unresolved, overlay or attach);
// attach a local DWG in model space as overlay (default) or attach, at x/y/z (default 0), scale, rotation, on a layer (default XREF,
// created when missing), the path stored relative to the drawing when possible (relativePath, default true).
// Block and layer names follow the AutoCAD symbol name rules.
const SymbolName = z.string().min(1).regex(/^[^<>/\\":;?*|,=`]+$/, { message: "a name cannot contain < > / \\ \" : ; ? * | , = `" });
const XrefsListArgs = z.object({ action: z.literal("xrefs_list") });
const XrefAttachArgs = z.object({
  action: z.literal("xref_attach"), path: DwgFile("path"), name: SymbolName.optional(), overlay: z.boolean().optional(),
  x: z.number().optional(), y: z.number().optional(), z: z.number().optional(), scale: z.number().positive().optional(), rotationDegrees: z.number().optional(),
  layer: SymbolName.optional(), relativePath: z.boolean().optional(),
});
// C97: detach a drawing reference (its insertions and dependent layers go; refused when nested, inserted in a block
// definition or on a locked layer), unload it (kept, not loaded) or reload it from its file.
const XrefByName = (action: "xref_detach" | "xref_unload" | "xref_reload") => z.object({ action: z.literal(action), name: z.string().min(1) });
// C85: profile view band sets with their top and bottom bands (type, style, gap, labels, intervals).
const BandSetsListArgs = z.object({ action: z.literal("band_sets_list"), contains: z.string().optional() });
// C86/C86c: create (from bottom, or copyFrom) or change a band set: removeStyles (top and bottom), bottom (replace) or appendBottom.
const BandItem = z.object({ bandType: z.enum(["ProfileData", "HorizontalGeometry", "VerticalGeometry", "SuperelevationData", "PipeNetwork", "PressureNetwork", "SectionalData"]), style: z.string().min(1), gap: z.number().min(0).optional() });
const BandSetSetArgs = z.object({
  action: z.literal("band_set_set"), name: z.string().min(1), copyFrom: z.string().min(1).optional(), removeStyles: z.array(z.string().min(1)).optional(),
  bottom: z.array(BandItem).optional(), appendBottom: z.array(BandItem).min(1).optional(),
}).refine((a) => !(a.bottom !== undefined && a.appendBottom !== undefined), { message: "give bottom or appendBottom, not both" });
// C87: drawn entities of a block definition: create it (empty or copyFrom), erase or change entities by selector
// (handles, type, layer, text, within [minX, minY, maxX, maxY], minLength, expect) and add lines, polylines, rectangles,
// circles, texts, multiline texts and inserts. Lineweight in hundredths of a mm (70 = 0,7 mm). Attribute definitions: block_definition_set.
const BlockPoint = z.tuple([z.number(), z.number()]);
const BlockColorArg = z.union([z.number().int().min(0).max(256), z.enum(["ByLayer", "ByBlock"])]);
const BlockLineweightArg = z.union([z.number().int().min(0), z.enum(["ByLayer", "ByBlock", "Default"])]);
const BlockEntityProps = { layer: z.string().min(1).optional(), color: BlockColorArg.optional(), linetype: z.string().min(1).optional(), linetypeScale: z.number().positive().optional(), lineweight: BlockLineweightArg.optional() };
const BlockSelector = z.object({
  handles: z.array(z.string().min(1)).optional(), type: z.enum(["any", "line", "polyline", "circle", "arc", "text", "mtext", "insert", "hatch"]).optional(),
  layer: z.string().optional(), text: z.string().optional(), textContains: z.string().optional(),
  within: z.tuple([z.number(), z.number(), z.number(), z.number()]).optional(), minLength: z.number().optional(), expect: z.number().int().min(0).optional(),
});
const BlockEntitySpec = z.discriminatedUnion("type", [
  z.object({ type: z.literal("line"), from: BlockPoint, to: BlockPoint, ...BlockEntityProps }),
  z.object({ type: z.literal("polyline"), points: z.array(BlockPoint).min(2), closed: z.boolean().optional(), ...BlockEntityProps }),
  z.object({ type: z.literal("rectangle"), min: BlockPoint, max: BlockPoint, ...BlockEntityProps }),
  z.object({ type: z.literal("circle"), center: BlockPoint, radius: z.number().positive(), ...BlockEntityProps }),
  z.object({ type: z.literal("text"), text: z.string(), at: BlockPoint, height: z.number().positive(), textStyle: z.string().optional(), justify: z.string().optional(), rotationDegrees: z.number().optional(), widthFactor: z.number().positive().optional(), ...BlockEntityProps }),
  z.object({ type: z.literal("mtext"), text: z.string(), at: BlockPoint, height: z.number().positive(), width: z.number().min(0).optional(), textStyle: z.string().optional(), attachment: z.string().optional(), rotationDegrees: z.number().optional(), ...BlockEntityProps }),
  z.object({ type: z.literal("insert"), block: z.string().min(1), at: BlockPoint, scale: z.union([z.number().positive(), BlockPoint]).optional(), rotationDegrees: z.number().optional(), ...BlockEntityProps }),
]);
const BlockEntitySet = z.object({
  select: BlockSelector, move: BlockPoint.optional(), start: BlockPoint.optional(), end: BlockPoint.optional(), position: BlockPoint.optional(),
  height: z.number().positive().optional(), text: z.string().optional(), widthFactor: z.number().positive().optional(), textStyle: z.string().optional(), ...BlockEntityProps,
});
const BlockEntitiesEditArgs = z.object({
  action: z.literal("block_entities_edit"), blockName: z.string().min(1), create: z.boolean().optional(), copyFrom: z.string().min(1).optional(), origin: BlockPoint.optional(),
  erase: z.array(BlockSelector).optional(), set: z.array(BlockEntitySet).optional(), add: z.array(BlockEntitySpec).optional(),
}).refine((a) => a.create === true || a.copyFrom !== undefined || (a.erase?.length ?? 0) + (a.set?.length ?? 0) + (a.add?.length ?? 0) > 0, { message: "give create or copyFrom, erase, set or add" });
// C87b: point existing references of one block at another (all layouts or layoutNames), optionally rescaled; attribute values are kept.
const BlockReferencesReplaceArgs = z.object({ action: z.literal("block_references_replace"), from: z.string().min(1), to: z.string().min(1), layoutNames: z.array(z.string().min(1)).optional(), scale: z.number().positive().optional() });
// C88a: layer standard from CSV (header Lager;Farg;Linjetyp;Linjebredd;Plott;Beskrivning[;Las;Fryst;Tand]); create/update
// default true; every row is checked first, an invalid row stops the import with its row number; dryRun only reports.
const LinFile = z.string().min(1).regex(/\.lin$/i, { message: "must be a .lin file (absolute path or a bare name such as acadiso.lin)" });
const LayersImportArgs = z.object({
  action: z.literal("layers_import"), path: z.string().min(1).regex(/\.(csv|txt)$/i, { message: "path must be a .csv or .txt file" }),
  create: z.boolean().optional(), update: z.boolean().optional(), dryRun: z.boolean().optional(), linFile: LinFile.optional(),
}).refine((a) => a.create !== false || a.update !== false || a.dryRun === true, { message: "create and update both false change nothing; use dryRun to check the file" });
// C88b: linetypes from a .lin file (names or all); loaded ones are skipped unless replace.
const LinetypesLoadArgs = z.object({ action: z.literal("linetypes_load"), path: LinFile, names: z.array(z.string().min(1)).min(1).optional(), all: z.boolean().optional(), replace: z.boolean().optional() })
  .refine((a) => (a.names !== undefined) !== (a.all === true), { message: "give names or all: true, not both" });
// C88c: Civil's object layers (Drawing Settings → Object Layers) by object type (SettingsObjectLayerType, e.g. Pipe, Structure, Alignment).
const ObjectLayersGetArgs = z.object({ action: z.literal("object_layers_get"), contains: z.string().optional() });
const ObjectLayerValue = z.object({ layer: z.string().min(1).optional(), modifier: z.enum(["None", "Prefix", "Suffix"]).optional(), modifierValue: z.string().optional(), locked: z.boolean().optional() })
  .strict().refine((v) => Object.keys(v).length > 0, { message: "give layer, modifier, modifierValue and/or locked" });
const ObjectLayersSetArgs = z.object({
  action: z.literal("object_layers_set"),
  values: z.record(z.string().min(1), ObjectLayerValue).refine((v) => Object.keys(v).length > 0, { message: "give at least one object type" }),
  createLayers: z.boolean().optional(),
});
// C88d: named layer states (save with every property; restore optionally into one viewport by handle).
// C88e: restore with viewport sets only that viewport's layer freeze (state's off/frozen layers frozen, its others thawed).
const LayerStatesListArgs = z.object({ action: z.literal("layer_states_list"), name: z.string().min(1).optional() });
const LayerStateSaveArgs = z.object({ action: z.literal("layer_state_save"), name: z.string().min(1), description: z.string().optional(), overwrite: z.boolean().optional(), viewport: Handle.optional() });
const LayerStateRestoreArgs = z.object({ action: z.literal("layer_state_restore"), name: z.string().min(1), viewport: Handle.optional() });
const LayerStateDeleteArgs = z.object({ action: z.literal("layer_state_delete"), name: z.string().min(1) });
// C88e: purge unused layers (Database.Purge decides) by names or regex pattern; dry run by default. Layer 0, Defpoints,
// the current layer, xref-dependent layers and Civil's object layers are never purged.
const LayersPurgeArgs = z.object({
  action: z.literal("layers_purge"), names: z.array(z.string().min(1)).min(1).optional(), pattern: z.string().min(1).optional(), dryRun: z.boolean().optional(),
}).refine((a) => a.names !== undefined || a.pattern !== undefined, { message: "give names or pattern" });
// C89a: coordinate crosses in paper space over a plan viewport every intervalM (default 100 m), size sizeMm (5), layer
// RIT-KOORDINATKRYSS, labels "X <northing>" / "Y <easting>" (XY, Swedish) or N/E in textStyle ISO at textHeightMm 1.8,
// turned with the twist; edgeLabels: only the outermost crosses labelled; replace: earlier crosses in the viewport erased.
const GridCrossesAddArgs = z.object({
  action: z.literal("grid_crosses_add"), layout: z.string().min(1), handle: Handle,
  intervalM: z.number().positive().optional(), sizeMm: z.number().positive().optional(), layer: z.string().min(1).optional(),
  textStyle: z.string().min(1).optional(), textHeightMm: z.number().positive().optional(), labels: z.enum(["XY", "NE"]).optional(),
  edgeLabels: z.boolean().optional(), replace: z.boolean().optional(),
});
// C89b: turn the north arrow block (default Norrpil) by the viewport's twist (handle, or the layout's first viewport).
const NorthArrowAlignArgs = z.object({ action: z.literal("north_arrow_align"), layout: z.string().min(1), blockName: z.string().min(1).optional(), handle: Handle.optional() });
// C90: label style texts (0/000 stations). A collection is a path under CivilDocument.Styles.LabelStyles, e.g.
// AlignmentLabelStyles.MajorStationLabelStyles, PipeLabelStyles.PlanProfileLabelStyles, StructureLabelStyles.LabelStyles;
// text components are Text, ReferenceText and TextForEach. contents replaces a whole text; find/replaceWith replaces a
// string (regex: true for a .NET regular expression, $1 in replaceWith); without style find/replaceWith runs over the collection.
const LabelStylesListArgs = z.object({ action: z.literal("label_styles_list"), collection: z.string().min(1).optional(), contains: z.string().optional() });
const LabelStyleTextGetArgs = z.object({ action: z.literal("label_style_text_get"), collection: z.string().min(1), style: z.string().min(1) });
const TextRewriteShape = { find: z.string().min(1).optional(), replaceWith: z.string().optional(), regex: z.boolean().optional(), dryRun: z.boolean().optional() };
const findPairComplete = (a: { find?: string; replaceWith?: string; regex?: boolean }) => (a.find === undefined) === (a.replaceWith === undefined) && (a.regex !== true || a.find !== undefined);
const LabelStyleTextSetArgs = z.object({
  action: z.literal("label_style_text_set"), collection: z.string().min(1), style: z.string().min(1).optional(), component: z.string().min(1).optional(),
  contents: z.string().optional(), ...TextRewriteShape,
}).refine((a) => (a.contents !== undefined) !== (a.find !== undefined), { message: "give contents or find/replaceWith" })
  .refine(findPairComplete, { message: "give find and replaceWith together (regex needs find)" })
  .refine((a) => a.contents === undefined || a.style !== undefined, { message: "contents needs style" });
// C93b: style names follow the AutoCAD symbol name rules ("Brunn Hv/Vv" made Civil 3D throw, live 2026-10-10).
const StyleName = z.string().min(1).regex(/^[^<>/\\":;?*|,=`]+$/, { message: "a style name cannot contain < > / \\ \" : ; ? * | , = `" });
const LabelStyleCopyArgs = z.object({ action: z.literal("label_style_copy"), collection: z.string().min(1), style: z.string().min(1), newName: StyleName });
// C90: profile view style axes (ProfileViewStyle.Bottom/Top/Left/RightAxis): major/minor tick label text and interval.
const AxisName = z.enum(["Bottom", "Top", "Left", "Right"]);
const TickName = z.enum(["Major", "Minor"]);
const ProfileViewAxisGetArgs = z.object({ action: z.literal("profile_view_axis_get"), style: z.string().min(1), axis: AxisName.optional() });
const ProfileViewAxisSetArgs = z.object({
  action: z.literal("profile_view_axis_set"), style: z.string().min(1), axis: AxisName, tick: TickName, labelText: z.string().optional(), interval: z.number().positive().optional(),
}).refine((a) => a.labelText !== undefined || a.interval !== undefined, { message: "give labelText and/or interval" });
// C90: find/replace in band styles' group label texts (one style, or every band style of bandType), optionally one group/component.
const BandStyleKind = z.enum(["ProfileData", "VerticalGeometry", "HorizontalGeometry", "SuperelevationData", "SectionalData", "PipeNetwork", "SectionData", "SectionSegment"]);
const BandStyleTextSetArgs = z.object({
  action: z.literal("band_style_text_set"), bandType: BandStyleKind, style: z.string().min(1).optional(), group: z.string().min(1).optional(), component: z.string().min(1).optional(),
  ...TextRewriteShape, find: z.string().min(1), replaceWith: z.string(),
});
// C98b: label sets (alignment by default) with their items (label type, label style, the collection that holds it,
// increment), and a label style's general properties as "Group.Property" (Label, Behavior, PlanReadability, Leader,
// DraggedStateComponents); PlanReadability.PlanReadableBias in degrees. Values as JSON booleans/numbers or text.
const LabelSetKind = z.enum(["Alignment", "Profile", "Section"]);
const LabelSetsListArgs = z.object({ action: z.literal("label_sets_list"), kind: LabelSetKind.optional(), name: z.string().min(1).optional(), contains: z.string().optional() });
const LabelStylePropertiesGetArgs = z.object({ action: z.literal("label_style_properties_get"), collection: z.string().min(1), style: z.string().min(1) });
const LabelStylePropertiesSetArgs = z.object({
  action: z.literal("label_style_properties_set"), collection: z.string().min(1), style: z.string().min(1),
  values: z.record(z.string().regex(/^[A-Za-z]+\.[A-Za-z0-9]+$/, "keys are Group.Property, e.g. PlanReadability.PlanReadable"), z.union([z.string(), z.number(), z.boolean()]))
    .refine((v) => Object.keys(v).length > 0, { message: "give at least one value" }),
  dryRun: z.boolean().optional(),
});
// C98c: label expressions of a label style collection (LabelStyleCollection.Expressions; the labels of pipe network band
// styles use those of PipeLabelStyles.PlanProfileLabelStyles). Properties in braces ({Wall Thickness}), drawing units
// (metres), angles in radians; formatResultAs is Civil's ExpressionFormatedType; reference is the field code for a text.
const ExpressionFormat = z.enum(["Angle", "Area", "Azimuth", "Coordinate", "Dimension", "Direction", "Distance", "Double", "Elevation", "GradeSlope", "Latitude", "Longitude", "Percent", "Station", "Volume"]);
const ExpressionName = z.string().trim().min(1).max(255).regex(/^[^{}[\]<>()|"]+$/, { message: "an expression name cannot contain { } [ ] < > ( ) | \"" });
const LabelExpressionsListArgs = z.object({ action: z.literal("label_expressions_list"), collection: z.string().min(1), contains: z.string().optional() });
const LabelExpressionSetArgs = z.object({
  action: z.literal("label_expression_set"), collection: z.string().min(1), name: ExpressionName,
  expression: z.string().trim().min(1).optional(), description: z.string().optional(), formatResultAs: ExpressionFormat.optional(), dryRun: z.boolean().optional(),
}).refine((a) => a.expression !== undefined || a.description !== undefined || a.formatResultAs !== undefined, { message: "give expression, description and/or formatResultAs" });
const LabelExpressionDeleteArgs = z.object({ action: z.literal("label_expression_delete"), collection: z.string().min(1), name: z.string().min(1), force: z.boolean().optional(), dryRun: z.boolean().optional() });
// C98e: display components (Display tab) of a style by its collection path under CivilDocument.Styles (default
// ProfileStyles; also AlignmentStyles, ProfileViewStyles, BandStyles.ProfileViewPipeNetworkBandStyles, ...): display
// {view: {component: {visible, layer, color, linetype, linetypeScale, lineweight, plotStyle}}} (a profile style: view
// Profile with Line, Curve, LineExtension, ...; Section ProfileMarker; Model Chain3D), and marker styles by name.
const STANDARD_LINEWEIGHTS = [0, 5, 9, 13, 15, 18, 20, 25, 30, 35, 40, 50, 53, 60, 70, 80, 90, 100, 106, 120, 140, 158, 200, 211];
const DisplayComponentProps = z.object({
  visible: z.boolean().optional(), layer: z.string().min(1).optional(), color: z.union([z.string().min(1), z.number().int().min(0).max(256)]).optional(),
  linetype: z.string().min(1).optional(), linetypeScale: z.number().positive().optional(),
  lineweight: z.union([z.number().int().refine((n) => STANDARD_LINEWEIGHTS.includes(n), { message: "a standard lineweight in hundredths of a mm (35 = 0.35 mm)" }), z.enum(["ByLayer", "ByBlock", "Default"])]).optional(),
  plotStyle: z.string().min(1).optional(),
}).strict().refine((p) => Object.keys(p).length > 0, { message: "give visible, layer, color, linetype, linetypeScale, lineweight and/or plotStyle" });
const StyleDisplayGetArgs = z.object({ action: z.literal("style_display_get"), collection: z.string().min(1).optional(), style: z.string().min(1) });
const StyleDisplaySetArgs = z.object({
  action: z.literal("style_display_set"), collection: z.string().min(1).optional(), style: z.string().min(1),
  display: z.record(z.string().min(1), z.record(z.string().min(1), DisplayComponentProps)).optional(),
  markers: z.record(z.string().min(1), z.string().min(1)).optional(),
  dryRun: z.boolean().optional(),
}).refine((a) => Object.keys(a.display ?? {}).length > 0 || Object.keys(a.markers ?? {}).length > 0, { message: "give display and/or markers" });
// C84: one colour for a block definition's entities (and a dynamic block's anonymous copies), optionally only fromColor.
const ColorArg = z.union([z.number().int().min(0).max(256), z.enum(["ByLayer", "ByBlock"])]);
const BlockEntitiesColorSetArgs = z.object({ action: z.literal("block_entities_color_set"), blockName: z.string().min(1), color: ColorArg, fromColor: ColorArg.optional() });
// C83: create or update a multileader style (MText content; sizes in paper mm when annotative) and make it current.
const MLeaderStyleSetArgs = z.object({
  action: z.literal("mleader_style_set"), name: z.string().min(1), textStyle: z.string().min(1).optional(), textHeight: z.number().min(0).optional(),
  arrowSize: z.number().min(0).optional(), landingGap: z.number().min(0).optional(), doglegLength: z.number().min(0).optional(),
  annotative: z.boolean().optional(), frame: z.boolean().optional(), backgroundColor: z.number().int().min(0).max(255).optional(), makeCurrent: z.boolean().optional(),
});
// C82: purge block definitions nothing refers to (Database.Purge decides), by names or pattern; dry run by default.
const BlocksPurgeArgs = z.object({
  action: z.literal("blocks_purge"), names: z.array(z.string().min(1)).min(1).optional(), pattern: z.string().min(1).optional(), dryRun: z.boolean().optional(),
}).refine((a) => a.names !== undefined || a.pattern !== undefined, { message: "give names or pattern" });
const StyleImportItem = z.object({ collection: z.string().min(1), names: z.array(z.string().min(1)).min(1) });
const StylesImportArgs = z.object({ action: z.literal("styles_import"), sourcePath: DwgPath, items: z.array(StyleImportItem).min(1), conflict: z.enum(["ignore", "rename", "override"]).optional() });
const BlockImportArgs = z.object({
  action: z.literal("block_import"),
  sourcePath: DwgPath,
  blockNames: z.array(z.string().min(1)).min(1).optional(),
  overwrite: z.boolean().optional(),
});
const BlockInsertArgs = z.object({
  action: z.literal("block_insert"),
  blockName: z.string().min(1),
  x: z.number(),
  y: z.number(),
  z: z.number().optional(),
  rotationDegrees: z.number().optional(),
  scale: z.number().positive().optional(),
  layer: z.string().min(1).optional(),
  createLayer: z.boolean().optional(),
  layout: z.string().min(1).optional(),
  attributes: z.record(z.string().min(1), z.string()).optional(),
});
const LayoutCreateArgs = z.object({ action: z.literal("layout_create"), name: z.string().min(1), copyFrom: z.string().min(1) });
const LayoutDeleteArgs = z.object({ action: z.literal("layout_delete"), name: z.string().min(1) })
  .refine((a) => a.name.toLowerCase() !== "model", { message: "Model space cannot be deleted." });
// Viewport view: viewCenterX/Y are MODEL coordinates shown at the viewport centre; twistDegrees is
// Viewport.TwistAngle (a model direction at angle a is drawn at a + twist on the sheet, so -a makes it horizontal);
// CustomScale = modelUnitsInMm / scaleDenominator (paper mm, model m -> 1000: 1:400 gives 2.5).
const ViewportViewShape = {
  viewCenterX: z.number().optional(),
  viewCenterY: z.number().optional(),
  twistDegrees: z.number().optional(),
  scaleDenominator: z.number().positive().optional(),
  modelUnitsInMm: z.number().positive().optional(),
  locked: z.boolean().optional(),
  layer: z.string().min(1).optional(),
  createLayer: z.boolean().optional(),
  frozenLayers: z.array(z.string().min(1)).optional(),
};
const bothOrNeitherCenter = (a: { viewCenterX?: number; viewCenterY?: number }) => (a.viewCenterX === undefined) === (a.viewCenterY === undefined);
const ViewportCreateArgs = z.object({
  action: z.literal("viewport_create"),
  layout: z.string().min(1),
  centerX: z.number(),
  centerY: z.number(),
  width: z.number().positive(),
  height: z.number().positive(),
  ...ViewportViewShape,
  viewCenterX: z.number(),
  viewCenterY: z.number(),
  scaleDenominator: z.number().positive(),
});
// C52: centerX/centerY/width/height place and size the viewport on the paper (were ignored before).
const ViewportPaperShape = {
  centerX: z.number().optional(),
  centerY: z.number().optional(),
  width: z.number().positive().optional(),
  height: z.number().positive().optional(),
  on: z.boolean().optional(),   // C75
};
const ViewportSetArgs = z.object({ action: z.literal("viewport_set"), layout: z.string().min(1), handle: Handle, ...ViewportPaperShape, ...ViewportViewShape })
  .refine(bothOrNeitherCenter, { message: "Give both viewCenterX and viewCenterY or neither." })
  .refine((a) => (a.centerX === undefined) === (a.centerY === undefined), { message: "Give both centerX and centerY or neither." })
  .refine((a) => [...Object.keys(ViewportPaperShape), ...Object.keys(ViewportViewShape)]
    .some((k) => k !== "createLayer" && k !== "modelUnitsInMm" && (a as Record<string, unknown>)[k] !== undefined),
    { message: "Give at least one of centerX/Y, width, height, on, viewCenterX/Y, twistDegrees, scaleDenominator, locked, layer, frozenLayers." });
const ViewportEraseArgs = z.object({ action: z.literal("viewport_erase"), layout: z.string().min(1), handle: Handle });
const SYSVARS = ["SAVETIME", "LTSCALE", "PSLTSCALE", "MSLTSCALE", "CANNOSCALE", "ANNOALLVISIBLE", "BACKGROUNDPLOT", "LWDISPLAY"] as const;
const SysvarName = z.preprocess((v) => (typeof v === "string" ? v.trim().toUpperCase() : v), z.enum(SYSVARS));
const SysvarGetArgs = z.object({ action: z.literal("sysvar_get"), name: SysvarName });
const SysvarSetArgs = z.object({ action: z.literal("sysvar_set"), name: SysvarName, value: z.union([z.number(), z.string(), z.boolean()]) });
// A paper layout to PDF (DWG To PDF, 1:1 on the layout's paper) or PNG (scaled to fit a raster size).
const LayoutPlotArgs = z.object({
  action: z.literal("layout_plot"),
  layout: z.string().min(1),
  output: z.string().min(1).regex(/\.(pdf|png)$/i, { message: "output must end in .pdf or .png" }),
  media: z.string().min(1).optional(),
  plotStyleTable: z.string().min(1).optional(),
  overwrite: z.boolean().optional(),
  // C72: plot as a named page setup says (paper, pen table, scale, rotation); PDF only, not with media/plotStyleTable.
  pageSetup: z.string().min(1).optional(),
}).refine((a) => !a.pageSetup || /\.pdf$/i.test(a.output), { message: "pageSetup plots to PDF only" })
  .refine((a) => !a.pageSetup || (a.media === undefined && a.plotStyleTable === undefined), { message: "pageSetup replaces media and plotStyleTable" });
// C105: script ids in XData (the plugin's script-id application) so that a rerun finds its own objects; data (a string, or JSON stored as its
// text, e.g. the position the script last wrote) is kept with the id. entities_list idPrefix reads them back. C114: move objects
// by handle as MOVE does (TransformBy with dx, dy, dz in drawing units). All handles are checked before anything changes.
const ScriptIdItem = z.object({ handle: Handle, id: z.string().min(1).max(255), data: z.unknown().optional() });
const EntitiesIdSetArgs = z.object({ action: z.literal("entities_id_set"), items: z.array(ScriptIdItem).min(1) });
const EntitiesIdClearArgs = z.object({ action: z.literal("entities_id_clear"), handles: z.array(Handle).min(1) });
const EntitiesMoveArgs = z.object({
  action: z.literal("entities_move"), handles: z.array(Handle).min(1), dx: z.number().optional(), dy: z.number().optional(), dz: z.number().optional(),
}).refine((a) => [a.dx, a.dy, a.dz].some((v) => v !== undefined && v !== 0), { message: "give a non-zero dx, dy or dz" });
// C117: a background mask on existing multileaders (by handle or layer); the style cannot hold one. Position and text stay.
const MLeaderMaskSetArgs = z.object({
  action: z.literal("mleader_mask_set"), handles: z.array(Handle).min(1).optional(), layers: z.array(z.string().min(1)).min(1).optional(),
  backgroundColor: z.number().int().min(0).max(255).optional(), maskScale: z.number().min(1).max(5).optional(), dryRun: z.boolean().optional(),
}).refine((a) => (a.handles?.length ?? 0) + (a.layers?.length ?? 0) > 0, { message: "give handles or layers" });
// C99h: a raster image (png, jpg, tif) in model space. Placement: origin (x, y) = the image's lower-left corner and the vectors of
// its bottom edge (ux, uy) and left edge (vx, vy) in drawing units (affine world file: rotation and skew kept), or width +
// rotationDegrees (height from the pixel aspect); the path is stored relative to the drawing when possible (relativePath, default true).
// C109: detach by image definition name: every image showing it is erased, then the definition; unknown names fail first.
const RasterPath = z.string().min(1).regex(/\.(png|jpe?g|tiff?)$/i, { message: "path must be a .png, .jpg, .jpeg, .tif or .tiff file" });
const RasterAttachArgs = z.object({
  action: z.literal("raster_attach"), path: RasterPath, name: z.string().min(1).optional(), x: z.number(), y: z.number(),
  ux: z.number().optional(), uy: z.number().optional(), vx: z.number().optional(), vy: z.number().optional(),
  width: z.number().positive().optional(), rotationDegrees: z.number().optional(),
  layer: SymbolName.optional(), createLayer: z.boolean().optional(), fade: z.number().int().min(0).max(100).optional(), relativePath: z.boolean().optional(),
}).refine((a) => [0, 4].includes([a.ux, a.uy, a.vx, a.vy].filter((v) => v !== undefined).length), { message: "give all of ux, uy, vx, vy or none of them" })
  .refine((a) => a.ux !== undefined || a.width !== undefined, { message: "give the placement vectors ux, uy, vx, vy or a width" });
const RasterDetachArgs = z.object({ action: z.literal("raster_detach"), names: z.array(z.string().min(1)).min(1) });

/** Plugin parameters: every parsed argument except the action, undefined values dropped, defaults filled in. */
function pluginParams(args: Record<string, unknown>, defaults: Record<string, unknown> = {}): Record<string, unknown> {
  const params: Record<string, unknown> = { ...defaults };
  for (const [key, value] of Object.entries(args)) {
    if (key !== "action" && value !== undefined) params[key] = value;
  }
  return params;
}
const forward = (method: string, defaults: Record<string, unknown> = {}) => async (args: Record<string, unknown>) =>
  await withApplicationConnection(async (appClient) => await appClient.sendCommand(method, pluginParams(args, defaults)));

const DRAWING_ACTIONS = [
  "info", "new", "save", "undo", "redo", "settings", "selected_objects_info", "list_object_types", "layer_stats", "style_set",
  "layer_list", "layer_set", "entities_list", "entities_layer_set", "layout_list", "block_attributes_get", "block_attributes_set", "layout_text_add",
  "corridor_feature_lines_set", "scale_get", "scale_set", "view_png",
  "mleader_add", "entities_erase", "block_list_external", "block_import", "block_insert", "layout_create", "layout_delete",
  "viewport_create", "viewport_set", "viewport_erase", "sysvar_get", "sysvar_set", "layout_plot",
  "entities_style_set", "linetypes_list",
  "textstyles_list", "textstyle_set", "block_definition_get", "block_definition_set",
  "plot_media_list", "page_setups_list", "page_setup_set", "page_setup_apply", "plugin_reload",
  "object_classes_list", "modules_list", "save_test", "solids_history_remove",
  "block_reference_get", "block_reference_set", "styles_list_external", "styles_import", "settings_ambient_get", "settings_ambient_set", "blocks_purge", "mleader_style_set", "block_entities_color_set", "band_sets_list", "band_set_set", "block_entities_edit", "block_references_replace",
  "layers_import", "linetypes_load", "object_layers_get", "object_layers_set", "layer_states_list", "layer_state_save", "layer_state_restore", "layer_state_delete",
  "grid_crosses_add", "north_arrow_align",
  "layers_purge",
  "label_styles_list", "label_style_text_get", "label_style_text_set", "label_style_copy", "profile_view_axis_get", "profile_view_axis_set", "band_style_text_set",
  "civil_settings_list", "civil_settings_get", "civil_settings_set",
  "structure_bend_angles",
  "documents_list", "document_open", "document_activate", "document_close",
  "drawing_transform",
  "xrefs_list", "xref_attach",
  "xref_detach", "xref_unload", "xref_reload",
  "label_sets_list", "label_style_properties_get", "label_style_properties_set",
  "label_expressions_list", "label_expression_set", "label_expression_delete",
  "style_display_get", "style_display_set",
  "entities_id_set", "entities_id_clear", "entities_move",
  "mleader_mask_set",
  "raster_attach", "raster_detach",
] as const;

export const DRAWING_RUNTIME_DOMAIN_DEFINITION: DomainToolDefinition = {
  domain: "drawing",
  actions: {
    info: { action: "info", inputSchema: DrawingInfoArgs, responseSchema: DrawingInfoResponseSchema, capabilities: ["query", "inspect"], requiresActiveDrawing: false, safeForRetry: true, pluginMethods: ["getDrawingInfo"], execute: async () => await withApplicationConnection(async (appClient) => await appClient.sendCommand("getDrawingInfo", {})) },
    new: { action: "new", inputSchema: DrawingNewArgs, responseSchema: GenericResponseSchema, capabilities: ["create", "manage"], requiresActiveDrawing: false, safeForRetry: false, pluginMethods: ["newDrawing"], execute: async (args) => await withApplicationConnection(async (appClient) => await appClient.sendCommand("newDrawing", { templatePath: args.templatePath })) },
    save: { action: "save", inputSchema: DrawingSaveArgs, responseSchema: GenericResponseSchema, capabilities: ["edit", "manage"], requiresActiveDrawing: false, safeForRetry: false, pluginMethods: ["saveDrawing"], execute: async (args) => await withApplicationConnection(async (appClient) => await appClient.sendCommand("saveDrawing", { saveAs: args.saveAs, overwrite: args.overwrite ?? false })) },
    undo: { action: "undo", inputSchema: DrawingUndoArgs, responseSchema: GenericResponseSchema, capabilities: ["edit", "manage"], requiresActiveDrawing: true, safeForRetry: false, pluginMethods: ["undoDrawing"], execute: async (args) => await withApplicationConnection(async (appClient) => await appClient.sendCommand("undoDrawing", { steps: args.steps ?? 1 })) },
    redo: { action: "redo", inputSchema: DrawingRedoArgs, responseSchema: GenericResponseSchema, capabilities: ["edit", "manage"], requiresActiveDrawing: true, safeForRetry: false, pluginMethods: ["redoDrawing"], execute: async (args) => await withApplicationConnection(async (appClient) => await appClient.sendCommand("redoDrawing", { steps: args.steps ?? 1 })) },
    settings: { action: "settings", inputSchema: DrawingSettingsArgs, responseSchema: DrawingSettingsResponseSchema, capabilities: ["query", "inspect"], requiresActiveDrawing: false, safeForRetry: true, pluginMethods: ["getDrawingSettings"], execute: async () => await withApplicationConnection(async (appClient) => await appClient.sendCommand("getDrawingSettings", {})) },
    selected_objects_info: { action: "selected_objects_info", inputSchema: SelectedObjectsArgs, responseSchema: SelectedCivilObjectsResponseSchema, capabilities: ["query", "inspect"], requiresActiveDrawing: true, safeForRetry: true, pluginMethods: ["getSelectedCivilObjectsInfo"], execute: async (args) => await withApplicationConnection(async (appClient) => await appClient.sendCommand("getSelectedCivilObjectsInfo", { limit: args.limit || 100 })) },
    list_object_types: { action: "list_object_types", inputSchema: ObjectTypesArgs, responseSchema: CivilObjectTypesResponseSchema, capabilities: ["query", "inspect"], requiresActiveDrawing: false, safeForRetry: true, pluginMethods: ["listCivilObjectTypes"], execute: async () => await withApplicationConnection(async (appClient) => await appClient.sendCommand("listCivilObjectTypes", {})) },
    layer_stats: { action: "layer_stats", inputSchema: LayerStatsArgs, responseSchema: GenericResponseSchema, capabilities: ["query", "inspect"], requiresActiveDrawing: true, safeForRetry: true, pluginMethods: ["getDrawingLayerStats"], execute: async (args) => await withApplicationConnection(async (appClient) => await appClient.sendCommand("getDrawingLayerStats", { limit: args.limit ?? 60 })) },
    style_set: { action: "style_set", inputSchema: StyleSetArgs, responseSchema: GenericResponseSchema, capabilities: ["edit"], requiresActiveDrawing: true, safeForRetry: false, pluginMethods: ["setObjectStyle"], execute: async (args) => await withApplicationConnection(async (appClient) => await appClient.sendCommand("setObjectStyle", { objectType: args.objectType, name: args.name, style: args.style, alignmentName: args.alignmentName, pipeStyle: args.pipeStyle, structureStyle: args.structureStyle, namePrefix: args.namePrefix, codeSetStyle: args.codeSetStyle, partFamily: args.partFamily, layer: args.layer, createLayer: args.createLayer ?? false })) },
    entities_list: { action: "entities_list", inputSchema: EntitiesListArgs, responseSchema: GenericResponseSchema, capabilities: ["query", "inspect"], requiresActiveDrawing: true, safeForRetry: true, pluginMethods: ["listEntities"], execute: async (args) => await withApplicationConnection(async (appClient) => await appClient.sendCommand("listEntities", { layer: args.layer, layerContains: args.layerContains, types: args.types, limit: args.limit ?? 200, maxVertices: args.maxVertices ?? 500, densifyArcs: args.densifyArcs, layout: args.layout, bbox: args.bbox, idPrefix: args.idPrefix, offset: args.offset })) },
    entities_layer_set: { action: "entities_layer_set", inputSchema: EntitiesLayerSetArgs, responseSchema: GenericResponseSchema, capabilities: ["edit"], requiresActiveDrawing: true, safeForRetry: false, pluginMethods: ["setEntitiesLayer"], execute: async (args) => await withApplicationConnection(async (appClient) => await appClient.sendCommand("setEntitiesLayer", { handles: args.handles, layer: args.layer, createLayer: args.createLayer ?? false })) },
    scale_get: { action: "scale_get", inputSchema: ScaleGetArgs, responseSchema: GenericResponseSchema, capabilities: ["query", "inspect"], requiresActiveDrawing: true, safeForRetry: true, pluginMethods: ["getScale"], execute: async () => await withApplicationConnection(async (appClient) => await appClient.sendCommand("getScale", {})) },
    scale_set: { action: "scale_set", inputSchema: ScaleSetArgs, responseSchema: GenericResponseSchema, capabilities: ["edit"], requiresActiveDrawing: true, safeForRetry: false, pluginMethods: ["setScale"], execute: async (args) => await withApplicationConnection(async (appClient) => await appClient.sendCommand("setScale", { scale: args.scale })) },
    corridor_feature_lines_set: { action: "corridor_feature_lines_set", inputSchema: CorridorFeatureLinesSetArgs, responseSchema: GenericResponseSchema, capabilities: ["edit"], requiresActiveDrawing: true, safeForRetry: false, pluginMethods: ["setCorridorFeatureLines"], execute: async (args) => await withApplicationConnection(async (appClient) => await appClient.sendCommand("setCorridorFeatureLines", { name: args.name, connected: args.connected, codes: args.codes, rebuild: args.rebuild ?? true })) },
    layout_list: { action: "layout_list", inputSchema: LayoutListArgs, responseSchema: GenericResponseSchema, capabilities: ["query", "inspect"], requiresActiveDrawing: true, safeForRetry: true, pluginMethods: ["listLayouts"], execute: async (args) => await withApplicationConnection(async (appClient) => await appClient.sendCommand("listLayouts", { layout: args.layout })) },
    block_attributes_get: { action: "block_attributes_get", inputSchema: BlockAttributesGetArgs, responseSchema: GenericResponseSchema, capabilities: ["query", "inspect"], requiresActiveDrawing: true, safeForRetry: true, pluginMethods: ["getBlockAttributes"], execute: async (args) => await withApplicationConnection(async (appClient) => await appClient.sendCommand("getBlockAttributes", { layout: args.layout, blockName: args.blockName })) },
    block_attributes_set: { action: "block_attributes_set", inputSchema: BlockAttributesSetArgs, responseSchema: GenericResponseSchema, capabilities: ["edit"], requiresActiveDrawing: true, safeForRetry: false, pluginMethods: ["setBlockAttributes"], execute: async (args) => await withApplicationConnection(async (appClient) => await appClient.sendCommand("setBlockAttributes", { layout: args.layout, blockName: args.blockName, values: args.values, positions: args.positions, heights: args.heights })) },
    view_png: { action: "view_png", inputSchema: DrawingViewPngArgs, responseSchema: GenericResponseSchema, capabilities: ["query", "inspect"], requiresActiveDrawing: true, safeForRetry: true, pluginMethods: ["plotViewPng"], execute: async (args) => await withApplicationConnection(async (appClient) => await appClient.sendCommand("plotViewPng", { output: args.output, overwrite: args.overwrite ?? false, profileViewName: args.profileViewName, x: args.x, y: args.y, width: args.width, height: args.height, margin: args.margin, media: args.media, plotStyleTable: args.plotStyleTable })) },
    layout_text_add: { action: "layout_text_add", inputSchema: LayoutTextAddArgs, responseSchema: GenericResponseSchema, capabilities: ["create"], requiresActiveDrawing: true, safeForRetry: false, pluginMethods: ["addLayoutText"], execute: async (args) => await withApplicationConnection(async (appClient) => await appClient.sendCommand("addLayoutText", { layout: args.layout, text: args.text, x: args.x, y: args.y, height: args.height ?? 2.5, width: args.width ?? 0, layer: args.layer, rotationDegrees: args.rotationDegrees, textStyle: args.textStyle })) },
    layer_list: { action: "layer_list", inputSchema: LayerListArgs, responseSchema: GenericResponseSchema, capabilities: ["query", "inspect"], requiresActiveDrawing: true, safeForRetry: true, pluginMethods: ["listLayers"], execute: async (args) => await withApplicationConnection(async (appClient) => await appClient.sendCommand("listLayers", { contains: args.contains })) },
    layer_set: { action: "layer_set", inputSchema: LayerSetArgs, responseSchema: GenericResponseSchema, capabilities: ["edit"], requiresActiveDrawing: true, safeForRetry: false, pluginMethods: ["setLayers"], execute: async (args) => await withApplicationConnection(async (appClient) => await appClient.sendCommand("setLayers", { layers: args.layers, on: args.on, frozen: args.frozen, color: args.color, linetype: args.linetype, lineweight: args.lineweight, plot: args.plot })) },
    entities_style_set: { action: "entities_style_set", inputSchema: EntitiesStyleSetArgs, responseSchema: GenericResponseSchema, capabilities: ["edit"], requiresActiveDrawing: true, safeForRetry: false, pluginMethods: ["setEntitiesStyle"], execute: forward("setEntitiesStyle") },
    linetypes_list: { action: "linetypes_list", inputSchema: LinetypesListArgs, responseSchema: GenericResponseSchema, capabilities: ["query", "inspect"], requiresActiveDrawing: true, safeForRetry: true, pluginMethods: ["listLinetypes"], execute: forward("listLinetypes") },
    // C37–C41: sheet production (multileaders, erase, blocks, layouts/viewports, system variables, layout plots).
    mleader_add: { action: "mleader_add", inputSchema: MLeaderAddArgs, responseSchema: GenericResponseSchema, capabilities: ["create"], requiresActiveDrawing: true, safeForRetry: false, pluginMethods: ["addMLeader"], execute: forward("addMLeader", { landing: true, frame: false, createLayer: false }) },
    entities_erase: { action: "entities_erase", inputSchema: EntitiesEraseArgs, responseSchema: GenericResponseSchema, capabilities: ["delete"], requiresActiveDrawing: true, safeForRetry: false, pluginMethods: ["eraseEntities"], execute: forward("eraseEntities") },
    block_list_external: { action: "block_list_external", inputSchema: BlockListExternalArgs, responseSchema: GenericResponseSchema, capabilities: ["query", "inspect"], requiresActiveDrawing: true, safeForRetry: true, pluginMethods: ["listExternalBlocks"], execute: forward("listExternalBlocks") },
    block_import: { action: "block_import", inputSchema: BlockImportArgs, responseSchema: GenericResponseSchema, capabilities: ["import"], requiresActiveDrawing: true, safeForRetry: false, pluginMethods: ["importBlocks"], execute: forward("importBlocks", { overwrite: false }) },
    block_insert: { action: "block_insert", inputSchema: BlockInsertArgs, responseSchema: GenericResponseSchema, capabilities: ["create"], requiresActiveDrawing: true, safeForRetry: false, pluginMethods: ["insertBlock"], execute: forward("insertBlock", { createLayer: false }) },
    layout_create: { action: "layout_create", inputSchema: LayoutCreateArgs, responseSchema: GenericResponseSchema, capabilities: ["create"], requiresActiveDrawing: true, safeForRetry: false, pluginMethods: ["createLayout"], execute: forward("createLayout") },
    layout_delete: { action: "layout_delete", inputSchema: LayoutDeleteArgs, responseSchema: GenericResponseSchema, capabilities: ["delete"], requiresActiveDrawing: true, safeForRetry: false, pluginMethods: ["deleteLayout"], execute: forward("deleteLayout") },
    viewport_create: { action: "viewport_create", inputSchema: ViewportCreateArgs, responseSchema: GenericResponseSchema, capabilities: ["create"], requiresActiveDrawing: true, safeForRetry: false, pluginMethods: ["createViewport"], execute: forward("createViewport", { modelUnitsInMm: 1000, createLayer: false }) },
    viewport_set: { action: "viewport_set", inputSchema: ViewportSetArgs, responseSchema: GenericResponseSchema, capabilities: ["edit"], requiresActiveDrawing: true, safeForRetry: false, pluginMethods: ["setViewport"], execute: forward("setViewport", { modelUnitsInMm: 1000, createLayer: false }) },
    viewport_erase: { action: "viewport_erase", inputSchema: ViewportEraseArgs, responseSchema: GenericResponseSchema, capabilities: ["delete"], requiresActiveDrawing: true, safeForRetry: false, pluginMethods: ["eraseViewport"], execute: forward("eraseViewport") },
    sysvar_get: { action: "sysvar_get", inputSchema: SysvarGetArgs, responseSchema: GenericResponseSchema, capabilities: ["query", "inspect"], requiresActiveDrawing: true, safeForRetry: true, pluginMethods: ["getSystemVariable"], execute: forward("getSystemVariable") },
    sysvar_set: { action: "sysvar_set", inputSchema: SysvarSetArgs, responseSchema: GenericResponseSchema, capabilities: ["edit"], requiresActiveDrawing: true, safeForRetry: false, pluginMethods: ["setSystemVariable"], execute: forward("setSystemVariable") },
    layout_plot: { action: "layout_plot", inputSchema: LayoutPlotArgs, responseSchema: GenericResponseSchema, capabilities: ["query", "inspect"], requiresActiveDrawing: true, safeForRetry: true, pluginMethods: ["plotLayout"], execute: forward("plotLayout", { overwrite: false }) },
    // C67: text styles (fonts with Swedish characters and the en dash for the title block).
    textstyles_list: { action: "textstyles_list", inputSchema: TextStylesListArgs, responseSchema: GenericResponseSchema, capabilities: ["query", "inspect"], requiresActiveDrawing: true, safeForRetry: true, pluginMethods: ["listTextStyles"], execute: forward("listTextStyles") },
    textstyle_set: { action: "textstyle_set", inputSchema: TextStyleSetArgs, responseSchema: GenericResponseSchema, capabilities: ["edit"], requiresActiveDrawing: true, safeForRetry: false, pluginMethods: ["setTextStyle"], execute: forward("setTextStyle") },
    // C68: block definitions (title block attributes into their fields, texts corrected, references synced).
    block_definition_get: { action: "block_definition_get", inputSchema: BlockDefinitionGetArgs, responseSchema: GenericResponseSchema, capabilities: ["query", "inspect"], requiresActiveDrawing: true, safeForRetry: true, pluginMethods: ["getBlockDefinition"], execute: forward("getBlockDefinition") },
    block_definition_set: { action: "block_definition_set", inputSchema: BlockDefinitionSetArgs, responseSchema: GenericResponseSchema, capabilities: ["edit"], requiresActiveDrawing: true, safeForRetry: false, pluginMethods: ["setBlockDefinition"], execute: forward("setBlockDefinition", { syncReferences: true }) },
    // C69: page setups (plot templates per paper size, black-and-white and colour, full and half scale).
    plot_media_list: { action: "plot_media_list", inputSchema: PlotMediaListArgs, responseSchema: GenericResponseSchema, capabilities: ["query", "inspect"], requiresActiveDrawing: true, safeForRetry: true, pluginMethods: ["listPlotMedia"], execute: forward("listPlotMedia") },
    page_setups_list: { action: "page_setups_list", inputSchema: PageSetupsListArgs, responseSchema: GenericResponseSchema, capabilities: ["query", "inspect"], requiresActiveDrawing: true, safeForRetry: true, pluginMethods: ["listPageSetups"], execute: forward("listPageSetups") },
    page_setup_set: { action: "page_setup_set", inputSchema: PageSetupSetArgs, responseSchema: GenericResponseSchema, capabilities: ["edit"], requiresActiveDrawing: true, safeForRetry: false, pluginMethods: ["setPageSetup"], execute: forward("setPageSetup") },
    page_setup_apply: { action: "page_setup_apply", inputSchema: PageSetupApplyArgs, responseSchema: GenericResponseSchema, capabilities: ["edit"], requiresActiveDrawing: true, safeForRetry: false, pluginMethods: ["applyPageSetup"], execute: forward("applyPageSetup") },
    plugin_reload: { action: "plugin_reload", inputSchema: PluginReloadArgs, responseSchema: GenericResponseSchema, capabilities: ["manage"], requiresActiveDrawing: false, safeForRetry: false, pluginMethods: ["reloadPlugin"], execute: forward("reloadPlugin") },
    object_classes_list: { action: "object_classes_list", inputSchema: ObjectClassesListArgs, responseSchema: GenericResponseSchema, capabilities: ["query", "inspect"], requiresActiveDrawing: true, safeForRetry: true, pluginMethods: ["listObjectClasses"], execute: forward("listObjectClasses") },
    modules_list: { action: "modules_list", inputSchema: ModulesListArgs, responseSchema: GenericResponseSchema, capabilities: ["query", "inspect"], requiresActiveDrawing: false, safeForRetry: true, pluginMethods: ["listLoadedModules"], execute: forward("listLoadedModules") },
    save_test: { action: "save_test", inputSchema: SaveTestArgs, responseSchema: GenericResponseSchema, capabilities: ["query", "inspect"], requiresActiveDrawing: true, safeForRetry: true, pluginMethods: ["saveTest"], execute: forward("saveTest") },
    block_reference_get: { action: "block_reference_get", inputSchema: BlockReferenceGetArgs, responseSchema: GenericResponseSchema, capabilities: ["query", "inspect"], requiresActiveDrawing: true, safeForRetry: true, pluginMethods: ["getBlockReference"], execute: forward("getBlockReference") },
    block_reference_set: { action: "block_reference_set", inputSchema: BlockReferenceSetArgs, responseSchema: GenericResponseSchema, capabilities: ["edit"], requiresActiveDrawing: true, safeForRetry: false, pluginMethods: ["setBlockReference"], execute: forward("setBlockReference") },
    settings_ambient_get: { action: "settings_ambient_get", inputSchema: SettingsAmbientGetArgs, responseSchema: GenericResponseSchema, capabilities: ["query", "inspect"], requiresActiveDrawing: true, safeForRetry: true, pluginMethods: ["getAmbientSettings"], execute: forward("getAmbientSettings") },
    settings_ambient_set: { action: "settings_ambient_set", inputSchema: SettingsAmbientSetArgs, responseSchema: GenericResponseSchema, capabilities: ["edit"], requiresActiveDrawing: true, safeForRetry: false, pluginMethods: ["setAmbientSettings"], execute: forward("setAmbientSettings") },
    band_set_set: { action: "band_set_set", inputSchema: BandSetSetArgs, responseSchema: GenericResponseSchema, capabilities: ["edit"], requiresActiveDrawing: true, safeForRetry: false, pluginMethods: ["setBandSet"], execute: forward("setBandSet") },
    band_sets_list: { action: "band_sets_list", inputSchema: BandSetsListArgs, responseSchema: GenericResponseSchema, capabilities: ["query", "inspect"], requiresActiveDrawing: true, safeForRetry: true, pluginMethods: ["listBandSets"], execute: forward("listBandSets") },
    block_entities_edit: { action: "block_entities_edit", inputSchema: BlockEntitiesEditArgs, responseSchema: GenericResponseSchema, capabilities: ["edit"], requiresActiveDrawing: true, safeForRetry: false, pluginMethods: ["editBlockEntities"], execute: forward("editBlockEntities") },
    block_references_replace: { action: "block_references_replace", inputSchema: BlockReferencesReplaceArgs, responseSchema: GenericResponseSchema, capabilities: ["edit"], requiresActiveDrawing: true, safeForRetry: false, pluginMethods: ["replaceBlockReferences"], execute: forward("replaceBlockReferences") },
    block_entities_color_set: { action: "block_entities_color_set", inputSchema: BlockEntitiesColorSetArgs, responseSchema: GenericResponseSchema, capabilities: ["edit"], requiresActiveDrawing: true, safeForRetry: false, pluginMethods: ["setBlockEntitiesColor"], execute: forward("setBlockEntitiesColor") },
    mleader_style_set: { action: "mleader_style_set", inputSchema: MLeaderStyleSetArgs, responseSchema: GenericResponseSchema, capabilities: ["edit"], requiresActiveDrawing: true, safeForRetry: false, pluginMethods: ["setMLeaderStyle"], execute: forward("setMLeaderStyle") },
    blocks_purge: { action: "blocks_purge", inputSchema: BlocksPurgeArgs, responseSchema: GenericResponseSchema, capabilities: ["delete"], requiresActiveDrawing: true, safeForRetry: false, pluginMethods: ["purgeBlocks"], execute: forward("purgeBlocks") },
    styles_list_external: { action: "styles_list_external", inputSchema: StylesListExternalArgs, responseSchema: GenericResponseSchema, capabilities: ["query", "inspect"], requiresActiveDrawing: true, safeForRetry: true, pluginMethods: ["listExternalStyles"], execute: forward("listExternalStyles") },
    styles_import: { action: "styles_import", inputSchema: StylesImportArgs, responseSchema: GenericResponseSchema, capabilities: ["import"], requiresActiveDrawing: true, safeForRetry: false, pluginMethods: ["importStyles"], execute: forward("importStyles") },
    solids_history_remove: { action: "solids_history_remove", inputSchema: SolidsHistoryRemoveArgs, responseSchema: GenericResponseSchema, capabilities: ["edit"], requiresActiveDrawing: true, safeForRetry: false, pluginMethods: ["replaceSolidsWithoutHistory"], execute: forward("replaceSolidsWithoutHistory") },
    // C88: layer standard from CSV, linetypes from .lin, Civil object layers, layer states.
    layers_import: { action: "layers_import", inputSchema: LayersImportArgs, responseSchema: GenericResponseSchema, capabilities: ["import"], requiresActiveDrawing: true, safeForRetry: false, pluginMethods: ["layersImport"], execute: forward("layersImport") },
    linetypes_load: { action: "linetypes_load", inputSchema: LinetypesLoadArgs, responseSchema: GenericResponseSchema, capabilities: ["import"], requiresActiveDrawing: true, safeForRetry: false, pluginMethods: ["linetypesLoad"], execute: forward("linetypesLoad") },
    object_layers_get: { action: "object_layers_get", inputSchema: ObjectLayersGetArgs, responseSchema: GenericResponseSchema, capabilities: ["query", "inspect"], requiresActiveDrawing: true, safeForRetry: true, pluginMethods: ["objectLayersGet"], execute: forward("objectLayersGet") },
    object_layers_set: { action: "object_layers_set", inputSchema: ObjectLayersSetArgs, responseSchema: GenericResponseSchema, capabilities: ["edit"], requiresActiveDrawing: true, safeForRetry: false, pluginMethods: ["objectLayersSet"], execute: forward("objectLayersSet") },
    layer_states_list: { action: "layer_states_list", inputSchema: LayerStatesListArgs, responseSchema: GenericResponseSchema, capabilities: ["query", "inspect"], requiresActiveDrawing: true, safeForRetry: true, pluginMethods: ["layerStatesList"], execute: forward("layerStatesList") },
    layer_state_save: { action: "layer_state_save", inputSchema: LayerStateSaveArgs, responseSchema: GenericResponseSchema, capabilities: ["create"], requiresActiveDrawing: true, safeForRetry: false, pluginMethods: ["layerStateSave"], execute: forward("layerStateSave") },
    layer_state_restore: { action: "layer_state_restore", inputSchema: LayerStateRestoreArgs, responseSchema: GenericResponseSchema, capabilities: ["edit"], requiresActiveDrawing: true, safeForRetry: false, pluginMethods: ["layerStateRestore"], execute: forward("layerStateRestore") },
    layer_state_delete: { action: "layer_state_delete", inputSchema: LayerStateDeleteArgs, responseSchema: GenericResponseSchema, capabilities: ["delete"], requiresActiveDrawing: true, safeForRetry: false, pluginMethods: ["layerStateDelete"], execute: forward("layerStateDelete") },
    layers_purge: { action: "layers_purge", inputSchema: LayersPurgeArgs, responseSchema: GenericResponseSchema, capabilities: ["delete"], requiresActiveDrawing: true, safeForRetry: false, pluginMethods: ["layersPurge"], execute: forward("layersPurge") },
    // C90: label style texts, profile view axes and band label texts (stations as 0/000).
    label_styles_list: { action: "label_styles_list", inputSchema: LabelStylesListArgs, responseSchema: GenericResponseSchema, capabilities: ["query", "inspect"], requiresActiveDrawing: true, safeForRetry: true, pluginMethods: ["labelStylesList"], execute: forward("labelStylesList") },
    label_style_text_get: { action: "label_style_text_get", inputSchema: LabelStyleTextGetArgs, responseSchema: GenericResponseSchema, capabilities: ["query", "inspect"], requiresActiveDrawing: true, safeForRetry: true, pluginMethods: ["labelStyleTextGet"], execute: forward("labelStyleTextGet") },
    label_style_text_set: { action: "label_style_text_set", inputSchema: LabelStyleTextSetArgs, responseSchema: GenericResponseSchema, capabilities: ["edit"], requiresActiveDrawing: true, safeForRetry: false, pluginMethods: ["labelStyleTextSet"], execute: forward("labelStyleTextSet") },
    label_style_copy: { action: "label_style_copy", inputSchema: LabelStyleCopyArgs, responseSchema: GenericResponseSchema, capabilities: ["create"], requiresActiveDrawing: true, safeForRetry: false, pluginMethods: ["labelStyleCopy"], execute: forward("labelStyleCopy") },
    profile_view_axis_get: { action: "profile_view_axis_get", inputSchema: ProfileViewAxisGetArgs, responseSchema: GenericResponseSchema, capabilities: ["query", "inspect"], requiresActiveDrawing: true, safeForRetry: true, pluginMethods: ["profileViewAxisGet"], execute: forward("profileViewAxisGet") },
    profile_view_axis_set: { action: "profile_view_axis_set", inputSchema: ProfileViewAxisSetArgs, responseSchema: GenericResponseSchema, capabilities: ["edit"], requiresActiveDrawing: true, safeForRetry: false, pluginMethods: ["profileViewAxisSet"], execute: forward("profileViewAxisSet") },
    band_style_text_set: { action: "band_style_text_set", inputSchema: BandStyleTextSetArgs, responseSchema: GenericResponseSchema, capabilities: ["edit"], requiresActiveDrawing: true, safeForRetry: false, pluginMethods: ["bandStyleTextSet"], execute: forward("bandStyleTextSet") },
    // C98b: label sets and a label style's general properties (plan readability, orientation reference, ...).
    label_sets_list: { action: "label_sets_list", inputSchema: LabelSetsListArgs, responseSchema: GenericResponseSchema, capabilities: ["query", "inspect"], requiresActiveDrawing: true, safeForRetry: true, pluginMethods: ["labelSetsList"], execute: forward("labelSetsList") },
    label_style_properties_get: { action: "label_style_properties_get", inputSchema: LabelStylePropertiesGetArgs, responseSchema: GenericResponseSchema, capabilities: ["query", "inspect"], requiresActiveDrawing: true, safeForRetry: true, pluginMethods: ["labelStylePropertiesGet"], execute: forward("labelStylePropertiesGet") },
    label_style_properties_set: { action: "label_style_properties_set", inputSchema: LabelStylePropertiesSetArgs, responseSchema: GenericResponseSchema, capabilities: ["edit"], requiresActiveDrawing: true, safeForRetry: false, pluginMethods: ["labelStylePropertiesSet"], execute: forward("labelStylePropertiesSet") },
    // C98c: label expressions (e.g. an outer diameter for the pipe network bands); deleting one still in use needs force.
    label_expressions_list: { action: "label_expressions_list", inputSchema: LabelExpressionsListArgs, responseSchema: GenericResponseSchema, capabilities: ["query", "inspect"], requiresActiveDrawing: true, safeForRetry: true, pluginMethods: ["labelExpressionsList"], execute: forward("labelExpressionsList") },
    label_expression_set: { action: "label_expression_set", inputSchema: LabelExpressionSetArgs, responseSchema: GenericResponseSchema, capabilities: ["create", "edit"], requiresActiveDrawing: true, safeForRetry: false, pluginMethods: ["labelExpressionSet"], execute: forward("labelExpressionSet") },
    label_expression_delete: { action: "label_expression_delete", inputSchema: LabelExpressionDeleteArgs, responseSchema: GenericResponseSchema, capabilities: ["delete"], requiresActiveDrawing: true, safeForRetry: false, pluginMethods: ["labelExpressionDelete"], execute: forward("labelExpressionDelete") },
    // C98e: display components and marker styles of a style (profile style lines that do not plot).
    style_display_get: { action: "style_display_get", inputSchema: StyleDisplayGetArgs, responseSchema: GenericResponseSchema, capabilities: ["query", "inspect"], requiresActiveDrawing: true, safeForRetry: true, pluginMethods: ["styleDisplayGet"], execute: forward("styleDisplayGet") },
    style_display_set: { action: "style_display_set", inputSchema: StyleDisplaySetArgs, responseSchema: GenericResponseSchema, capabilities: ["edit"], requiresActiveDrawing: true, safeForRetry: false, pluginMethods: ["styleDisplaySet"], execute: forward("styleDisplaySet") },
    // C91: feature and command settings (defaults for new profile views, networks, parts lists, name templates).
    civil_settings_list: { action: "civil_settings_list", inputSchema: CivilSettingsListArgs, responseSchema: GenericResponseSchema, capabilities: ["query", "inspect"], requiresActiveDrawing: true, safeForRetry: true, pluginMethods: ["civilSettingsList"], execute: forward("civilSettingsList") },
    civil_settings_get: { action: "civil_settings_get", inputSchema: CivilSettingsGetArgs, responseSchema: GenericResponseSchema, capabilities: ["query", "inspect"], requiresActiveDrawing: true, safeForRetry: true, pluginMethods: ["civilSettingsGet"], execute: forward("civilSettingsGet") },
    civil_settings_set: { action: "civil_settings_set", inputSchema: CivilSettingsSetArgs, responseSchema: GenericResponseSchema, capabilities: ["edit"], requiresActiveDrawing: true, safeForRetry: false, pluginMethods: ["civilSettingsSet"], execute: forward("civilSettingsSet") },
    // C92: Hv/Vv text for structure labels (<[Description(CP)]>).
    structure_bend_angles: { action: "structure_bend_angles", inputSchema: StructureBendAnglesArgs, responseSchema: GenericResponseSchema, capabilities: ["edit"], requiresActiveDrawing: true, safeForRetry: false, pluginMethods: ["structureBendAngles"], execute: forward("structureBendAngles") },
    // C93: open drawings. Opening and activating write nothing (and later writes need approval for the drawing then active);
    // closing discards unsaved changes or saves, so it needs approval.
    documents_list: { action: "documents_list", inputSchema: DocumentsListArgs, responseSchema: GenericResponseSchema, capabilities: ["query", "inspect"], requiresActiveDrawing: false, safeForRetry: true, pluginMethods: ["documentsList"], execute: forward("documentsList") },
    document_open: { action: "document_open", inputSchema: DocumentOpenArgs, responseSchema: GenericResponseSchema, capabilities: ["manage"], requiresActiveDrawing: false, safeForRetry: true, pluginMethods: ["documentOpen"], execute: forward("documentOpen", { readOnly: false, activate: true }) },
    document_activate: { action: "document_activate", inputSchema: DocumentActivateArgs, responseSchema: GenericResponseSchema, capabilities: ["manage"], requiresActiveDrawing: false, safeForRetry: true, pluginMethods: ["documentActivate"], execute: forward("documentActivate") },
    document_close: { action: "document_close", inputSchema: DocumentCloseArgs, responseSchema: GenericResponseSchema, capabilities: ["manage"], requiresActiveDrawing: false, safeForRetry: false, pluginMethods: ["documentClose"], execute: forward("documentClose", { save: false, discard: false }) },
    // C94: writes a new drawing file (export), so it needs approval; runs in the active drawing's command context.
    drawing_transform: { action: "drawing_transform", inputSchema: DrawingTransformArgs, responseSchema: GenericResponseSchema, capabilities: ["create", "export"], requiresActiveDrawing: true, safeForRetry: false, pluginMethods: ["drawingTransform"], execute: forward("drawingTransform", { overwrite: false }) },
    // C95: drawing references; attaching changes the drawing (approval).
    xrefs_list: { action: "xrefs_list", inputSchema: XrefsListArgs, responseSchema: GenericResponseSchema, capabilities: ["query", "inspect"], requiresActiveDrawing: true, safeForRetry: true, pluginMethods: ["xrefsList"], execute: forward("xrefsList") },
    xref_attach: { action: "xref_attach", inputSchema: XrefAttachArgs, responseSchema: GenericResponseSchema, capabilities: ["create", "import"], requiresActiveDrawing: true, safeForRetry: false, pluginMethods: ["xrefAttach"], execute: forward("xrefAttach", { overlay: true, layer: "XREF", relativePath: true }) },
    // C97: detaching erases the reference (approval, delete); unloading and reloading change what the drawing shows (approval).
    xref_detach: { action: "xref_detach", inputSchema: XrefByName("xref_detach"), responseSchema: GenericResponseSchema, capabilities: ["delete"], requiresActiveDrawing: true, safeForRetry: false, pluginMethods: ["xrefDetach"], execute: forward("xrefDetach") },
    xref_unload: { action: "xref_unload", inputSchema: XrefByName("xref_unload"), responseSchema: GenericResponseSchema, capabilities: ["edit"], requiresActiveDrawing: true, safeForRetry: false, pluginMethods: ["xrefUnload"], execute: forward("xrefUnload") },
    xref_reload: { action: "xref_reload", inputSchema: XrefByName("xref_reload"), responseSchema: GenericResponseSchema, capabilities: ["edit"], requiresActiveDrawing: true, safeForRetry: false, pluginMethods: ["xrefReload"], execute: forward("xrefReload") },
    // C89: coordinate crosses in paper space, north arrow by the viewport's twist.
    grid_crosses_add: { action: "grid_crosses_add", inputSchema: GridCrossesAddArgs, responseSchema: GenericResponseSchema, capabilities: ["create"], requiresActiveDrawing: true, safeForRetry: false, pluginMethods: ["gridCrossesAdd"], execute: forward("gridCrossesAdd") },
    north_arrow_align: { action: "north_arrow_align", inputSchema: NorthArrowAlignArgs, responseSchema: GenericResponseSchema, capabilities: ["edit"], requiresActiveDrawing: true, safeForRetry: false, pluginMethods: ["northArrowAlign"], execute: forward("northArrowAlign") },
    // C105/C114: script ids in XData and moving objects by handle change the drawing (approval).
    entities_id_set: { action: "entities_id_set", inputSchema: EntitiesIdSetArgs, responseSchema: GenericResponseSchema, capabilities: ["edit"], requiresActiveDrawing: true, safeForRetry: false, pluginMethods: ["entityIdsSet"], execute: forward("entityIdsSet") },
    entities_id_clear: { action: "entities_id_clear", inputSchema: EntitiesIdClearArgs, responseSchema: GenericResponseSchema, capabilities: ["edit"], requiresActiveDrawing: true, safeForRetry: false, pluginMethods: ["entityIdsClear"], execute: forward("entityIdsClear") },
    entities_move: { action: "entities_move", inputSchema: EntitiesMoveArgs, responseSchema: GenericResponseSchema, capabilities: ["edit"], requiresActiveDrawing: true, safeForRetry: false, pluginMethods: ["moveEntities"], execute: forward("moveEntities") },
    // C117: masking multileaders changes the drawing (approval).
    mleader_mask_set: { action: "mleader_mask_set", inputSchema: MLeaderMaskSetArgs, responseSchema: GenericResponseSchema, capabilities: ["edit"], requiresActiveDrawing: true, safeForRetry: false, pluginMethods: ["setMLeaderMask"], execute: forward("setMLeaderMask") },
    // C99h/C109: attaching a raster image changes the drawing (approval); detaching erases its images and definition (approval, delete).
    raster_attach: { action: "raster_attach", inputSchema: RasterAttachArgs, responseSchema: GenericResponseSchema, capabilities: ["create", "import"], requiresActiveDrawing: true, safeForRetry: false, pluginMethods: ["rasterAttach"], execute: forward("rasterAttach") },
    raster_detach: { action: "raster_detach", inputSchema: RasterDetachArgs, responseSchema: GenericResponseSchema, capabilities: ["delete"], requiresActiveDrawing: true, safeForRetry: false, pluginMethods: ["rasterDetach"], execute: forward("rasterDetach") },
  },
  exposures: [
    { toolName: "civil3d_drawing", displayName: "Civil 3D Drawing", description: "Reads drawing state, settings, selection context, and document operations through a single domain tool.", inputShape: { action: z.enum(DRAWING_ACTIONS), templatePath: z.string().optional(), saveAs: z.string().optional(), overwrite: z.boolean().optional(), steps: z.number().int().min(1).max(10).optional(), limit: z.number().optional(), objectType: StyleObjectType.optional(), name: z.string().optional(), style: z.string().optional(), alignmentName: z.string().optional(), pipeStyle: z.string().optional(), structureStyle: z.string().optional(), namePrefix: z.string().optional(), codeSetStyle: z.string().optional(), partFamily: z.string().optional(), layer: z.string().optional(), createLayer: z.boolean().optional(), contains: z.string().optional(), layers: z.array(z.string()).optional(), on: z.boolean().optional(), frozen: z.boolean().optional(), layerContains: z.string().optional(), types: z.array(EntityType).optional(), bbox: z.object({ minX: z.number(), minY: z.number(), maxX: z.number(), maxY: z.number() }).optional().describe("entities_list: only entities whose extents touch this area (model units)."), maxVertices: z.number().optional().describe("entities_list: vertices (and bulges) returned per polyline, default 500."), densifyArcs: z.number().optional().describe("entities_list: largest distance (model units, e.g. 0.005, at least 0.001) between a polyline arc and its chords; adds densified [x,y,z] points following the arcs: every vertex, the whole polyline regardless of maxVertices, at most 20 000 per polyline."), handles: z.array(z.string()).optional(), layout: z.string().optional(), blockName: z.string().optional(),
      // C88c: object_layers_set takes objects per object type; block_attributes_set and settings_ambient_set take strings.
      values: z.record(z.string(), z.union([z.string(), z.number(), z.boolean(), z.record(z.string(), z.unknown())])).optional().describe("block_attributes_set: tag → value; settings_ambient_set: Group.Property → value; object_layers_set: object type → {layer?, modifier? (None|Prefix|Suffix), modifierValue?, locked?}; civil_settings_set: Group.Property → value, a style setting by style name or {collection, name}; label_style_properties_set: Group.Property → value, e.g. {\"PlanReadability.PlanReadable\": true, \"PlanReadability.PlanReadableBias\": 90, \"Behavior.OrientationReference\": \"View\"}."),
      positions: z.record(z.string(), z.tuple([z.number(), z.number()])).optional().describe("block_attributes_set: tag → [x, y] in the layout's units (the insertion point of a BaseLeft attribute, else its alignment point)."), heights: z.record(z.string(), z.number()).optional(), text: z.string().optional(), x: z.number().optional(), y: z.number().optional(), height: z.number().optional(), width: z.number().optional(), connected: z.boolean().optional(), codes: z.array(z.string()).optional(), rebuild: z.boolean().optional(),
      // scale: annotation scale name for scale_set (string), block scale factor for block_insert (number).
      scale: z.union([z.string(), z.number()]).optional(), output: z.string().optional(), profileViewName: z.string().optional(), margin: z.number().optional(), media: z.string().optional(), plotStyleTable: z.string().optional(),
      textX: z.number().optional(), textY: z.number().optional(), textHeight: z.number().optional(), rotationDegrees: z.number().optional(), frame: z.boolean().optional(), backgroundColor: z.number().int().optional(), arrowSize: z.number().optional(), landing: z.boolean().optional(), attachment: z.enum(["left", "right"]).optional(),
      sourcePath: z.string().optional(), blockNames: z.array(z.string()).optional(), attributes: z.record(z.string(), z.string()).optional(),
      // z: insertion height for block_insert (number), height transformation for drawing_transform ({scale, offset}).
      z: z.union([z.number(), HeightTransform]).optional().describe("block_insert: insertion height; drawing_transform: heights {scale?, offset?}, z' = scale*z + offset (default unchanged)."),
      outputPath: z.string().optional().describe("drawing_transform: new .dwg in the export roots (never the source; overwrite to replace an existing file)."),
      includeCivilObjects: z.boolean().optional().describe("drawing_transform: also transform Civil 3D objects (AECC_*; default false, they are skipped and listed)."),
      transform: z.union([HelmertTransform, AffineTransform]).optional().describe("drawing_transform: {a, b, tx, ty} (Helmert: x' = a*x - b*y + tx, y' = b*x + a*y + ty) or {m11, m12, m13, m21, m22, m23} (affine)."),
      copyFrom: z.string().optional(), handle: z.string().optional(), centerX: z.number().optional(), centerY: z.number().optional(),
      viewCenterX: z.number().optional().describe("Model X shown at the viewport centre."), viewCenterY: z.number().optional().describe("Model Y shown at the viewport centre."),
      twistDegrees: z.number().optional().describe("Viewport twist: a model direction at angle a (CCW from +X) is drawn at a + twist; -a makes it horizontal."),
      scaleDenominator: z.number().optional().describe("400 for 1:400 (CustomScale = modelUnitsInMm / scaleDenominator)."), modelUnitsInMm: z.number().optional().describe("Millimetres per model unit; 1000 (default) for metres."),
      locked: z.boolean().optional(), frozenLayers: z.array(z.string()).optional(),
      color: z.union([z.number().int(), z.string()]).optional().describe("ACI number; entities also ByLayer/ByBlock."),
      linetype: z.string().optional().describe("Loaded linetype name (linetypes_list)."),
      lineweight: z.union([z.number().int(), z.string()]).optional().describe("Hundredths of a mm (35 = 0.35 mm) or Default; entities also ByLayer/ByBlock."),
      plot: z.boolean().optional(), linetypeScale: z.number().optional(), value: z.union([z.number(), z.string(), z.boolean()]).optional(),
      fileName: z.string().optional().describe("textstyle_set: font file, .shx or .ttf (e.g. isocpeur.ttf)."), typeface: z.string().optional().describe("textstyle_set: TrueType typeface name."), bigFontFileName: z.string().optional(),
      widthFactor: z.number().optional(), obliquingDegrees: z.number().optional(), create: z.boolean().optional(), makeCurrent: z.boolean().optional(),
      attributeDefinitions: z.array(z.record(z.string(), z.unknown())).optional().describe("block_definition_set: [{tag, newTag?, create?, x?, y? (block units), height?, widthFactor?, textStyle?, justify?, prompt?, defaultValue?, invisible?, color? (ACI 0-256 or ByLayer/ByBlock), layer?, multiline? (true makes a multiline MText attribute; cannot be turned off), width? (multiline wrapping width in block units, 0 = no wrapping)}]"),
      removeAttributes: z.array(z.string()).optional(), texts: z.array(z.object({ find: z.string(), replace: z.string() })).optional(), syncReferences: z.boolean().optional(), attsync: z.boolean().optional(),
      includeGeometry: z.boolean().optional(), maxEntities: z.number().optional(),
      device: z.string().optional().describe("plot_media_list/page_setup_set: plotter configuration, default DWG To PDF.pc3."), widthMm: z.number().optional(), heightMm: z.number().optional(),
      plotStyles: z.boolean().optional(), lineweights: z.boolean().optional(), scaleLineweights: z.boolean().optional(), plotTransparency: z.boolean().optional().describe("page_setup_set: plot transparency (layer and object transparency in the plot)."), layoutNames: z.array(z.string()).optional(),
      pageSetup: z.string().optional().describe("layout_plot: named page setup to plot with (PDF)."), plotOriginMm: z.tuple([z.number(), z.number()]).optional(),
      attempts: z.number().optional(), version: z.string().optional(), format: z.string().optional(), eraseDictionaryEntries: z.array(z.string()).optional(),
      eraseClasses: z.array(z.string()).optional(), eraseBlocks: z.array(z.string()).optional(), replaceSolidsInBlocks: z.array(z.string()).optional(),
      clearSolidHistory: z.boolean().optional(), blocks: z.array(z.string()).optional(),
      scaleX: z.number().optional(), scaleY: z.number().optional(), textStyle: z.string().optional(),
      collection: z.string().optional().describe("styles_list_external/styles_import: property path under CivilDocument.Styles, e.g. PipeStyles, PartsListSet, LabelStyles.PipeLabelStyles.PlanProfileLabelStyles; label_styles_list/label_style_*/label_expression*: path under CivilDocument.Styles.LabelStyles, e.g. AlignmentLabelStyles.MajorStationLabelStyles, PipeLabelStyles.PlanProfileLabelStyles; style_display_get/set: path under CivilDocument.Styles, default ProfileStyles."),
      // styles_import items or entities_id_set items; each action checks its own.
      items: z.array(z.union([StyleImportItem, ScriptIdItem])).optional().describe("styles_import: [{collection, names}]; entities_id_set: [{handle, id (at most 255 characters), data? (a string, or JSON stored as its text)}]."),
      conflict: z.enum(["ignore", "rename", "override"]).optional(),
      names: z.array(z.string()).optional().describe("blocks_purge/layers_purge: names to purge; raster_detach: image definition names."), pattern: z.string().optional(), dryRun: z.boolean().optional(),
      landingGap: z.number().optional(), doglegLength: z.number().optional(), annotative: z.boolean().optional(), removeStyles: z.array(z.string()).optional(), bottom: z.array(BandItem).optional(), appendBottom: z.array(BandItem).optional(), fromColor: z.union([z.number(), z.string()]).optional(),
      group: z.string().optional().describe("settings_ambient_get: one ambient settings group, e.g. Station, Elevation, Grade; band_style_text_set: one band group (location), e.g. majorStations."),
      dynamicProperties: z.record(z.string(), z.union([z.string(), z.number(), z.boolean()])).optional().describe("block_reference_set: dynamic block property values by name, e.g. a visibility state."),
      erase: z.array(z.record(z.string(), z.unknown())).optional().describe("block_entities_edit: selectors [{handles?, type?, layer?, text?, textContains?, within?, minLength?, expect?}]."),
      set: z.array(z.record(z.string(), z.unknown())).optional().describe("block_entities_edit: [{select, move?, start?, end?, position?, height?, text?, layer?, color?, linetype?, lineweight?}]."),
      add: z.array(z.record(z.string(), z.unknown())).optional().describe("block_entities_edit: entities [{type: line|polyline|rectangle|circle|text|mtext|insert, …}]."),
      origin: z.tuple([z.number(), z.number()]).optional(), from: z.string().optional(), to: z.string().optional(),
      path: z.string().optional().describe("layers_import: CSV file (absolute path); linetypes_load: .lin file (absolute path or a bare name such as acadiso.lin); document_open: local .dwg/.dwt in the import or export roots; document_activate/document_close: the open drawing by its file (or give name: the document name, file name or file name without extension); xref_attach: local .dwg to reference (name defaults to its file name, layer to XREF); raster_attach: local .png/.jpg/.tif in the import or export roots (name defaults to its file name)."),
      readOnly: z.boolean().optional().describe("document_open: open read-only (default false)."),
      activate: z.boolean().optional().describe("document_open: make it the active drawing (default true); later calls run in the active drawing."),
      save: z.boolean().optional().describe("document_close: save over its own file before closing (default false)."),
      discard: z.boolean().optional().describe("document_close: close without saving although it has unsaved changes (needed then unless save)."),
      overlay: z.boolean().optional().describe("xref_attach: overlay (default true; nested references are not carried on) or attach (false)."),
      relativePath: z.boolean().optional().describe("xref_attach/raster_attach: store the path relative to the drawing folder when possible (default true)."),
      update: z.boolean().optional().describe("layers_import: existing layers get the row's values (default true)."),
      linFile: z.string().optional().describe("layers_import: .lin file for linetypes the drawing lacks."),
      all: z.boolean().optional().describe("linetypes_load: every linetype in the file."),
      replace: z.boolean().optional().describe("linetypes_load: redefine linetypes already in the drawing from the file; grid_crosses_add: erase earlier crosses on the layer inside the viewport first."),
      createLayers: z.boolean().optional().describe("object_layers_set: create missing layers."),
      description: z.string().optional().describe("layer_state_save: layer state description."),
      viewport: z.string().optional().describe("layer_state_save: handle of a layout viewport whose VP freeze is saved with the state; layer_state_restore: viewport to restore into as VP freeze only (the state's off/frozen layers frozen there, its others thawed; global on/off and freeze unchanged)."),
      intervalM: z.number().optional().describe("grid_crosses_add: grid interval in model metres (default 100)."),
      sizeMm: z.number().optional().describe("grid_crosses_add: cross size on the sheet in mm (default 5)."),
      textHeightMm: z.number().optional().describe("grid_crosses_add: label height on the sheet in mm (default 1.8)."),
      labels: z.enum(["XY", "NE"]).optional().describe("grid_crosses_add: XY = \"X <northing>\" / \"Y <easting>\" (Swedish, default) or NE."),
      edgeLabels: z.boolean().optional().describe("grid_crosses_add: label only the outermost crosses (northing on each row's first cross, easting on each column's lowest)."),
      component: z.string().optional().describe("label_style_text_set/band_style_text_set: one text component by name (default all)."),
      contents: z.string().optional().describe("label_style_text_set: the whole new text, field codes included, e.g. <[Station Value(Um|FS|P0|RN|AP|Sn|TP|B3|EN|W0|OLB)]>/<[Station Value(Um|FS|P0|RN|AP|Sn|TP|B3|EN|W0|ORB)]>."),
      find: z.string().optional().describe("label_style_text_set/band_style_text_set: text to replace (a .NET regular expression with regex: true)."),
      replaceWith: z.string().optional().describe("label_style_text_set/band_style_text_set: replacement for find (may be empty; $1 etc. with regex)."),
      regex: z.boolean().optional().describe("label_style_text_set/band_style_text_set: find is a regular expression."),
      newName: z.string().optional().describe("label_style_copy: name of the copy."),
      axis: AxisName.optional().describe("profile_view_axis_get/set: Bottom, Top, Left or Right."), tick: TickName.optional().describe("profile_view_axis_set: Major or Minor tick style."),
      labelText: z.string().optional().describe("profile_view_axis_set: tick label text with field codes."),
      interval: z.number().optional().describe("profile_view_axis_set: tick interval in drawing units (stations along Top/Bottom, elevations along Left/Right)."),
      bandType: BandStyleKind.optional().describe("band_style_text_set: band style type."),
      kind: LabelSetKind.optional().describe("label_sets_list: Alignment (default), Profile or Section label sets."),
      expression: z.string().optional().describe("label_expression_set: the expression, label properties in braces in drawing units (metres, angles in radians), e.g. ({Inner Pipe Diameter}+2*{Wall Thickness})*1000."),
      formatResultAs: ExpressionFormat.optional().describe("label_expression_set: how the result is formatted (Double = plain number, Dimension/Distance/Elevation = a length in drawing units the field code converts; new expressions: Double)."),
      force: z.boolean().optional().describe("label_expression_delete: delete although label texts or pipe network band labels use the expression."),
      display: z.record(z.string(), z.record(z.string(), z.record(z.string(), z.union([z.string(), z.number(), z.boolean()])))).optional().describe("style_display_set: {view: {component: {visible?, layer?, color? (ACI, ByLayer, ByBlock or r,g,b), linetype?, linetypeScale?, lineweight? (hundredths of a mm or ByLayer/ByBlock/Default), plotStyle?}}}, e.g. {\"Profile\": {\"Line\": {\"visible\": true}, \"Curve\": {\"visible\": true}}}."),
      markers: z.record(z.string(), z.string()).optional().describe("style_display_set: marker (e.g. VIntersectionPoint, BeginPoint) -> marker style name."),
      settingsType: z.string().optional().describe("civil_settings_get/set: settings type, e.g. SettingsCmdCreateProfileView, SettingsPipeNetwork, SettingsCmdCreateNetwork (Settings prefix optional)."),
      includeAmbient: z.boolean().optional().describe("civil_settings_list/get: include the inherited ambient groups (Station, Elevation, …)."),
      networkName: z.string().optional().describe("structure_bend_angles: pipe network."), structureNames: z.array(z.string()).optional().describe("structure_bend_angles: only these structures."),
      write: z.boolean().optional().describe("structure_bend_angles: write the Hv/Vv text to each structure's Description."),
      decimals: z.number().int().optional().describe("structure_bend_angles: decimals in the text (0-3, default 0)."),
      minHvDeg: z.number().optional().describe("structure_bend_angles: smallest Hv written (default 1)."), minVvDeg: z.number().optional().describe("structure_bend_angles: smallest Vv written (default 1)."),
      keepExisting: z.boolean().optional().describe("structure_bend_angles: keep the rest of the description; the text goes after \" | \"."),
      idPrefix: z.string().optional().describe("entities_list: only objects whose script id (script-id XData) starts with this (\"\" = any id); id and idData are returned."),
      offset: z.number().int().optional().describe("entities_list: skip this many matches (page through more than limit objects); the answer gives matched, returned and offset."),
      dx: z.number().optional().describe("entities_move: displacement along X in drawing units."), dy: z.number().optional().describe("entities_move: displacement along Y."), dz: z.number().optional().describe("entities_move: displacement along Z."), maskScale: z.number().min(1).max(5).optional().describe("mleader_mask_set: mask border scale 1-5 (default 1.25)."),
      ux: z.number().optional().describe("raster_attach: X of the image's bottom edge vector in drawing units (with uy, vx, vy; from a world file)."), uy: z.number().optional().describe("raster_attach: Y of the bottom edge vector."),
      vx: z.number().optional().describe("raster_attach: X of the image's left edge vector."), vy: z.number().optional().describe("raster_attach: Y of the left edge vector."),
      fade: z.number().int().optional().describe("raster_attach: image fade 0-100 (default 0)."),
    }, supportedActions: [...DRAWING_ACTIONS], resolveAction: (rawArgs) => ({ action: String(rawArgs.action ?? ""), args: rawArgs }) },
    { toolName: "get_drawing_info", displayName: "Get Drawing Info", description: "Retrieves basic information about the active Civil 3D drawing.", inputShape: {}, supportedActions: ["info"], resolveAction: () => ({ action: "info", args: { action: "info" } }) },
    { toolName: "get_selected_civil_objects_info", displayName: "Get Selected Civil Objects Info", description: "Gets basic properties of currently selected Civil 3D objects.", inputShape: { limit: z.number().optional() }, supportedActions: ["selected_objects_info"], resolveAction: (rawArgs) => ({ action: "selected_objects_info", args: { action: "selected_objects_info", ...rawArgs } }) },
    { toolName: "list_civil_object_types", displayName: "List Civil Object Types", description: "Lists major Civil 3D object types available in the current context.", inputShape: {}, supportedActions: ["list_object_types"], resolveAction: () => ({ action: "list_object_types", args: { action: "list_object_types" } }) },
  ],
};
