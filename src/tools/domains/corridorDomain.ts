import { z } from "zod";
import { withApplicationConnection } from "../../utils/ConnectionManager.js";
import type { DomainToolDefinition } from "../domainRuntime.js";
import { JobLaunchResponseSchema } from "./sharedSchemas.js";

// ─── Shared schemas ───────────────────────────────────────────────────────────

const GenericCorridorResponseSchema = z.object({}).passthrough();

const CorridorStateSchema = z.enum(["built", "out_of_date", "error"]);

const CorridorSummarySchema = z.object({
  name: z.string(),
  handle: z.string(),
  baselineCount: z.number(),
  regionCount: z.number(),
  surfaceCount: z.number(),
  state: CorridorStateSchema,
  lastBuildTime: z.string().nullable(),
});

const CorridorListResponseSchema = z.object({
  corridors: z.array(CorridorSummarySchema),
});

const CorridorRegionSchema = z.object({
  name: z.string(),
  assemblyName: z.string(),
  startStation: z.number(),
  endStation: z.number(),
  frequency: z.number(),
  frequencyAtCurves: z.number().optional(),
  frequencyAlongProfileCurves: z.number().optional(),
  appliedAtProfileGeometryPoints: z.boolean().optional(),
});

const CorridorBaselineSchema = z.object({
  name: z.string(),
  alignmentName: z.string(),
  profileName: z.string(),
  regions: z.array(CorridorRegionSchema),
});

const CorridorSurfaceSchema = z.object({
  name: z.string(),
  boundaries: z.array(z.string()),
});

const CorridorDetailResponseSchema = z.object({
  name: z.string(),
  handle: z.string(),
  style: z.string(),
  layer: z.string(),
  baselines: z.array(CorridorBaselineSchema),
  surfaces: z.array(CorridorSurfaceSchema),
  featureLineCount: z.number(),
  state: CorridorStateSchema,
});

const CorridorSurfacesResponseSchema = z.object({
  corridorName: z.string().optional(),
  surfaces: z.array(CorridorSurfaceSchema),
});

const CorridorFeatureLineSchema = z.record(z.unknown());

// with includeGeometry (C60): 3D points per feature line of the main baselines, side by the median offset
const CorridorFeatureLineGeometrySchema = z.object({
  baseline: z.string(),
  code: z.string(),
  side: z.enum(["left", "right", "center"]),
  medianOffset: z.number(),
  startStation: z.number(),
  endStation: z.number(),
  points: z.array(z.array(z.number()).length(3)),
});

const CorridorFeatureLinesResponseSchema = z.object({
  corridorName: z.string().optional(),
  featureLines: z.array(CorridorFeatureLineSchema),
  lines: z.array(CorridorFeatureLineGeometrySchema).optional(),
  diagnostics: z.array(z.object({ baseline: z.string(), code: z.string(), featureLines: z.number(), points: z.number() })).optional(),
});

// The plugin now returns {jobId, state:"running", message} and completes the
// rebuild on a background task. Use the shared job-launch envelope so the
// contract is explicit.
const CorridorRebuildResponseSchema = JobLaunchResponseSchema;

const CorridorVolumesResponseSchema = z.object({
  cutVolume: z.number(),
  fillVolume: z.number(),
  netVolume: z.number(),
  cutArea: z.number().nullable().optional(),
  fillArea: z.number().nullable().optional(),
  units: z.object({
    volume: z.string(),
    area: z.string().optional(),
  }),
});

const CorridorFullSummaryResponseSchema = z.object({
  corridor: CorridorDetailResponseSchema,
  surfaceInventory: CorridorSurfacesResponseSchema,
  volumeAnalysis: CorridorVolumesResponseSchema.nullable(),
  summary: z.object({
    baselineCount: z.number(),
    regionCount: z.number(),
    corridorSurfaceCount: z.number(),
    featureLineCount: z.number(),
    state: CorridorStateSchema,
    totalRegionLength: z.number(),
    selectedCorridorSurface: z.string().nullable(),
    referenceSurface: z.string().nullable(),
    volumeComputationStatus: z.enum(["computed", "skipped"]),
  }),
});

// ─── Per-action input schemas ─────────────────────────────────────────────────

const CorridorListArgsSchema = z.object({
  action: z.literal("list"),
});

const CorridorGetArgsSchema = z.object({
  action: z.literal("get"),
  name: z.string(),
});

const CorridorRebuildArgsSchema = z.object({
  action: z.literal("rebuild"),
  name: z.string(),
});

const CorridorGetSurfacesArgsSchema = z.object({
  action: z.literal("get_surfaces"),
  name: z.string(),
});

const CorridorGetFeatureLinesArgsSchema = z.object({
  action: z.literal("get_feature_lines"),
  name: z.string(),
  includeGeometry: z.boolean().optional(),
  codes: z.array(z.string()).optional(),
  decimals: z.number().int().min(0).max(6).optional(),
});

const CorridorComputeVolumesArgsSchema = z.object({
  action: z.literal("compute_volumes"),
  name: z.string(),
  corridorSurface: z.string(),
  referenceSurface: z.string(),
});

const CorridorSummaryArgsSchema = z.object({
  action: z.literal("summary"),
  name: z.string(),
  corridorSurface: z.string().optional(),
  referenceSurface: z.string().optional(),
});

const CorridorTargetMappingGetArgsSchema = z.object({
  action: z.literal("target_mapping_get"),
  name: z.string(),
  regionIndex: z.number().int().nonnegative().optional(),
  baselineIndex: z.number().int().nonnegative().optional(),
});

// A target row is matched against SubassemblyTargetInfo.LogicalName or
// DisplayName (optionally narrowed to one subassembly) and receives the
// object named targetName as its TargetIds.
const CorridorTargetRowShape = {
  logicalName: z.string().optional(),
  displayName: z.string().optional(),
  subassemblyName: z.string().optional(),
  targetType: z.enum(["surface", "alignment", "profile", "polyline"]),
  targetName: z.string(),
};

const CorridorTargetRowSchema = z.object(CorridorTargetRowShape).refine(
  (row) => Boolean(row.logicalName || row.displayName),
  { message: "Each target needs logicalName or displayName (see target_mapping_get)." },
);

const CorridorTargetScopeSchema = z.enum(["region", "baseline", "corridor"]);

const CorridorTargetMappingSetArgsSchema = z.object({
  action: z.literal("target_mapping_set"),
  name: z.string(),
  scope: CorridorTargetScopeSchema.optional(),
  regionIndex: z.number().int().nonnegative().optional(),
  baselineIndex: z.number().int().nonnegative().optional(),
  targets: z.array(CorridorTargetRowSchema).min(1),
  rebuild: z.boolean().optional(),
});

const CorridorFrequencyShape = {
  frequency: z.number().positive().optional(),
  frequencyAtCurves: z.number().positive().optional(),
  frequencyAlongProfileCurves: z.number().positive().optional(),
  appliedAtProfileGeometryPoints: z.boolean().optional(),
};

const CorridorCreateArgsSchema = z.object({
  action: z.literal("create"),
  name: z.string(),
  alignmentName: z.string(),
  profileName: z.string(),
  assemblyName: z.string(),
  baselineName: z.string().optional(),
  regionName: z.string().optional(),
  startStation: z.number().optional(),
  endStation: z.number().optional(),
  ...CorridorFrequencyShape,
});

const CorridorRegionAddArgsSchema = z.object({
  action: z.literal("region_add"),
  name: z.string(),
  baselineIndex: z.number().int().nonnegative().optional(),
  assemblyName: z.string(),
  regionName: z.string().optional(),
  startStation: z.number(),
  endStation: z.number(),
  ...CorridorFrequencyShape,
  // Not a Civil 3D AppliedAssemblySetting property; rejected instead of ignored.
  frequencyAtKneePoints: z.never().optional(),
  rebuild: z.boolean().optional(),
});

const CorridorRegionEditArgsSchema = z.object({
  action: z.literal("region_edit"),
  name: z.string(),
  baselineIndex: z.number().int().nonnegative().optional(),
  regionIndex: z.number().int().nonnegative(),
  regionName: z.string().optional(),
  assemblyName: z.string().optional(),
  startStation: z.number().optional(),
  endStation: z.number().optional(),
  ...CorridorFrequencyShape,
  rebuild: z.boolean().optional(),
});

const CorridorRegionSplitArgsSchema = z.object({
  action: z.literal("region_split"),
  name: z.string(),
  baselineIndex: z.number().int().nonnegative().optional(),
  regionIndex: z.number().int().nonnegative(),
  station: z.number(),
  rebuild: z.boolean().optional(),
});

const CorridorRegionDeleteArgsSchema = z.object({
  action: z.literal("region_delete"),
  name: z.string(),
  baselineIndex: z.number().int().nonnegative().optional(),
  regionIndex: z.number().int().nonnegative(),
});

const CorridorOverhangSchema = z.enum(["none", "top_links", "bottom_links"]);

const CorridorSurfaceCreateArgsSchema = z.object({
  action: z.literal("surface_create"),
  name: z.string(),
  surfaceName: z.string(),
  linkCodes: z.array(z.string()).min(1),
  addAsBreakline: z.boolean().optional(),
  boundaryFromExtents: z.boolean().optional(),
  overhangCorrection: CorridorOverhangSchema.optional(),
});

const CorridorBoundaryPointSchema = z.object({ x: z.number(), y: z.number() });

const CorridorSurfaceBoundaryAddArgsSchema = z.object({
  action: z.literal("surface_boundary_add"),
  name: z.string(),
  surfaceName: z.string(),
  boundaryName: z.string(),
  boundaryType: z.enum(["outside", "inside"]).optional(),
  points: z.array(CorridorBoundaryPointSchema).min(3),
  replace: z.boolean().optional(),
});

const CorridorDeleteArgsSchema = z.object({
  action: z.literal("delete"),
  name: z.string(),
});

const CorridorQuantitiesArgsSchema = z.object({
  action: z.literal("quantities"),
  name: z.string(),
  baselineIndex: z.number().int().nonnegative().optional(),
  startStation: z.number().optional(),
  endStation: z.number().optional(),
  shapeCodes: z.array(z.string()).optional(),
  bottomCode: z.string().optional(),
  daylightCode: z.string().optional(),
  includeRows: z.boolean().optional(),
});

// ─── Canonical input shape ────────────────────────────────────────────────────

const canonicalCorridorInputShape = {
  action: z.enum([
    "list",
    "get",
    "rebuild",
    "get_surfaces",
    "get_feature_lines",
    "compute_volumes",
    "summary",
    "target_mapping_get",
    "target_mapping_set",
    "region_add",
    "region_edit",
    "region_split",
    "region_delete",
    "create",
    "delete",
    "surface_create",
    "surface_boundary_add",
    "quantities",
  ]),
  name: z.string().optional(),
  includeGeometry: z.boolean().optional(),
  codes: z.array(z.string()).optional(),
  decimals: z.number().int().min(0).max(6).optional(),
  corridorSurface: z.string().optional(),
  referenceSurface: z.string().optional(),
  regionIndex: z.number().int().nonnegative().optional(),
  baselineIndex: z.number().int().nonnegative().optional(),
  scope: CorridorTargetScopeSchema.optional(),
  targets: z.array(z.object(CorridorTargetRowShape)).optional(),
  alignmentName: z.string().optional(),
  profileName: z.string().optional(),
  assemblyName: z.string().optional(),
  baselineName: z.string().optional(),
  regionName: z.string().optional(),
  startStation: z.number().optional(),
  endStation: z.number().optional(),
  station: z.number().optional(),
  ...CorridorFrequencyShape,
  rebuild: z.boolean().optional(),
  surfaceName: z.string().optional(),
  linkCodes: z.array(z.string()).optional(),
  addAsBreakline: z.boolean().optional(),
  boundaryFromExtents: z.boolean().optional(),
  overhangCorrection: CorridorOverhangSchema.optional(),
  boundaryName: z.string().optional(),
  boundaryType: z.enum(["outside", "inside"]).optional(),
  points: z.array(CorridorBoundaryPointSchema).optional(),
  replace: z.boolean().optional(),
  shapeCodes: z.array(z.string()).optional(),
  bottomCode: z.string().optional(),
  daylightCode: z.string().optional(),
  includeRows: z.boolean().optional(),
};

// ─── Domain definition ────────────────────────────────────────────────────────

export const CORRIDOR_DOMAIN_DEFINITION: DomainToolDefinition = {
  domain: "corridor",
  actions: {
    list: {
      action: "list",
      inputSchema: CorridorListArgsSchema,
      responseSchema: CorridorListResponseSchema,
      capabilities: ["query"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["listCorridors"],
      execute: async () => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("listCorridors", {}),
      ),
    },
    get: {
      action: "get",
      inputSchema: CorridorGetArgsSchema,
      responseSchema: CorridorDetailResponseSchema,
      capabilities: ["query", "inspect"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["getCorridor"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("getCorridor", {
          name: args.name,
        }),
      ),
    },
    rebuild: {
      action: "rebuild",
      inputSchema: CorridorRebuildArgsSchema,
      responseSchema: CorridorRebuildResponseSchema,
      capabilities: ["manage"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["rebuildCorridor"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("rebuildCorridor", {
          name: args.name,
        }),
      ),
    },
    get_surfaces: {
      action: "get_surfaces",
      inputSchema: CorridorGetSurfacesArgsSchema,
      responseSchema: CorridorSurfacesResponseSchema,
      capabilities: ["query"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["getCorridorSurfaces"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("getCorridorSurfaces", {
          name: args.name,
        }),
      ),
    },
    get_feature_lines: {
      action: "get_feature_lines",
      inputSchema: CorridorGetFeatureLinesArgsSchema,
      responseSchema: CorridorFeatureLinesResponseSchema,
      capabilities: ["query"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["getCorridorFeatureLines"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("getCorridorFeatureLines", {
          name: args.name,
          includeGeometry: args.includeGeometry ?? false,
          codes: args.codes ?? null,
          decimals: args.decimals ?? 3,
        }),
      ),
    },
    compute_volumes: {
      action: "compute_volumes",
      inputSchema: CorridorComputeVolumesArgsSchema,
      responseSchema: CorridorVolumesResponseSchema,
      capabilities: ["analyze"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["computeCorridorVolumes"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("computeCorridorVolumes", {
          name: args.name,
          corridorSurface: args.corridorSurface,
          referenceSurface: args.referenceSurface,
        }),
      ),
    },
    summary: {
      action: "summary",
      inputSchema: CorridorSummaryArgsSchema,
      responseSchema: CorridorFullSummaryResponseSchema,
      capabilities: ["query", "analyze", "generate"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["getCorridor", "getCorridorSurfaces", "computeCorridorVolumes"],
      execute: async (args) => await withApplicationConnection(async (appClient) => {
        const corridor = CorridorDetailResponseSchema.parse(
          await appClient.sendCommand("getCorridor", { name: args.name }),
        );

        const surfaceInventory = CorridorSurfacesResponseSchema.parse(
          await appClient.sendCommand("getCorridorSurfaces", { name: args.name }),
        );

        const selectedCorridorSurface = (args.corridorSurface as string | undefined)
          ?? (surfaceInventory.surfaces.length === 1 ? surfaceInventory.surfaces[0].name : null);
        const referenceSurface = (args.referenceSurface as string | undefined) ?? null;

        let volumeAnalysis: z.infer<typeof CorridorVolumesResponseSchema> | null = null;
        if (selectedCorridorSurface && referenceSurface) {
          volumeAnalysis = CorridorVolumesResponseSchema.parse(
            await appClient.sendCommand("computeCorridorVolumes", {
              name: args.name,
              corridorSurface: selectedCorridorSurface,
              referenceSurface,
            }),
          );
        }

        const regionCount = corridor.baselines.reduce(
          (total, baseline) => total + baseline.regions.length,
          0,
        );

        const totalRegionLength = corridor.baselines.reduce(
          (baselineTotal, baseline) =>
            baselineTotal + baseline.regions.reduce(
              (regionTotal, region) => regionTotal + (region.endStation - region.startStation),
              0,
            ),
          0,
        );

        return CorridorFullSummaryResponseSchema.parse({
          corridor,
          surfaceInventory,
          volumeAnalysis,
          summary: {
            baselineCount: corridor.baselines.length,
            regionCount,
            corridorSurfaceCount: surfaceInventory.surfaces.length,
            featureLineCount: corridor.featureLineCount,
            state: corridor.state,
            totalRegionLength,
            selectedCorridorSurface,
            referenceSurface,
            volumeComputationStatus: volumeAnalysis ? "computed" : "skipped",
          },
        });
      }),
    },
    target_mapping_get: {
      action: "target_mapping_get",
      inputSchema: CorridorTargetMappingGetArgsSchema,
      responseSchema: GenericCorridorResponseSchema,
      capabilities: ["query", "inspect"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["getCorridorTargetMappings"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("getCorridorTargetMappings", {
          corridorName: args.name,
          regionIndex: args.regionIndex ?? null,
          baselineIndex: args.baselineIndex ?? 0,
        }),
      ),
    },
    target_mapping_set: {
      action: "target_mapping_set",
      inputSchema: CorridorTargetMappingSetArgsSchema,
      responseSchema: GenericCorridorResponseSchema,
      capabilities: ["edit", "manage"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["setCorridorTargetMappings"],
      execute: async (args) => await withApplicationConnection(async (appClient) => {
        const scope = (args.scope as string | undefined) ?? "region";
        const targets = (args.targets as Array<Record<string, unknown>>).map((row) => ({
          logicalName: row.logicalName ?? null,
          displayName: row.displayName ?? null,
          subassemblyName: row.subassemblyName ?? null,
          targetType: row.targetType,
          targetName: row.targetName,
        }));
        return await appClient.sendCommand("setCorridorTargetMappings", {
          corridorName: args.name,
          scope,
          baselineIndex: args.baselineIndex ?? 0,
          regionIndex: scope === "region" ? (args.regionIndex ?? 0) : null,
          targets,
          rebuild: args.rebuild ?? true,
        });
      }),
    },
    create: {
      action: "create",
      inputSchema: CorridorCreateArgsSchema,
      responseSchema: GenericCorridorResponseSchema,
      capabilities: ["create"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["createCorridor"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("createCorridor", {
          name: args.name,
          alignmentName: args.alignmentName,
          profileName: args.profileName,
          assemblyName: args.assemblyName,
          baselineName: args.baselineName ?? null,
          regionName: args.regionName ?? null,
          startStation: args.startStation ?? null,
          endStation: args.endStation ?? null,
          frequency: args.frequency ?? null,
          frequencyAtCurves: args.frequencyAtCurves ?? null,
          frequencyAlongProfileCurves: args.frequencyAlongProfileCurves ?? null,
          appliedAtProfileGeometryPoints: args.appliedAtProfileGeometryPoints ?? null,
        }),
      ),
    },
    region_add: {
      action: "region_add",
      inputSchema: CorridorRegionAddArgsSchema,
      responseSchema: GenericCorridorResponseSchema,
      capabilities: ["create", "edit"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["addCorridorRegion"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("addCorridorRegion", {
          corridorName: args.name,
          baselineIndex: args.baselineIndex ?? 0,
          assemblyName: args.assemblyName,
          regionName: args.regionName ?? null,
          startStation: args.startStation,
          endStation: args.endStation,
          frequency: args.frequency ?? null,
          frequencyAtCurves: args.frequencyAtCurves ?? null,
          frequencyAlongProfileCurves: args.frequencyAlongProfileCurves ?? null,
          appliedAtProfileGeometryPoints: args.appliedAtProfileGeometryPoints ?? null,
          rebuild: args.rebuild ?? true,
        }),
      ),
    },
    region_edit: {
      action: "region_edit",
      inputSchema: CorridorRegionEditArgsSchema,
      responseSchema: GenericCorridorResponseSchema,
      capabilities: ["edit"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["editCorridorRegion"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("editCorridorRegion", {
          corridorName: args.name,
          baselineIndex: args.baselineIndex ?? 0,
          regionIndex: args.regionIndex,
          regionName: args.regionName ?? null,
          assemblyName: args.assemblyName ?? null,
          startStation: args.startStation ?? null,
          endStation: args.endStation ?? null,
          frequency: args.frequency ?? null,
          frequencyAtCurves: args.frequencyAtCurves ?? null,
          frequencyAlongProfileCurves: args.frequencyAlongProfileCurves ?? null,
          appliedAtProfileGeometryPoints: args.appliedAtProfileGeometryPoints ?? null,
          rebuild: args.rebuild ?? true,
        }),
      ),
    },
    region_split: {
      action: "region_split",
      inputSchema: CorridorRegionSplitArgsSchema,
      responseSchema: GenericCorridorResponseSchema,
      capabilities: ["edit"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["splitCorridorRegion"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("splitCorridorRegion", {
          corridorName: args.name,
          baselineIndex: args.baselineIndex ?? 0,
          regionIndex: args.regionIndex,
          station: args.station,
          rebuild: args.rebuild ?? true,
        }),
      ),
    },
    surface_create: {
      action: "surface_create",
      inputSchema: CorridorSurfaceCreateArgsSchema,
      responseSchema: GenericCorridorResponseSchema,
      capabilities: ["create"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["createCorridorSurface"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("createCorridorSurface", {
          corridorName: args.name,
          surfaceName: args.surfaceName,
          linkCodes: args.linkCodes,
          addAsBreakline: args.addAsBreakline ?? true,
          boundaryFromExtents: args.boundaryFromExtents ?? true,
          overhangCorrection: args.overhangCorrection ?? null,
        }),
      ),
    },
    surface_boundary_add: {
      action: "surface_boundary_add",
      inputSchema: CorridorSurfaceBoundaryAddArgsSchema,
      responseSchema: GenericCorridorResponseSchema,
      capabilities: ["edit"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["addCorridorSurfaceBoundary"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("addCorridorSurfaceBoundary", {
          corridorName: args.name,
          surfaceName: args.surfaceName,
          boundaryName: args.boundaryName,
          boundaryType: args.boundaryType ?? "outside",
          points: args.points,
          replace: args.replace ?? false,
        }),
      ),
    },
    quantities: {
      action: "quantities",
      inputSchema: CorridorQuantitiesArgsSchema,
      responseSchema: GenericCorridorResponseSchema,
      capabilities: ["analyze"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["corridorQuantities"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("corridorQuantities", {
          corridorName: args.name,
          baselineIndex: args.baselineIndex ?? 0,
          startStation: args.startStation ?? null,
          endStation: args.endStation ?? null,
          shapeCodes: args.shapeCodes ?? null,
          bottomCode: args.bottomCode ?? "Trench_Bottom",
          daylightCode: args.daylightCode ?? "Trench_Daylight",
          includeRows: args.includeRows ?? false,
        }),
      ),
    },
    delete: {
      action: "delete",
      inputSchema: CorridorDeleteArgsSchema,
      responseSchema: GenericCorridorResponseSchema,
      capabilities: ["delete"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["deleteCorridor"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("deleteCorridor", { corridorName: args.name }),
      ),
    },
    region_delete: {
      action: "region_delete",
      inputSchema: CorridorRegionDeleteArgsSchema,
      responseSchema: GenericCorridorResponseSchema,
      capabilities: ["delete", "edit"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["deleteCorridorRegion"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("deleteCorridorRegion", {
          corridorName: args.name,
          baselineIndex: args.baselineIndex ?? 0,
          regionIndex: args.regionIndex,
        }),
      ),
    },
  },
  exposures: [
    {
      toolName: "civil3d_corridor",
      displayName: "Civil 3D Corridor",
      description: "Creates and reads Civil 3D corridors, controls rebuild, manages regions, target mappings and corridor surfaces, and reports per-station quantities through a single domain tool.",
      inputShape: canonicalCorridorInputShape,
      supportedActions: [
        "list",
        "get",
        "rebuild",
        "get_surfaces",
        "get_feature_lines",
        "compute_volumes",
        "summary",
        "target_mapping_get",
        "target_mapping_set",
        "region_add",
        "region_edit",
        "region_split",
        "region_delete",
        "create",
        "delete",
        "surface_create",
        "surface_boundary_add",
        "quantities",
      ],
      resolveAction: (rawArgs) => ({
        action: String(rawArgs.action ?? ""),
        args: rawArgs,
      }),
    },
    {
      toolName: "civil3d_corridor_summary",
      displayName: "Civil 3D Corridor Summary",
      description: "Builds a corridor summary by fetching corridor details, corridor surfaces, and optional volume analysis against a reference surface.",
      inputShape: {
        name: z.string(),
        corridorSurface: z.string().optional(),
        referenceSurface: z.string().optional(),
      },
      supportedActions: ["summary"],
      resolveAction: (rawArgs) => ({
        action: "summary",
        args: {
          action: "summary",
          name: rawArgs.name,
          corridorSurface: rawArgs.corridorSurface,
          referenceSurface: rawArgs.referenceSurface,
        },
      }),
    },
    {
      toolName: "civil3d_corridor_target_mapping_get",
      displayName: "Civil 3D Corridor Target Mapping Get",
      description: "Retrieve the current subassembly target mappings for a Civil 3D corridor. Returns all target parameters for each baseline region.",
      inputShape: {
        corridorName: z.string(),
        regionIndex: z.number().int().nonnegative().optional(),
        baselineIndex: z.number().int().nonnegative().optional(),
      },
      supportedActions: ["target_mapping_get"],
      resolveAction: (rawArgs) => ({
        action: "target_mapping_get",
        args: {
          action: "target_mapping_get",
          name: rawArgs.corridorName,
          regionIndex: rawArgs.regionIndex,
          baselineIndex: rawArgs.baselineIndex,
        },
      }),
    },
    {
      toolName: "civil3d_corridor_target_mapping_set",
      displayName: "Civil 3D Corridor Target Mapping Set",
      description: "Set subassembly target mappings on a Civil 3D corridor region, baseline, or whole corridor. Targets are matched by logicalName or displayName from target_mapping_get.",
      inputShape: {
        corridorName: z.string(),
        scope: CorridorTargetScopeSchema.optional(),
        regionIndex: z.number().int().nonnegative().optional(),
        baselineIndex: z.number().int().nonnegative().optional(),
        targets: z.array(z.object(CorridorTargetRowShape)),
        rebuild: z.boolean().optional(),
      },
      supportedActions: ["target_mapping_set"],
      resolveAction: (rawArgs) => ({
        action: "target_mapping_set",
        args: {
          action: "target_mapping_set",
          name: rawArgs.corridorName,
          scope: rawArgs.scope,
          regionIndex: rawArgs.regionIndex,
          baselineIndex: rawArgs.baselineIndex,
          targets: rawArgs.targets,
          rebuild: rawArgs.rebuild,
        },
      }),
    },
    {
      toolName: "civil3d_corridor_region_add",
      displayName: "Civil 3D Corridor Region Add",
      description: "Add a new region to a Civil 3D corridor baseline, defining which assembly applies over a station range and at what sampling frequency.",
      inputShape: {
        corridorName: z.string(),
        baselineIndex: z.number().int().nonnegative().optional(),
        assemblyName: z.string(),
        regionName: z.string().optional(),
        startStation: z.number(),
        endStation: z.number(),
        ...CorridorFrequencyShape,
        rebuild: z.boolean().optional(),
      },
      supportedActions: ["region_add"],
      resolveAction: (rawArgs) => ({
        action: "region_add",
        args: {
          action: "region_add",
          name: rawArgs.corridorName,
          baselineIndex: rawArgs.baselineIndex,
          assemblyName: rawArgs.assemblyName,
          regionName: rawArgs.regionName,
          startStation: rawArgs.startStation,
          endStation: rawArgs.endStation,
          frequency: rawArgs.frequency,
          frequencyAtCurves: rawArgs.frequencyAtCurves,
          frequencyAlongProfileCurves: rawArgs.frequencyAlongProfileCurves,
          appliedAtProfileGeometryPoints: rawArgs.appliedAtProfileGeometryPoints,
          rebuild: rawArgs.rebuild,
        },
      }),
    },
    {
      toolName: "civil3d_corridor_region_delete",
      displayName: "Civil 3D Corridor Region Delete",
      description: "Delete a region from a Civil 3D corridor baseline by its zero-based index. Rebuilds the corridor after deletion.",
      inputShape: {
        corridorName: z.string(),
        baselineIndex: z.number().int().nonnegative().optional(),
        regionIndex: z.number().int().nonnegative(),
      },
      supportedActions: ["region_delete"],
      resolveAction: (rawArgs) => ({
        action: "region_delete",
        args: {
          action: "region_delete",
          name: rawArgs.corridorName,
          baselineIndex: rawArgs.baselineIndex,
          regionIndex: rawArgs.regionIndex,
        },
      }),
    },
  ],
};
