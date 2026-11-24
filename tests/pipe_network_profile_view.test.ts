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
import { PROFILE_DOMAIN_DEFINITION } from "../src/tools/domains/profileDomain.js";
import { GENERATED_TOOL_CATALOG_ENTRIES } from "../src/tools/toolManifest.js";

const pipe = PIPE_DOMAIN_DEFINITION.actions;
const profile = PROFILE_DOMAIN_DEFINITION.actions;

describe("pipe network in a profile view", () => {
  beforeEach(() => sendCommandMock.mockReset());

  it("adds all pipes and structures of a network to a profile view", async () => {
    sendCommandMock.mockResolvedValue({});
    await pipe.network_profile_view_add.execute(pipe.network_profile_view_add.inputSchema.parse({
      action: "network_profile_view_add", networkName: "Spillvatten", profileViewName: "PV_Schakt_HK",
    }));
    expect(sendCommandMock).toHaveBeenCalledWith("addNetworkToProfileView", {
      networkName: "Spillvatten", profileViewName: "PV_Schakt_HK",
    });
  });

  it("needs approval and is exposed on the canonical pipe tool", () => {
    const a = pipe.network_profile_view_add;
    expect(isApprovalRequired({ toolName: "civil3d_pipe", action: "network_profile_view_add", capabilities: a.capabilities, safeForRetry: a.safeForRetry })).toBe(true);
    const entry = GENERATED_TOOL_CATALOG_ENTRIES.find((e) => e.toolName === "civil3d_pipe");
    expect(entry!.operations).toContain("network_profile_view_add");
  });

  it("rejects a call without network or profile view", () => {
    expect(pipe.network_profile_view_add.inputSchema.safeParse({ action: "network_profile_view_add", networkName: "S" }).success).toBe(false);
    expect(pipe.network_profile_view_add.inputSchema.safeParse({ action: "network_profile_view_add", profileViewName: "PV" }).success).toBe(false);
  });
});

describe("profile view bands from a pipe network", () => {
  beforeEach(() => sendCommandMock.mockReset());

  it("forwards a pipe network as a band data source", async () => {
    sendCommandMock.mockResolvedValue({});
    const bands = [{ location: "bottom", index: 7, network: "Spillvatten" }];
    await profile.view_bands_set.execute(profile.view_bands_set.inputSchema.parse({ action: "view_bands_set", profileViewName: "PV", bands }));
    expect(sendCommandMock).toHaveBeenCalledWith("setProfileViewBands", { profileViewName: "PV", bands });
  });
});
