import { beforeEach, describe, expect, it, vi } from "vitest";
import { z } from "zod";

const { sendCommandMock } = vi.hoisted(() => ({ sendCommandMock: vi.fn() }));

vi.mock("../src/utils/ConnectionManager.js", () => ({
  withApplicationConnection: async <T>(
    operation: (client: { sendCommand: typeof sendCommandMock }) => Promise<T>,
  ) => await operation({ sendCommand: sendCommandMock }),
}));

import { isApprovalRequired } from "../src/tools/approvalPolicy.js";
import { PROFILE_DOMAIN_DEFINITION } from "../src/tools/domains/profileDomain.js";
import { GENERATED_TOOL_CATALOG_ENTRIES } from "../src/tools/toolManifest.js";

const profile = PROFILE_DOMAIN_DEFINITION.actions;
const approval = (action: string) =>
  isApprovalRequired({ toolName: "civil3d_profile", action, capabilities: profile[action].capabilities as never, safeForRetry: profile[action].safeForRetry });
const run = async (action: string, args: Record<string, unknown>) =>
  await profile[action].execute(profile[action].inputSchema.parse({ action, ...args }) as never);
const survivesExposure = (args: Record<string, unknown>) => {
  const exposure = PROFILE_DOMAIN_DEFINITION.exposures.find((e) => e.toolName === "civil3d_profile")!;
  const parsed = z.object(exposure.inputShape).parse(args) as Record<string, unknown>;
  expect(parsed).toEqual(args);
};

describe("C103 profile from a pipe network", () => {
  beforeEach(() => sendCommandMock.mockReset());

  it("forwards every parameter to profileFromNetwork behind approval", async () => {
    sendCommandMock.mockResolvedValue({ vertices: [], dryRun: true });
    const args = {
      networkName: "Sewer", alignmentName: "Main_St_CL", profileName: "Top_Sewer", reference: "top",
      minSpacing: 6, maxOffset: 3, endSnap: 1.5, fromStation: 0, toStation: 220, fixedStations: [12.5, 104],
      pipeNames: ["S1", "S2"], replace: true, dryRun: true, style: "Package", layer: "C-PROF", labelSet: "None",
      drawInProfileViews: ["PV_Main_St"],
    };
    await run("create_from_network", args);
    expect(sendCommandMock).toHaveBeenCalledWith("profileFromNetwork", args);
    survivesExposure({ action: "create_from_network", ...args });
    expect(approval("create_from_network")).toBe(true);
  });

  it("needs network, alignment and profile, and checks reference, spacing and the station window", () => {
    const s = profile.create_from_network.inputSchema;
    const base = { action: "create_from_network", networkName: "Sewer", alignmentName: "A", profileName: "P" };
    expect(s.safeParse(base).success).toBe(true);
    expect(s.safeParse({ ...base, networkName: undefined }).success).toBe(false);
    expect(s.safeParse({ ...base, profileName: "" }).success).toBe(false);
    expect(s.safeParse({ ...base, reference: "crown" }).success).toBe(false);
    expect(s.safeParse({ ...base, reference: "center" }).success).toBe(true);
    expect(s.safeParse({ ...base, minSpacing: -1 }).success).toBe(false);
    expect(s.safeParse({ ...base, maxOffset: 0 }).success).toBe(false);
    expect(s.safeParse({ ...base, endSnap: -0.5 }).success).toBe(false);
    expect(s.safeParse({ ...base, fromStation: 50, toStation: 20 }).success).toBe(false);
    expect(s.safeParse({ ...base, pipeNames: [] }).success).toBe(false);
    expect(s.safeParse({ ...base, drawInProfileViews: true }).success).toBe(true);
    expect(s.safeParse({ ...base, drawInProfileViews: [] }).success).toBe(false);
  });

  // C102: before this, the canonical profile tool dropped drawInProfileViews before create_from_surface/create_layout saw it.
  it("keeps drawInProfileViews through the canonical tool for the other profile creators", async () => {
    sendCommandMock.mockResolvedValue({});
    const args = { action: "create_layout", alignmentName: "A", profileName: "New", drawInProfileViews: true };
    survivesExposure(args);
    await profile.create_layout.execute(profile.create_layout.inputSchema.parse(args) as never);
    expect(sendCommandMock).toHaveBeenLastCalledWith("createLayoutProfile", expect.objectContaining({ drawInProfileViews: true }));
    survivesExposure({ action: "create_from_surface", alignmentName: "A", profileName: "Ground", surfaceName: "EG", drawInProfileViews: ["PV1"] });
  });
});

describe("C104 band set applied band by band", () => {
  beforeEach(() => sendCommandMock.mockReset());

  it("forwards band set, mode, default sources, per-band sources and label defaults behind approval", async () => {
    sendCommandMock.mockResolvedValue({ warnings: [] });
    const args = {
      profileViewName: "PV_Main_St", bandSet: "Package - Stationing", mode: "add", profile1: "Ground", network: "Sewer",
      matchIncrementToGridIntervals: false,
      bands: [
        { index: 0, location: "bottom", profile1: "Top_Sewer", profile2: "Ground" },
        { style: "Package - Top", occurrence: 2, network: "Storm", showLabels: true, verticalGeometryPoints: ["all"] },
      ],
      labels: { showLabels: true, labelAtStartStation: true, labelAtEndStation: true, horizontalGeometryPoints: ["all"] },
    };
    await run("view_band_set_apply", args);
    expect(sendCommandMock).toHaveBeenCalledWith("profileViewApplyBandSet", args);
    survivesExposure({ action: "view_band_set_apply", ...args });
    expect(approval("view_band_set_apply")).toBe(true);
  });

  it("gives each band by index or by style, occurrence only with style", () => {
    const s = profile.view_band_set_apply.inputSchema;
    const base = { action: "view_band_set_apply", profileViewName: "PV", bandSet: "Package" };
    expect(s.safeParse(base).success).toBe(true);
    expect(s.safeParse({ ...base, bandSet: "" }).success).toBe(false);
    expect(s.safeParse({ ...base, mode: "replace" }).success).toBe(false);
    expect(s.safeParse({ ...base, bands: [{ profile1: "Ground" }] }).success).toBe(false);
    expect(s.safeParse({ ...base, bands: [{ index: 0, style: "Package - Top" }] }).success).toBe(false);
    expect(s.safeParse({ ...base, bands: [{ index: 1, occurrence: 2 }] }).success).toBe(false);
    expect(s.safeParse({ ...base, bands: [{ style: "Package - Top", occurrence: 0 }] }).success).toBe(false);
    expect(s.safeParse({ ...base, bands: [{ index: 0, location: "side" }] }).success).toBe(false);
    expect(s.safeParse({ ...base, bands: [{ style: "Package - Top", occurrence: 2, location: "top" }] }).success).toBe(true);
  });

  it("still keeps view_bands_set items whole through the canonical tool", () => {
    survivesExposure({ action: "view_bands_set", profileViewName: "PV", bands: [{ location: "bottom", index: 1, profile1: "Ground", majorInterval: 50 }] });
    expect(profile.view_bands_set.inputSchema.safeParse({ action: "view_bands_set", profileViewName: "PV", bands: [{ style: "x" }] }).success).toBe(false);
  });
});

describe("C106 profile view delete", () => {
  beforeEach(() => sendCommandMock.mockReset());

  it("erases a profile view or only reports with dryRun, behind approval", async () => {
    sendCommandMock.mockResolvedValue({ erased: false, dryRun: true });
    await run("view_delete", { profileViewName: "PV_old", dryRun: true });
    expect(sendCommandMock).toHaveBeenLastCalledWith("profileViewDelete", { profileViewName: "PV_old", dryRun: true });
    await run("view_delete", { profileViewName: "PV_old" });
    expect(sendCommandMock).toHaveBeenLastCalledWith("profileViewDelete", { profileViewName: "PV_old", dryRun: undefined });
    survivesExposure({ action: "view_delete", profileViewName: "PV_old", dryRun: true });
    expect(approval("view_delete")).toBe(true);
    expect(profile.view_delete.inputSchema.safeParse({ action: "view_delete", profileViewName: "" }).success).toBe(false);
  });
});

describe("new profile actions on the canonical tool", () => {
  it("lists them with their plugin methods", () => {
    const entry = GENERATED_TOOL_CATALOG_ENTRIES.find((e) => e.toolName === "civil3d_profile")!;
    for (const a of ["create_from_network", "view_band_set_apply", "view_delete"]) expect(entry.operations).toContain(a);
    for (const m of ["profileFromNetwork", "profileViewApplyBandSet", "profileViewDelete"]) expect(entry.pluginMethods).toContain(m);
  });
});
