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
import { DRAWING_RUNTIME_DOMAIN_DEFINITION } from "../src/tools/domains/drawingRuntimeDomain.js";
import { GENERATED_TOOL_CATALOG_ENTRIES } from "../src/tools/toolManifest.js";

const stats = DRAWING_RUNTIME_DOMAIN_DEFINITION.actions.layer_stats;

describe("drawing layer statistics", () => {
  beforeEach(() => sendCommandMock.mockReset());

  it("asks the plugin for the largest layers, 60 by default", async () => {
    sendCommandMock.mockResolvedValue({});
    await stats.execute(stats.inputSchema.parse({ action: "layer_stats" }));
    expect(sendCommandMock).toHaveBeenCalledWith("getDrawingLayerStats", { limit: 60 });
    await stats.execute(stats.inputSchema.parse({ action: "layer_stats", limit: 20 }));
    expect(sendCommandMock).toHaveBeenLastCalledWith("getDrawingLayerStats", { limit: 20 });
  });

  it("rejects a non-positive or too large limit", () => {
    expect(stats.inputSchema.safeParse({ action: "layer_stats", limit: 0 }).success).toBe(false);
    expect(stats.inputSchema.safeParse({ action: "layer_stats", limit: 1001 }).success).toBe(false);
  });

  it("is read-only and exposed on the canonical drawing tool", () => {
    expect(isApprovalRequired({ toolName: "civil3d_drawing", action: "layer_stats", capabilities: stats.capabilities, safeForRetry: stats.safeForRetry })).toBe(false);
    const entry = GENERATED_TOOL_CATALOG_ENTRIES.find((e) => e.toolName === "civil3d_drawing");
    expect(entry!.operations).toContain("layer_stats");
  });
});
