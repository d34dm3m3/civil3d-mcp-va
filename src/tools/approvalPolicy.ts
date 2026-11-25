import { createHash, randomUUID } from "node:crypto";
import { withApplicationConnection } from "../utils/ConnectionManager.js";
import type { ToolCapability } from "./toolMetadata.js";

type JsonObject = Record<string, unknown>;

export interface ApprovalTarget {
  toolName: string;
  action: string;
  capabilities: ToolCapability[];
  safeForRetry: boolean;
  requiresActiveDrawing?: boolean;
}

interface ApprovalGrant {
  toolName: string;
  action: string;
  parametersHash: string;
  drawingFingerprint: string;
  expiresAt: number;
}

export interface ApprovalReceipt {
  approvalToken: string;
  expiresAt: string;
  drawingFingerprint: string;
}

export interface RunApprovalReceipt {
  approvalToken: string;
  scope: "run";
  drawing: string;
  expiresAt: string;
}

interface RunApprovalGrant {
  drawing: string;
  expiresAt: number;
}

export class ApprovalRequiredError extends Error {}

export class ApprovalValidationError extends Error {
  public constructor(message: string, public readonly code?: string) {
    super(message);
  }
}

export type DrawingFingerprintProvider = () => Promise<string>;

const APPROVAL_TOKEN_TTL_MS = 5 * 60 * 1000;
export const RUN_APPROVAL_DEFAULT_TTL_MS = 30 * 60 * 1000;
export const RUN_APPROVAL_MAX_TTL_MS = 120 * 60 * 1000;
const RUN_APPROVAL_MIN_TTL_MS = 60 * 1000;

/**
 * C230 (2026-11-24, F2): plugin methods a run approval never covers; each call still needs its own per-call token.
 * They act outside the active drawing or outside the plugin's expectDrawing check (C150), cannot be told apart from
 * the user's own work afterwards, or remove whole objects that are expensive to rebuild.
 */
export const RUN_APPROVAL_EXCLUDED_METHODS: ReadonlySet<string> = new Set([
  // Documents and the application: another drawing, a new file, or a drawing closed (unsaved changes lost).
  "newDrawing", "documentClose", "dxfToDwg", "exportToAutocad", "saveTest", "drawingTransform",
  // Undo/redo can take back changes made by hand; startCommand runs any AutoCAD command; startJob runs later, outside
  // the request; reloadPlugin loads code; setSystemVariable can change security settings (SECURELOAD, TRUSTEDPATHS).
  "undoDrawing", "redoDrawing", "startCommand", "startJob", "reloadPlugin", "setSystemVariable",
  // Whole objects and purges.
  "deletePipeNetwork", "deletePressureNetwork", "deleteSurface", "deleteAlignment", "deleteCorridor",
  "deleteGradingGroup", "purgeBlocks", "layersPurge", "xrefDetach", "rasterDetach", "removePartsListFamily",
  // Data shortcuts and project setup write project files that other drawings read.
  "publishDataShortcuts", "promoteDataShortcut", "syncDataShortcuts", "setDataShortcutProject", "createDataShortcut",
  "createDataShortcutReference", "dataShortcutPublishSyncWorkflow", "dataShortcutReferenceSyncWorkflow",
  "projectStartupWorkflow", "projectReferenceSetupWorkflow",
]);

const RUN_NOT_COVERED = "Run approval does not cover";
const RUN_PER_CALL_HINT = "request a per-call approval (civil3d_request_approval with toolName, action and parameters) for this call.";

/** The file name of a drawing name or path (either separator), trimmed. */
export function drawingFileName(value: string): string {
  const parts = value.trim().split(/[\\/]/);
  return (parts[parts.length - 1] ?? "").trim();
}

function withoutDrawingExtension(name: string): string {
  return name.replace(/\.(?:dwg|dwt)$/i, "");
}

/** Same drawing by file name, ignoring case and directory; a name without extension matches the name with .dwg/.dwt
 * (as the plugin's C150 check does), but x.dwg never matches x.dwt. */
export function sameDrawingName(a: string, b: string): boolean {
  const left = drawingFileName(a).toLowerCase();
  const right = drawingFileName(b).toLowerCase();
  if (!left || !right) return false;
  if (left === right) return true;
  const leftBare = withoutDrawingExtension(left);
  const rightBare = withoutDrawingExtension(right);
  return (left === leftBare && left === rightBare) || (right === rightBare && right === leftBare);
}

function nonEmptyString(value: unknown): string | undefined {
  return typeof value === "string" && value.trim() ? value.trim() : undefined;
}

function asObject(value: unknown): JsonObject | undefined {
  return value && typeof value === "object" && !Array.isArray(value) ? value as JsonObject : undefined;
}

/** Why a run approval for `drawing` does not cover this plugin method with these params (undefined: covered). */
function runMethodRefusal(drawing: string, method: unknown, params: JsonObject | undefined, inBatch: boolean): string | undefined {
  const name = nonEmptyString(method);
  if (!name) return `${RUN_NOT_COVERED} a call without a plugin method name; ${RUN_PER_CALL_HINT}`;
  if (RUN_APPROVAL_EXCLUDED_METHODS.has(name)) return `${RUN_NOT_COVERED} '${name}'; ${RUN_PER_CALL_HINT}`;

  if (name === "documentOpen" || name === "documentActivate") {
    const target = nonEmptyString(params?.path) ?? nonEmptyString(params?.name);
    if (!target || !sameDrawingName(target, drawing)) {
      return `${RUN_NOT_COVERED} '${name}' of another drawing than '${drawing}'; ${RUN_PER_CALL_HINT}`;
    }
  }

  if (name === "saveDrawing" && nonEmptyString(params?.saveAs)) {
    return `${RUN_NOT_COVERED} 'saveDrawing' with saveAs (a new or overwritten file); ${RUN_PER_CALL_HINT}`;
  }

  if (name === "batch") {
    if (inBatch) return `${RUN_NOT_COVERED} a batch inside a batch.`;
    const calls = params?.calls;
    if (!Array.isArray(calls)) return `${RUN_NOT_COVERED} a batch without a calls list.`;
    for (const [index, entry] of calls.entries()) {
      const call = asObject(entry);
      const callParams = asObject(call?.params);
      // C150: a call's own expectDrawing replaces the batch's in the plugin, so it must name the run's drawing too.
      const own = nonEmptyString(callParams?.expectDrawing);
      if (own && !sameDrawingName(own, drawing)) {
        return `Run approval is for drawing '${drawing}'; batch call ${index} names '${own}' in expectDrawing.`;
      }
      const refusal = runMethodRefusal(drawing, call?.method, callParams, true);
      if (refusal) return `batch call ${index}: ${refusal}`;
    }
  }

  return undefined;
}

/**
 * Why a run approval for `drawing` does not cover this call (undefined: covered). Covered are civil3d_invoke action
 * write and action raw on any tool: both pass params unchanged to the plugin, which refuses the call when the active
 * drawing is not params.expectDrawing (C150). Other domain actions strip expectDrawing in their schemas, so the plugin
 * could not check the drawing; they keep per-call approval.
 */
export function runApprovalRefusal(drawing: string, target: ApprovalTarget, parameters: JsonObject): string | undefined {
  const covered = target.action === "raw" || (target.toolName === "civil3d_invoke" && target.action === "write");
  if (!covered) {
    return `${RUN_NOT_COVERED} '${target.toolName}' action '${target.action}' (only civil3d_invoke write and action raw); ${RUN_PER_CALL_HINT}`;
  }
  const params = asObject(parameters.params);
  const expect = nonEmptyString(params?.expectDrawing);
  if (!expect) {
    return `Run approval requires params.expectDrawing naming the run's drawing '${drawing}'; the call has none. Nothing was run.`;
  }
  if (!sameDrawingName(expect, drawing)) {
    return `Run approval is for drawing '${drawing}', but params.expectDrawing is '${expect}'. Request a run approval for that drawing. Nothing was run.`;
  }
  return runMethodRefusal(drawing, parameters.method, params, false);
}
const NO_ACTIVE_DRAWING_FINGERPRINT = "no-active-drawing";
const MUTATING_CAPABILITIES = new Set<ToolCapability>([
  "create",
  "edit",
  "delete",
  "manage",
  "import",
  "export",
]);
const EXPLICIT_APPROVAL_ACTION = /(?:^|_)(?:delete|remove|import|export|save|new|undo|redo|overwrite|replace|publish|promote|sync|fix|remediate)(?:_|$)/i;

function stableJson(value: unknown): string {
  if (Array.isArray(value)) {
    return `[${value.map((item) => stableJson(item)).join(",")}]`;
  }

  if (value && typeof value === "object") {
    const object = value as Record<string, unknown>;
    return `{${Object.keys(object)
      .sort()
      .map((key) => `${JSON.stringify(key)}:${stableJson(object[key])}`)
      .join(",")}}`;
  }

  return JSON.stringify(value);
}

function hashParameters(parameters: JsonObject): string {
  const normalized = { ...parameters };
  delete normalized.approvalToken;
  return createHash("sha256").update(stableJson(normalized)).digest("hex");
}

export function isApprovalRequired(target: ApprovalTarget): boolean {
  if (process.env.CIVIL3D_APPROVAL_MODE === "disabled") {
    return false;
  }

  return hasApprovalRisk(target);
}

export function hasApprovalRisk(target: ApprovalTarget): boolean {
  const mutatesState = target.capabilities.some((capability) => MUTATING_CAPABILITIES.has(capability));
  return (
    target.capabilities.some((capability) => ["delete", "import", "export"].includes(capability)) ||
    EXPLICIT_APPROVAL_ACTION.test(target.action) ||
    (!target.safeForRetry && mutatesState)
  );
}

export async function getActiveDrawingFingerprint(): Promise<string> {
  const drawingInfo = await withApplicationConnection((client) => client.sendCommand("getDrawingInfo", {}));
  return createHash("sha256").update(stableJson(drawingInfo)).digest("hex");
}

function isNoActiveDrawingError(error: unknown): boolean {
  const code = error && typeof error === "object" && "code" in error
    ? String(error.code)
    : "";
  const message = error instanceof Error ? error.message : String(error);
  return code === "CIVIL3D.NO_DRAWING" || code === "CIVIL3D.NO_ACTIVE_DRAWING" ||
    /no active (?:civil 3d )?drawing|no active (?:civil 3d )?document/i.test(message);
}

export class ApprovalPolicyService {
  private readonly grants = new Map<string, ApprovalGrant>();
  private readonly runGrants = new Map<string, RunApprovalGrant>();

  public constructor(
    private readonly drawingFingerprint: DrawingFingerprintProvider = getActiveDrawingFingerprint,
    private readonly now: () => number = () => Date.now(),
  ) {}

  private async fingerprintFor(target: ApprovalTarget): Promise<string> {
    try {
      return await this.drawingFingerprint();
    } catch (error) {
      if (target.requiresActiveDrawing === false && isNoActiveDrawingError(error)) {
        return NO_ACTIVE_DRAWING_FINGERPRINT;
      }
      throw error;
    }
  }

  public async requestApproval(
    target: ApprovalTarget,
    parameters: JsonObject,
    ttlMs = APPROVAL_TOKEN_TTL_MS,
  ): Promise<ApprovalReceipt> {
    if (!isApprovalRequired(target)) {
      throw new ApprovalValidationError(
        `'${target.toolName}' action '${target.action}' does not require approval. Execute it directly.`,
      );
    }

    const drawingFingerprint = await this.fingerprintFor(target);
    const expiresAt = this.now() + ttlMs;
    const approvalToken = randomUUID();
    this.grants.set(approvalToken, {
      toolName: target.toolName,
      action: target.action,
      parametersHash: hashParameters(parameters),
      drawingFingerprint,
      expiresAt,
    });

    return {
      approvalToken,
      expiresAt: new Date(expiresAt).toISOString(),
      drawingFingerprint,
    };
  }

  /**
   * C230 (F2, 2026-11-24): a reusable per-run token for one drawing, valid ttlMs (default 30
   * min, at most 120). It covers civil3d_invoke write and action raw whose params.expectDrawing names that drawing;
   * see runApprovalRefusal for what it never covers.
   */
  public requestRunApproval(drawing: string, ttlMs = RUN_APPROVAL_DEFAULT_TTL_MS): RunApprovalReceipt {
    const name = drawingFileName(drawing ?? "");
    if (!name) {
      throw new ApprovalValidationError("A run approval needs the drawing it is for (drawing: file name, e.g. 'sheet-101.dwg').");
    }
    if (/[*?<>|"]/.test(name) || name.length > 260) {
      throw new ApprovalValidationError(`'${name}' is not a drawing file name.`);
    }
    if (!Number.isFinite(ttlMs) || ttlMs < RUN_APPROVAL_MIN_TTL_MS || ttlMs > RUN_APPROVAL_MAX_TTL_MS) {
      throw new ApprovalValidationError("A run approval lasts 1 to 120 minutes.");
    }

    const now = this.now();
    for (const [token, grant] of this.runGrants) {
      if (grant.expiresAt <= now) this.runGrants.delete(token);
    }
    const expiresAt = now + ttlMs;
    const approvalToken = randomUUID();
    this.runGrants.set(approvalToken, { drawing: name, expiresAt });
    return { approvalToken, scope: "run", drawing: name, expiresAt: new Date(expiresAt).toISOString() };
  }

  private enforceRun(approvalToken: string, grant: RunApprovalGrant, target: ApprovalTarget, parameters: JsonObject): void {
    if (grant.expiresAt <= this.now()) {
      this.runGrants.delete(approvalToken);
      throw new ApprovalValidationError(
        `Run approval token has expired. Request a new run approval (scope run) for '${grant.drawing}'.`,
        "CIVIL3D.FORBIDDEN",
      );
    }
    const refusal = runApprovalRefusal(grant.drawing, target, parameters);
    if (refusal) throw new ApprovalValidationError(refusal, "CIVIL3D.FORBIDDEN");
  }

  public async enforce(target: ApprovalTarget, parameters: JsonObject): Promise<void> {
    if (!isApprovalRequired(target)) {
      return;
    }

    const approvalToken = typeof parameters.approvalToken === "string" ? parameters.approvalToken : undefined;
    if (!approvalToken) {
      throw new ApprovalRequiredError(
        `Approval required for '${target.toolName}' action '${target.action}'. ` +
        "Call civil3d_request_approval with the same toolName, action, and parameters, then retry with approvalToken.",
      );
    }

    const runGrant = this.runGrants.get(approvalToken);
    if (runGrant) {
      this.enforceRun(approvalToken, runGrant, target, parameters);
      return;
    }

    const grant = this.grants.get(approvalToken);
    if (!grant) {
      throw new ApprovalValidationError("Approval token is missing, expired, or has already been used.");
    }

    this.grants.delete(approvalToken);

    if (grant.expiresAt <= this.now()) {
      throw new ApprovalValidationError("Approval token has expired. Request a new approval.");
    }

    if (
      grant.toolName !== target.toolName ||
      grant.action !== target.action ||
      grant.parametersHash !== hashParameters(parameters)
    ) {
      throw new ApprovalValidationError("Approval token does not match this tool action and its parameters.");
    }

    const activeDrawingFingerprint = await this.fingerprintFor(target);
    if (grant.drawingFingerprint !== activeDrawingFingerprint) {
      throw new ApprovalValidationError("The active drawing changed after approval. Request approval for the current drawing.");
    }
  }
}

export const approvalPolicy = new ApprovalPolicyService();
