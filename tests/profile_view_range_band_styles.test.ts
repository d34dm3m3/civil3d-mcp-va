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
  isApprovalRequired({ toolName: "civil3d_profile", action, capabilities: profile[action].capabilities, safeForRetry: profile[action].safeForRetry });
const shape = z.object(PROFILE_DOMAIN_DEFINITION.exposures.find((e) => e.toolName === "civil3d_profile")!.inputShape);

describe("C42 profile view station and elevation range", () => {
  beforeEach(() => sendCommandMock.mockReset());

  it("sets a user station range and automatic elevations", async () => {
    sendCommandMock.mockResolvedValue({});
    const args = { action: "view_range_set", profileViewName: "PV_Trench", startStation: 0, endStation: 312.5, automaticElevation: true };
    await profile.view_range_set.execute(profile.view_range_set.inputSchema.parse(args));
    expect(sendCommandMock).toHaveBeenCalledWith("setProfileViewRange", {
      profileViewName: "PV_Trench", startStation: 0, endStation: 312.5, automaticStation: undefined,
      minElevation: undefined, maxElevation: undefined, automaticElevation: true,
    });
    expect(Object.keys(shape.parse(args)).sort()).toEqual(Object.keys(args).sort());
    expect(Object.keys(shape.parse({ ...args, automaticElevation: undefined, minElevation: 40, maxElevation: 60, automaticStation: false })))
      .toEqual(expect.arrayContaining(["minElevation", "maxElevation", "automaticStation"]));
    expect(approval("view_range_set")).toBe(true);
  });

  it("validates the ranges", () => {
    const s = profile.view_range_set.inputSchema;
    const pv = { action: "view_range_set", profileViewName: "PV" };
    expect(s.safeParse(pv).success).toBe(false);
    expect(s.safeParse({ ...pv, startStation: 10 }).success).toBe(false);
    expect(s.safeParse({ ...pv, startStation: 10, endStation: 5 }).success).toBe(false);
    expect(s.safeParse({ ...pv, minElevation: 40, maxElevation: 60, automaticElevation: true }).success).toBe(false);
    expect(s.safeParse({ ...pv, automaticStation: true }).success).toBe(true);
    expect(s.safeParse({ ...pv, minElevation: 40, maxElevation: 60 }).success).toBe(true);
  });
});

describe("C43 band styles", () => {
  beforeEach(() => sendCommandMock.mockReset());

  it("lists band styles, optionally for one band type (case-insensitive)", async () => {
    sendCommandMock.mockResolvedValue({});
    await profile.band_styles_list.execute(profile.band_styles_list.inputSchema.parse({ action: "band_styles_list" }));
    expect(sendCommandMock).toHaveBeenLastCalledWith("listBandStyles", { bandType: undefined });
    await profile.band_styles_list.execute(profile.band_styles_list.inputSchema.parse({ action: "band_styles_list", bandType: "pipenetwork" }));
    expect(sendCommandMock).toHaveBeenLastCalledWith("listBandStyles", { bandType: "PipeNetwork" });
    expect(profile.band_styles_list.inputSchema.safeParse({ action: "band_styles_list", bandType: "Cant" }).success).toBe(false);
    expect(approval("band_styles_list")).toBe(false);
  });

  it("reads one band style's label groups without approval", async () => {
    sendCommandMock.mockResolvedValue({});
    await profile.band_style_get.execute(profile.band_style_get.inputSchema.parse({ action: "band_style_get", bandType: "ProfileData", style: "Elevations" }));
    expect(sendCommandMock).toHaveBeenCalledWith("getBandStyle", { bandType: "ProfileData", style: "Elevations" });
    expect(profile.band_style_get.inputSchema.safeParse({ action: "band_style_get", bandType: "ProfileData" }).success).toBe(false);
    expect(approval("band_style_get")).toBe(false);
    const entry = GENERATED_TOOL_CATALOG_ENTRIES.find((e) => e.toolName === "civil3d_profile");
    expect(entry!.operations).toEqual(expect.arrayContaining(["view_range_set", "band_styles_list", "band_style_get"]));
  });
});

describe("C45 band style copy and set", () => {
  beforeEach(() => sendCommandMock.mockReset());

  it("copies a band style under a new name with approval", async () => {
    sendCommandMock.mockResolvedValue({});
    const args = { action: "band_style_copy", bandType: "profiledata", style: "Elevations", newName: "Material and size" };
    await profile.band_style_copy.execute(profile.band_style_copy.inputSchema.parse(args));
    expect(sendCommandMock).toHaveBeenCalledWith("copyBandStyle", { bandType: "ProfileData", style: "Elevations", newName: "Material and size" });
    expect(profile.band_style_copy.inputSchema.safeParse({ ...args, newName: "" }).success).toBe(false);
    expect(Object.keys(shape.parse(args)).sort()).toEqual(Object.keys(args).sort());
    expect(approval("band_style_copy")).toBe(true);
  });

  it("sets title, height and label groups, forwarding every field", async () => {
    sendCommandMock.mockResolvedValue({});
    const groups = [
      { location: "verticalGeometryPoints", labelsVisible: true, ticksVisible: true, labelText: "<[Profile1 Elevation(Um|P2|RN|AP|GC|UN|SN|OF)]>", labelComponent: "Elevation" },
      { location: "majorStations", labelsVisible: false, ticksVisible: false },
    ];
    const args = { action: "band_style_set", bandType: "ProfileData", style: "Invert", titleText: "INVERT\\PLEVEL", bandHeight: 0.005, titleBoxWidth: 0.02, groups };
    await profile.band_style_set.execute(profile.band_style_set.inputSchema.parse(args));
    expect(sendCommandMock).toHaveBeenCalledWith("setBandStyle", {
      bandType: "ProfileData", style: "Invert", titleText: "INVERT\\PLEVEL", bandHeight: 0.005, titleBoxWidth: 0.02, groups,
    });
    const parsed = shape.parse(args) as Record<string, unknown>;
    expect(Object.keys(parsed).sort()).toEqual(Object.keys(args).sort());
    expect(parsed.groups).toEqual(groups);
    expect(approval("band_style_set")).toBe(true);
    const entry = GENERATED_TOOL_CATALOG_ENTRIES.find((e) => e.toolName === "civil3d_profile");
    expect(entry!.operations).toEqual(expect.arrayContaining(["band_style_copy", "band_style_set"]));
  });

  // C52: the elevation component of the vertical geometry points was hidden in the copied band style.
  it("shows a label text component (C52)", async () => {
    sendCommandMock.mockResolvedValue({});
    const groups = [{ location: "verticalGeometryPoints", labelVisible: true, labelComponent: "Station" }];
    const args = { action: "band_style_set", bandType: "ProfileData", style: "Package - Invert", groups };
    await profile.band_style_set.execute(profile.band_style_set.inputSchema.parse(args));
    expect(sendCommandMock).toHaveBeenCalledWith("setBandStyle", expect.objectContaining({ style: "Package - Invert", groups }));
    expect((shape.parse(args) as Record<string, unknown>).groups).toEqual(groups);
  });

  // C54: the elevations of "Package - Invert" were drawn level under the band; the reference drawing has them upright in it.
  it("places a label text component (C54)", async () => {
    sendCommandMock.mockResolvedValue({});
    const groups = [{ location: "verticalGeometryPoints", labelComponent: "Station", labelAnchor: "BandMiddle", labelAttachment: "MiddleCenter",
      labelAngleDegrees: 90, labelXOffset: 0, labelYOffset: 0, labelHeight: 0.002 }];
    const args = { action: "band_style_set", bandType: "ProfileData", style: "Package - Invert", groups };
    await profile.band_style_set.execute(profile.band_style_set.inputSchema.parse(args));
    expect(sendCommandMock).toHaveBeenCalledWith("setBandStyle", expect.objectContaining({ groups }));
    expect((shape.parse(args) as Record<string, unknown>).groups).toEqual(groups);
  });

  // C56: the reference drawing has a line through the level row at each level; the band's VGP ticks were not full height.
  it("sets a group's ticks to full band height (C56)", async () => {
    sendCommandMock.mockResolvedValue({});
    const groups = [{ location: "verticalGeometryPoints", tickFullHeight: true, ticksVisible: true }];
    const args = { action: "band_style_set", bandType: "ProfileData", style: "Package - Invert", groups };
    await profile.band_style_set.execute(profile.band_style_set.inputSchema.parse(args));
    expect(sendCommandMock).toHaveBeenCalledWith("setBandStyle", expect.objectContaining({ groups }));
    expect(profile.band_style_set.inputSchema.safeParse({ ...args, groups: [{ location: "verticalGeometryPoints", tickFullHeight: true }] }).success).toBe(true);
  });

  it("needs something to change, and something per group", () => {
    const s = profile.band_style_set.inputSchema;
    const base = { action: "band_style_set", bandType: "ProfileData", style: "X" };
    expect(s.safeParse(base).success).toBe(false);
    expect(s.safeParse({ ...base, groups: [{ location: "majorStations" }] }).success).toBe(false);
    expect(s.safeParse({ ...base, groups: [] }).success).toBe(false);
    expect(s.safeParse({ ...base, bandHeight: 0 }).success).toBe(false);
    expect(s.safeParse({ ...base, titleText: "MATERIAL & SIZE IN MM" }).success).toBe(true);
  });
});
