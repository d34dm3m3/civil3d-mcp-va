import { beforeEach, describe, expect, it, vi } from "vitest";

const { sendCommandMock } = vi.hoisted(() => ({
  sendCommandMock: vi.fn(),
}));

vi.mock("../src/utils/ConnectionManager.js", () => ({
  withApplicationConnection: async <T>(
    operation: (client: { sendCommand: typeof sendCommandMock }) => Promise<T>,
  ) => await operation({
    sendCommand: sendCommandMock,
  }),
}));

import { isApprovalRequired } from "../src/tools/approvalPolicy.js";
import { ASSEMBLY_DOMAIN_DEFINITION } from "../src/tools/domains/assemblyDomain.js";
import { CORRIDOR_DOMAIN_DEFINITION } from "../src/tools/domains/corridorDomain.js";
import { GEOMETRY_DOMAIN_DEFINITION } from "../src/tools/domains/geometryDomain.js";
import { GENERATED_TOOL_CATALOG_ENTRIES } from "../src/tools/toolManifest.js";

const assembly = ASSEMBLY_DOMAIN_DEFINITION.actions;
const corridor = CORRIDOR_DOMAIN_DEFINITION.actions;

describe("assembly actions for side-less stock subassemblies", () => {
  beforeEach(() => sendCommandMock.mockReset());

  it("accepts side None and forwards it to the plugin", async () => {
    const args = assembly.create_subassembly.inputSchema.parse({
      action: "create_subassembly",
      assemblyName: "Schakt_HI",
      subassemblyType: "Subassembly.TrenchPipe1",
      side: "None",
      parameters: { Width: 1.885, Sideslope: 1 },
    });
    sendCommandMock.mockResolvedValue({});

    await assembly.create_subassembly.execute(args);

    expect(sendCommandMock).toHaveBeenCalledWith("createSubassembly", {
      assemblyName: "Schakt_HI",
      subassemblyType: "Subassembly.TrenchPipe1",
      side: "None",
      parameters: { Width: 1.885, Sideslope: 1 },
    });
  });

  it("forwards verbose=false by default on get", async () => {
    sendCommandMock.mockResolvedValue({});
    await assembly.get.execute({ action: "get", name: "Schakt_HI" });
    expect(sendCommandMock).toHaveBeenCalledWith("getAssembly", { name: "Schakt_HI", verbose: false });
  });

  it("accepts a non-verbose subassembly without reflected parameters", () => {
    const parsed = assembly.get.responseSchema!.safeParse({
      name: "Schakt_HI",
      handle: "1",
      style: null,
      subassemblies: [{
        name: "TrenchPipe1-None-1",
        side: "none",
        className: "Subassembly",
        stockParameters: { Width: 1.885 },
      }],
      usedByCorridors: ["Schakt_HK"],
    });
    expect(parsed.success).toBe(true);
  });
});

describe("corridor create, regions, targets, surfaces and quantities", () => {
  beforeEach(() => sendCommandMock.mockReset());

  it("creates a corridor with baseline and first region", async () => {
    sendCommandMock.mockResolvedValue({});
    const args = corridor.create.inputSchema.parse({
      action: "create",
      name: "Schakt_HK",
      alignmentName: "Schakt_CL_HK",
      profileName: "Schaktbotten",
      assemblyName: "Schakt_HI",
      startStation: 0,
      endStation: 324.4,
      frequency: 5,
    });

    await corridor.create.execute(args);

    expect(sendCommandMock).toHaveBeenCalledWith("createCorridor", {
      name: "Schakt_HK",
      alignmentName: "Schakt_CL_HK",
      profileName: "Schaktbotten",
      assemblyName: "Schakt_HI",
      baselineName: null,
      regionName: null,
      startStation: 0,
      endStation: 324.4,
      frequency: 5,
      frequencyAtCurves: null,
      frequencyAlongProfileCurves: null,
      appliedAtProfileGeometryPoints: null,
    });
  });

  it("passes every region frequency on create instead of dropping them", async () => {
    // Live 2026-10-02: frequencyAtCurves 5 on create was stripped by the action schema
    // and the region kept Civil 3D's 25 m along curves.
    sendCommandMock.mockResolvedValue({});
    const args = corridor.create.inputSchema.parse({
      action: "create",
      name: "Schakt_HK",
      alignmentName: "Schakt_CL_HK",
      profileName: "Schaktbotten",
      assemblyName: "Schakt_HI",
      frequency: 5,
      frequencyAtCurves: 5,
      frequencyAlongProfileCurves: 2,
      appliedAtProfileGeometryPoints: true,
    });

    await corridor.create.execute(args);

    expect(sendCommandMock).toHaveBeenCalledWith("createCorridor", expect.objectContaining({
      frequency: 5,
      frequencyAtCurves: 5,
      frequencyAlongProfileCurves: 2,
      appliedAtProfileGeometryPoints: true,
    }));
  });

  it("adds a region without inventing a default frequency", async () => {
    sendCommandMock.mockResolvedValue({});
    const args = corridor.region_add.inputSchema.parse({
      action: "region_add",
      name: "Schakt_HK",
      assemblyName: "Schakt_IJ",
      regionName: "IJ_1",
      startStation: 324.4,
      endStation: 655,
      appliedAtProfileGeometryPoints: true,
      rebuild: false,
    });

    await corridor.region_add.execute(args);

    expect(sendCommandMock).toHaveBeenCalledWith("addCorridorRegion", {
      corridorName: "Schakt_HK",
      baselineIndex: 0,
      assemblyName: "Schakt_IJ",
      regionName: "IJ_1",
      startStation: 324.4,
      endStation: 655,
      frequency: null,
      frequencyAtCurves: null,
      frequencyAlongProfileCurves: null,
      appliedAtProfileGeometryPoints: true,
      rebuild: false,
    });
  });

  it("no longer accepts the unsupported frequencyAtKneePoints field", () => {
    const parsed = corridor.region_add.inputSchema.safeParse({
      action: "region_add",
      name: "Schakt_HK",
      assemblyName: "Schakt_IJ",
      startStation: 0,
      endStation: 10,
      frequencyAtKneePoints: 5,
    });
    expect(parsed.success).toBe(false);
  });

  it("edits a region", async () => {
    sendCommandMock.mockResolvedValue({});
    await corridor.region_edit.execute(corridor.region_edit.inputSchema.parse({
      action: "region_edit",
      name: "Schakt_HK",
      regionIndex: 1,
      endStation: 655,
    }));
    expect(sendCommandMock).toHaveBeenCalledWith("editCorridorRegion", {
      corridorName: "Schakt_HK",
      baselineIndex: 0,
      regionIndex: 1,
      regionName: null,
      assemblyName: null,
      startStation: null,
      endStation: 655,
      frequency: null,
      frequencyAtCurves: null,
      frequencyAlongProfileCurves: null,
      appliedAtProfileGeometryPoints: null,
      rebuild: true,
    });
  });

  it("splits a region at a station", async () => {
    sendCommandMock.mockResolvedValue({});
    await corridor.region_split.execute(corridor.region_split.inputSchema.parse({
      action: "region_split",
      name: "Schakt_HK",
      regionIndex: 1,
      station: 655,
    }));
    expect(sendCommandMock).toHaveBeenCalledWith("splitCorridorRegion", {
      corridorName: "Schakt_HK",
      baselineIndex: 0,
      regionIndex: 1,
      station: 655,
      rebuild: true,
    });
  });

  it("sets targets by logical name across the whole corridor", async () => {
    sendCommandMock.mockResolvedValue({});
    await corridor.target_mapping_set.execute(corridor.target_mapping_set.inputSchema.parse({
      action: "target_mapping_set",
      name: "Schakt_HK",
      scope: "corridor",
      targets: [{ logicalName: "TargetDTM", targetType: "surface", targetName: "bef_mh" }],
    }));
    expect(sendCommandMock).toHaveBeenCalledWith("setCorridorTargetMappings", {
      corridorName: "Schakt_HK",
      scope: "corridor",
      baselineIndex: 0,
      regionIndex: null,
      targets: [{
        logicalName: "TargetDTM",
        displayName: null,
        subassemblyName: null,
        targetType: "surface",
        targetName: "bef_mh",
      }],
      rebuild: true,
    });
  });

  it("rejects a target without logicalName or displayName", () => {
    const parsed = corridor.target_mapping_set.inputSchema.safeParse({
      action: "target_mapping_set",
      name: "Schakt_HK",
      targets: [{ targetType: "surface", targetName: "bef_mh" }],
    });
    expect(parsed.success).toBe(false);
  });

  it("creates a corridor surface from link codes", async () => {
    sendCommandMock.mockResolvedValue({});
    await corridor.surface_create.execute(corridor.surface_create.inputSchema.parse({
      action: "surface_create",
      name: "Schakt_HK",
      surfaceName: "Schakt_HK_Datum",
      linkCodes: ["Datum"],
      overhangCorrection: "bottom_links",
    }));
    expect(sendCommandMock).toHaveBeenCalledWith("createCorridorSurface", {
      corridorName: "Schakt_HK",
      surfaceName: "Schakt_HK_Datum",
      linkCodes: ["Datum"],
      addAsBreakline: true,
      boundaryFromExtents: true,
      overhangCorrection: "bottom_links",
    });
  });

  it("requests corridor quantities with trench defaults and no rows", async () => {
    sendCommandMock.mockResolvedValue({});
    await corridor.quantities.execute(corridor.quantities.inputSchema.parse({
      action: "quantities",
      name: "Schakt_HK",
    }));
    expect(sendCommandMock).toHaveBeenCalledWith("corridorQuantities", {
      corridorName: "Schakt_HK",
      baselineIndex: 0,
      startStation: null,
      endStation: null,
      shapeCodes: null,
      bottomCode: "Trench_Bottom",
      daylightCode: "Trench_Daylight",
      includeRows: false,
    });
  });

  it("adds a polygon boundary to a corridor surface as outside by default", async () => {
    // Live 2026-10-02: extents boundaries are rejected ("crossing polygon") when the
    // baseline has angle points; a footprint polygon is supplied instead.
    sendCommandMock.mockResolvedValue({});
    await corridor.surface_boundary_add.execute(corridor.surface_boundary_add.inputSchema.parse({
      action: "surface_boundary_add",
      name: "Schakt_HK",
      surfaceName: "Schakt_HK_datum",
      boundaryName: "Fotavtryck",
      points: [{ x: 0, y: 0 }, { x: 10, y: 0 }, { x: 10, y: 2 }],
    }));
    expect(sendCommandMock).toHaveBeenCalledWith("addCorridorSurfaceBoundary", {
      corridorName: "Schakt_HK",
      surfaceName: "Schakt_HK_datum",
      boundaryName: "Fotavtryck",
      boundaryType: "outside",
      points: [{ x: 0, y: 0 }, { x: 10, y: 0 }, { x: 10, y: 2 }],
      replace: false,
    });
  });

  it("rejects a corridor surface boundary with fewer than three points", () => {
    const parsed = corridor.surface_boundary_add.inputSchema.safeParse({
      action: "surface_boundary_add",
      name: "Schakt_HK",
      surfaceName: "Schakt_HK_datum",
      boundaryName: "Fotavtryck",
      points: [{ x: 0, y: 0 }, { x: 10, y: 0 }],
    });
    expect(parsed.success).toBe(false);
  });

  it("deletes a corridor by name through deleteCorridor", async () => {
    sendCommandMock.mockResolvedValue({});
    await corridor.delete.execute(corridor.delete.inputSchema.parse({ action: "delete", name: "T_C2b" }));
    expect(sendCommandMock).toHaveBeenCalledWith("deleteCorridor", { corridorName: "T_C2b" });
  });

  it("requires approval to delete a corridor", () => {
    expect(isApprovalRequired({
      toolName: "civil3d_corridor",
      action: "delete",
      capabilities: corridor.delete.capabilities,
      safeForRetry: corridor.delete.safeForRetry,
    })).toBe(true);
  });

  it("exposes the new actions on the canonical corridor tool", () => {
    const entry = GENERATED_TOOL_CATALOG_ENTRIES.find((e) => e.toolName === "civil3d_corridor");
    for (const action of ["create", "delete", "region_edit", "region_split", "surface_create", "surface_boundary_add", "quantities"]) {
      expect(entry!.operations).toContain(action);
    }
  });
});

describe("polyline layer handling", () => {
  beforeEach(() => sendCommandMock.mockReset());

  it("forwards createLayer=false by default so a missing layer is an error, not layer 0", async () => {
    sendCommandMock.mockResolvedValue({});
    const action = GEOMETRY_DOMAIN_DEFINITION.actions.create_polyline;
    await action.execute(action.inputSchema.parse({
      action: "create_polyline",
      points: [{ x: 0, y: 0 }, { x: 1, y: 1 }],
      layer: "VA_Schakt",
    }));
    expect(sendCommandMock).toHaveBeenCalledWith("createPolyline", {
      points: [{ x: 0, y: 0 }, { x: 1, y: 1 }],
      closed: 0,
      layer: "VA_Schakt",
      createLayer: false,
    });
  });

  // C58: the divider between plan and profile is a sheet line in paper space.
  it("forwards layout to draw in a paper layout (C58)", async () => {
    sendCommandMock.mockResolvedValue({});
    const action = GEOMETRY_DOMAIN_DEFINITION.actions.create_polyline;
    await action.execute(action.inputSchema.parse({
      action: "create_polyline", points: [{ x: 40, y: 329 }, { x: 721, y: 329 }], layer: "RIT-BLANKETT", layout: "R-51-1-101",
    }));
    expect(sendCommandMock).toHaveBeenCalledWith("createPolyline", expect.objectContaining({ layout: "R-51-1-101", layer: "RIT-BLANKETT" }));
  });
});
