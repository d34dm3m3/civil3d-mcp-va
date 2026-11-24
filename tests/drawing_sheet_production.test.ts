import { beforeEach, describe, expect, it, vi } from "vitest";
import { z } from "zod";

const { sendCommandMock } = vi.hoisted(() => ({ sendCommandMock: vi.fn() }));

vi.mock("../src/utils/ConnectionManager.js", () => ({
  withApplicationConnection: async <T>(
    operation: (client: { sendCommand: typeof sendCommandMock }) => Promise<T>,
  ) => await operation({ sendCommand: sendCommandMock }),
}));

import { isApprovalRequired } from "../src/tools/approvalPolicy.js";
import { DRAWING_RUNTIME_DOMAIN_DEFINITION } from "../src/tools/domains/drawingRuntimeDomain.js";
import { GEOMETRY_DOMAIN_DEFINITION } from "../src/tools/domains/geometryDomain.js";
import { GENERATED_TOOL_CATALOG_ENTRIES } from "../src/tools/toolManifest.js";

const drawing = DRAWING_RUNTIME_DOMAIN_DEFINITION.actions;
const geometry = GEOMETRY_DOMAIN_DEFINITION.actions;
const approval = (toolName: string, action: string, def: { capabilities: string[]; safeForRetry: boolean }) =>
  isApprovalRequired({ toolName, action, capabilities: def.capabilities as never, safeForRetry: def.safeForRetry });
const run = async (domain: typeof drawing, action: string, args: Record<string, unknown>) =>
  await domain[action].execute(domain[action].inputSchema.parse({ action, ...args }) as never);

// Every field of a full argument set must survive the MCP tool's input shape (unknown keys are stripped there).
const survivesExposure = (toolName: string, args: Record<string, unknown>) => {
  const exposure = [...DRAWING_RUNTIME_DOMAIN_DEFINITION.exposures, ...GEOMETRY_DOMAIN_DEFINITION.exposures].find((e) => e.toolName === toolName)!;
  const parsed = z.object(exposure.inputShape).parse(args) as Record<string, unknown>;
  expect(Object.keys(parsed).sort()).toEqual(Object.keys(args).sort());
};

describe("C37 multileaders and erase", () => {
  beforeEach(() => sendCommandMock.mockReset());

  it("adds a multileader, forwarding every parameter with defaults", async () => {
    sendCommandMock.mockResolvedValue({});
    const args = {
      x: 100, y: 200, textX: 104, textY: 203, text: "V200 PE\nSDR 11", textHeight: 1, rotationDegrees: -32.5,
      layer: "VA-TEXT", createLayer: true, backgroundColor: 254, arrowSize: 0.8, attachment: "left",
    };
    await run(drawing, "mleader_add", args);
    expect(sendCommandMock).toHaveBeenCalledWith("addMLeader", { ...args, landing: true, frame: false });
    survivesExposure("civil3d_drawing", { action: "mleader_add", ...args, frame: true, landing: false });
  });

  it("validates multileader input and needs approval", () => {
    const s = drawing.mleader_add.inputSchema;
    const base = { action: "mleader_add", x: 0, y: 0, textX: 1, textY: 1, text: "A", textHeight: 1 };
    expect(s.safeParse(base).success).toBe(true);
    expect(s.safeParse({ ...base, textHeight: 0 }).success).toBe(false);
    expect(s.safeParse({ ...base, backgroundColor: 256 }).success).toBe(false);
    expect(s.safeParse({ ...base, attachment: "top" }).success).toBe(false);
    expect(approval("civil3d_drawing", "mleader_add", drawing.mleader_add)).toBe(true);
  });

  it("erases by handle with approval and lists multileaders", async () => {
    sendCommandMock.mockResolvedValue({});
    await run(drawing, "entities_erase", { handles: ["4A1", "4a2"] });
    expect(sendCommandMock).toHaveBeenCalledWith("eraseEntities", { handles: ["4A1", "4a2"] });
    expect(drawing.entities_erase.inputSchema.safeParse({ action: "entities_erase", handles: [] }).success).toBe(false);
    expect(drawing.entities_erase.inputSchema.safeParse({ action: "entities_erase", handles: ["XYZ"] }).success).toBe(false);
    expect(approval("civil3d_drawing", "entities_erase", drawing.entities_erase)).toBe(true);
    expect(drawing.entities_list.inputSchema.safeParse({ action: "entities_list", types: ["mleader", "text"] }).success).toBe(true);
  });
});

describe("C38 blocks", () => {
  beforeEach(() => sendCommandMock.mockReset());

  it("lists the blocks of another drawing without approval", async () => {
    sendCommandMock.mockResolvedValue({});
    await run(drawing, "block_list_external", { sourcePath: "D:/Mallar/Symboler.dwg" });
    expect(sendCommandMock).toHaveBeenCalledWith("listExternalBlocks", { sourcePath: "D:/Mallar/Symboler.dwg" });
    expect(drawing.block_list_external.inputSchema.safeParse({ action: "block_list_external", sourcePath: "D:/a.pdf" }).success).toBe(false);
    expect(approval("civil3d_drawing", "block_list_external", drawing.block_list_external)).toBe(false);
  });

  it("imports blocks (skip existing by default) with approval", async () => {
    sendCommandMock.mockResolvedValue({});
    await run(drawing, "block_import", { sourcePath: "D:/Mallar/Symboler.dwg", blockNames: ["Norrpil", "Ritningshuvud"] });
    expect(sendCommandMock).toHaveBeenCalledWith("importBlocks", { sourcePath: "D:/Mallar/Symboler.dwg", blockNames: ["Norrpil", "Ritningshuvud"], overwrite: false });
    expect(drawing.block_import.inputSchema.safeParse({ action: "block_import", sourcePath: "D:/a.dwg", blockNames: [] }).success).toBe(false);
    expect(approval("civil3d_drawing", "block_import", drawing.block_import)).toBe(true);
  });

  it("inserts a block with attributes in a layout", async () => {
    sendCommandMock.mockResolvedValue({});
    const args = { blockName: "Norrpil", x: 800, y: 560, z: 0, rotationDegrees: 32.5, scale: 1, layer: "Ritram", createLayer: true, layout: "Blad 1", attributes: { SKALA: "1:400" } };
    await run(drawing, "block_insert", args);
    expect(sendCommandMock).toHaveBeenCalledWith("insertBlock", args);
    survivesExposure("civil3d_drawing", { action: "block_insert", ...args });
    expect(drawing.block_insert.inputSchema.safeParse({ action: "block_insert", blockName: "B", x: 0, y: 0, scale: 0 }).success).toBe(false);
    expect(approval("civil3d_drawing", "block_insert", drawing.block_insert)).toBe(true);
  });

  it("keeps scale_set's scale name working beside block_insert's numeric scale", () => {
    expect(drawing.scale_set.inputSchema.safeParse({ action: "scale_set", scale: "1:400" }).success).toBe(true);
    expect(drawing.scale_set.inputSchema.safeParse({ action: "scale_set", scale: 400 }).success).toBe(false);
    survivesExposure("civil3d_drawing", { action: "scale_set", scale: "1:400" });
  });
});

describe("C39 layouts and viewports", () => {
  beforeEach(() => sendCommandMock.mockReset());

  it("copies and deletes layouts", async () => {
    sendCommandMock.mockResolvedValue({});
    await run(drawing, "layout_create", { name: "Blad 2", copyFrom: "Blad 1" });
    expect(sendCommandMock).toHaveBeenLastCalledWith("createLayout", { name: "Blad 2", copyFrom: "Blad 1" });
    await run(drawing, "layout_delete", { name: "Blad 2" });
    expect(sendCommandMock).toHaveBeenLastCalledWith("deleteLayout", { name: "Blad 2" });
    expect(drawing.layout_delete.inputSchema.safeParse({ action: "layout_delete", name: "Model" }).success).toBe(false);
    expect(drawing.layout_create.inputSchema.safeParse({ action: "layout_create", name: "Blad 2" }).success).toBe(false);
    expect(approval("civil3d_drawing", "layout_create", drawing.layout_create)).toBe(true);
    expect(approval("civil3d_drawing", "layout_delete", drawing.layout_delete)).toBe(true);
  });

  it("creates a viewport centred on a model point at 1:400 with twist", async () => {
    sendCommandMock.mockResolvedValue({});
    const args = {
      layout: "Blad 1", centerX: 420, centerY: 400, width: 780, height: 300,
      viewCenterX: 192890, viewCenterY: 6232500, twistDegrees: -32.5, scaleDenominator: 400,
      locked: true, layer: "VP", frozenLayers: ["C-TOPO-Text"],
    };
    await run(drawing, "viewport_create", args);
    expect(sendCommandMock).toHaveBeenCalledWith("createViewport", { ...args, modelUnitsInMm: 1000, createLayer: false });
    survivesExposure("civil3d_drawing", { action: "viewport_create", ...args, modelUnitsInMm: 1000, createLayer: true });
    const s = drawing.viewport_create.inputSchema;
    const { scaleDenominator: _scale, ...noScale } = args;
    expect(s.safeParse({ action: "viewport_create", ...noScale }).success).toBe(false);
    expect(s.safeParse({ action: "viewport_create", ...args, width: 0 }).success).toBe(false);
  });

  it("changes and erases a viewport by handle", async () => {
    sendCommandMock.mockResolvedValue({});
    await run(drawing, "viewport_set", { layout: "Blad 1", handle: "5F2", twistDegrees: 10 });
    expect(sendCommandMock).toHaveBeenLastCalledWith("setViewport", { layout: "Blad 1", handle: "5F2", twistDegrees: 10, modelUnitsInMm: 1000, createLayer: false });
    const s = drawing.viewport_set.inputSchema;
    expect(s.safeParse({ action: "viewport_set", layout: "Blad 1", handle: "5F2" }).success).toBe(false);
    expect(s.safeParse({ action: "viewport_set", layout: "Blad 1", handle: "5F2", viewCenterX: 1 }).success).toBe(false);
    expect(s.safeParse({ action: "viewport_set", layout: "Blad 1", handle: "5F2", frozenLayers: [] }).success).toBe(true);
    // C52: paper centre and size
    expect(s.safeParse({ action: "viewport_set", layout: "Blad 1", handle: "5F2", centerX: 380.5, centerY: 452, width: 681, height: 244 }).success).toBe(true);
    expect(s.safeParse({ action: "viewport_set", layout: "Blad 1", handle: "5F2", centerX: 380.5 }).success).toBe(false);
    expect(s.safeParse({ action: "viewport_set", layout: "Blad 1", handle: "5F2", height: 0 }).success).toBe(false);
    await run(drawing, "viewport_erase", { layout: "Blad 1", handle: "5F2" });
    expect(sendCommandMock).toHaveBeenLastCalledWith("eraseViewport", { layout: "Blad 1", handle: "5F2" });
    expect(approval("civil3d_drawing", "viewport_set", drawing.viewport_set)).toBe(true);
    expect(approval("civil3d_drawing", "viewport_erase", drawing.viewport_erase)).toBe(true);
  });
});

describe("C40 system variables", () => {
  beforeEach(() => sendCommandMock.mockReset());

  it("reads and sets allowlisted variables only", async () => {
    sendCommandMock.mockResolvedValue({});
    await run(drawing, "sysvar_get", { name: "ltscale" });
    expect(sendCommandMock).toHaveBeenLastCalledWith("getSystemVariable", { name: "LTSCALE" });
    await run(drawing, "sysvar_set", { name: "MSLTSCALE", value: 1 });
    expect(sendCommandMock).toHaveBeenLastCalledWith("setSystemVariable", { name: "MSLTSCALE", value: 1 });
    expect(drawing.sysvar_set.inputSchema.safeParse({ action: "sysvar_set", name: "FILEDIA", value: 0 }).success).toBe(false);
    expect(drawing.sysvar_set.inputSchema.safeParse({ action: "sysvar_set", name: "LTSCALE" }).success).toBe(false);
    survivesExposure("civil3d_drawing", { action: "sysvar_set", name: "CANNOSCALE", value: "1:400" });
    expect(approval("civil3d_drawing", "sysvar_get", drawing.sysvar_get)).toBe(false);
    expect(approval("civil3d_drawing", "sysvar_set", drawing.sysvar_set)).toBe(true);
  });
});

describe("C41 layout plot", () => {
  beforeEach(() => sendCommandMock.mockReset());

  it("plots a layout to PDF without approval", async () => {
    sendCommandMock.mockResolvedValue({});
    await run(drawing, "layout_plot", { layout: "Blad 1", output: "D:/tmp/blad1.pdf", plotStyleTable: "monochrome.ctb" });
    expect(sendCommandMock).toHaveBeenCalledWith("plotLayout", { layout: "Blad 1", output: "D:/tmp/blad1.pdf", plotStyleTable: "monochrome.ctb", overwrite: false });
    expect(drawing.layout_plot.inputSchema.safeParse({ action: "layout_plot", layout: "Blad 1", output: "D:/tmp/a.dwg" }).success).toBe(false);
    expect(approval("civil3d_drawing", "layout_plot", drawing.layout_plot)).toBe(false);
  });

  it("exposes every new action on civil3d_drawing", () => {
    const entry = GENERATED_TOOL_CATALOG_ENTRIES.find((e) => e.toolName === "civil3d_drawing");
    expect(entry!.operations).toEqual(expect.arrayContaining([
      "mleader_add", "entities_erase", "block_list_external", "block_import", "block_insert", "layout_create", "layout_delete",
      "viewport_create", "viewport_set", "viewport_erase", "sysvar_get", "sysvar_set", "layout_plot",
    ]));
  });
});

describe("C44 drafting: line styles, layers, text rotation in degrees", () => {
  beforeEach(() => sendCommandMock.mockReset());

  it("draws a polyline with linetype, colour, lineweight, linetype scale and width", async () => {
    sendCommandMock.mockResolvedValue({});
    const style = { linetype: "BH90_VA_S_TR_PROJ", color: "ByLayer", lineweight: 35, linetypeScale: 0.5 };
    await run(geometry, "create_polyline", { points: [{ x: 0, y: 0 }, { x: 10, y: 0 }], layer: "VA-SPILL-CNTR", width: 0.2, ...style });
    expect(sendCommandMock).toHaveBeenCalledWith("createPolyline", {
      points: [{ x: 0, y: 0 }, { x: 10, y: 0 }], closed: 0, layer: "VA-SPILL-CNTR", createLayer: false, width: 0.2, ...style,
    });
    survivesExposure("civil3d_geometry", { action: "create_polyline", points: [{ x: 0, y: 0 }, { x: 1, y: 1 }], width: 0.2, ...style });
    survivesExposure("acad_create_polyline", { points: [{ x: 0, y: 0 }, { x: 1, y: 1 }], width: 0.2, ...style });
    const s = geometry.create_polyline.inputSchema;
    expect(s.safeParse({ action: "create_polyline", points: [{ x: 0, y: 0 }, { x: 1, y: 1 }], color: 300 }).success).toBe(false);
    expect(s.safeParse({ action: "create_polyline", points: [{ x: 0, y: 0 }, { x: 1, y: 1 }], lineweight: "Thick" }).success).toBe(false);
  });

  it("forwards layer and createLayer for line segments and texts, and rotationDegrees", async () => {
    sendCommandMock.mockResolvedValue({});
    await run(geometry, "create_line_segment", { startX: 0, startY: 0, endX: 5, endY: 0, layer: "Legend", createLayer: true, color: 1 });
    expect(sendCommandMock).toHaveBeenLastCalledWith("createLineSegment", expect.objectContaining({ layer: "Legend", createLayer: true, color: 1 }));
    await run(geometry, "create_text", { text: "Spillvatten", x: 0, y: 0, rotationDegrees: 30, layer: "Legend", createLayer: true });
    expect(sendCommandMock).toHaveBeenLastCalledWith("createText", expect.objectContaining({ rotationDegrees: 30, layer: "Legend", createLayer: true }));
    await run(geometry, "create_mtext", { text: "Vatten", x: 0, y: 0, rotationDegrees: -15, createLayer: false });
    expect(sendCommandMock).toHaveBeenLastCalledWith("createMText", expect.objectContaining({ rotationDegrees: -15, createLayer: false }));
    survivesExposure("acad_create_text", { text: "A", x: 0, y: 0, rotationDegrees: 30, createLayer: true });
    survivesExposure("create_line_segment", { startX: 0, startY: 0, endX: 1, endY: 1, layer: "L", createLayer: true, linetype: "DASHED" });
  });
});

describe("C47 layer and entity display properties", () => {
  beforeEach(() => sendCommandMock.mockReset());

  it("sets layer colour, linetype, lineweight and plot; on/frozen still work alone", async () => {
    sendCommandMock.mockResolvedValue({});
    const args = { layers: ["VA-SPILL-CNTR"], color: 30, linetype: "BH90_VA_S_TR_PROJ", lineweight: 35, plot: true };
    await run(drawing, "layer_set", args);
    expect(sendCommandMock).toHaveBeenCalledWith("setLayers", { ...args, on: undefined, frozen: undefined });
    survivesExposure("civil3d_drawing", { action: "layer_set", ...args });
    const s = drawing.layer_set.inputSchema;
    expect(s.safeParse({ action: "layer_set", layers: ["A"], frozen: true }).success).toBe(true);
    expect(s.safeParse({ action: "layer_set", layers: ["A"], lineweight: "Default" }).success).toBe(true);
    expect(s.safeParse({ action: "layer_set", layers: ["A"], color: 0 }).success).toBe(false);
    expect(s.safeParse({ action: "layer_set", layers: ["A"] }).success).toBe(false);
  });

  it("sets entity display properties by handle with approval", async () => {
    sendCommandMock.mockResolvedValue({});
    const args = { handles: ["4A1"], color: "ByLayer", linetype: "ByLayer", lineweight: 25, linetypeScale: 0.5 };
    await run(drawing, "entities_style_set", args);
    expect(sendCommandMock).toHaveBeenCalledWith("setEntitiesStyle", args);
    survivesExposure("civil3d_drawing", { action: "entities_style_set", ...args });
    expect(drawing.entities_style_set.inputSchema.safeParse({ action: "entities_style_set", handles: ["4A1"] }).success).toBe(false);
    expect(approval("civil3d_drawing", "entities_style_set", drawing.entities_style_set)).toBe(true);
  });

  it("lists loaded linetypes without approval", async () => {
    sendCommandMock.mockResolvedValue({});
    await run(drawing, "linetypes_list", { contains: "BH90" });
    expect(sendCommandMock).toHaveBeenCalledWith("listLinetypes", { contains: "BH90" });
    expect(approval("civil3d_drawing", "linetypes_list", drawing.linetypes_list)).toBe(false);
  });
});
