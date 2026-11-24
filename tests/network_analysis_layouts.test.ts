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
import { PIPE_DOMAIN_DEFINITION } from "../src/tools/domains/pipeDomain.js";

const pipe = PIPE_DOMAIN_DEFINITION.actions;
const drawing = DRAWING_RUNTIME_DOMAIN_DEFINITION.actions;
const approval = (toolName: string, def: { capabilities: string[]; safeForRetry: boolean }, action: string) =>
  isApprovalRequired({ toolName, action, capabilities: def.capabilities as never, safeForRetry: def.safeForRetry });

describe("network analysis", () => {
  beforeEach(() => sendCommandMock.mockReset());

  it("checks clearance between networks, ignoring a pipe inside its casing", async () => {
    sendCommandMock.mockResolvedValue({});
    await pipe.network_clearance.execute(pipe.network_clearance.inputSchema.parse({
      action: "network_clearance", networks: ["Water", "Sewer", "Casing"], minClearance: 0.3,
      ignorePairs: [["SR315", "V200"]],
    }));
    expect(sendCommandMock).toHaveBeenCalledWith("networkClearance", {
      networks: ["Water", "Sewer", "Casing"], minClearance: 0.3, includeSameRun: false,
      ignorePairs: [["SR315", "V200"]], limit: 100,
    });
    expect(pipe.network_clearance.inputSchema.safeParse({ action: "network_clearance", networks: [] }).success).toBe(false);
    expect(approval("civil3d_pipe", pipe.network_clearance, "network_clearance")).toBe(false);
  });

  it("sums quantities per network and run", async () => {
    sendCommandMock.mockResolvedValue({});
    await pipe.network_quantities.execute(pipe.network_quantities.inputSchema.parse({ action: "network_quantities" }));
    expect(sendCommandMock).toHaveBeenCalledWith("networkQuantities", { networks: undefined });
    expect(approval("civil3d_pipe", pipe.network_quantities, "network_quantities")).toBe(false);
  });
});

describe("layouts", () => {
  beforeEach(() => sendCommandMock.mockReset());

  it("lists layouts without approval", async () => {
    sendCommandMock.mockResolvedValue({});
    await drawing.layout_list.execute(drawing.layout_list.inputSchema.parse({ action: "layout_list" }));
    expect(sendCommandMock).toHaveBeenCalledWith("listLayouts", { layout: undefined });
    expect(approval("civil3d_drawing", drawing.layout_list, "layout_list")).toBe(false);
  });

  it("lists title block attributes without approval", async () => {
    sendCommandMock.mockResolvedValue({});
    const s = drawing.block_attributes_get.inputSchema;
    await drawing.block_attributes_get.execute(s.parse({ action: "block_attributes_get", layout: "C-101", blockName: "Stamp" }));
    expect(sendCommandMock).toHaveBeenCalledWith("getBlockAttributes", { layout: "C-101", blockName: "Stamp" });
    expect(s.safeParse({ action: "block_attributes_get" }).success).toBe(false);
    expect(approval("civil3d_drawing", drawing.block_attributes_get, "block_attributes_get")).toBe(false);
  });

  it("sets title block attributes with approval", async () => {
    sendCommandMock.mockResolvedValue({});
    const s = drawing.block_attributes_set.inputSchema;
    await drawing.block_attributes_set.execute(s.parse({
      action: "block_attributes_set", layout: "A1 Plan over Profile 1-1000", blockName: "Title block", values: { DWGNO: "V-101", DATE: "2026-10-06" },
    }));
    expect(sendCommandMock).toHaveBeenCalledWith("setBlockAttributes", {
      layout: "A1 Plan over Profile 1-1000", blockName: "Title block", values: { DWGNO: "V-101", DATE: "2026-10-06" },
    });
    expect(s.safeParse({ action: "block_attributes_set", blockName: "B", values: {} }).success).toBe(false);
    expect(approval("civil3d_drawing", drawing.block_attributes_set, "block_attributes_set")).toBe(true);
  });

  // C52: the Stamp block's attributes sat outside the fields the template draws.
  it("moves title block attributes into their fields and sets heights", async () => {
    sendCommandMock.mockResolvedValue({});
    const s = drawing.block_attributes_set.inputSchema;
    const args = { action: "block_attributes_set", layout: "C-101", blockName: "Stamp", positions: { FORMAT: [758.5, 35.5], SCALE: [771.5, 35.5] }, heights: { SCALE: 3 } };
    await drawing.block_attributes_set.execute(s.parse(args));
    expect(sendCommandMock).toHaveBeenCalledWith("setBlockAttributes", {
      layout: "C-101", blockName: "Stamp", values: undefined, positions: { FORMAT: [758.5, 35.5], SCALE: [771.5, 35.5] }, heights: { SCALE: 3 },
    });
    expect(s.safeParse({ action: "block_attributes_set", blockName: "Stamp", positions: { FORMAT: [1] } }).success).toBe(false);
    expect(s.safeParse({ action: "block_attributes_set", blockName: "Stamp", heights: { FORMAT: 0 } }).success).toBe(false);
    expect(s.safeParse({ action: "block_attributes_set", blockName: "Stamp", positions: {} }).success).toBe(false);
    const shape = z.object(DRAWING_RUNTIME_DOMAIN_DEFINITION.exposures.find((e) => e.toolName === "civil3d_drawing")!.inputShape);
    expect(Object.keys(shape.parse(args)).sort()).toEqual(Object.keys(args).sort());
  });

  it("adds a note to a layout", async () => {
    sendCommandMock.mockResolvedValue({});
    await drawing.layout_text_add.execute(drawing.layout_text_add.inputSchema.parse({
      action: "layout_text_add", layout: "Sheet 1", text: "Overflow chambers: flat-bottom plastic chamber DN600.", x: 20, y: 60,
    }));
    expect(sendCommandMock).toHaveBeenCalledWith("addLayoutText", {
      layout: "Sheet 1", text: "Overflow chambers: flat-bottom plastic chamber DN600.", x: 20, y: 60, height: 2.5, width: 0, layer: undefined,
    });
    expect(approval("civil3d_drawing", drawing.layout_text_add, "layout_text_add")).toBe(true);
  });
});
