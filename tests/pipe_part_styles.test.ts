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
import { PIPE_DOMAIN_DEFINITION } from "../src/tools/domains/pipeDomain.js";

const pipe = PIPE_DOMAIN_DEFINITION.actions;
const approval = (def: { capabilities: string[]; safeForRetry: boolean }, action: string) =>
  isApprovalRequired({ toolName: "civil3d_pipe", action, capabilities: def.capabilities as never, safeForRetry: def.safeForRetry });

describe("pipe and structure styles", () => {
  beforeEach(() => sendCommandMock.mockReset());

  it("reads a structure style without approval", async () => {
    sendCommandMock.mockResolvedValue({});
    await pipe.part_style_get.execute(pipe.part_style_get.inputSchema.parse({ action: "part_style_get", kind: "structure", style: "Propp V" }));
    expect(sendCommandMock).toHaveBeenCalledWith("getPartStyle", { kind: "structure", style: "Propp V" });
    expect(approval(pipe.part_style_get, "part_style_get")).toBe(false);
  });

  it("copies and changes a style: plan size and outer walls", async () => {
    sendCommandMock.mockResolvedValue({});
    await pipe.part_style_copy.execute(pipe.part_style_copy.inputSchema.parse({ action: "part_style_copy", kind: "structure", style: "Propp V", newName: "Propp V liten" }));
    expect(sendCommandMock).toHaveBeenLastCalledWith("copyPartStyle", { kind: "structure", style: "Propp V", newName: "Propp V liten" });
    const s = pipe.part_style_set.inputSchema;
    const input = s.parse({
      action: "part_style_set", kind: "pipe", style: "Vatten PE",
      options: { plan: { WallSizeType: "UseAbsoluteUnits" } },
      display: { plan: { OutsideWalls: { visible: false }, Centerline: { color: 150, lineweight: 50 } } },
    });
    await pipe.part_style_set.execute(input);
    expect(sendCommandMock.mock.calls[1][1]).toMatchObject({ kind: "pipe", style: "Vatten PE", display: { plan: { OutsideWalls: { visible: false } } } });
    expect(s.safeParse({ action: "part_style_set", kind: "pipe", style: "X" }).success).toBe(false);
    expect(s.safeParse({ action: "part_style_set", kind: "pipe", style: "X", display: { roof: { A: { visible: true } } } }).success).toBe(false);
    expect(approval(pipe.part_style_set, "part_style_set")).toBe(true);
    expect(approval(pipe.part_style_copy, "part_style_copy")).toBe(true);
  });
});
