import { mkdtempSync, readFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { describe, expect, it } from "vitest";
import { bridgeTokenFile, newBridgeToken, writeBridgeToken } from "../src/utils/bridgeToken.js";

// C65: a new token per bridge start in the user's profile, unless a fixed token is configured or it is turned off.
describe("C65 bridge token", () => {
  it("lives in LOCALAPPDATA by default", () => {
    expect(bridgeTokenFile({ LOCALAPPDATA: "C:/Users/x/AppData/Local" })).toBe(join("C:/Users/x/AppData/Local", "Civil3DMcp", "bridge.token"));
  });

  it("is not used with a fixed MCP_HTTP_TOKEN or when turned off, and can be moved", () => {
    expect(bridgeTokenFile({ MCP_HTTP_TOKEN: "fixed", LOCALAPPDATA: "C:/x" })).toBeUndefined();
    expect(bridgeTokenFile({ MCP_HTTP_TOKEN_FILE: "off", LOCALAPPDATA: "C:/x" })).toBeUndefined();
    expect(bridgeTokenFile({ MCP_HTTP_TOKEN_FILE: "D:/t/bridge.token" })).toBe("D:/t/bridge.token");
  });

  it("is 64 hex characters, new every time, written whole", () => {
    const a = newBridgeToken();
    expect(a).toMatch(/^[0-9a-f]{64}$/);
    expect(newBridgeToken()).not.toBe(a);
    const dir = mkdtempSync(join(tmpdir(), "c3dmcp-token-"));
    try {
      const file = join(dir, "sub", "bridge.token");
      writeBridgeToken(file, a);
      expect(readFileSync(file, "utf8")).toBe(a);
      writeBridgeToken(file, "b".repeat(64));
      expect(readFileSync(file, "utf8")).toBe("b".repeat(64));
    } finally {
      rmSync(dir, { recursive: true, force: true });
    }
  });
});
