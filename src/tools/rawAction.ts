import { z } from "zod";
import { withApplicationConnection } from "../utils/ConnectionManager.js";
import type { DomainActionDefinition, DomainToolDefinition, DomainToolExposure } from "./domainRuntime.js";

/**
 * C64 (2026-10-08): `action: "raw"` on every multi-action domain tool that writes sends `{method, params}` unchanged to the
 * plugin (TCP 8080). A new plugin method or parameter is reachable at once, without a Node schema change and a
 * restart of the MCP client (the tool input shapes strip unknown keys, and most execute functions copy fields one
 * by one). Always behind an approval token, whatever the method does; the plugin validates the parameters.
 */
export const RAW_ACTION = "raw";

const RawArgsSchema = z.object({
  action: z.literal(RAW_ACTION),
  method: z.string().regex(/^[A-Za-z][A-Za-z0-9]*$/, "method is a plugin method name (letters and digits), e.g. 'batch'"),
  params: z.record(z.string(), z.unknown()).optional(),
});

type RawArgs = z.infer<typeof RawArgsSchema>;

const RAW_ACTION_DEFINITION: DomainActionDefinition<RawArgs> = {
  action: RAW_ACTION,
  inputSchema: RawArgsSchema,
  capabilities: ["edit"],
  requiresActiveDrawing: true,
  safeForRetry: false,
  execute: async (args) => await withApplicationConnection(
    async (appClient) => await appClient.sendCommand(args.method, args.params ?? {}),
  ),
};

const MUTATING = new Set(["create", "edit", "delete", "manage", "generate", "register", "export", "import"]);

/** Only tools that already write: a read-only tool keeps its read-only/idempotent annotations (MCP clients may
 * allow those without asking), and one writing tool per domain is enough to reach any plugin method. */
function acceptsRaw(definition: DomainToolDefinition, exposure: DomainToolExposure): boolean {
  if (!(exposure.inputShape.action instanceof z.ZodEnum)) return false;
  const writes = exposure.supportedActions.some((name) =>
    (definition.actions[name]?.capabilities ?? []).some((capability) => MUTATING.has(capability)));
  if (!writes) return false;
  try {
    return exposure.resolveAction({ action: RAW_ACTION }).action === RAW_ACTION;
  } catch {
    return false;
  }
}

/** The definition with the raw action on every exposure whose action enum passes "raw" through unchanged. */
export function withRawAction(definition: DomainToolDefinition): DomainToolDefinition {
  if (definition.actions[RAW_ACTION]) return definition;
  return {
    ...definition,
    actions: { ...definition.actions, [RAW_ACTION]: RAW_ACTION_DEFINITION as unknown as DomainActionDefinition },
    exposures: definition.exposures.map((exposure) => {
      if (!acceptsRaw(definition, exposure)) return exposure;
      const action = exposure.inputShape.action as z.ZodEnum<[string, ...string[]]>;
      return {
        ...exposure,
        inputShape: {
          ...exposure.inputShape,
          action: z.enum([...action.options, RAW_ACTION] as [string, ...string[]]),
          method: z.string().optional(),
          params: z.record(z.string(), z.unknown()).optional(),
        },
        supportedActions: [...exposure.supportedActions, RAW_ACTION],
      };
    }),
  };
}
