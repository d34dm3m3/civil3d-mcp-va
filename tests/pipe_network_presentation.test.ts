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
import { PIPE_DOMAIN_DEFINITION } from "../src/tools/domains/pipeDomain.js";
import { GENERATED_TOOL_CATALOG_ENTRIES } from "../src/tools/toolManifest.js";

const pipe = PIPE_DOMAIN_DEFINITION.actions;
const drawing = DRAWING_RUNTIME_DOMAIN_DEFINITION.actions;

describe("pipe network presentation", () => {
  beforeEach(() => sendCommandMock.mockReset());

  it("adds spanning labels to one run in a profile view, replacing earlier ones", async () => {
    sendCommandMock.mockResolvedValue({});
    await pipe.network_labels_add.execute(pipe.network_labels_add.inputSchema.parse({
      action: "network_labels_add", networkName: "Sewer", target: "profile", partKind: "spanning",
      profileViewName: "PV_Trench", namePrefix: "S225", labelStyle: "Pipe", replace: true,
    }));
    expect(sendCommandMock).toHaveBeenCalledWith("addNetworkLabels", {
      networkName: "Sewer", target: "profile", partKind: "spanning", profileViewName: "PV_Trench",
      namePrefix: "S225", partFamily: undefined, labelStyle: "Pipe", ratio: 0.5, replace: true, layer: undefined,
    });
  });

  it("passes the label layer through to the plugin", async () => {
    sendCommandMock.mockResolvedValue({});
    await pipe.network_labels_add.execute(pipe.network_labels_add.inputSchema.parse({
      action: "network_labels_add", networkName: "Fiber", target: "profile", partKind: "spanning",
      profileViewName: "PV_Trench", labelStyle: "Pipe data PE OD", layer: "C-PROF-TEXT",
    }));
    expect(sendCommandMock.mock.calls[0][1]).toMatchObject({ layer: "C-PROF-TEXT" });
    expect(pipe.network_labels_add.inputSchema.safeParse({
      action: "network_labels_add", networkName: "N", target: "plan", partKind: "pipe", labelStyle: "L", layer: "",
    }).success).toBe(false);
  });

  it("accepts selected objects without a name (labels)", () => {
    const parsed = drawing.selected_objects_info.responseSchema.parse([
      { handle: "406A584", objectType: "SpanningPipeProfileLabel", name: null, layer: "0", description: "PE80" },
    ]);
    expect(parsed[0]).toMatchObject({ name: null, layer: "0" });
  });

  it("needs a profile view for profile labels and a label style to add", () => {
    const s = pipe.network_labels_add.inputSchema;
    const base = { action: "network_labels_add", networkName: "N", partKind: "structure", labelStyle: "L" };
    expect(s.safeParse({ ...base, target: "profile" }).success).toBe(false);
    expect(s.safeParse({ ...base, target: "plan" }).success).toBe(true);
    expect(s.safeParse({ ...base, target: "plan", labelStyle: undefined }).success).toBe(false);
    expect(s.safeParse({ ...base, target: "plan", ratio: 1.5 }).success).toBe(false);
    const d = pipe.network_labels_delete.inputSchema;
    expect(d.safeParse({ action: "network_labels_delete", networkName: "N", target: "plan", partKind: "pipe" }).success).toBe(true);
  });

  it("sets a family's default style in a parts list", async () => {
    sendCommandMock.mockResolvedValue({});
    await pipe.parts_list_style_set.execute(pipe.parts_list_style_set.inputSchema.parse({
      action: "parts_list_style_set", partsList: "Water", familyName: "Bend point", domain: "structure", style: "No display - structure",
    }));
    expect(sendCommandMock).toHaveBeenCalledWith("setPartsListFamilyStyle", {
      partsList: "Water", familyName: "Bend point", domain: "structure", style: "No display - structure",
    });
    // C81: only the sizes that have fromStyle today (Inlet/Outlet mixes two styles)
    await pipe.parts_list_style_set.execute(pipe.parts_list_style_set.inputSchema.parse({
      action: "parts_list_style_set", partsList: "Storm", familyName: "Inlet/Outlet", domain: "structure", style: "Outlet S P109", fromStyle: "Outlet S",
    }));
    expect(sendCommandMock).toHaveBeenLastCalledWith("setPartsListFamilyStyle", {
      partsList: "Storm", familyName: "Inlet/Outlet", domain: "structure", style: "Outlet S P109", fromStyle: "Outlet S",
    });
  });

  it("network_build passes pipe and structure styles; profile view add takes a prefix", async () => {
    sendCommandMock.mockResolvedValue({});
    const vertices = [{ x: 0, y: 0, z: 1 }, { x: 1, y: 0, z: 1, structure: "Drain" }];
    await pipe.network_build.execute(pipe.network_build.inputSchema.parse({
      action: "network_build", networkName: "Water", partsList: "Water", pipeFamily: "F", pipeDiameter: 176.2, namePrefix: "V200",
      vertices, pipeStyle: "Water", structureStyles: { "Bend point": "No display - structure", "Drain": "Drain valve W" },
    }));
    expect(sendCommandMock.mock.calls[0][1]).toMatchObject({
      pipeStyle: "Water", structureStyles: { "Bend point": "No display - structure", "Drain": "Drain valve W" },
    });
    await pipe.network_profile_view_add.execute(pipe.network_profile_view_add.inputSchema.parse({
      action: "network_profile_view_add", networkName: "Water", profileViewName: "PV", namePrefix: "V200",
    }));
    expect(sendCommandMock).toHaveBeenLastCalledWith("addNetworkToProfileView", { networkName: "Water", profileViewName: "PV", namePrefix: "V200" });
  });

  it("drawing style_set picks parts by family and can move them to a layer", async () => {
    sendCommandMock.mockResolvedValue({});
    const s = drawing.style_set.inputSchema;
    await drawing.style_set.execute(s.parse({
      action: "style_set", objectType: "pipe_network", name: "Water", partFamily: "Bend point", structureStyle: "No display - structure",
    }));
    expect(sendCommandMock.mock.calls[0][1]).toMatchObject({ partFamily: "Bend point", structureStyle: "No display - structure", createLayer: false });
    expect(s.safeParse({ action: "style_set", objectType: "pipe_network", name: "Fiber", layer: "C-FIBER", createLayer: true }).success).toBe(true);
  });

  it("writes need approval and all new actions are on civil3d_pipe", () => {
    for (const a of ["parts_list_style_set", "network_labels_add", "network_labels_delete"] as const) {
      expect(isApprovalRequired({ toolName: "civil3d_pipe", action: a, capabilities: pipe[a].capabilities, safeForRetry: pipe[a].safeForRetry })).toBe(true);
    }
    const entry = GENERATED_TOOL_CATALOG_ENTRIES.find((e) => e.toolName === "civil3d_pipe");
    expect(entry!.operations).toEqual(expect.arrayContaining(["parts_list_style_set", "network_labels_add", "network_labels_delete"]));
  });
});
