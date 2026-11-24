import { readFileSync } from "node:fs";
import { beforeEach, describe, expect, it, vi } from "vitest";

const { sendCommandMock } = vi.hoisted(() => ({ sendCommandMock: vi.fn() }));

vi.mock("../src/utils/ConnectionManager.js", () => ({
  withApplicationConnection: async <T>(
    operation: (client: { sendCommand: typeof sendCommandMock }) => Promise<T>,
  ) => await operation({ sendCommand: sendCommandMock }),
}));

import { isApprovalRequired } from "../src/tools/approvalPolicy.js";
import { DRAWING_RUNTIME_DOMAIN_DEFINITION } from "../src/tools/domains/drawingRuntimeDomain.js";
import { STANDARDS_DOMAIN_DEFINITION } from "../src/tools/domains/standardsDomain.js";

const plugin = (file: string) => readFileSync(new URL(`../Civil3D-MCP-Plugin/${file}`, import.meta.url), "utf8");

describe("C97 a successful save leaves the drawing saved", () => {
  beforeEach(() => sendCommandMock.mockReset());

  it("saves through the document (QSAVE or SaveAs) and reports unsavedChanges afterwards", () => {
    const source = plugin("DrawingCommands.cs");
    expect(source).toContain('InvokeComMethod(acadDocument, "Save")');
    expect(source).toContain('InvokeComMethod(acadDocument, "SaveAs", target, AcNative)');
    expect(source).toContain('["unsavedChanges"] = DocumentCommands.HasUnsavedChanges(doc)');
    // A drawing never saved has no file of its own: its Database.Filename names the template, never saved over.
    expect(source).toContain("saveDrawing requires 'saveAs'");
    // COM is reached only through the compatibility boundary.
    expect(plugin("Civil3DCompatibility.cs")).toContain("public static object? InvokeComMethod(");
  });

  it("still saves behind approval, forwarding saveAs and overwrite", async () => {
    const save = DRAWING_RUNTIME_DOMAIN_DEFINITION.actions.save;
    sendCommandMock.mockResolvedValue({ saved: true, unsavedChanges: false });
    await save.execute(save.inputSchema.parse({ action: "save", saveAs: "C:/work/project/Main_St.dwg", overwrite: true }) as never);
    expect(sendCommandMock).toHaveBeenLastCalledWith("saveDrawing", { saveAs: "C:/work/project/Main_St.dwg", overwrite: true });
    expect(isApprovalRequired({ toolName: "civil3d_drawing", action: "save", capabilities: save.capabilities as never, safeForRetry: save.safeForRetry })).toBe(true);
  });
});

describe("C97 alignment label sets", () => {
  beforeEach(() => sendCommandMock.mockReset());

  it("imports the label set by its exact name instead of setting a property the alignment lacks", () => {
    const source = plugin("LabelCommands.cs");
    const start = source.indexOf('string.Equals(objectType, "alignment", StringComparison.OrdinalIgnoreCase) && string.Equals(labelType, "label_set"');
    const end = source.indexOf('string.Equals(labelType, "station"', start);
    const block = source.slice(start, end);
    expect(block).toContain("alignment.ImportLabelSet(labelSetId)");
    expect(block).toContain("GetStyleIdStrict(civilDoc.Styles.LabelSetStyles.AlignmentLabelSetStyles");
    expect(block).not.toContain('"LabelSetId"');
    expect(block).toContain("label_set needs labelStyle");
  });

  it("forwards the label set name behind approval", async () => {
    const add = STANDARDS_DOMAIN_DEFINITION.actions.label_add;
    sendCommandMock.mockResolvedValue({ applied: true });
    const args = { action: "label_add", objectType: "alignment", objectName: "Main_St_CL", labelType: "label_set", labelStyle: "No radius-TP" };
    await add.execute(add.inputSchema.parse(args) as never);
    expect(sendCommandMock).toHaveBeenLastCalledWith("addLabel", expect.objectContaining({ objectType: "alignment", objectName: "Main_St_CL", labelType: "label_set", labelStyle: "No radius-TP" }));
    expect(isApprovalRequired({ toolName: "civil3d_standards", action: "label_add", capabilities: add.capabilities as never, safeForRetry: add.safeForRetry })).toBe(true);
  });
});
