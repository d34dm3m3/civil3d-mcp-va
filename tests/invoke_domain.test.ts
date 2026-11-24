import { readFileSync } from "node:fs";
import { beforeEach, describe, expect, it, vi } from "vitest";

const { sendCommandMock } = vi.hoisted(() => ({ sendCommandMock: vi.fn() }));

vi.mock("../src/utils/ConnectionManager.js", () => ({
  withApplicationConnection: async <T>(
    operation: (client: { sendCommand: typeof sendCommandMock }) => Promise<T>,
  ) => await operation({ sendCommand: sendCommandMock }),
}));

import { isApprovalRequired } from "../src/tools/approvalPolicy.js";
import { INVOKE_DOMAIN_DEFINITION } from "../src/tools/domains/invokeDomain.js";
import { GENERATED_TOOL_CATALOG_ENTRIES } from "../src/tools/toolManifest.js";

const invoke = INVOKE_DOMAIN_DEFINITION.actions;
const TABLE = {
  commands: [
    { name: "listLayouts", domain: "Layout", kind: "read" },
    { name: "createCircle", domain: "Acad", kind: "write" },
  ],
};

function route(extra: Record<string, unknown> = {}) {
  sendCommandMock.mockImplementation(async (method: string, params: unknown) =>
    method === "listCommands" ? TABLE : { method, params, ...extra });
}

describe("C154 civil3d_invoke", () => {
  beforeEach(() => sendCommandMock.mockReset());

  it("is in the tool catalog; read and list need no approval, write does", () => {
    const entries = GENERATED_TOOL_CATALOG_ENTRIES.filter((entry) => entry.toolName === "civil3d_invoke");
    expect(entries.length).toBeGreaterThan(0);
    const target = (action: string) => ({ toolName: "civil3d_invoke", action, capabilities: invoke[action].capabilities, safeForRetry: invoke[action].safeForRetry });
    expect(isApprovalRequired(target("list"))).toBe(false);
    expect(isApprovalRequired(target("read"))).toBe(false);
    expect(isApprovalRequired(target("write"))).toBe(true);
  });

  it("read passes the parameters through unchanged to a read command", async () => {
    route();
    const params = { layout: "A3", anything: { nested: [1, 2] }, expectDrawing: "x.dwg" };
    const result = await invoke.read.execute(invoke.read.inputSchema.parse({ action: "read", method: "listLayouts", params }));
    expect(result).toMatchObject({ method: "listLayouts", params });
  });

  it("read refuses a write command", async () => {
    route();
    await expect(invoke.read.execute(invoke.read.inputSchema.parse({ action: "read", method: "createCircle" })))
      .rejects.toThrow(/action write/);
    expect(sendCommandMock).not.toHaveBeenCalledWith("createCircle", expect.anything());
  });

  it("refuses an unknown method after reading the table again", async () => {
    route();
    await expect(invoke.write.execute(invoke.write.inputSchema.parse({ action: "write", method: "noSuchCommand" })))
      .rejects.toThrow(/does not exist/);
  });

  it("write runs any command in the table", async () => {
    route();
    const result = await invoke.write.execute(invoke.write.inputSchema.parse({ action: "write", method: "createCircle", params: { radius: 1 } }));
    expect(result).toMatchObject({ method: "createCircle", params: { radius: 1 } });
  });

  it("the plugin registry marks every command read or write and lists itself", () => {
    const registry = readFileSync(new URL("../Civil3D-MCP-Plugin/CommandRegistry.cs", import.meta.url), "utf8");
    const rows = [...registry.matchAll(/^\s*R\("([^"]+)", "([^"]+)", (R_|W_),/gm)];
    expect(rows.length).toBeGreaterThan(400);
    expect(rows.map((row) => row[1])).toContain("listCommands");
    expect(new Set(rows.map((row) => row[1])).size).toBe(rows.length);
  });
});
