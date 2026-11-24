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
type Action = "view_parts_get" | "view_parts_set" | "view_styles_list" | "view_style_set";

function approvalRequired(action: Action): boolean {
  return isApprovalRequired({
    toolName: "civil3d_profile",
    action,
    capabilities: profile[action].capabilities,
    safeForRetry: profile[action].safeForRetry,
  });
}

describe("parts and style of a profile view", () => {
  beforeEach(() => sendCommandMock.mockReset());

  it("hides one network's pipes in a profile view through setProfileViewParts", async () => {
    sendCommandMock.mockResolvedValue({});
    await profile.view_parts_set.execute(profile.view_parts_set.inputSchema.parse({
      action: "view_parts_set", profileViewName: "PV_Schakt_HK", network: "Spillvatten", partKind: "pipe", drawParts: false,
    }));
    expect(sendCommandMock).toHaveBeenCalledWith("setProfileViewParts", {
      profileViewName: "PV_Schakt_HK", network: "Spillvatten", namePrefix: undefined, partKind: "pipe",
      drawParts: false, overrideStyle: undefined,
    });
  });

  it("needs a change, and a pipe or structure kind for a style override", () => {
    const s = profile.view_parts_set.inputSchema;
    const base = { action: "view_parts_set", profileViewName: "PV" };
    expect(s.safeParse(base).success).toBe(false);
    expect(s.safeParse({ ...base, overrideStyle: "Ledningspaket" }).success).toBe(false);
    expect(s.safeParse({ ...base, overrideStyle: "Ledningspaket", partKind: "all" }).success).toBe(false);
    expect(s.safeParse({ ...base, overrideStyle: "Ledningspaket", partKind: "pipe" }).success).toBe(true);
    expect(s.safeParse({ ...base, overrideStyle: "", partKind: "all" }).success).toBe(true);   // clears overrides
    expect(s.safeParse({ ...base, drawParts: true }).success).toBe(true);
  });

  it("reads parts and styles, sets the profile view style", async () => {
    sendCommandMock.mockResolvedValue({});
    await profile.view_parts_get.execute(profile.view_parts_get.inputSchema.parse({ action: "view_parts_get", profileViewName: "PV" }));
    expect(sendCommandMock).toHaveBeenLastCalledWith("getProfileViewParts", {
      profileViewName: "PV", network: undefined, namePrefix: undefined, partKind: undefined,
    });
    await profile.view_styles_list.execute(profile.view_styles_list.inputSchema.parse({ action: "view_styles_list" }));
    expect(sendCommandMock).toHaveBeenLastCalledWith("listProfileViewStyles", {});
    await profile.view_style_set.execute(profile.view_style_set.inputSchema.parse({ action: "view_style_set", profileViewName: "PV", style: "1:4" }));
    expect(sendCommandMock).toHaveBeenLastCalledWith("setProfileViewStyle", { profileViewName: "PV", style: "1:4" });
  });

  it("reading needs no approval, changing does", () => {
    expect(approvalRequired("view_parts_get")).toBe(false);
    expect(approvalRequired("view_styles_list")).toBe(false);
    expect(approvalRequired("view_parts_set")).toBe(true);
    expect(approvalRequired("view_style_set")).toBe(true);
  });

  it("is exposed on the canonical profile tool", () => {
    const entry = GENERATED_TOOL_CATALOG_ENTRIES.find((e) => e.toolName === "civil3d_profile");
    for (const a of ["view_parts_get", "view_parts_set", "view_styles_list", "view_style_set"]) {
      expect(entry!.operations).toContain(a);
    }
  });
});
