import { beforeEach, describe, expect, it, vi } from "vitest";

const { sendCommandMock } = vi.hoisted(() => ({ sendCommandMock: vi.fn() }));

vi.mock("../src/utils/ConnectionManager.js", () => ({
  withApplicationConnection: async <T>(
    operation: (client: { sendCommand: typeof sendCommandMock }) => Promise<T>,
  ) => await operation({ sendCommand: sendCommandMock }),
}));

import { isApprovalRequired } from "../src/tools/approvalPolicy.js";
import { DRAWING_RUNTIME_DOMAIN_DEFINITION } from "../src/tools/domains/drawingRuntimeDomain.js";
import { GENERATED_TOOL_CATALOG_ENTRIES } from "../src/tools/toolManifest.js";

const drawing = DRAWING_RUNTIME_DOMAIN_DEFINITION.actions;

describe("PNG of a model-space window or profile view", () => {
  beforeEach(() => sendCommandMock.mockReset());

  it("plots a profile view through plotViewPng", async () => {
    sendCommandMock.mockResolvedValue({});
    await drawing.view_png.execute(drawing.view_png.inputSchema.parse({
      action: "view_png", output: "D:/tmp/pv.png", profileViewName: "PV_Schakt_HK",
    }));
    expect(sendCommandMock).toHaveBeenCalledWith("plotViewPng", {
      output: "D:/tmp/pv.png", overwrite: false, profileViewName: "PV_Schakt_HK", x: undefined, y: undefined,
      width: undefined, height: undefined, margin: undefined, media: undefined, plotStyleTable: undefined,
    });
  });

  it("needs exactly one of a profile view and a window", () => {
    const s = drawing.view_png.inputSchema;
    const base = { action: "view_png", output: "D:/tmp/a.png" };
    expect(s.safeParse(base).success).toBe(false);
    expect(s.safeParse({ ...base, x: 0, y: 0, width: 10, height: 5 }).success).toBe(true);
    expect(s.safeParse({ ...base, x: 0, y: 0, width: 10 }).success).toBe(false);
    expect(s.safeParse({ ...base, profileViewName: "PV", x: 0, y: 0, width: 10, height: 5 }).success).toBe(false);
  });

  it("needs no approval and is on the canonical drawing tool", () => {
    expect(isApprovalRequired({ toolName: "civil3d_drawing", action: "view_png", capabilities: drawing.view_png.capabilities, safeForRetry: drawing.view_png.safeForRetry })).toBe(false);
    const entry = GENERATED_TOOL_CATALOG_ENTRIES.find((e) => e.toolName === "civil3d_drawing");
    expect(entry!.operations).toContain("view_png");
  });
});
