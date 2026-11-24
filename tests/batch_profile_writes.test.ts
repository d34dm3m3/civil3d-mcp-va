import { beforeEach, describe, expect, it, vi } from "vitest";
import { z } from "zod";

const { sendCommandMock } = vi.hoisted(() => ({ sendCommandMock: vi.fn() }));

vi.mock("../src/utils/ConnectionManager.js", () => ({
  withApplicationConnection: async <T>(
    operation: (client: { sendCommand: typeof sendCommandMock }) => Promise<T>,
  ) => await operation({ sendCommand: sendCommandMock }),
}));

import { PIPE_DOMAIN_DEFINITION } from "../src/tools/domains/pipeDomain.js";
import { PROFILE_DOMAIN_DEFINITION } from "../src/tools/domains/profileDomain.js";

const pipe = PIPE_DOMAIN_DEFINITION.actions;
const profile = PROFILE_DOMAIN_DEFINITION.actions;
const pipeShape = z.object(PIPE_DOMAIN_DEFINITION.exposures.find((e) => e.toolName === "civil3d_pipe")!.inputShape);
const profileShape = z.object(PROFILE_DOMAIN_DEFINITION.exposures.find((e) => e.toolName === "civil3d_profile")!.inputShape);

// C63: Civil 3D updates the profile views after every committed profile edit (about 100 s live), so these
// writes go in one transaction.
describe("C63 batched profile writes", () => {
  beforeEach(() => sendCommandMock.mockReset());

  it("adds several networks to several profile views in one call", async () => {
    sendCommandMock.mockResolvedValue({});
    const args = { action: "network_profile_view_add", networkNames: ["Spillvatten", "Fiber"], profileViewNames: ["PV_R101", "PV_R102"] };
    await pipe.network_profile_view_add.execute(pipe.network_profile_view_add.inputSchema.parse(args));
    expect(sendCommandMock).toHaveBeenCalledWith("addNetworkToProfileView", expect.objectContaining({
      networkNames: ["Spillvatten", "Fiber"], profileViewNames: ["PV_R101", "PV_R102"],
    }));
    expect(Object.keys(pipeShape.parse(args)).sort()).toEqual(Object.keys(args).sort());
  });

  it("keeps the single form and needs exactly one of name and names", () => {
    const s = pipe.network_profile_view_add.inputSchema;
    expect(s.safeParse({ action: "network_profile_view_add", networkName: "Fiber", profileViewName: "PV_R101" }).success).toBe(true);
    expect(s.safeParse({ action: "network_profile_view_add", networkName: "Fiber", networkNames: ["Fiber"], profileViewName: "PV_R101" }).success).toBe(false);
    expect(s.safeParse({ action: "network_profile_view_add", networkNames: ["Fiber"] }).success).toBe(false);
  });

  it("lets network_build keep the run in its profile views", async () => {
    sendCommandMock.mockResolvedValue({});
    const args = {
      action: "network_build", networkName: "Fiber", partsList: "Vatten", pipeFamily: "F", pipeDiameter: 32.6, namePrefix: "Fiber",
      replace: true, vertices: [{ x: 0, y: 0, z: 10 }, { x: 10, y: 0, z: 10 }], keepProfileViews: true,
    };
    await pipe.network_build.execute(pipe.network_build.inputSchema.parse(args));
    expect(sendCommandMock.mock.calls[0][1]).toMatchObject({ keepProfileViews: true });
    expect(Object.keys(pipeShape.parse(args)).sort()).toEqual(Object.keys(args).sort());
  });

  it("adds many PVIs at once, optionally replacing all", async () => {
    sendCommandMock.mockResolvedValue({});
    const args = { action: "add_pvi", alignmentName: "A", profileName: "P", pvis: [{ station: 0, elevation: 40 }, { station: 10, elevation: 39.5 }], replaceAll: true };
    await profile.add_pvi.execute(profile.add_pvi.inputSchema.parse(args));
    expect(sendCommandMock).toHaveBeenCalledWith("profileAddPvi", expect.objectContaining({ pvis: args.pvis, replaceAll: true }));
    expect(Object.keys(profileShape.parse(args)).sort()).toEqual(Object.keys(args).sort());
    const s = profile.add_pvi.inputSchema;
    expect(s.safeParse({ action: "add_pvi", alignmentName: "A", profileName: "P", station: 1, elevation: 2 }).success).toBe(true);
    expect(s.safeParse({ action: "add_pvi", alignmentName: "A", profileName: "P", station: 1, elevation: 2, replaceAll: true }).success).toBe(false);
    expect(s.safeParse({ action: "add_pvi", alignmentName: "A", profileName: "P" }).success).toBe(false);
  });
});
