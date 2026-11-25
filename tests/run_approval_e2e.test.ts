import { readFileSync } from "node:fs";
import { beforeEach, describe, expect, it, vi } from "vitest";

// C230 (F2): run approval through the registered handlers, as the HTTP bridge calls them.

const { sendCommandMock } = vi.hoisted(() => ({ sendCommandMock: vi.fn() }));

vi.mock("../src/utils/ConnectionManager.js", () => ({
  withApplicationConnection: async <T>(
    operation: (client: { sendCommand: typeof sendCommandMock }) => Promise<T>,
  ) => await operation({ sendCommand: sendCommandMock }),
}));

import { RUN_APPROVAL_EXCLUDED_METHODS } from "../src/tools/approvalPolicy.js";
import { registerApprovalTool } from "../src/tools/approvalTool.js";
import { captureDomainToolHandlers } from "../src/tools/domainRuntime.js";
import { INVOKE_DOMAIN_DEFINITION } from "../src/tools/domains/invokeDomain.js";
import { getToolHandler } from "../src/tools/toolHandlerRegistry.js";

const TABLE = {
  commands: [
    { name: "createCircle", domain: "Acad", kind: "write" },
    { name: "documentClose", domain: "Document", kind: "write" },
  ],
};

describe("run approval end to end", () => {
  beforeEach(() => {
    sendCommandMock.mockReset();
    sendCommandMock.mockImplementation(async (method: string, params: unknown) =>
      method === "listCommands" ? TABLE : method === "getDrawingInfo" ? { fileName: "sheet-101.dwg" } : { method, params });
    registerApprovalTool({ registerTool: () => undefined } as never);
    captureDomainToolHandlers(INVOKE_DOMAIN_DEFINITION);
  });

  it("runs writes on the run's drawing and stops the others before the plugin", async () => {
    const approve = getToolHandler("civil3d_request_approval")!;
    const invoke = getToolHandler("civil3d_invoke")!;
    const run = await approve({ scope: "run", drawing: "sheet-101.dwg" });
    const approvalToken = String(run.structuredContent?.approvalToken);

    for (let i = 0; i < 3; i += 1) {
      const params = { expectDrawing: "sheet-101.dwg", radius: i + 1 };
      const ok = await invoke({ action: "write", method: "createCircle", params, approvalToken });
      expect(ok.isError).toBeUndefined();
      expect(sendCommandMock).toHaveBeenCalledWith("createCircle", params);
    }

    const before = sendCommandMock.mock.calls.length;
    const wrong = await invoke({ action: "write", method: "createCircle", params: { expectDrawing: "other-101.dwg" }, approvalToken });
    expect(wrong.isError).toBe(true);
    expect(wrong.errorCode).toBe("CIVIL3D.FORBIDDEN");
    expect(wrong.content[0].text).toMatch(/Run approval is for drawing 'sheet-101.dwg'/);
    expect(wrong.content[0].text).not.toContain(approvalToken);

    const close = await invoke({ action: "write", method: "documentClose", params: { expectDrawing: "sheet-101.dwg", name: "sheet-101.dwg", discard: true }, approvalToken });
    expect(close.isError).toBe(true);
    expect(close.content[0].text).toMatch(/Run approval does not cover 'documentClose'/);
    expect(sendCommandMock.mock.calls.length).toBe(before);
  });

  it("names only plugin methods that exist", () => {
    const registry = readFileSync(new URL("../Civil3D-MCP-Plugin/CommandRegistry.cs", import.meta.url), "utf8");
    const names = new Set([...registry.matchAll(/^\s*R\("([^"]+)", "([^"]+)", (R_|W_),/gm)].map((row) => row[1]));
    const missing = [...RUN_APPROVAL_EXCLUDED_METHODS].filter((method) => !names.has(method));
    expect(missing).toEqual([]);
  });
});
