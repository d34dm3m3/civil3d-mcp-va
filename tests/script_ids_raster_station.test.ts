import { beforeEach, describe, expect, it, vi } from "vitest";
import { z } from "zod";

const { sendCommandMock } = vi.hoisted(() => ({ sendCommandMock: vi.fn() }));

vi.mock("../src/utils/ConnectionManager.js", () => ({
  withApplicationConnection: async <T>(
    operation: (client: { sendCommand: typeof sendCommandMock }) => Promise<T>,
  ) => await operation({ sendCommand: sendCommandMock }),
}));

import { isApprovalRequired } from "../src/tools/approvalPolicy.js";
import { ALIGNMENT_DOMAIN_DEFINITION } from "../src/tools/domains/alignmentDomain.js";
import { DRAWING_RUNTIME_DOMAIN_DEFINITION } from "../src/tools/domains/drawingRuntimeDomain.js";
import { GENERATED_TOOL_CATALOG_ENTRIES } from "../src/tools/toolManifest.js";

const drawing = DRAWING_RUNTIME_DOMAIN_DEFINITION.actions;
const approval = (action: string) =>
  isApprovalRequired({ toolName: "civil3d_drawing", action, capabilities: drawing[action].capabilities as never, safeForRetry: drawing[action].safeForRetry });
const run = async (action: string, args: Record<string, unknown>) =>
  await drawing[action].execute(drawing[action].inputSchema.parse({ action, ...args }) as never);
const survivesExposure = (args: Record<string, unknown>) => {
  const exposure = DRAWING_RUNTIME_DOMAIN_DEFINITION.exposures.find((e) => e.toolName === "civil3d_drawing")!;
  const parsed = z.object(exposure.inputShape).parse(args) as Record<string, unknown>;
  expect(parsed).toEqual(args);
};

describe("C105 script ids and C114 moving objects", () => {
  beforeEach(() => sendCommandMock.mockReset());

  it("writes script ids with optional data (string or JSON) behind approval", async () => {
    sendCommandMock.mockResolvedValue({ written: [] });
    const items = [
      { handle: "4A2F", id: "V2/flag/S1" },
      { handle: "4a30", id: "V2/flag/S2", data: { x: 1405.2, y: 6223.9 } },
      { handle: "4A31", id: "V2/text/S3", data: "manual" },
    ];
    await run("entities_id_set", { items });
    expect(sendCommandMock).toHaveBeenCalledWith("entityIdsSet", { items });
    survivesExposure({ action: "entities_id_set", items });
    expect(approval("entities_id_set")).toBe(true);
    const s = drawing.entities_id_set.inputSchema;
    expect(s.safeParse({ action: "entities_id_set", items: [] }).success).toBe(false);
    expect(s.safeParse({ action: "entities_id_set", items: [{ handle: "XYZ", id: "a" }] }).success).toBe(false);
    expect(s.safeParse({ action: "entities_id_set", items: [{ handle: "4A2F", id: "" }] }).success).toBe(false);
    expect(s.safeParse({ action: "entities_id_set", items: [{ handle: "4A2F", id: "a".repeat(256) }] }).success).toBe(false);
  });

  it("keeps styles_import items whole next to the script id items", () => {
    survivesExposure({ action: "styles_import", sourcePath: "C:/templates/company.dwt", items: [{ collection: "PipeStyles", names: ["Sewer"] }] });
  });

  it("clears script ids by handle behind approval", async () => {
    sendCommandMock.mockResolvedValue({ cleared: ["4A2F"], withoutId: 0 });
    await run("entities_id_clear", { handles: ["4A2F"] });
    expect(sendCommandMock).toHaveBeenCalledWith("entityIdsClear", { handles: ["4A2F"] });
    survivesExposure({ action: "entities_id_clear", handles: ["4A2F"] });
    expect(approval("entities_id_clear")).toBe(true);
    expect(drawing.entities_id_clear.inputSchema.safeParse({ action: "entities_id_clear", handles: [] }).success).toBe(false);
  });

  it("moves objects by handle with a non-zero displacement behind approval", async () => {
    sendCommandMock.mockResolvedValue({ moved: ["4A2F"] });
    await run("entities_move", { handles: ["4A2F", "4A30"], dx: 2.5, dy: -1 });
    expect(sendCommandMock).toHaveBeenCalledWith("moveEntities", { handles: ["4A2F", "4A30"], dx: 2.5, dy: -1 });
    survivesExposure({ action: "entities_move", handles: ["4A2F"], dx: 2.5, dy: -1, dz: 0.2 });
    expect(approval("entities_move")).toBe(true);
    const s = drawing.entities_move.inputSchema;
    expect(s.safeParse({ action: "entities_move", handles: ["4A2F"] }).success).toBe(false);
    expect(s.safeParse({ action: "entities_move", handles: ["4A2F"], dx: 0, dy: 0 }).success).toBe(false);
    expect(s.safeParse({ action: "entities_move", handles: [], dz: 1 }).success).toBe(false);
  });

  it("masks multileaders by handle or layer behind approval, and mleader_add takes the mask 0 (C117, C99g)", async () => {
    sendCommandMock.mockResolvedValue({ changed: ["4A2F"] });
    await run("mleader_mask_set", { layers: ["NOTE-FLAG-C101"], dryRun: true });
    expect(sendCommandMock).toHaveBeenCalledWith("setMLeaderMask", { layers: ["NOTE-FLAG-C101"], dryRun: true });
    survivesExposure({ action: "mleader_mask_set", handles: ["4A2F"], layers: ["NOTE-HV-C101"], backgroundColor: 0, maskScale: 1.25, dryRun: false });
    expect(approval("mleader_mask_set")).toBe(true);
    const s = drawing.mleader_mask_set.inputSchema;
    expect(s.safeParse({ action: "mleader_mask_set" }).success).toBe(false);
    expect(s.safeParse({ action: "mleader_mask_set", layers: ["A"], maskScale: 6 }).success).toBe(false);
    const add = drawing.mleader_add.inputSchema;
    expect(add.safeParse({ action: "mleader_add", x: 0, y: 0, textX: 1, textY: 1, text: "A", textHeight: 1, backgroundColor: 0 }).success).toBe(true);
  });

  it("lists objects by script id prefix and pages with offset, without approval (C105, C111)", async () => {
    sendCommandMock.mockResolvedValue({ matched: 0, returned: 0, offset: 200, entities: [] });
    const args = { action: "entities_list", idPrefix: "V2/", offset: 200, limit: 200 };
    await run("entities_list", { idPrefix: "V2/", offset: 200, limit: 200 });
    expect(sendCommandMock).toHaveBeenCalledWith("listEntities", expect.objectContaining({ idPrefix: "V2/", offset: 200, limit: 200 }));
    survivesExposure(args);
    expect(drawing.entities_list.inputSchema.safeParse({ action: "entities_list", idPrefix: "" }).success).toBe(true);
    expect(drawing.entities_list.inputSchema.safeParse({ action: "entities_list", offset: -1 }).success).toBe(false);
    expect(drawing.entities_list.inputSchema.safeParse({ action: "entities_list", offset: 1.5 }).success).toBe(false);
    expect(approval("entities_list")).toBe(false);
  });
});

describe("C99h/C109 raster images", () => {
  beforeEach(() => sendCommandMock.mockReset());

  it("attaches a georeferenced image with world file vectors behind approval", async () => {
    sendCommandMock.mockResolvedValue({ handle: "5B01" });
    const args = {
      path: "C:/work/project/utility_maps/telecom_s1_georef.png", name: "Telecom_s1", x: 1405100.5, y: 6223800.25,
      ux: 120.4, uy: 3.1, vx: -2.2, vy: 85.6, layer: "RASTER-UTILITY-OWNER", createLayer: true, fade: 30, relativePath: true,
    };
    await run("raster_attach", args);
    expect(sendCommandMock).toHaveBeenCalledWith("rasterAttach", args);
    survivesExposure({ action: "raster_attach", ...args });
    expect(approval("raster_attach")).toBe(true);
  });

  it("needs all four vectors or a width, an image file and a fade of 0-100", () => {
    const s = drawing.raster_attach.inputSchema;
    const base = { action: "raster_attach", path: "D:/scan.tif", x: 0, y: 0 };
    expect(s.safeParse({ ...base, width: 100, rotationDegrees: 12 }).success).toBe(true);
    expect(s.safeParse(base).success).toBe(false);
    expect(s.safeParse({ ...base, ux: 1, uy: 0, vx: 0 }).success).toBe(false);
    expect(s.safeParse({ ...base, width: 0 }).success).toBe(false);
    expect(s.safeParse({ ...base, path: "D:/scan.pdf", width: 10 }).success).toBe(false);
    expect(s.safeParse({ ...base, width: 10, fade: 101 }).success).toBe(false);
    expect(s.safeParse({ ...base, path: "D:/scan.JPEG", width: 10 }).success).toBe(true);
  });

  it("detaches image definitions by name behind approval", async () => {
    sendCommandMock.mockResolvedValue({ detached: [] });
    await run("raster_detach", { names: ["Telecom_s1", "Telecom_s2"] });
    expect(sendCommandMock).toHaveBeenCalledWith("rasterDetach", { names: ["Telecom_s1", "Telecom_s2"] });
    survivesExposure({ action: "raster_detach", names: ["Telecom_s1"] });
    expect(approval("raster_detach")).toBe(true);
    expect(drawing.raster_detach.inputSchema.safeParse({ action: "raster_detach", names: [] }).success).toBe(false);
  });
});

describe("C116 multiline attributes and attribute positions", () => {
  beforeEach(() => sendCommandMock.mockReset());

  it("forwards multiline, width and layer of an attribute definition", async () => {
    sendCommandMock.mockResolvedValue({});
    const attributeDefinitions = [{ tag: "HEADING", multiline: true, width: 85, layer: "DWG-TEXT" }];
    await run("block_definition_set", { blockName: "Stamp", attributeDefinitions });
    expect(sendCommandMock).toHaveBeenCalledWith("setBlockDefinition", { blockName: "Stamp", attributeDefinitions, syncReferences: true });
    survivesExposure({ action: "block_definition_set", blockName: "Stamp", attributeDefinitions });
  });

  it("refuses turning multiline off and a negative width", () => {
    const s = drawing.block_definition_set.inputSchema;
    expect(s.safeParse({ action: "block_definition_set", blockName: "Stamp", attributeDefinitions: [{ tag: "HEADING", multiline: false }] }).success).toBe(false);
    expect(s.safeParse({ action: "block_definition_set", blockName: "Stamp", attributeDefinitions: [{ tag: "HEADING", width: -1 }] }).success).toBe(false);
    expect(s.safeParse({ action: "block_definition_set", blockName: "Stamp", attributeDefinitions: [{ tag: "HEADING", width: 0 }] }).success).toBe(true);
  });

  it("takes attribute positions as [x, y]", () => {
    const s = drawing.block_attributes_set.inputSchema;
    expect(s.safeParse({ action: "block_attributes_set", blockName: "Stamp", positions: { HEADING: [512.5, 40] } }).success).toBe(true);
    expect(s.safeParse({ action: "block_attributes_set", blockName: "Stamp", positions: { HEADING: [512.5] } }).success).toBe(false);
    expect(s.safeParse({ action: "block_attributes_set", blockName: "Stamp", positions: { HEADING: { x: 1, y: 2 } } }).success).toBe(false);
  });
});

describe("C101 point_to_station strict and maxOffset", () => {
  const alignment = ALIGNMENT_DOMAIN_DEFINITION.actions;
  beforeEach(() => sendCommandMock.mockReset());

  it("forwards strict and maxOffset and keeps outOfRange, equallyNearStations and warning in the answer", async () => {
    const answer = {
      station: 0, offset: -1.2, distanceFromAlignment: 1.8, outOfRange: true, units: "meters",
      equallyNearStations: [12.5, 12.7], warning: "The point lies beyond the alignment's end; the end station is given (strict: true makes this an error).",
    };
    sendCommandMock.mockResolvedValue(answer);
    const args = { action: "point_to_station", name: "Main_St_CL", x: 1405120, y: 6223810, strict: false, maxOffset: 5 };
    await alignment.point_to_station.execute(alignment.point_to_station.inputSchema.parse(args) as never);
    expect(sendCommandMock).toHaveBeenCalledWith("alignmentPointToStation", { name: "Main_St_CL", x: 1405120, y: 6223810, strict: false, maxOffset: 5 });
    expect(alignment.point_to_station.responseSchema!.parse(answer)).toEqual(answer);
    const exposure = ALIGNMENT_DOMAIN_DEFINITION.exposures.find((e) => e.toolName === "civil3d_alignment")!;
    expect(z.object(exposure.inputShape).parse(args)).toEqual(args);
    const s = alignment.point_to_station.inputSchema;
    expect(s.safeParse({ ...args, maxOffset: 0 }).success).toBe(false);
    expect(s.safeParse({ action: "point_to_station", name: "A", x: 0, y: 0 }).success).toBe(true);
  });
});

describe("new drawing actions in the catalog", () => {
  it("lists them with their plugin methods", () => {
    const entry = GENERATED_TOOL_CATALOG_ENTRIES.find((e) => e.toolName === "civil3d_drawing")!;
    for (const a of ["entities_id_set", "entities_id_clear", "entities_move", "raster_attach", "raster_detach"]) expect(entry.operations).toContain(a);
    for (const m of ["entityIdsSet", "entityIdsClear", "moveEntities", "rasterAttach", "rasterDetach"]) expect(entry.pluginMethods).toContain(m);
  });
});
