import { readFileSync } from "node:fs";
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
import { SURFACE_DOMAIN_DEFINITION } from "../src/tools/domains/surfaceDomain.js";
import { GENERATED_TOOL_CATALOG_ENTRIES } from "../src/tools/toolManifest.js";

const surface = SURFACE_DOMAIN_DEFINITION.actions;
const square = [{ x: 0, y: 0 }, { x: 10, y: 0 }, { x: 10, y: 10 }];

function approvalRequired(action: keyof typeof surface): boolean {
  return isApprovalRequired({
    toolName: "civil3d_surface",
    action,
    capabilities: surface[action].capabilities,
    safeForRetry: surface[action].safeForRetry,
  });
}

describe("surface paste", () => {
  beforeEach(() => sendCommandMock.mockReset());

  it("pastes a source surface into the target through pasteSurface", async () => {
    sendCommandMock.mockResolvedValue({});
    await surface.paste.execute(surface.paste.inputSchema.parse({
      action: "paste", name: "bef_mh", sourceSurface: "bef_mh_lucka500",
    }));
    expect(sendCommandMock).toHaveBeenCalledWith("pasteSurface", {
      surfaceName: "bef_mh", sourceSurfaceName: "bef_mh_lucka500",
    });
  });

  it("requires a source surface", () => {
    expect(surface.paste.inputSchema.safeParse({ action: "paste", name: "bef_mh" }).success).toBe(false);
  });

  it("requires approval", () => {
    expect(approvalRequired("paste")).toBe(true);
  });
});

describe("surface simplify", () => {
  beforeEach(() => sendCommandMock.mockReset());

  it("defaults to point removal over the whole surface", async () => {
    sendCommandMock.mockResolvedValue({});
    await surface.simplify.execute(surface.simplify.inputSchema.parse({
      action: "simplify", name: "hjalp", maxElevationChange: 0.02,
    }));
    expect(sendCommandMock).toHaveBeenCalledWith("simplifySurface", {
      surfaceName: "hjalp", method: "point_removal", maxElevationChange: 0.02, region: null,
    });
  });

  it("forwards edge contraction inside a polygon region", async () => {
    sendCommandMock.mockResolvedValue({});
    await surface.simplify.execute(surface.simplify.inputSchema.parse({
      action: "simplify", name: "hjalp", maxElevationChange: 0.02,
      simplifyMethod: "edge_contraction", region: square,
    }));
    expect(sendCommandMock).toHaveBeenCalledWith("simplifySurface", {
      surfaceName: "hjalp", method: "edge_contraction", maxElevationChange: 0.02, region: square,
    });
  });

  it("rejects a non-positive elevation tolerance and a two-point region", () => {
    expect(surface.simplify.inputSchema.safeParse({ action: "simplify", name: "s", maxElevationChange: 0 }).success).toBe(false);
    expect(surface.simplify.inputSchema.safeParse({
      action: "simplify", name: "s", maxElevationChange: 0.02, region: square.slice(0, 2),
    }).success).toBe(false);
  });

  it("requires approval", () => {
    expect(approvalRequired("simplify")).toBe(true);
  });
});

describe("surface build options", () => {
  beforeEach(() => sendCommandMock.mockReset());

  it("forwards only the options that were given", async () => {
    sendCommandMock.mockResolvedValue({});
    await surface.build_options_set.execute(surface.build_options_set.inputSchema.parse({
      action: "build_options_set", name: "bef_mh", copyDeletedDependentObjects: true,
    }));
    expect(sendCommandMock).toHaveBeenCalledWith("setSurfaceBuildOptions", {
      surfaceName: "bef_mh", copyDeletedDependentObjects: true,
      useMaximumTriangleLength: null, maximumTriangleLength: null,
    });
  });

  it("rejects a call without any option", () => {
    expect(surface.build_options_set.inputSchema.safeParse({ action: "build_options_set", name: "bef_mh" }).success).toBe(false);
  });

  it("requires approval", () => {
    expect(approvalRequired("build_options_set")).toBe(true);
  });
});

describe("surface find_gaps", () => {
  beforeEach(() => sendCommandMock.mockReset());

  it("uses a 10 m edge threshold and 50 clusters by default", async () => {
    sendCommandMock.mockResolvedValue({});
    await surface.find_gaps.execute(surface.find_gaps.inputSchema.parse({ action: "find_gaps", name: "bef_mh" }));
    expect(sendCommandMock).toHaveBeenCalledWith("findSurfaceGaps", {
      surfaceName: "bef_mh", minEdgeLength: 10, region: null, maxClusters: 50,
    });
  });

  it("is read-only and needs no approval", () => {
    expect(approvalRequired("find_gaps")).toBe(false);
    expect(surface.find_gaps.safeForRetry).toBe(true);
  });
});

describe("canonical surface tool", () => {
  it("exposes the new actions", () => {
    const entry = GENERATED_TOOL_CATALOG_ENTRIES.find((e) => e.toolName === "civil3d_surface");
    for (const action of ["paste", "simplify", "build_options_set", "find_gaps"]) {
      expect(entry!.operations).toContain(action);
    }
  });
});

describe("surface export_landxml", () => {
  beforeEach(() => {
    sendCommandMock.mockReset();
    sendCommandMock.mockResolvedValue({ outputPath: "x.xml", surfaces: [] });
  });

  it("sends names and clipped surfaces, coordinate system and defaults (C59)", async () => {
    await surface.export_landxml.execute(surface.export_landxml.inputSchema.parse({
      action: "export_landxml",
      surfaces: ["Schakt_HK_datum", { name: "mark_laz", exportName: "Mark", clip: square }],
      outputPath: "D:/ut/schakt.xml",
      crs: { name: "SWEREF 99 13 30", epsgCode: "3008", verticalDatum: "RH 2000" },
    }));
    expect(sendCommandMock).toHaveBeenCalledWith("exportLandXml", {
      surfaces: ["Schakt_HK_datum", { name: "mark_laz", exportName: "Mark", clip: square }],
      outputPath: "D:/ut/schakt.xml",
      overwrite: false,
      decimals: 3,
      projectName: null,
      coordinateSystem: { name: "SWEREF 99 13 30", epsgCode: "3008", verticalDatum: "RH 2000" },
    });
  });

  it("needs at least one surface and an output path; a clip needs three points", () => {
    const schema = surface.export_landxml.inputSchema;
    expect(() => schema.parse({ action: "export_landxml", surfaces: [], outputPath: "a.xml" })).toThrow();
    expect(() => schema.parse({ action: "export_landxml", surfaces: ["a"] })).toThrow();
    expect(() => schema.parse({ action: "export_landxml", surfaces: [{ name: "a", clip: square.slice(0, 2) }], outputPath: "a.xml" })).toThrow();
  });
});

describe("C97 surfaces without data", () => {
  // Civil 3D throws InvalidOperationException ("The surface has no point or triangle") from the property getters of a
  // new, empty surface; pasting into it, its build options and its statistics read them safely (live 2026-10-10).
  const plugin = (file: string) => readFileSync(new URL(`../Civil3D-MCP-Plugin/${file}`, import.meta.url), "utf8");

  it("reads points and triangles of an empty surface as 0 instead of failing", () => {
    const commands = plugin("SurfaceCommands.cs");
    expect(commands).toContain("internal static GeneralSurfaceProperties? GeneralPropertiesOrNull(CivilSurface surface)");
    expect(commands).toContain("catch (InvalidOperationException)");
    expect(commands).toContain('["empty"] = true');
    const edit = plugin("SurfaceEditCommands.cs");
    expect(edit).toContain("var before = SurfaceCommands.PointCount(target);");
    expect(edit).toContain('["pointsAfter"] = SurfaceCommands.PointCount(target)');
    expect(edit).toContain('["numberOfTriangles"] = SurfaceCommands.TriangleCount(surface)');
    expect(edit).not.toMatch(/target\.GetGeneralProperties\(\)/);
  });

  it("still pastes and sets build options behind approval", async () => {
    sendCommandMock.mockReset();
    sendCommandMock.mockResolvedValue({ pointsBefore: 0 });
    await surface.paste.execute(surface.paste.inputSchema.parse({ action: "paste", name: "Samlad", sourceSurface: "Schakt" }) as never);
    expect(sendCommandMock).toHaveBeenLastCalledWith("pasteSurface", expect.objectContaining({ surfaceName: "Samlad" }));
    expect(approvalRequired("paste")).toBe(true);
  });
});
