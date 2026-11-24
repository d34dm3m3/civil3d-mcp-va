import { randomBytes } from "node:crypto";
import { mkdirSync, renameSync, writeFileSync } from "node:fs";
import { homedir } from "node:os";
import { dirname, join } from "node:path";

/**
 * C65 (2026-10-09): the HTTP bridge (127.0.0.1:3000) had no token, so any local process could drive Civil 3D
 * through it. Unless MCP_HTTP_TOKEN is set, a new random token is made at every start and written to
 * %LOCALAPPDATA%\Civil3DMcp\bridge.token (the user's own profile, so the folder ACL keeps other users out); local
 * clients read it and send it as X-MCP-Token. Nothing goes into .mcp.json or the repository.
 * MCP_HTTP_TOKEN_FILE=<path> moves the file; MCP_HTTP_TOKEN_FILE=off turns the token off (old behaviour).
 */
export function bridgeTokenFile(env: NodeJS.ProcessEnv = process.env): string | undefined {
  if (env.MCP_HTTP_TOKEN?.trim()) return undefined;              // a fixed token is configured; no file
  const configured = env.MCP_HTTP_TOKEN_FILE?.trim();
  if (configured?.toLowerCase() === "off") return undefined;
  if (configured) return configured;
  return join(env.LOCALAPPDATA || join(homedir(), "AppData", "Local"), "Civil3DMcp", "bridge.token");
}

export function newBridgeToken(): string {
  return randomBytes(32).toString("hex");
}

/** Written only after the bridge listens, so a second server that cannot bind the port does not replace the
 * running one's token. Atomic: a client never reads half a token. */
export function writeBridgeToken(file: string, token: string): void {
  mkdirSync(dirname(file), { recursive: true });
  const temporary = `${file}.${process.pid}.tmp`;
  writeFileSync(temporary, token, { encoding: "utf8", mode: 0o600 });
  renameSync(temporary, file);
}
