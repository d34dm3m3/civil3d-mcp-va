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
type Act = "view_bands_get" | "view_bands_set" | "label_styles_list" | "line_labels_add";
const approval = (a: Act) => isApprovalRequired({
  toolName: "civil3d_profile", action: a, capabilities: profile[a].capabilities, safeForRetry: profile[a].safeForRetry,
});

describe("profile view bands", () => {
  beforeEach(() => sendCommandMock.mockReset());

  it("reads the bands of a profile view", async () => {
    sendCommandMock.mockResolvedValue({});
    await profile.view_bands_get.execute(profile.view_bands_get.inputSchema.parse({ action: "view_bands_get", profileViewName: "PV" }));
    expect(sendCommandMock).toHaveBeenCalledWith("getProfileViewBands", { profileViewName: "PV" });
  });

  it("sets band data sources and intervals by location and index", async () => {
    sendCommandMock.mockResolvedValue({});
    const bands = [{ location: "bottom", index: 0, profile1: "Ground_laz", profile2: "Trench_bottom_laz", majorInterval: 20 }];
    await profile.view_bands_set.execute(profile.view_bands_set.inputSchema.parse({ action: "view_bands_set", profileViewName: "PV", bands }));
    expect(sendCommandMock).toHaveBeenCalledWith("setProfileViewBands", { profileViewName: "PV", bands });
  });

  // C53: a band added through the API labelled no vertical geometry points, so "Package - Invert" stayed empty.
  it("forwards the geometry points to label and the label switches (C53)", async () => {
    sendCommandMock.mockResolvedValue({});
    const bands = [{ location: "bottom", index: 3, showLabels: true, gap: 0, verticalGeometryPoints: ["PVI", "Start", "End"], horizontalGeometryPoints: [] }];
    await profile.view_bands_set.execute(profile.view_bands_set.inputSchema.parse({ action: "view_bands_set", profileViewName: "PV_C101", bands }));
    expect(sendCommandMock).toHaveBeenCalledWith("setProfileViewBands", { profileViewName: "PV_C101", bands });
  });

  // C55: with the grid match on, the bands' own intervals were overwritten by the grid's (100/20).
  it("turns off the grid match and weeds close labels (C55)", async () => {
    sendCommandMock.mockResolvedValue({});
    const bands = [{ location: "bottom", index: 0, majorInterval: 50, minorInterval: 10 }, { location: "bottom", index: 3, weeding: 0.003 }];
    await profile.view_bands_set.execute(profile.view_bands_set.inputSchema.parse({
      action: "view_bands_set", profileViewName: "PV_C101", bands, matchIncrementToGridIntervals: false,
    }));
    expect(sendCommandMock).toHaveBeenCalledWith("setProfileViewBands", { profileViewName: "PV_C101", bands, matchIncrementToGridIntervals: false });
    expect(profile.view_bands_set.inputSchema.safeParse({ action: "view_bands_set", profileViewName: "PV", matchIncrementToGridIntervals: false }).success).toBe(true);
    expect(profile.view_bands_set.inputSchema.safeParse({ action: "view_bands_set", profileViewName: "PV" }).success).toBe(false);
  });

  it("rejects an empty band list and a negative index", () => {
    expect(profile.view_bands_set.inputSchema.safeParse({ action: "view_bands_set", profileViewName: "PV", bands: [] }).success).toBe(false);
    expect(profile.view_bands_set.inputSchema.safeParse({
      action: "view_bands_set", profileViewName: "PV", bands: [{ location: "bottom", index: -1, profile1: "M" }],
    }).success).toBe(false);
  });
});

describe("profile labels", () => {
  beforeEach(() => sendCommandMock.mockReset());

  it("lists profile label styles of a kind", async () => {
    sendCommandMock.mockResolvedValue({});
    await profile.label_styles_list.execute(profile.label_styles_list.inputSchema.parse({ action: "label_styles_list", labelKind: "line" }));
    expect(sendCommandMock).toHaveBeenCalledWith("listProfileLabelStyles", { labelKind: "line" });
  });

  it("adds line labels (length and grade) to a profile in a profile view", async () => {
    sendCommandMock.mockResolvedValue({});
    await profile.line_labels_add.execute(profile.line_labels_add.inputSchema.parse({
      action: "line_labels_add", profileViewName: "PV", profileName: "Trench_bottom_laz", style: "Grade and length",
    }));
    expect(sendCommandMock).toHaveBeenCalledWith("addProfileLineLabels", {
      profileViewName: "PV", profileName: "Trench_bottom_laz", style: "Grade and length", replace: false,
    });
  });

  it("reads need no approval, writes do", () => {
    expect(approval("view_bands_get")).toBe(false);
    expect(approval("label_styles_list")).toBe(false);
    expect(approval("view_bands_set")).toBe(true);
    expect(approval("line_labels_add")).toBe(true);
  });

  it("exposes the actions on the canonical profile tool", () => {
    const entry = GENERATED_TOOL_CATALOG_ENTRIES.find((e) => e.toolName === "civil3d_profile");
    for (const a of ["view_bands_get", "view_bands_set", "label_styles_list", "line_labels_add"]) {
      expect(entry!.operations).toContain(a);
    }
  });
});
