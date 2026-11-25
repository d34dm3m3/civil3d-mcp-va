import { beforeEach, describe, expect, it, vi } from "vitest";
import {
  ApprovalPolicyService,
  ApprovalRequiredError,
  ApprovalValidationError,
  RUN_APPROVAL_EXCLUDED_METHODS,
  sameDrawingName,
} from "../src/tools/approvalPolicy.js";

// C230 (F2): run approvals, one reusable token per run and drawing.

const invokeWrite = {
  toolName: "civil3d_invoke",
  action: "write",
  capabilities: ["edit"] as const,
  safeForRetry: false,
  requiresActiveDrawing: true,
};
const drawingRaw = { ...invokeWrite, toolName: "civil3d_drawing", action: "raw" };
const drawingSave = { ...invokeWrite, toolName: "civil3d_drawing", action: "save", capabilities: ["edit", "manage"] as const };

function write(method: string, params: Record<string, unknown>, approvalToken: string) {
  return { action: "write", method, params, approvalToken };
}

describe("run approval", () => {
  let now: number;
  let fingerprintCalls: number;
  let policy: ApprovalPolicyService;

  beforeEach(() => {
    now = 1_000_000;
    fingerprintCalls = 0;
    policy = new ApprovalPolicyService(async () => {
      fingerprintCalls += 1;
      return `drawing-${fingerprintCalls}`;   // changes with every write, as getDrawingInfo's object counts do
    }, () => now);
  });

  it("covers repeated writes whose expectDrawing names the run's drawing, without reading the drawing", async () => {
    const run = policy.requestRunApproval("C:\\work\\project\\sheet-101.dwg");
    expect(run.scope).toBe("run");
    expect(run.drawing).toBe("sheet-101.dwg");
    expect(Date.parse(run.expiresAt) - now).toBe(30 * 60 * 1000);

    for (const expectDrawing of ["sheet-101.dwg", "SHEET-101.DWG", "sheet-101", "C:/work/project/sheet-101.dwg"]) {
      await expect(policy.enforce(invokeWrite, write("createPolyline", { expectDrawing, points: [] }, run.approvalToken)))
        .resolves.toBeUndefined();
    }
    await expect(policy.enforce(drawingRaw, {
      action: "raw", method: "eraseEntities", params: { expectDrawing: "sheet-101.dwg", idPrefix: "S1" },
      approvalToken: run.approvalToken,
    })).resolves.toBeUndefined();
    expect(fingerprintCalls).toBe(0);
  });

  it("refuses a call whose expectDrawing names another drawing", async () => {
    const run = policy.requestRunApproval("sheet-101.dwg");
    await expect(policy.enforce(invokeWrite, write("createPolyline", { expectDrawing: "sheet-102.dwg" }, run.approvalToken)))
      .rejects.toThrow(/Run approval is for drawing 'sheet-101.dwg', but params.expectDrawing is 'sheet-102.dwg'/);
    await expect(policy.enforce(invokeWrite, write("createPolyline", { expectDrawing: "sheet-101.dwt" }, run.approvalToken)))
      .rejects.toBeInstanceOf(ApprovalValidationError);
  });

  it("refuses a call without expectDrawing", async () => {
    const run = policy.requestRunApproval("sheet-101.dwg");
    await expect(policy.enforce(invokeWrite, write("createPolyline", {}, run.approvalToken)))
      .rejects.toThrow(/requires params.expectDrawing/);
    await expect(policy.enforce(invokeWrite, { action: "write", method: "createPolyline", approvalToken: run.approvalToken }))
      .rejects.toThrow(/requires params.expectDrawing/);
    // expectDrawing outside params never reaches the plugin, so it does not count.
    await expect(policy.enforce(invokeWrite, {
      action: "write", method: "createPolyline", params: {}, expectDrawing: "sheet-101.dwg", approvalToken: run.approvalToken,
    })).rejects.toThrow(/requires params.expectDrawing/);
  });

  it("refuses an expired run token and forgets it", async () => {
    const run = policy.requestRunApproval("sheet-101.dwg", 5 * 60 * 1000);
    now += 5 * 60 * 1000;
    const call = write("createPolyline", { expectDrawing: "sheet-101.dwg" }, run.approvalToken);
    await expect(policy.enforce(invokeWrite, call)).rejects.toThrow(/Run approval token has expired/);
    await expect(policy.enforce(invokeWrite, call)).rejects.toThrow(/missing, expired, or has already been used/);
  });

  it("is not issued without a drawing or outside 1 to 120 minutes", () => {
    expect(() => policy.requestRunApproval("")).toThrow(ApprovalValidationError);
    expect(() => policy.requestRunApproval("C:\\work\\")).toThrow(/needs the drawing/);
    expect(() => policy.requestRunApproval("*.dwg")).toThrow(/not a drawing file name/);
    expect(() => policy.requestRunApproval("x.dwg", 121 * 60 * 1000)).toThrow(/1 to 120 minutes/);
    expect(() => policy.requestRunApproval("x.dwg", 30 * 1000)).toThrow(/1 to 120 minutes/);
    expect(() => policy.requestRunApproval("x.dwg", 120 * 60 * 1000)).not.toThrow();
  });

  it("keeps destructive and document calls per call", async () => {
    const run = policy.requestRunApproval("sheet-101.dwg");
    const token = run.approvalToken;
    const expectDrawing = "sheet-101.dwg";
    for (const method of ["documentClose", "newDrawing", "undoDrawing", "startCommand", "deletePipeNetwork", "reloadPlugin",
      "setSystemVariable", "publishDataShortcuts"]) {
      expect(RUN_APPROVAL_EXCLUDED_METHODS.has(method)).toBe(true);
      await expect(policy.enforce(invokeWrite, write(method, { expectDrawing, name: expectDrawing }, token)))
        .rejects.toThrow(/Run approval does not cover/);
    }
    await expect(policy.enforce(invokeWrite, write("saveDrawing", { expectDrawing, saveAs: "D:\\x\\copy.dwg" }, token)))
      .rejects.toThrow(/saveAs/);
    await expect(policy.enforce(invokeWrite, write("saveDrawing", { expectDrawing }, token))).resolves.toBeUndefined();

    // Activating or opening only the run's own drawing.
    await expect(policy.enforce(invokeWrite, write("documentActivate", { expectDrawing, name: "D:\\p\\sheet-101.dwg" }, token)))
      .resolves.toBeUndefined();
    await expect(policy.enforce(invokeWrite, write("documentActivate", { expectDrawing, name: "other-101.dwg" }, token)))
      .rejects.toThrow(/of another drawing/);
    await expect(policy.enforce(invokeWrite, write("documentOpen", { expectDrawing, path: "D:\\p\\Other.dwg" }, token)))
      .rejects.toThrow(/of another drawing/);

    // Other domain actions strip expectDrawing, so the plugin could not check the drawing: per call.
    await expect(policy.enforce(drawingSave, { action: "save", approvalToken: token }))
      .rejects.toThrow(/only civil3d_invoke write and action raw/);
  });

  it("checks every call of a batch", async () => {
    const run = policy.requestRunApproval("sheet-101.dwg");
    const batch = (calls: unknown[]) => write("batch", { expectDrawing: "sheet-101.dwg", calls }, run.approvalToken);
    await expect(policy.enforce(invokeWrite, batch([
      { method: "createPolyline", params: {} },
      { method: "saveDrawing", params: {} },
    ]))).resolves.toBeUndefined();
    await expect(policy.enforce(invokeWrite, batch([{ method: "deleteSurface", params: { name: "EG" } }])))
      .rejects.toThrow(/batch call 0: Run approval does not cover 'deleteSurface'/);
    await expect(policy.enforce(invokeWrite, batch([{ method: "createText", params: { expectDrawing: "Other.dwg" } }])))
      .rejects.toThrow(/batch call 0 names 'Other.dwg'/);
    await expect(policy.enforce(invokeWrite, write("batch", { expectDrawing: "sheet-101.dwg" }, run.approvalToken)))
      .rejects.toThrow(/without a calls list/);
  });

  it("leaves the per-call token as it was: exact parameters, single use", async () => {
    const parameters = { action: "write", method: "documentClose", params: { name: "sheet-101.dwg", discard: true } };
    await expect(policy.enforce(invokeWrite, parameters)).rejects.toBeInstanceOf(ApprovalRequiredError);
    fingerprintCalls = 0;
    policy = new ApprovalPolicyService(async () => "same-drawing", () => now);
    const receipt = await policy.requestApproval(invokeWrite, parameters);
    await expect(policy.enforce(invokeWrite, { ...parameters, approvalToken: receipt.approvalToken })).resolves.toBeUndefined();
    await expect(policy.enforce(invokeWrite, { ...parameters, approvalToken: receipt.approvalToken }))
      .rejects.toThrow(/already been used/);
  });

  it("matches drawing names like the plugin's expectDrawing check", () => {
    expect(sameDrawingName("a.dwg", "A.DWG")).toBe(true);
    expect(sameDrawingName("a", "C:\\x\\a.dwt")).toBe(true);
    expect(sameDrawingName("a.dwg", "a.dwt")).toBe(false);
    expect(sameDrawingName("a.dwg", "b.dwg")).toBe(false);
    expect(sameDrawingName("", "a.dwg")).toBe(false);
  });
});

describe("civil3d_request_approval scope run", () => {
  it("issues a run token over the registered handler and refuses mixed or drawing-less requests", async () => {
    vi.resetModules();
    const { registerApprovalTool } = await import("../src/tools/approvalTool.js");
    const { getToolHandler } = await import("../src/tools/toolHandlerRegistry.js");
    const registered: string[] = [];
    registerApprovalTool({ registerTool: (name: string) => { registered.push(name); } } as never);
    expect(registered).toContain("civil3d_request_approval");
    const handler = getToolHandler("civil3d_request_approval")!;

    const ok = await handler({ scope: "run", drawing: "D:\\p\\sheet-101.dwg", ttlMinutes: 60 });
    expect(ok.isError).toBeUndefined();
    expect(ok.structuredContent).toMatchObject({ status: "approved", scope: "run", drawing: "sheet-101.dwg" });
    expect(typeof ok.structuredContent?.approvalToken).toBe("string");

    expect((await handler({ scope: "run" })).isError).toBe(true);
    expect((await handler({ scope: "run", drawing: "x.dwg", ttlMinutes: 121 })).isError).toBe(true);
    expect((await handler({ scope: "run", drawing: "x.dwg", toolName: "civil3d_invoke" })).isError).toBe(true);
    expect((await handler({ drawing: "x.dwg" })).isError).toBe(true);
  });
});
