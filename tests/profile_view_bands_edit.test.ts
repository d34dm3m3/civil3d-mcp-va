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
import { DRAWING_RUNTIME_DOMAIN_DEFINITION } from "../src/tools/domains/drawingRuntimeDomain.js";
import { PROFILE_DOMAIN_DEFINITION } from "../src/tools/domains/profileDomain.js";

const profile = PROFILE_DOMAIN_DEFINITION.actions;
const drawing = DRAWING_RUNTIME_DOMAIN_DEFINITION.actions;
const approval = (toolName: string, def: { capabilities: string[]; safeForRetry: boolean }, action: string) =>
  isApprovalRequired({ toolName, action, capabilities: def.capabilities as never, safeForRetry: def.safeForRetry });

describe("profile view bands and labels", () => {
  beforeEach(() => sendCommandMock.mockReset());

  it("adds a pipe network band at a position", async () => {
    sendCommandMock.mockResolvedValue({});
    const s = profile.view_band_edit.inputSchema;
    await profile.view_band_edit.execute(s.parse({
      action: "view_band_edit", profileViewName: "PV", op: "add", bandType: "PipeNetwork", style: "Vatten", network: "Vatten V160", position: 6,
    }));
    expect(sendCommandMock.mock.calls[0][0]).toBe("editProfileViewBands");
    expect(sendCommandMock.mock.calls[0][1]).toMatchObject({ op: "add", bandType: "PipeNetwork", style: "Vatten", network: "Vatten V160", position: 6, location: "bottom" });
    expect(s.safeParse({ action: "view_band_edit", profileViewName: "PV", op: "add", style: "Vatten" }).success).toBe(false);
    expect(s.safeParse({ action: "view_band_edit", profileViewName: "PV", op: "remove" }).success).toBe(false);
    expect(s.safeParse({ action: "view_band_edit", profileViewName: "PV", op: "swap", index: 1 }).success).toBe(false);
    expect(s.safeParse({ action: "view_band_edit", profileViewName: "PV", op: "remove", index: 6 }).success).toBe(true);
    expect(approval("civil3d_profile", profile.view_band_edit, "view_band_edit")).toBe(true);
  });

  it("clears labels on profiles in a view, and pipe label styles can be read", async () => {
    sendCommandMock.mockResolvedValue({});
    await profile.view_profile_labels_clear.execute(profile.view_profile_labels_clear.inputSchema.parse({
      action: "view_profile_labels_clear", profileViewName: "PV", profiles: ["Schaktbotten_laz", "Mark_laz"],
    }));
    expect(sendCommandMock).toHaveBeenCalledWith("clearProfileViewLabels", { profileViewName: "PV", profiles: ["Schaktbotten_laz", "Mark_laz"] });
    expect(profile.label_style_get.inputSchema.safeParse({ action: "label_style_get", labelKind: "pipe", style: "Vatten" }).success).toBe(true);
  });

  it("reads and sets the drawing scale", async () => {
    sendCommandMock.mockResolvedValue({});
    await drawing.scale_set.execute(drawing.scale_set.inputSchema.parse({ action: "scale_set", scale: "1:1000" }));
    expect(sendCommandMock).toHaveBeenCalledWith("setScale", { scale: "1:1000" });
    expect(approval("civil3d_drawing", drawing.scale_get, "scale_get")).toBe(false);
    expect(approval("civil3d_drawing", drawing.scale_set, "scale_set")).toBe(true);
  });
});
