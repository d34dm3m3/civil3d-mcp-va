import { beforeEach, describe, expect, it, vi } from "vitest";
import { z } from "zod";

const { sendCommandMock } = vi.hoisted(() => ({ sendCommandMock: vi.fn() }));

vi.mock("../src/utils/ConnectionManager.js", () => ({
  withApplicationConnection: async <T>(
    operation: (client: { sendCommand: typeof sendCommandMock }) => Promise<T>,
  ) => await operation({ sendCommand: sendCommandMock }),
}));

import { isApprovalRequired } from "../src/tools/approvalPolicy.js";
import { findManifestAction, GENERATED_TOOL_CATALOG_ENTRIES, MIGRATED_DOMAIN_DEFINITIONS } from "../src/tools/toolManifest.js";

// C64: action "raw" sends {method, params} unchanged to the plugin, on every domain tool that already writes.
describe("C64 raw action", () => {
  beforeEach(() => sendCommandMock.mockReset());

  it("is on writing domain tools, behind approval, and passes method and params through", async () => {
    const target = findManifestAction("civil3d_pipe", "raw");
    expect(target).toBeDefined();
    expect(isApprovalRequired({ toolName: "civil3d_pipe", action: "raw", capabilities: target!.actionDefinition.capabilities, safeForRetry: target!.actionDefinition.safeForRetry })).toBe(true);
    sendCommandMock.mockResolvedValue({ run: 2 });
    const args = { action: "raw", method: "batch", params: { calls: [{ method: "createMText", params: { text: "A" } }] } };
    const parsed = target!.actionDefinition.inputSchema.parse(args);
    await target!.actionDefinition.execute(parsed);
    expect(sendCommandMock).toHaveBeenCalledWith("batch", args.params);
    const shape = z.object(target!.exposure.inputShape);
    expect(Object.keys(shape.parse(args)).sort()).toEqual(Object.keys(args).sort());
    expect(target!.exposure.resolveAction(args).action).toBe("raw");
  });

  it("refuses a method name that is not a plain identifier and needs a method", () => {
    const s = findManifestAction("civil3d_profile", "raw")!.actionDefinition.inputSchema;
    expect(s.safeParse({ action: "raw", method: "batch" }).success).toBe(true);
    expect(s.safeParse({ action: "raw", method: "../x" }).success).toBe(false);
    expect(s.safeParse({ action: "raw" }).success).toBe(false);
  });

  it("leaves read-only tools read-only", () => {
    expect(findManifestAction("civil3d_survey", "raw")).toBeUndefined();
    const survey = GENERATED_TOOL_CATALOG_ENTRIES.find((e) => e.toolName === "civil3d_survey");
    expect(survey!.operations).not.toContain("raw");
    expect(survey!.safeForRetry).toBe(true);
  });

  it("reaches most domains", () => {
    const withRaw = MIGRATED_DOMAIN_DEFINITIONS.filter((d) => d.exposures.some((e) => e.supportedActions.includes("raw")));
    expect(withRaw.length).toBeGreaterThanOrEqual(15);
    expect(withRaw.map((d) => d.domain)).toEqual(expect.arrayContaining(["pipe", "profile", "corridor", "surface", "alignment"]));
  });
});
