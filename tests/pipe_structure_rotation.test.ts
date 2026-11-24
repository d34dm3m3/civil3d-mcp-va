import { beforeEach, describe, expect, it, vi } from "vitest";
import { z } from "zod";

const { sendCommandMock } = vi.hoisted(() => ({ sendCommandMock: vi.fn() }));

vi.mock("../src/utils/ConnectionManager.js", () => ({
  withApplicationConnection: async <T>(
    operation: (client: { sendCommand: typeof sendCommandMock }) => Promise<T>,
  ) => await operation({ sendCommand: sendCommandMock }),
}));

import { isApprovalRequired } from "../src/tools/approvalPolicy.js";
import { PIPE_DOMAIN_DEFINITION } from "../src/tools/domains/pipeDomain.js";
import { GENERATED_TOOL_CATALOG_ENTRIES } from "../src/tools/toolManifest.js";

const pipe = PIPE_DOMAIN_DEFINITION.actions;
const shape = z.object(PIPE_DOMAIN_DEFINITION.exposures.find((e) => e.toolName === "civil3d_pipe")!.inputShape);

describe("C46 structure rotation", () => {
  beforeEach(() => sendCommandMock.mockReset());

  it("aligns valve structures to their pipes with an offset", async () => {
    sendCommandMock.mockResolvedValue({});
    const args = { action: "structure_rotation_set", networkName: "Vatten", partFamily: "_AV", namePrefix: "V200", alignToPipe: true, offsetDegrees: 90 };
    await pipe.structure_rotation_set.execute(pipe.structure_rotation_set.inputSchema.parse(args));
    expect(sendCommandMock).toHaveBeenCalledWith("setStructureRotation", {
      networkName: "Vatten", structureNames: undefined, namePrefix: "V200", partFamily: "_AV", rotationDegrees: undefined, alignToPipe: true, offsetDegrees: 90,
    });
    expect(Object.keys(shape.parse(args)).sort()).toEqual(Object.keys(args).sort());
  });

  it("sets an absolute rotation for named structures", async () => {
    sendCommandMock.mockResolvedValue({});
    const args = { action: "structure_rotation_set", networkName: "Vatten", structureNames: ["V200-B003"], rotationDegrees: -21 };
    await pipe.structure_rotation_set.execute(pipe.structure_rotation_set.inputSchema.parse(args));
    expect(sendCommandMock).toHaveBeenCalledWith("setStructureRotation", expect.objectContaining({ structureNames: ["V200-B003"], rotationDegrees: -21, alignToPipe: false, offsetDegrees: 0 }));
    expect(Object.keys(shape.parse(args)).sort()).toEqual(Object.keys(args).sort());
  });

  it("needs exactly one of rotationDegrees and alignToPipe, and approval", () => {
    const s = pipe.structure_rotation_set.inputSchema;
    const base = { action: "structure_rotation_set", networkName: "Vatten" };
    expect(s.safeParse(base).success).toBe(false);
    expect(s.safeParse({ ...base, rotationDegrees: 10, alignToPipe: true }).success).toBe(false);
    expect(s.safeParse({ ...base, alignToPipe: false }).success).toBe(false);
    expect(s.safeParse({ ...base, structureNames: [], alignToPipe: true }).success).toBe(false);
    expect(isApprovalRequired({ toolName: "civil3d_pipe", action: "structure_rotation_set", capabilities: pipe.structure_rotation_set.capabilities, safeForRetry: pipe.structure_rotation_set.safeForRetry })).toBe(true);
    const entry = GENERATED_TOOL_CATALOG_ENTRIES.find((e) => e.toolName === "civil3d_pipe");
    expect(entry!.operations).toContain("structure_rotation_set");
  });

  it("lets network_build align its structures to the pipes", async () => {
    sendCommandMock.mockResolvedValue({});
    const args = {
      action: "network_build", networkName: "Vatten", partsList: "Vatten", pipeFamily: "F", pipeDiameter: 176.2, namePrefix: "V200",
      vertices: [{ x: 0, y: 0, z: 10 }, { x: 10, y: -4, z: 9.9, structure: "_AV" }], alignStructuresToPipes: true,
    };
    await pipe.network_build.execute(pipe.network_build.inputSchema.parse(args));
    expect(sendCommandMock.mock.calls[0][1]).toMatchObject({ alignStructuresToPipes: true });
    expect(Object.keys(shape.parse(args)).sort()).toEqual(Object.keys(args).sort());
  });
});
