import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { approvalPolicy, isApprovalRequired } from "./approvalPolicy.js";
import { captureToolHandler } from "./toolHandlerRegistry.js";
import { findManifestAction } from "./toolManifest.js";

type JsonObject = Record<string, unknown>;

const approvalInputShape = {
  toolName: z.string().min(1),
  action: z.string().min(1),
  parameters: z.record(z.unknown()),
  ttlSeconds: z.number().int().min(30).max(600).optional(),
};

// C230 (F2): scope "run" issues a reusable token for one drawing (drawing, ttlMinutes); scope "call" (default) is the
// per-call token above. The run form leaves toolName, action and parameters out.
const requestInputShape = {
  scope: z.enum(["call", "run"]).optional(),
  drawing: z.string().min(1).max(260).optional(),
  ttlMinutes: z.number().int().min(1).max(120).optional(),
  toolName: approvalInputShape.toolName.optional(),
  action: approvalInputShape.action.optional(),
  parameters: approvalInputShape.parameters.optional(),
  ttlSeconds: approvalInputShape.ttlSeconds,
};

const RunRequestSchema = z.object({
  scope: z.literal("run"),
  drawing: z.string().min(1).max(260),
  ttlMinutes: z.number().int().min(1).max(120).optional(),
}).strict();

const CallRequestSchema = z.object({
  scope: z.literal("call").optional(),
  toolName: approvalInputShape.toolName,
  action: approvalInputShape.action,
  parameters: approvalInputShape.parameters,
  ttlSeconds: approvalInputShape.ttlSeconds,
});

const PreviewResponseSchema = z.object({
  status: z.enum(["approval_required", "ready"]),
  toolName: z.string(),
  action: z.string(),
  capabilities: z.array(z.string()),
  safeForRetry: z.boolean(),
  instruction: z.string(),
});

const ApprovalResponseSchema = z.object({
  status: z.literal("approved"),
  scope: z.enum(["call", "run"]).optional(),
  toolName: z.string().optional(),
  action: z.string().optional(),
  drawing: z.string().optional(),
  approvalToken: z.string(),
  expiresAt: z.string(),
  drawingFingerprint: z.string().optional(),
  instruction: z.string(),
});

function resolveApprovalTarget(args: { toolName: string; action: string; parameters: JsonObject }) {
  const target = findManifestAction(args.toolName, args.action);
  if (!target) {
    throw new Error(`Unknown tool action '${args.toolName}' / '${args.action}'.`);
  }

  const resolved = target.exposure.resolveAction(args.parameters);
  if (resolved.action !== args.action) {
    throw new Error(
      `Parameters do not resolve to action '${args.action}' for tool '${args.toolName}'. ` +
      "Include the tool action in parameters when calling a multi-action tool.",
    );
  }
  target.actionDefinition.inputSchema.parse(resolved.args);
  return target;
}

function successResult(structuredContent: JsonObject) {
  return {
    content: [{
      type: "text" as const,
      text: JSON.stringify(structuredContent, null, 2),
    }],
    structuredContent,
  };
}

function errorResult(toolName: string, error: unknown) {
  const message = error instanceof Error ? error.message : String(error);
  return {
    content: [{ type: "text" as const, text: `${toolName} failed: ${message}` }],
    isError: true,
  };
}

export function registerApprovalTool(server: McpServer) {
  const previewHandler = async (rawArgs: JsonObject) => {
    try {
      const args = rawArgs as { toolName: string; action: string; parameters: JsonObject };
      const target = resolveApprovalTarget(args);
      const requiresApproval = isApprovalRequired({
        toolName: args.toolName,
        action: args.action,
        capabilities: target.actionDefinition.capabilities,
        safeForRetry: target.actionDefinition.safeForRetry,
        requiresActiveDrawing: target.actionDefinition.requiresActiveDrawing,
      });
      return successResult({
        status: requiresApproval ? "approval_required" : "ready",
        toolName: args.toolName,
        action: args.action,
        capabilities: target.actionDefinition.capabilities,
        safeForRetry: target.actionDefinition.safeForRetry,
        instruction: requiresApproval
          ? "Call civil3d_request_approval with the same toolName, action, and parameters before execution."
          : "This action may be executed directly.",
      });
    } catch (error) {
      return errorResult("civil3d_preview_action", error);
    }
  };

  const requestHandler = async (rawArgs: JsonObject) => {
    try {
      if (rawArgs?.scope === "run") {
        const run = RunRequestSchema.parse(rawArgs);
        const receipt = approvalPolicy.requestRunApproval(run.drawing, (run.ttlMinutes ?? 30) * 60 * 1000);
        return successResult({
          status: "approved",
          ...receipt,
          instruction:
            `Pass approvalToken on civil3d_invoke action write and on action raw calls whose params.expectDrawing is ` +
            `'${receipt.drawing}', until expiresAt. Calls it does not cover (closing or creating drawings, undo, ` +
            "startCommand, whole-object deletes, data shortcuts, other domain actions) still need a per-call approval.",
        });
      }

      const args = CallRequestSchema.parse(rawArgs) as {
        toolName: string;
        action: string;
        parameters: JsonObject;
        ttlSeconds?: number;
      };
      const target = resolveApprovalTarget(args);
      const receipt = await approvalPolicy.requestApproval(
        {
          toolName: args.toolName,
          action: args.action,
          capabilities: target.actionDefinition.capabilities,
          safeForRetry: target.actionDefinition.safeForRetry,
          requiresActiveDrawing: target.actionDefinition.requiresActiveDrawing,
        },
        args.parameters,
        (args.ttlSeconds ?? 300) * 1000,
      );

      return successResult({
        status: "approved",
        scope: "call",
        toolName: args.toolName,
        action: args.action,
        ...receipt,
        instruction: "Retry the approved tool call with approvalToken and the identical parameters.",
      });
    } catch (error) {
      return errorResult("civil3d_request_approval", error);
    }
  };

  captureToolHandler("civil3d_preview_action", previewHandler);
  captureToolHandler("civil3d_request_approval", requestHandler);

  server.registerTool(
    "civil3d_preview_action",
    {
      title: "Preview Civil 3D Action",
      description: "Validates and previews whether a Civil 3D action requires approval before it can execute. This tool never changes the drawing.",
      inputSchema: {
        toolName: approvalInputShape.toolName,
        action: approvalInputShape.action,
        parameters: approvalInputShape.parameters,
      },
      outputSchema: PreviewResponseSchema,
      annotations: {
        title: "Preview Civil 3D Action",
        readOnlyHint: true,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    previewHandler,
  );

  server.registerTool(
    "civil3d_request_approval",
    {
      title: "Request Civil 3D Approval",
      description:
        "Issues a short-lived, single-use approval token for a specific destructive or ambiguous Civil 3D action in the " +
        "active drawing (toolName, action, parameters). With scope 'run' and drawing (file name), it issues a reusable " +
        "run token for that drawing (ttlMinutes, default 30, at most 120) that covers civil3d_invoke write and action raw " +
        "calls whose params.expectDrawing names the drawing; document close/new, undo, startCommand, whole-object " +
        "deletes and data shortcuts still need a per-call token.",
      inputSchema: requestInputShape,
      outputSchema: ApprovalResponseSchema,
      annotations: {
        title: "Request Civil 3D Approval",
        readOnlyHint: false,
        destructiveHint: false,
        idempotentHint: false,
        openWorldHint: false,
      },
    },
    requestHandler,
  );
}
