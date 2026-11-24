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
import { PROFILE_DOMAIN_DEFINITION } from "../src/tools/domains/profileDomain.js";
import { GENERATED_TOOL_CATALOG_ENTRIES } from "../src/tools/toolManifest.js";

const profile = PROFILE_DOMAIN_DEFINITION.actions;

function approvalRequired(action: "view_profiles_get" | "view_profiles_set"): boolean {
  return isApprovalRequired({
    toolName: "civil3d_profile",
    action,
    capabilities: profile[action].capabilities,
    safeForRetry: profile[action].safeForRetry,
  });
}

describe("profiles drawn in a profile view", () => {
  beforeEach(() => sendCommandMock.mockReset());

  it("lists the profiles of a profile view through getProfileViewProfiles", async () => {
    sendCommandMock.mockResolvedValue({});
    await profile.view_profiles_get.execute(profile.view_profiles_get.inputSchema.parse({
      action: "view_profiles_get", profileViewName: "PV_Schakt_HK",
    }));
    expect(sendCommandMock).toHaveBeenCalledWith("getProfileViewProfiles", { profileViewName: "PV_Schakt_HK" });
  });

  it("sets Draw per profile through setProfileViewProfiles", async () => {
    sendCommandMock.mockResolvedValue({});
    const draw = { Mark_laz: true, Schaktbotten_laz: true, Mark_bef_mh: false, Schaktbotten: false };
    await profile.view_profiles_set.execute(profile.view_profiles_set.inputSchema.parse({
      action: "view_profiles_set", profileViewName: "PV_Schakt_HK", draw,
    }));
    expect(sendCommandMock).toHaveBeenCalledWith("setProfileViewProfiles", { profileViewName: "PV_Schakt_HK", draw });
  });

  it("rejects an empty draw map", () => {
    expect(profile.view_profiles_set.inputSchema.safeParse({
      action: "view_profiles_set", profileViewName: "PV_Schakt_HK", draw: {},
    }).success).toBe(false);
  });

  it("reading needs no approval, changing does", () => {
    expect(approvalRequired("view_profiles_get")).toBe(false);
    expect(approvalRequired("view_profiles_set")).toBe(true);
  });

  it("is exposed on the canonical profile tool", () => {
    const entry = GENERATED_TOOL_CATALOG_ENTRIES.find((e) => e.toolName === "civil3d_profile");
    expect(entry!.operations).toContain("view_profiles_get");
    expect(entry!.operations).toContain("view_profiles_set");
  });
});
