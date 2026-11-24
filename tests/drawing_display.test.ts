import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { z } from "zod";

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
import { DRAWING_RUNTIME_DOMAIN_DEFINITION } from "../src/tools/domains/drawingRuntimeDomain.js";
import { GENERATED_TOOL_CATALOG_ENTRIES } from "../src/tools/toolManifest.js";

const drawing = DRAWING_RUNTIME_DOMAIN_DEFINITION.actions;

describe("drawing display: styles and layers", () => {
  beforeEach(() => sendCommandMock.mockReset());

  it("sets the style of a surface", async () => {
    sendCommandMock.mockResolvedValue({});
    await drawing.style_set.execute(drawing.style_set.inputSchema.parse({
      action: "style_set", objectType: "surface", name: "Trench_datum", style: "No display - surface",
    }));
    expect(sendCommandMock).toHaveBeenCalledWith("setObjectStyle", expect.objectContaining({
      objectType: "surface", name: "Trench_datum", style: "No display - surface",
    }));
  });

  it("validates what each object type needs", () => {
    const s = drawing.style_set.inputSchema;
    expect(s.safeParse({ action: "style_set", objectType: "surface", name: "Y" }).success).toBe(false);
    expect(s.safeParse({ action: "style_set", objectType: "pipe_network", name: "Sewer", style: "x" }).success).toBe(false);
    expect(s.safeParse({ action: "style_set", objectType: "pipe_network", name: "Sewer", pipeStyle: "Sewer force main", namePrefix: "S225" }).success).toBe(true);
    expect(s.safeParse({ action: "style_set", objectType: "profile", name: "P", style: "x" }).success).toBe(false);
    expect(s.safeParse({ action: "style_set", objectType: "profile", name: "P", style: "x", alignmentName: "A" }).success).toBe(true);
  });

  it("sets a corridor's code set style (feature lines) without its corridor style", async () => {
    sendCommandMock.mockResolvedValue({});
    const s = drawing.style_set.inputSchema;
    await drawing.style_set.execute(s.parse({ action: "style_set", objectType: "corridor", name: "Trench", codeSetStyle: "No display" }));
    expect(sendCommandMock.mock.calls[0][1]).toMatchObject({ objectType: "corridor", name: "Trench", codeSetStyle: "No display" });
    expect(s.safeParse({ action: "style_set", objectType: "corridor", name: "Trench" }).success).toBe(false);
  });

  it("turns layers on/off and freezes them, and lists layers", async () => {
    sendCommandMock.mockResolvedValue({});
    await drawing.layer_set.execute(drawing.layer_set.inputSchema.parse({ action: "layer_set", layers: ["Wall.geo"], on: false }));
    expect(sendCommandMock).toHaveBeenCalledWith("setLayers", { layers: ["Wall.geo"], on: false, frozen: undefined });
    expect(drawing.layer_set.inputSchema.safeParse({ action: "layer_set", layers: ["A"] }).success).toBe(false);
    expect(drawing.layer_set.inputSchema.safeParse({ action: "layer_set", layers: [], on: true }).success).toBe(false);
    await drawing.layer_list.execute(drawing.layer_list.inputSchema.parse({ action: "layer_list", contains: "geo" }));
    expect(sendCommandMock).toHaveBeenLastCalledWith("listLayers", { contains: "geo" });
  });

  it("lists plain entities by layer and type without approval", async () => {
    sendCommandMock.mockResolvedValue({});
    await drawing.entities_list.execute(drawing.entities_list.inputSchema.parse({
      action: "entities_list", layer: "_manhole line", types: ["line", "polyline"],
    }));
    expect(sendCommandMock).toHaveBeenCalledWith("listEntities", {
      layer: "_manhole line", layerContains: undefined, types: ["line", "polyline"], limit: 200, maxVertices: 500, densifyArcs: undefined,
    });
    expect(drawing.entities_list.inputSchema.safeParse({ action: "entities_list", types: ["spline"] }).success).toBe(false);
    expect(isApprovalRequired({ toolName: "civil3d_drawing", action: "entities_list", capabilities: drawing.entities_list.capabilities, safeForRetry: drawing.entities_list.safeForRetry })).toBe(false);
  });

  // C49: a buffer around a centre line with an arc (R ≈ 18 m) was computed along the chord, 3.9 m off.
  it("forwards densifyArcs so polyline arcs come back as points (C49)", async () => {
    sendCommandMock.mockResolvedValue({});
    const args = { action: "entities_list", layer: "Road_centerline", types: ["polyline"], densifyArcs: 0.005 };
    await drawing.entities_list.execute(drawing.entities_list.inputSchema.parse(args));
    expect(sendCommandMock).toHaveBeenCalledWith("listEntities", {
      layer: "Road_centerline", layerContains: undefined, types: ["polyline"], limit: 200, maxVertices: 500, densifyArcs: 0.005,
    });
    const shape = z.object(DRAWING_RUNTIME_DOMAIN_DEFINITION.exposures.find((e) => e.toolName === "civil3d_drawing")!.inputShape);
    expect(Object.keys(shape.parse(args)).sort()).toEqual(Object.keys(args).sort());
    const s = drawing.entities_list.inputSchema;
    expect(s.safeParse({ action: "entities_list", densifyArcs: 0 }).success).toBe(false);
    expect(s.safeParse({ action: "entities_list", densifyArcs: -0.01 }).success).toBe(false);
    expect(s.safeParse({ action: "entities_list", densifyArcs: 0.0005 }).success).toBe(false);
    expect(s.safeParse({ action: "entities_list", densifyArcs: 0.001 }).success).toBe(true);
  });

  // C52: the template's scale bar and north arrow sit in a paper layout, not in model space.
  it("lists a paper layout's entities (C52)", async () => {
    sendCommandMock.mockResolvedValue({});
    const args = { action: "entities_list", layout: "C-101", types: ["insert"] };
    await drawing.entities_list.execute(drawing.entities_list.inputSchema.parse(args));
    expect(sendCommandMock).toHaveBeenCalledWith("listEntities", {
      layer: undefined, layerContains: undefined, types: ["insert"], limit: 200, maxVertices: 500, densifyArcs: undefined, layout: "C-101",
    });
    const shape = z.object(DRAWING_RUNTIME_DOMAIN_DEFINITION.exposures.find((e) => e.toolName === "civil3d_drawing")!.inputShape);
    expect(Object.keys(shape.parse(args)).sort()).toEqual(Object.keys(args).sort());
  });

  it("returns bulges per vertex and caps densified points in the plugin (C49)", () => {
    const source = readFileSync(fileURLToPath(new URL("../Civil3D-MCP-Plugin/EntityQueryCommands.cs", import.meta.url)), "utf8");
    expect(source).toContain('GetOptionalDouble(parameters, "densifyArcs")');
    expect(source).toMatch(/MaxDensifiedPoints = 20000;/);
    expect(source).toMatch(/MinDensifyTolerance = 0\.001;/);
    expect(source.match(/d\["bulges"\] = bulges\.Take\(maxVertices\)/g)).toHaveLength(2);
    expect(source.match(/d\["vertices"\] = points\.Take\(maxVertices\)/g)).toHaveLength(2);
    expect(source).toContain('d["densified"] = points;');
    expect(source).toContain('d["densifiedOmitted"]');
  });

  it("moves entities by handle to a layer, with approval", async () => {
    sendCommandMock.mockResolvedValue({});
    const s = drawing.entities_layer_set.inputSchema;
    await drawing.entities_layer_set.execute(s.parse({ action: "entities_layer_set", handles: ["405D2C8", "405dce0"], layer: "_Trench_model", createLayer: true }));
    expect(sendCommandMock).toHaveBeenCalledWith("setEntitiesLayer", { handles: ["405D2C8", "405dce0"], layer: "_Trench_model", createLayer: true });
    expect(s.safeParse({ action: "entities_layer_set", handles: ["XYZ"], layer: "L" }).success).toBe(false);
    expect(s.safeParse({ action: "entities_layer_set", handles: [], layer: "L" }).success).toBe(false);
    expect(drawing.entities_list.inputSchema.safeParse({ action: "entities_list", types: ["image"] }).success).toBe(true);
    expect(isApprovalRequired({ toolName: "civil3d_drawing", action: "entities_layer_set", capabilities: drawing.entities_layer_set.capabilities, safeForRetry: drawing.entities_layer_set.safeForRetry })).toBe(true);
  });

  it("turns a corridor's feature lines off, all codes or some", async () => {
    sendCommandMock.mockResolvedValue({});
    const s = drawing.corridor_feature_lines_set.inputSchema;
    await drawing.corridor_feature_lines_set.execute(s.parse({ action: "corridor_feature_lines_set", name: "Trench", connected: false }));
    expect(sendCommandMock).toHaveBeenCalledWith("setCorridorFeatureLines", { name: "Trench", connected: false, codes: undefined, rebuild: true });
    expect(s.safeParse({ action: "corridor_feature_lines_set", name: "Trench" }).success).toBe(false);
    expect(isApprovalRequired({ toolName: "civil3d_drawing", action: "corridor_feature_lines_set", capabilities: drawing.corridor_feature_lines_set.capabilities, safeForRetry: drawing.corridor_feature_lines_set.safeForRetry })).toBe(true);
  });

  it("writes need approval, listing does not; all are on civil3d_drawing", () => {
    const needs = (a: "style_set" | "layer_set" | "layer_list") =>
      isApprovalRequired({ toolName: "civil3d_drawing", action: a, capabilities: drawing[a].capabilities, safeForRetry: drawing[a].safeForRetry });
    expect(needs("style_set")).toBe(true);
    expect(needs("layer_set")).toBe(true);
    expect(needs("layer_list")).toBe(false);
    const entry = GENERATED_TOOL_CATALOG_ENTRIES.find((e) => e.toolName === "civil3d_drawing");
    expect(entry!.operations).toEqual(expect.arrayContaining(["style_set", "layer_list", "layer_set"]));
  });
});
