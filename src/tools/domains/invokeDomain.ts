import { z } from "zod";
import { withApplicationConnection } from "../../utils/ConnectionManager.js";
import type { DomainToolDefinition } from "../domainRuntime.js";

/**
 * C154 (2026-10-21): civil3d_invoke reaches every plugin command through the plugin's own command table (listCommands,
 * C153) instead of a hand-written Node schema per action. A command added to the plugin is reachable as soon as the
 * plugin is reloaded: no Node change, no restart of Claude, and the parameters go through unchanged (no zod stripping).
 *
 * - list: the commands (name, domain, read/write), filtered by domain or a name fragment.
 * - read: runs a command the plugin marks as read, without approval.
 * - write: runs any command, behind an approval token (civil3d_request_approval), like the domain tools that write.
 *
 * The table is cached for a minute and read again when a method is not in it (a reloaded plugin).
 */

type Command = { name: string; domain: string; kind: "read" | "write" };

const CACHE_MS = 60_000;
let cache: { at: number; commands: Map<string, Command> } | undefined;

async function commandTable(refresh: boolean): Promise<Map<string, Command>> {
  if (!refresh && cache && Date.now() - cache.at < CACHE_MS) return cache.commands;
  const result = await withApplicationConnection(async (appClient) => await appClient.sendCommand("listCommands", {})) as {
    commands?: Command[];
  };
  const commands = new Map((result.commands ?? []).map((command) => [command.name, command]));
  cache = { at: Date.now(), commands };
  return commands;
}

async function findCommand(method: string): Promise<Command> {
  const found = (await commandTable(false)).get(method) ?? (await commandTable(true)).get(method);
  if (!found) {
    throw Object.assign(
      new Error(`Plugin method '${method}' does not exist. Use civil3d_invoke action list (domain or contains) to find it.`),
      { code: "CIVIL3D.METHOD_NOT_FOUND" },
    );
  }
  return found;
}

const MethodSchema = z.string().regex(/^[A-Za-z][A-Za-z0-9]*$/, "method is a plugin method name (letters and digits), e.g. 'listLayouts'");
const ParamsSchema = z.record(z.string(), z.unknown()).optional();

const ListArgs = z.object({ action: z.literal("list"), domain: z.string().optional(), contains: z.string().optional() });
const ReadArgs = z.object({ action: z.literal("read"), method: MethodSchema, params: ParamsSchema });
const WriteArgs = z.object({ action: z.literal("write"), method: MethodSchema, params: ParamsSchema });

export const INVOKE_DOMAIN_DEFINITION: DomainToolDefinition = {
  domain: "invoke",
  actions: {
    list: {
      action: "list",
      inputSchema: ListArgs,
      capabilities: ["query", "inspect"],
      requiresActiveDrawing: false,
      safeForRetry: true,
      pluginMethods: ["listCommands"],
      execute: async (input) => {
        const args = ListArgs.parse(input);
        return await withApplicationConnection(async (appClient) =>
          await appClient.sendCommand("listCommands", { domain: args.domain, contains: args.contains }));
      },
    },
    read: {
      action: "read",
      inputSchema: ReadArgs,
      capabilities: ["query", "inspect"],
      requiresActiveDrawing: false,
      safeForRetry: true,
      execute: async (input) => {
        const args = ReadArgs.parse(input);
        const command = await findCommand(args.method);
        if (command.kind !== "read") {
          throw Object.assign(
            new Error(`'${args.method}' changes the drawing or writes files; run it with action write (approval required).`),
            { code: "CIVIL3D.INVALID_INPUT" },
          );
        }
        return await withApplicationConnection(async (appClient) => await appClient.sendCommand(args.method, args.params ?? {}));
      },
    },
    write: {
      action: "write",
      inputSchema: WriteArgs,
      capabilities: ["edit"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      execute: async (input) => {
        const args = WriteArgs.parse(input);
        await findCommand(args.method);
        return await withApplicationConnection(async (appClient) => await appClient.sendCommand(args.method, args.params ?? {}));
      },
    },
  },
  exposures: [{
    toolName: "civil3d_invoke",
    displayName: "Civil 3D Invoke",
    description:
      "Runs any Civil 3D plugin command by name with its parameters passed through unchanged. action list shows the commands " +
      "(name, domain, read/write; filter with domain or contains). action read runs a read command without approval; action " +
      "write runs any command after civil3d_request_approval. Parameters are the plugin's (see the command's errors); every " +
      "call may carry expectDrawing (file name or path) so it is refused when another drawing is active.",
    inputShape: {
      action: z.enum(["list", "read", "write"]),
      method: z.string().optional(),
      params: z.record(z.string(), z.unknown()).optional(),
      domain: z.string().optional(),
      contains: z.string().optional(),
    },
    supportedActions: ["list", "read", "write"],
    resolveAction: (rawArgs) => ({ action: String(rawArgs.action ?? ""), args: rawArgs }),
  }],
};
