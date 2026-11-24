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
import { GENERATED_TOOL_CATALOG_ENTRIES } from "../src/tools/toolManifest.js";

const pipe = PIPE_DOMAIN_DEFINITION.actions;

const vertices = [
  { x: 0, y: 0, z: 10 },
  { x: 10, y: 0, z: 9.9, structure: "Air valve" },
  { x: 20, y: 0, z: 9.8 },
];

describe("pipe network build", () => {
  beforeEach(() => sendCommandMock.mockReset());

  it("builds a run of pipes and structures in one call", async () => {
    sendCommandMock.mockResolvedValue({});
    await pipe.network_build.execute(pipe.network_build.inputSchema.parse({
      action: "network_build", networkName: "Sewer", partsList: "Sewer", referenceSurface: "ground",
      pipeFamily: "PE100 pressure pipe PN10", pipeDiameter: 198.2, namePrefix: "S225", vertices,
    }));
    expect(sendCommandMock).toHaveBeenCalledWith("buildPipeNetworkRun", {
      networkName: "Sewer", partsList: "Sewer", referenceSurface: "ground", referenceAlignment: undefined,
      pipeFamily: "PE100 pressure pipe PN10", pipeDiameter: 198.2, namePrefix: "S225",
      defaultStructure: "Brytpunkt", replace: false, vertices,
    });
  });

  it("needs at least two vertices, a positive diameter and a name prefix", () => {
    const base = { action: "network_build", networkName: "N", partsList: "P", pipeFamily: "F", pipeDiameter: 100, namePrefix: "X" };
    expect(pipe.network_build.inputSchema.safeParse({ ...base, vertices: [vertices[0]] }).success).toBe(false);
    expect(pipe.network_build.inputSchema.safeParse({ ...base, vertices, pipeDiameter: 0 }).success).toBe(false);
    expect(pipe.network_build.inputSchema.safeParse({ ...base, vertices, namePrefix: "" }).success).toBe(false);
    expect(pipe.network_build.inputSchema.safeParse({ ...base, vertices }).success).toBe(true);
  });

  it("passes a structure size (mm) for families with several sizes", async () => {
    sendCommandMock.mockResolvedValue({});
    const sized = [vertices[0], { x: 10, y: 0, z: 9.9, structure: "Inspection chamber", structureDiameter: 400 }];
    const input = pipe.network_build.inputSchema.parse({
      action: "network_build", networkName: "Casing", partsList: "Sewer", pipeFamily: "F", pipeDiameter: 312.8,
      namePrefix: "SR355", vertices: sized,
    });
    await pipe.network_build.execute(input);
    expect(sendCommandMock.mock.calls[0][1].vertices[1]).toEqual(sized[1]);
    const bad = [vertices[0], { ...sized[1], structureDiameter: 0 }];
    expect(pipe.network_build.inputSchema.safeParse({ ...input, vertices: bad }).success).toBe(false);
  });

  it("deletes a pipe network by name", async () => {
    sendCommandMock.mockResolvedValue({});
    await pipe.network_delete.execute(pipe.network_delete.inputSchema.parse({ action: "network_delete", networkName: "Sewer" }));
    expect(sendCommandMock).toHaveBeenCalledWith("deletePipeNetwork", { networkName: "Sewer" });
  });

  it("both need approval and are exposed on the canonical pipe tool", () => {
    for (const a of ["network_build", "network_delete"] as const) {
      expect(isApprovalRequired({ toolName: "civil3d_pipe", action: a, capabilities: pipe[a].capabilities, safeForRetry: pipe[a].safeForRetry })).toBe(true);
    }
    const entry = GENERATED_TOOL_CATALOG_ENTRIES.find((e) => e.toolName === "civil3d_pipe");
    expect(entry!.operations).toEqual(expect.arrayContaining(["network_build", "network_delete"]));
  });
});
