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
type Act = "label_style_get" | "label_style_copy" | "label_style_text_set";
const approval = (a: Act) => isApprovalRequired({
  toolName: "civil3d_profile", action: a, capabilities: profile[a].capabilities, safeForRetry: profile[a].safeForRetry,
});

describe("profile label style components", () => {
  beforeEach(() => sendCommandMock.mockReset());

  it("reads the components of a profile label style", async () => {
    sendCommandMock.mockResolvedValue({});
    await profile.label_style_get.execute(profile.label_style_get.inputSchema.parse({
      action: "label_style_get", labelKind: "line", style: "Tangent",
    }));
    expect(sendCommandMock).toHaveBeenCalledWith("getProfileLabelStyle", { labelKind: "line", style: "Tangent" });
  });

  it("copies a label style as a sibling under a new name", async () => {
    sendCommandMock.mockResolvedValue({});
    await profile.label_style_copy.execute(profile.label_style_copy.inputSchema.parse({
      action: "label_style_copy", labelKind: "line", style: "Tangent", newStyle: "Tangent length and grade",
    }));
    expect(sendCommandMock).toHaveBeenCalledWith("copyProfileLabelStyle", {
      labelKind: "line", style: "Tangent", newStyle: "Tangent length and grade",
    });
  });

  it("adds or updates a text component, laid out like an existing one", async () => {
    sendCommandMock.mockResolvedValue({});
    const text = {
      component: "Length",
      contents: "L=<[Tangent Horizontal Length(Um|P1|RN|AP|Sn|OF|AP)]> m",
      like: "Grade",
      attachment: "TopCenter",
      yOffset: -0.5,
    };
    await profile.label_style_text_set.execute(profile.label_style_text_set.inputSchema.parse({
      action: "label_style_text_set", labelKind: "line", style: "Tangent length and grade", text,
    }));
    expect(sendCommandMock).toHaveBeenCalledWith("setProfileLabelStyleText", {
      labelKind: "line", style: "Tangent length and grade", text,
    });
  });

  it("rejects a text update without a component, without any setting, or with an unknown attachment", () => {
    const parse = (text: unknown) => profile.label_style_text_set.inputSchema.safeParse({
      action: "label_style_text_set", labelKind: "line", style: "S", text,
    }).success;
    expect(parse({ contents: "x" })).toBe(false);
    expect(parse({ component: "Length" })).toBe(false);
    expect(parse({ component: "Length", attachment: "Somewhere" })).toBe(false);
    expect(parse({ component: "Length", visible: true })).toBe(true);
  });

  it("rejects copying to the same or an empty name", () => {
    const parse = (newStyle: string) => profile.label_style_copy.inputSchema.safeParse({
      action: "label_style_copy", labelKind: "line", style: "Tangent", newStyle,
    }).success;
    expect(parse("")).toBe(false);
    expect(parse("Tangent")).toBe(false);
  });

  it("reads need no approval, writes do", () => {
    expect(approval("label_style_get")).toBe(false);
    expect(approval("label_style_copy")).toBe(true);
    expect(approval("label_style_text_set")).toBe(true);
  });

  it("exposes the actions on the canonical profile tool", () => {
    const entry = GENERATED_TOOL_CATALOG_ENTRIES.find((e) => e.toolName === "civil3d_profile");
    for (const a of ["label_style_get", "label_style_copy", "label_style_text_set"]) {
      expect(entry!.operations).toContain(a);
    }
  });
});
