import { beforeEach, describe, expect, it, vi } from "vitest";
import { z } from "zod";

const { sendCommandMock } = vi.hoisted(() => ({ sendCommandMock: vi.fn() }));

vi.mock("../src/utils/ConnectionManager.js", () => ({
  withApplicationConnection: async <T>(
    operation: (client: { sendCommand: typeof sendCommandMock }) => Promise<T>,
  ) => await operation({ sendCommand: sendCommandMock }),
}));

import { isApprovalRequired } from "../src/tools/approvalPolicy.js";
import { DRAWING_RUNTIME_DOMAIN_DEFINITION } from "../src/tools/domains/drawingRuntimeDomain.js";
import { JOB_DOMAIN_DEFINITION } from "../src/tools/domains/jobDomain.js";

const drawing = DRAWING_RUNTIME_DOMAIN_DEFINITION.actions;
const approval = (action: string) =>
  isApprovalRequired({ toolName: "civil3d_drawing", action, capabilities: drawing[action].capabilities as never, safeForRetry: drawing[action].safeForRetry });
const run = async (action: string, args: Record<string, unknown>) =>
  await drawing[action].execute(drawing[action].inputSchema.parse({ action, ...args }) as never);
const survivesExposure = (args: Record<string, unknown>) => {
  const exposure = DRAWING_RUNTIME_DOMAIN_DEFINITION.exposures.find((e) => e.toolName === "civil3d_drawing")!;
  const parsed = z.object(exposure.inputShape).parse(args) as Record<string, unknown>;
  expect(Object.keys(parsed).sort()).toEqual(Object.keys(args).sort());
};

describe("C67 text styles", () => {
  beforeEach(() => sendCommandMock.mockReset());

  it("lists text styles, optionally filtered, without approval", async () => {
    sendCommandMock.mockResolvedValue({ styles: [] });
    await run("textstyles_list", { contains: "iso" });
    expect(sendCommandMock).toHaveBeenCalledWith("listTextStyles", { contains: "iso" });
    expect(approval("textstyles_list")).toBe(false);
  });

  it("sets a text style font, forwarding every parameter, behind approval", async () => {
    sendCommandMock.mockResolvedValue({});
    const args = { name: "Company", fileName: "isocpeur.ttf", height: 0, widthFactor: 1, obliquingDegrees: 0, create: true, makeCurrent: true };
    await run("textstyle_set", args);
    expect(sendCommandMock).toHaveBeenCalledWith("setTextStyle", args);
    survivesExposure({ action: "textstyle_set", ...args, typeface: "ISOCPEUR", bigFontFileName: "" });
    expect(approval("textstyle_set")).toBe(true);
  });

  it("rejects a text style change with nothing to change or a bad font file", () => {
    const s = drawing.textstyle_set.inputSchema;
    expect(s.safeParse({ action: "textstyle_set", name: "Company" }).success).toBe(false);
    expect(s.safeParse({ action: "textstyle_set", name: "Company", fileName: "font.otf" }).success).toBe(false);
    expect(s.safeParse({ action: "textstyle_set", name: "Company", typeface: "Arial" }).success).toBe(true);
    expect(s.safeParse({ action: "textstyle_set", name: "Company", widthFactor: 0 }).success).toBe(false);
  });
});

describe("C68 block definitions", () => {
  beforeEach(() => sendCommandMock.mockReset());

  it("reads a block definition without approval", async () => {
    sendCommandMock.mockResolvedValue({});
    await run("block_definition_get", { blockName: "Stamp", includeGeometry: true, maxEntities: 300 });
    expect(sendCommandMock).toHaveBeenCalledWith("getBlockDefinition", { blockName: "Stamp", includeGeometry: true, maxEntities: 300 });
    expect(approval("block_definition_get")).toBe(false);
  });

  it("changes attribute definitions and texts, syncing references by default, behind approval", async () => {
    sendCommandMock.mockResolvedValue({});
    const args = {
      blockName: "Stamp",
      attributeDefinitions: [
        { tag: "PROJNO", x: -118, y: 5, height: 3.5, justify: "BaseLeft", textStyle: "Company" },
        { tag: "ISSUE", create: true, x: -118, y: 18, height: 3.5, prompt: "Document status", defaultValue: "" },
        { tag: "LINE4", newTag: "TITLE4", color: 7 },
        { tag: "DRAWN", color: "ByLayer" },
      ],
      removeAttributes: ["FIXED"],
      texts: [{ find: "REV?SION", replace: "REVISION" }],
    };
    await run("block_definition_set", args);
    expect(sendCommandMock).toHaveBeenCalledWith("setBlockDefinition", { ...args, syncReferences: true });
    survivesExposure({ action: "block_definition_set", ...args, syncReferences: false });
    expect(approval("block_definition_set")).toBe(true);
  });

  it("validates block definition changes", () => {
    const s = drawing.block_definition_set.inputSchema;
    const base = { action: "block_definition_set", blockName: "Stamp" };
    expect(s.safeParse(base).success).toBe(false);
    expect(s.safeParse({ ...base, attributeDefinitions: [{ tag: "NY", create: true, x: 0 }] }).success).toBe(false);
    expect(s.safeParse({ ...base, attributeDefinitions: [{ tag: "A", height: 0 }] }).success).toBe(false);
    expect(s.safeParse({ ...base, attributeDefinitions: [{ tag: "A", justify: "Middle" }] }).success).toBe(false);
    expect(s.safeParse({ ...base, attributeDefinitions: [{ tag: "A", justify: "MiddleLeft" }] }).success).toBe(true);
    expect(s.safeParse({ ...base, texts: [{ find: "", replace: "x" }] }).success).toBe(false);
    expect(s.safeParse({ ...base, attributeDefinitions: [{ tag: "A", color: 257 }] }).success).toBe(false);
    expect(s.safeParse({ ...base, attributeDefinitions: [{ tag: "A", color: "Red" }] }).success).toBe(false);
  });
});

describe("C68b full attribute sync", () => {
  beforeEach(() => sendCommandMock.mockReset());

  it("runs a full ATTSYNC on its own, behind approval", async () => {
    sendCommandMock.mockResolvedValue({});
    await run("block_definition_set", { blockName: "Stamp", attsync: true });
    expect(sendCommandMock).toHaveBeenCalledWith("setBlockDefinition", { blockName: "Stamp", attsync: true, syncReferences: true });
    survivesExposure({ action: "block_definition_set", blockName: "Stamp", attsync: true });
    expect(drawing.block_definition_set.inputSchema.safeParse({ action: "block_definition_set", blockName: "Stamp", attsync: false }).success).toBe(false);
  });
});

describe("C69 page setups", () => {
  beforeEach(() => sendCommandMock.mockReset());

  it("lists plot media and page setups without approval", async () => {
    sendCommandMock.mockResolvedValue({});
    await run("plot_media_list", { device: "DWG To PDF.pc3", widthMm: 594, heightMm: 297 });
    expect(sendCommandMock).toHaveBeenCalledWith("listPlotMedia", { device: "DWG To PDF.pc3", widthMm: 594, heightMm: 297 });
    await run("page_setups_list", {});
    expect(sendCommandMock).toHaveBeenLastCalledWith("listPageSetups", {});
    expect(approval("plot_media_list")).toBe(false);
    expect(approval("page_setups_list")).toBe(false);
  });

  it("creates or updates a named page setup, forwarding every parameter, behind approval", async () => {
    sendCommandMock.mockResolvedValue({});
    const args = {
      name: "PDF A1 to A3 monochrome", device: "DWG To PDF.pc3", media: "ISO_full_bleed_A3_(420.00_x_297.00_MM)",
      plotStyleTable: "monochrome.ctb", scaleDenominator: 2, rotationDegrees: 0, plotStyles: true, lineweights: true, scaleLineweights: false,
      plotOriginMm: [2.9, 8.9], plotTransparency: true,
    };
    await run("page_setup_set", args);
    expect(sendCommandMock).toHaveBeenCalledWith("setPageSetup", args);
    survivesExposure({ action: "page_setup_set", ...args, widthMm: 420, heightMm: 297 });
    expect(approval("page_setup_set")).toBe(true);
  });

  it("applies a page setup to layouts behind approval", async () => {
    sendCommandMock.mockResolvedValue({});
    await run("page_setup_apply", { name: "PDF A1 monochrome", layoutNames: ["A1", "A1 Plan+Profile"] });
    expect(sendCommandMock).toHaveBeenCalledWith("applyPageSetup", { name: "PDF A1 monochrome", layoutNames: ["A1", "A1 Plan+Profile"] });
    survivesExposure({ action: "page_setup_apply", name: "PDF A1 monochrome", layoutNames: ["A1"] });
    expect(approval("page_setup_apply")).toBe(true);
  });

  it("validates page setup input", () => {
    const set = drawing.page_setup_set.inputSchema;
    const base = { action: "page_setup_set", name: "PDF A1 monochrome" };
    expect(set.safeParse({ ...base, media: "ISO A1" }).success).toBe(true);
    expect(set.safeParse({ ...base, widthMm: 594 }).success).toBe(false);
    expect(set.safeParse({ ...base, media: "ISO A1", widthMm: 594, heightMm: 841 }).success).toBe(false);
    expect(set.safeParse({ ...base, rotationDegrees: 45 }).success).toBe(false);
    expect(set.safeParse({ ...base, scaleDenominator: 0 }).success).toBe(false);
    expect(set.safeParse({ ...base, plotOriginMm: [1] }).success).toBe(false);
    // C93b: plot transparency on its own (the grey base map at 50 % layer transparency).
    expect(set.safeParse({ ...base, plotTransparency: true }).success).toBe(true);
    expect(set.safeParse({ ...base, plotTransparency: "ja" }).success).toBe(false);
    expect(drawing.page_setup_apply.inputSchema.safeParse({ action: "page_setup_apply", name: "X", layoutNames: [] }).success).toBe(false);
    expect(drawing.plot_media_list.inputSchema.safeParse({ action: "plot_media_list", widthMm: 594 }).success).toBe(false);
  });
});

describe("C70 plugin reload", () => {
  beforeEach(() => sendCommandMock.mockReset());

  it("asks the plugin to reload the newest build, behind approval", async () => {
    sendCommandMock.mockResolvedValue({ scheduled: true });
    await run("plugin_reload", {});
    expect(sendCommandMock).toHaveBeenCalledWith("reloadPlugin", {});
    expect(approval("plugin_reload")).toBe(true);
  });
});

describe("C72 layout plot with a page setup", () => {
  beforeEach(() => sendCommandMock.mockReset());

  it("plots a layout as a named page setup says, PDF only", async () => {
    sendCommandMock.mockResolvedValue({});
    await run("layout_plot", { layout: "A1", output: "D:/tmp/a1_a3.pdf", pageSetup: "PDF A1 on A3 monochrome" });
    expect(sendCommandMock).toHaveBeenCalledWith("plotLayout", { layout: "A1", output: "D:/tmp/a1_a3.pdf", pageSetup: "PDF A1 on A3 monochrome", overwrite: false });
    survivesExposure({ action: "layout_plot", layout: "A1", output: "D:/tmp/a.pdf", pageSetup: "PDF A1 monochrome" });
    const s = drawing.layout_plot.inputSchema;
    expect(s.safeParse({ action: "layout_plot", layout: "A1", output: "D:/tmp/a.png", pageSetup: "X" }).success).toBe(false);
    expect(s.safeParse({ action: "layout_plot", layout: "A1", output: "D:/tmp/a.pdf", pageSetup: "X", media: "ISO A3" }).success).toBe(false);
  });
});

describe("C73 save diagnostics", () => {
  beforeEach(() => sendCommandMock.mockReset());

  it("reads object classes and loaded modules without approval; side-database save tests behind approval", async () => {
    sendCommandMock.mockResolvedValue({});
    await run("object_classes_list", { sourcePath: "C:/templates/template.dwt" });
    expect(sendCommandMock).toHaveBeenLastCalledWith("listObjectClasses", { sourcePath: "C:/templates/template.dwt" });
    await run("modules_list", { contains: "acge" });
    expect(sendCommandMock).toHaveBeenLastCalledWith("listLoadedModules", { contains: "acge" });
    const args = { sourcePath: "C:/templates/template.dwt", attempts: 2, format: "dxf", version: "2018", eraseDictionaryEntries: ["X"], eraseClasses: ["3DSOLID"], eraseBlocks: ["B"], replaceSolidsInBlocks: ["3D-Top-Block1"], clearSolidHistory: true };
    await run("save_test", args);
    expect(sendCommandMock).toHaveBeenLastCalledWith("saveTest", args);
    survivesExposure({ action: "save_test", ...args });
    for (const a of ["object_classes_list", "modules_list"]) expect(approval(a)).toBe(false);
    expect(approval("save_test")).toBe(true);   // writes temp files; "save" in the name asks for approval
    expect(drawing.save_test.inputSchema.safeParse({ action: "save_test", sourcePath: "x.dwt", attempts: 9 }).success).toBe(false);
    expect(drawing.save_test.inputSchema.safeParse({ action: "save_test", sourcePath: "x.dwt", format: "pdf" }).success).toBe(false);
  });

  it("replaces 3D solids with history-free copies behind approval", async () => {
    sendCommandMock.mockResolvedValue({});
    await run("solids_history_remove", { blocks: ["3D-Top-Block1", "3D-Bottom-block1"] });
    expect(sendCommandMock).toHaveBeenCalledWith("replaceSolidsWithoutHistory", { blocks: ["3D-Top-Block1", "3D-Bottom-block1"] });
    survivesExposure({ action: "solids_history_remove", blocks: ["3D-Top-Block1"] });
    expect(approval("solids_history_remove")).toBe(true);
  });
});

describe("C75 block references and viewport on/off", () => {
  beforeEach(() => sendCommandMock.mockReset());

  it("reads a block reference without approval and sets position, scale, rotation and dynamic properties behind approval", async () => {
    sendCommandMock.mockResolvedValue({});
    await run("block_reference_get", { handle: "4047449" });
    expect(sendCommandMock).toHaveBeenLastCalledWith("getBlockReference", { handle: "4047449" });
    const args = { handle: "4047449", x: 0, y: 0, scaleX: 1, scaleY: 1, rotationDegrees: 0, dynamicProperties: { Skala: "1:400", Avstand: 12.5, Spegla: true } };
    await run("block_reference_set", args);
    expect(sendCommandMock).toHaveBeenLastCalledWith("setBlockReference", args);
    survivesExposure({ action: "block_reference_set", ...args });
    expect(approval("block_reference_get")).toBe(false);
    expect(approval("block_reference_set")).toBe(true);
    expect(drawing.block_reference_set.inputSchema.safeParse({ action: "block_reference_set", handle: "4047449", x: 1 }).success).toBe(false);
    expect(drawing.block_reference_set.inputSchema.safeParse({ action: "block_reference_set", handle: "4047449" }).success).toBe(false);
    expect(drawing.block_reference_set.inputSchema.safeParse({ action: "block_reference_set", handle: "xyz", x: 1, y: 2 }).success).toBe(false);
  });

  it("turns a viewport on", async () => {
    sendCommandMock.mockResolvedValue({});
    await run("viewport_set", { layout: "A0", handle: "4049C81", on: true });
    expect(sendCommandMock).toHaveBeenLastCalledWith("setViewport", expect.objectContaining({ layout: "A0", handle: "4049C81", on: true }));
  });
});

describe("C77 layout text with rotation, text style and fields", () => {
  beforeEach(() => sendCommandMock.mockReset());

  it("forwards rotation and text style; field codes pass through as text", async () => {
    sendCommandMock.mockResolvedValue({});
    const text = "%<\AcVar Filename>%  %<\AcVar ctab>%  %<\AcVar PlotDate \f \"yyyy-MM-dd HH:mm\">%";
    const args = { layout: "A1", text, x: 36.2, y: 20, height: 1.8, layer: "DWG-FORM", rotationDegrees: 90, textStyle: "ISO" };
    await run("layout_text_add", args);
    expect(sendCommandMock).toHaveBeenCalledWith("addLayoutText", { ...args, width: 0 });
    survivesExposure({ action: "layout_text_add", ...args });
  });
});

describe("C78 styles between drawings", () => {
  beforeEach(() => sendCommandMock.mockReset());

  it("lists another drawing's style collections without approval and imports named styles behind approval", async () => {
    sendCommandMock.mockResolvedValue({});
    await run("styles_list_external", { sourcePath: "C:/work/project/source.dwg", collection: "PipeStyles", contains: "profile" });
    expect(sendCommandMock).toHaveBeenLastCalledWith("listExternalStyles", { sourcePath: "C:/work/project/source.dwg", collection: "PipeStyles", contains: "profile" });
    await run("styles_list_external", {});
    expect(sendCommandMock).toHaveBeenLastCalledWith("listExternalStyles", {});
    const args = { sourcePath: "C:/work/project/source.dwg", items: [{ collection: "PipeStyles", names: ["Pipe package profile"] }], conflict: "ignore" };
    await run("styles_import", args);
    expect(sendCommandMock).toHaveBeenLastCalledWith("importStyles", args);
    survivesExposure({ action: "styles_import", ...args });
    expect(approval("styles_list_external")).toBe(false);
    expect(approval("styles_import")).toBe(true);
    expect(drawing.styles_import.inputSchema.safeParse({ action: "styles_import", sourcePath: "x.dwg", items: [] }).success).toBe(false);
    expect(drawing.styles_import.inputSchema.safeParse({ action: "styles_import", sourcePath: "x.dwg", items: [{ collection: "PipeStyles", names: ["A"] }], conflict: "merge" }).success).toBe(false);
  });
});

describe("C80 ambient settings", () => {
  beforeEach(() => sendCommandMock.mockReset());

  it("reads ambient settings without approval and sets Group.Property values behind approval", async () => {
    sendCommandMock.mockResolvedValue({});
    await run("settings_ambient_get", { group: "Grade" });
    expect(sendCommandMock).toHaveBeenLastCalledWith("getAmbientSettings", { group: "Grade" });
    const args = { values: { "Grade.Format": "PerMille", "Elevation.Precision": "2" } };
    await run("settings_ambient_set", args);
    expect(sendCommandMock).toHaveBeenLastCalledWith("setAmbientSettings", args);
    survivesExposure({ action: "settings_ambient_set", ...args });
    expect(approval("settings_ambient_get")).toBe(false);
    expect(approval("settings_ambient_set")).toBe(true);
    expect(drawing.settings_ambient_set.inputSchema.safeParse({ action: "settings_ambient_set", values: { Grade: "PerMille" } }).success).toBe(false);
    expect(drawing.settings_ambient_set.inputSchema.safeParse({ action: "settings_ambient_set", values: {} }).success).toBe(false);
  });
});

describe("C82 template clean-up", () => {
  beforeEach(() => sendCommandMock.mockReset());

  it("purges unreferenced blocks by names or pattern behind approval", async () => {
    sendCommandMock.mockResolvedValue({});
    const args = { names: ["Catch Basin"], pattern: "^Flared", dryRun: true };
    await run("blocks_purge", args);
    expect(sendCommandMock).toHaveBeenLastCalledWith("purgeBlocks", args);
    survivesExposure({ action: "blocks_purge", ...args });
    expect(approval("blocks_purge")).toBe(true);
    expect(drawing.blocks_purge.inputSchema.safeParse({ action: "blocks_purge" }).success).toBe(false);
  });
});

describe("C83 multileader style", () => {
  beforeEach(() => sendCommandMock.mockReset());

  it("creates or updates a multileader style behind approval", async () => {
    sendCommandMock.mockResolvedValue({});
    const args = { name: "Pipe flag", textStyle: "ISO", textHeight: 2.5, arrowSize: 2, landingGap: 1, doglegLength: 2.5, annotative: true, frame: true, backgroundColor: 0, makeCurrent: true };
    await run("mleader_style_set", args);
    expect(sendCommandMock).toHaveBeenLastCalledWith("setMLeaderStyle", args);
    survivesExposure({ action: "mleader_style_set", ...args });
    expect(approval("mleader_style_set")).toBe(true);
  });
});

describe("C84 block entity colour", () => {
  beforeEach(() => sendCommandMock.mockReset());

  it("sets one colour on a block definition's entities behind approval", async () => {
    sendCommandMock.mockResolvedValue({});
    const args = { blockName: "Scale bar Meter", color: "ByBlock", fromColor: 3 };
    await run("block_entities_color_set", args);
    expect(sendCommandMock).toHaveBeenLastCalledWith("setBlockEntitiesColor", args);
    survivesExposure({ action: "block_entities_color_set", ...args });
    expect(approval("block_entities_color_set")).toBe(true);
    expect(drawing.block_entities_color_set.inputSchema.safeParse({ action: "block_entities_color_set", blockName: "B", color: 300 }).success).toBe(false);
  });
});

describe("C85 band sets", () => {
  beforeEach(() => sendCommandMock.mockReset());

  it("lists profile view band sets without approval", async () => {
    sendCommandMock.mockResolvedValue({});
    await run("band_sets_list", { contains: "VA" });
    expect(sendCommandMock).toHaveBeenLastCalledWith("listBandSets", { contains: "VA" });
    expect(approval("band_sets_list")).toBe(false);
  });
});

describe("C86 band set write", () => {
  beforeEach(() => sendCommandMock.mockReset());

  it("creates or changes a band set behind approval", async () => {
    sendCommandMock.mockResolvedValue({});
    const args = { name: "Gravity", copyFrom: "Profile bands - split - NV", removeStyles: ["Soil type", "Ground improvement", "Compaction height"] };
    await run("band_set_set", args);
    expect(sendCommandMock).toHaveBeenLastCalledWith("setBandSet", args);
    survivesExposure({ action: "band_set_set", ...args });
    const add = { name: "Package", appendBottom: [{ bandType: "ProfileData", style: "Package - Stationing", gap: 0 }] };
    await run("band_set_set", add);
    expect(sendCommandMock).toHaveBeenLastCalledWith("setBandSet", add);
    expect(approval("band_set_set")).toBe(true);
    expect(drawing.band_set_set.inputSchema.safeParse({ action: "band_set_set", name: "X", bottom: [], appendBottom: [{ bandType: "ProfileData", style: "S" }] }).success).toBe(false);
  });
});

describe("C87 block geometry", () => {
  beforeEach(() => sendCommandMock.mockReset());

  it("edits a block definition's drawn entities behind approval", async () => {
    sendCommandMock.mockResolvedValue({});
    const args = {
      blockName: "Stamp",
      erase: [{ handles: ["4049CE3"], expect: 1 }],
      set: [{ select: { type: "text", layer: "DWG-STAMP-TEXT" }, height: 1.8 }, { select: { type: "line", minLength: 100 }, lineweight: 70 }],
      add: [
        { type: "line", from: [-100, 99], to: [0, 99], layer: "DWG-STAMP-LINE" },
        { type: "text", text: "REF NO", at: [-12.5, 100.5], height: 1.8, textStyle: "ISO" },
        { type: "insert", block: "Logo", at: [-50, 80], scale: 0.6 },
      ],
    };
    await run("block_entities_edit", args);
    expect(sendCommandMock).toHaveBeenLastCalledWith("editBlockEntities", args);
    survivesExposure({ action: "block_entities_edit", ...args });
    expect(approval("block_entities_edit")).toBe(true);
  });

  it("creates a block as a copy and rejects calls without work or with unknown entity types", () => {
    const schema = drawing.block_entities_edit.inputSchema;
    expect(schema.safeParse({ action: "block_entities_edit", blockName: "Stamp compact", copyFrom: "Stamp" }).success).toBe(true);
    expect(schema.safeParse({ action: "block_entities_edit", blockName: "Stamp" }).success).toBe(false);
    expect(schema.safeParse({ action: "block_entities_edit", blockName: "Stamp", add: [{ type: "spline", at: [0, 0] }] }).success).toBe(false);
    expect(schema.safeParse({ action: "block_entities_edit", blockName: "Stamp", set: [{ select: { type: "line" }, lineweight: 0.7 }] }).success).toBe(false);
  });

  it("points references at another block behind approval", async () => {
    sendCommandMock.mockResolvedValue({});
    const args = { from: "Stamp", to: "Stamp compact", layoutNames: ["A3", "A4"], scale: 1 };
    await run("block_references_replace", args);
    expect(sendCommandMock).toHaveBeenLastCalledWith("replaceBlockReferences", args);
    survivesExposure({ action: "block_references_replace", ...args });
    expect(approval("block_references_replace")).toBe(true);
  });
});

describe("C88 layer standard", () => {
  beforeEach(() => sendCommandMock.mockReset());

  it("imports a layer standard from CSV behind approval, forwarding every parameter", async () => {
    sendCommandMock.mockResolvedValue({});
    const args = { path: "C:/templates/layer_standard.csv", create: true, update: false, dryRun: true, linFile: "acadiso.lin" };
    await run("layers_import", args);
    expect(sendCommandMock).toHaveBeenLastCalledWith("layersImport", args);
    survivesExposure({ action: "layers_import", ...args });
    expect(approval("layers_import")).toBe(true);
    const s = drawing.layers_import.inputSchema;
    expect(s.safeParse({ action: "layers_import", path: "D:/x/layers.xlsx" }).success).toBe(false);
    expect(s.safeParse({ action: "layers_import", path: "D:/x/layers.csv", linFile: "acad.txt" }).success).toBe(false);
    expect(s.safeParse({ action: "layers_import", path: "D:/x/layers.csv", create: false, update: false }).success).toBe(false);
    expect(s.safeParse({ action: "layers_import", path: "D:/x/layers.csv", create: false, update: false, dryRun: true }).success).toBe(true);
  });

  it("loads named or all linetypes from a .lin file behind approval", async () => {
    sendCommandMock.mockResolvedValue({});
    const args = { path: "acadiso.lin", names: ["DASHED", "HIDDEN2"], replace: true };
    await run("linetypes_load", args);
    expect(sendCommandMock).toHaveBeenLastCalledWith("linetypesLoad", args);
    survivesExposure({ action: "linetypes_load", ...args });
    await run("linetypes_load", { path: "C:/templates/company.lin", all: true });
    expect(sendCommandMock).toHaveBeenLastCalledWith("linetypesLoad", { path: "C:/templates/company.lin", all: true });
    expect(approval("linetypes_load")).toBe(true);
    const s = drawing.linetypes_load.inputSchema;
    expect(s.safeParse({ action: "linetypes_load", path: "acadiso.lin" }).success).toBe(false);
    expect(s.safeParse({ action: "linetypes_load", path: "acadiso.lin", names: ["DASHED"], all: true }).success).toBe(false);
    expect(s.safeParse({ action: "linetypes_load", path: "acadiso.txt", all: true }).success).toBe(false);
    expect(s.safeParse({ action: "linetypes_load", path: "acadiso.lin", names: [] }).success).toBe(false);
  });

  it("reads Civil's object layers without approval and sets them behind approval", async () => {
    sendCommandMock.mockResolvedValue({});
    await run("object_layers_get", { contains: "Pipe" });
    expect(sendCommandMock).toHaveBeenLastCalledWith("objectLayersGet", { contains: "Pipe" });
    const args = { values: { Pipe: { layer: "C-PIPE", modifier: "Suffix", modifierValue: "-*" }, Structure: { layer: "C-STRUCT", locked: false } }, createLayers: true };
    await run("object_layers_set", args);
    expect(sendCommandMock).toHaveBeenLastCalledWith("objectLayersSet", args);
    survivesExposure({ action: "object_layers_set", ...args });
    expect(approval("object_layers_get")).toBe(false);
    expect(approval("object_layers_set")).toBe(true);
    const s = drawing.object_layers_set.inputSchema;
    expect(s.safeParse({ action: "object_layers_set", values: {} }).success).toBe(false);
    expect(s.safeParse({ action: "object_layers_set", values: { Pipe: {} } }).success).toBe(false);
    expect(s.safeParse({ action: "object_layers_set", values: { Pipe: { modifier: "Infix" } } }).success).toBe(false);
    expect(s.safeParse({ action: "object_layers_set", values: { Pipe: { lockLayer: true } } }).success).toBe(false);
  });

  it("still passes string values (block attributes, ambient settings) through the widened values field", () => {
    survivesExposure({ action: "block_attributes_set", blockName: "Stamp", values: { PROJNR: "1234" } });
    survivesExposure({ action: "settings_ambient_set", values: { "Grade.Format": "PerMille" } });
  });

  it("lists layer states without approval; saves, restores and deletes them behind approval", async () => {
    sendCommandMock.mockResolvedValue({});
    await run("layer_states_list", { name: "Plan" });
    expect(sendCommandMock).toHaveBeenLastCalledWith("layerStatesList", { name: "Plan" });
    const save = { name: "Plan", description: "Plan drawing, utility coordination off", overwrite: true, viewport: "4049C81" };
    await run("layer_state_save", save);
    expect(sendCommandMock).toHaveBeenLastCalledWith("layerStateSave", save);
    survivesExposure({ action: "layer_state_save", ...save });
    await run("layer_state_restore", { name: "Plan", viewport: "4049C81" });
    expect(sendCommandMock).toHaveBeenLastCalledWith("layerStateRestore", { name: "Plan", viewport: "4049C81" });
    survivesExposure({ action: "layer_state_restore", name: "Plan", viewport: "4049C81" });
    await run("layer_state_delete", { name: "Plan" });
    expect(sendCommandMock).toHaveBeenLastCalledWith("layerStateDelete", { name: "Plan" });
    expect(approval("layer_states_list")).toBe(false);
    for (const a of ["layer_state_save", "layer_state_restore", "layer_state_delete"]) expect(approval(a)).toBe(true);
    expect(drawing.layer_state_restore.inputSchema.safeParse({ action: "layer_state_restore", name: "Plan", viewport: "VP1" }).success).toBe(false);
    expect(drawing.layer_state_save.inputSchema.safeParse({ action: "layer_state_save", name: "" }).success).toBe(false);
    expect(drawing.layer_state_save.inputSchema.safeParse({ action: "layer_state_save", name: "Plan", viewport: "VP1" }).success).toBe(false);
    expect(drawing.layer_state_save.inputSchema.safeParse({ action: "layer_state_save", name: "Plan", description: "" }).success).toBe(true);
  });
});

describe("C89 coordinate crosses and north arrow", () => {
  beforeEach(() => sendCommandMock.mockReset());

  it("draws coordinate crosses for a viewport behind approval, forwarding every parameter", async () => {
    sendCommandMock.mockResolvedValue({});
    const args = { layout: "A1 Plan+Profile", handle: "4049C81", intervalM: 100, sizeMm: 5, layer: "RIT-KOORDINATKRYSS", textStyle: "ISO", textHeightMm: 1.8, labels: "XY", edgeLabels: true, replace: true };
    await run("grid_crosses_add", args);
    expect(sendCommandMock).toHaveBeenLastCalledWith("gridCrossesAdd", args);
    survivesExposure({ action: "grid_crosses_add", ...args });
    await run("grid_crosses_add", { layout: "A1", handle: "4049C81", labels: "NE" });
    // Defaults (100 m, 5 mm, RIT-KOORDINATKRYSS, ISO 1.8, XY) are the plugin's; Node forwards only what is given.
    expect(sendCommandMock).toHaveBeenLastCalledWith("gridCrossesAdd", { layout: "A1", handle: "4049C81", labels: "NE" });
    expect(approval("grid_crosses_add")).toBe(true);
    const s = drawing.grid_crosses_add.inputSchema;
    const base = { action: "grid_crosses_add", layout: "A1", handle: "4049C81" };
    expect(s.safeParse({ ...base, intervalM: 0 }).success).toBe(false);
    expect(s.safeParse({ ...base, sizeMm: -5 }).success).toBe(false);
    expect(s.safeParse({ ...base, textHeightMm: 0 }).success).toBe(false);
    expect(s.safeParse({ ...base, labels: "EN" }).success).toBe(false);
    expect(s.safeParse({ ...base, handle: "VP-1" }).success).toBe(false);
    expect(s.safeParse({ action: "grid_crosses_add", layout: "A1" }).success).toBe(false);
  });

  it("turns the north arrow by the viewport's twist behind approval", async () => {
    sendCommandMock.mockResolvedValue({});
    await run("north_arrow_align", { layout: "A1" });
    expect(sendCommandMock).toHaveBeenLastCalledWith("northArrowAlign", { layout: "A1" });
    const args = { layout: "A1 Plan+Profile", blockName: "North arrow", handle: "4049C81" };
    await run("north_arrow_align", args);
    expect(sendCommandMock).toHaveBeenLastCalledWith("northArrowAlign", args);
    survivesExposure({ action: "north_arrow_align", ...args });
    expect(approval("north_arrow_align")).toBe(true);
    expect(drawing.north_arrow_align.inputSchema.safeParse({ action: "north_arrow_align", layout: "A1", blockName: "" }).success).toBe(false);
    expect(drawing.north_arrow_align.inputSchema.safeParse({ action: "north_arrow_align", layout: "A1", handle: "x" }).success).toBe(false);
    expect(drawing.north_arrow_align.inputSchema.safeParse({ action: "north_arrow_align" }).success).toBe(false);
  });
});

describe("C88e layer standard additions", () => {
  beforeEach(() => sendCommandMock.mockReset());

  it("purges unused layers by names or pattern behind approval, dry run left to the plugin's default", async () => {
    sendCommandMock.mockResolvedValue({});
    await run("layers_purge", { pattern: "^(C-|V-)" });
    expect(sendCommandMock).toHaveBeenLastCalledWith("layersPurge", { pattern: "^(C-|V-)" });
    const args = { names: ["OLD-TEXT", "TEMP"], pattern: "^XX-", dryRun: false };
    await run("layers_purge", args);
    expect(sendCommandMock).toHaveBeenLastCalledWith("layersPurge", args);
    survivesExposure({ action: "layers_purge", ...args });
    expect(approval("layers_purge")).toBe(true);
    const s = drawing.layers_purge.inputSchema;
    expect(s.safeParse({ action: "layers_purge" }).success).toBe(false);
    expect(s.safeParse({ action: "layers_purge", names: [] }).success).toBe(false);
    expect(s.safeParse({ action: "layers_purge", pattern: "" }).success).toBe(false);
    expect(s.safeParse({ action: "layers_purge", names: ["A"], dryRun: "no" }).success).toBe(false);
  });

  it("restores a layer state into a viewport with the same call shape (viewport freeze only is the plugin's)", async () => {
    sendCommandMock.mockResolvedValue({});
    await run("layer_state_restore", { name: "Plan", viewport: "4049C81" });
    expect(sendCommandMock).toHaveBeenLastCalledWith("layerStateRestore", { name: "Plan", viewport: "4049C81" });
    expect(approval("layer_state_restore")).toBe(true);
  });
});

describe("C90 label style texts, profile view axes and band label texts", () => {
  beforeEach(() => sendCommandMock.mockReset());
  const station = "<[Station Value(Um|FS|P0|RN|AP|Sn|TP|B3|EN|W0|OLB)]>/<[Station Value(Um|FS|P0|RN|AP|Sn|TP|B3|EN|W0|ORB)]>";

  it("lists label style collections and styles and reads a style's texts without approval", async () => {
    sendCommandMock.mockResolvedValue({});
    await run("label_styles_list", {});
    expect(sendCommandMock).toHaveBeenLastCalledWith("labelStylesList", {});
    await run("label_styles_list", { collection: "AlignmentLabelStyles.MajorStationLabelStyles", contains: "Company" });
    expect(sendCommandMock).toHaveBeenLastCalledWith("labelStylesList", { collection: "AlignmentLabelStyles.MajorStationLabelStyles", contains: "Company" });
    await run("label_style_text_get", { collection: "StructureLabelStyles.LabelStyles", style: "Manhole with station" });
    expect(sendCommandMock).toHaveBeenLastCalledWith("labelStyleTextGet", { collection: "StructureLabelStyles.LabelStyles", style: "Manhole with station" });
    expect(approval("label_styles_list")).toBe(false);
    expect(approval("label_style_text_get")).toBe(false);
    expect(drawing.label_style_text_get.inputSchema.safeParse({ action: "label_style_text_get", collection: "X" }).success).toBe(false);
  });

  it("sets a whole text or finds and replaces over a collection behind approval", async () => {
    sendCommandMock.mockResolvedValue({});
    const whole = { collection: "AlignmentLabelStyles.MajorStationLabelStyles", style: "Station 0/000", component: "Station", contents: station };
    await run("label_style_text_set", whole);
    expect(sendCommandMock).toHaveBeenLastCalledWith("labelStyleTextSet", whole);
    survivesExposure({ action: "label_style_text_set", ...whole });
    const swap = { collection: "PipeLabelStyles.PlanProfileLabelStyles", find: "<\[Station Value\([^\]]*\)\]>", replaceWith: station, regex: true, dryRun: true };
    await run("label_style_text_set", swap);
    expect(sendCommandMock).toHaveBeenLastCalledWith("labelStyleTextSet", swap);
    survivesExposure({ action: "label_style_text_set", ...swap });
    expect(approval("label_style_text_set")).toBe(true);
    const s = drawing.label_style_text_set.inputSchema;
    const base = { action: "label_style_text_set", collection: "StructureLabelStyles.LabelStyles", style: "S" };
    expect(s.safeParse(base).success).toBe(false);
    expect(s.safeParse({ ...base, contents: "x", find: "a", replaceWith: "b" }).success).toBe(false);
    expect(s.safeParse({ ...base, find: "a" }).success).toBe(false);
    expect(s.safeParse({ ...base, replaceWith: "b" }).success).toBe(false);
    expect(s.safeParse({ ...base, contents: "x", regex: true }).success).toBe(false);
    expect(s.safeParse({ action: "label_style_text_set", collection: "StructureLabelStyles.LabelStyles", contents: "x" }).success).toBe(false);
    expect(s.safeParse({ ...base, find: "", replaceWith: "b" }).success).toBe(false);
    expect(s.safeParse({ ...base, find: "a", replaceWith: "" }).success).toBe(true);
    expect(s.safeParse({ ...base, contents: "" }).success).toBe(true);
  });

  it("copies a label style behind approval", async () => {
    sendCommandMock.mockResolvedValue({});
    const args = { collection: "StructureLabelStyles.LabelStyles", style: "Manhole name", newName: "Manhole Hv-Vv" };
    await run("label_style_copy", args);
    expect(sendCommandMock).toHaveBeenLastCalledWith("labelStyleCopy", args);
    survivesExposure({ action: "label_style_copy", ...args });
    expect(approval("label_style_copy")).toBe(true);
    expect(drawing.label_style_copy.inputSchema.safeParse({ action: "label_style_copy", collection: "C", style: "S" }).success).toBe(false);
    // C93b: "/" and the other characters AutoCAD symbol names forbid are refused before Civil 3D sees them.
    for (const bad of ["Manhole Hv/Vv", "A<B", "A\\B", "A:B", "A|B", "A=B", "A`B"])
      expect(drawing.label_style_copy.inputSchema.safeParse({ action: "label_style_copy", collection: "C", style: "S", newName: bad }).success).toBe(false);
    expect(drawing.label_style_copy.inputSchema.safeParse({ action: "label_style_copy", collection: "C", style: "S", newName: "Manhole Hv-Vv (Company)" }).success).toBe(true);
  });

  it("reads profile view axes without approval and sets a tick label text or interval behind approval", async () => {
    sendCommandMock.mockResolvedValue({});
    await run("profile_view_axis_get", { style: "1:4 vertical exaggeration", axis: "Bottom" });
    expect(sendCommandMock).toHaveBeenLastCalledWith("profileViewAxisGet", { style: "1:4 vertical exaggeration", axis: "Bottom" });
    const args = { style: "1:4 vertical exaggeration", axis: "Bottom", tick: "Major", labelText: station, interval: 20 };
    await run("profile_view_axis_set", args);
    expect(sendCommandMock).toHaveBeenLastCalledWith("profileViewAxisSet", args);
    survivesExposure({ action: "profile_view_axis_set", ...args });
    expect(approval("profile_view_axis_get")).toBe(false);
    expect(approval("profile_view_axis_set")).toBe(true);
    const s = drawing.profile_view_axis_set.inputSchema;
    const base = { action: "profile_view_axis_set", style: "S", axis: "Top", tick: "Minor" };
    expect(s.safeParse(base).success).toBe(false);
    expect(s.safeParse({ ...base, interval: 0 }).success).toBe(false);
    expect(s.safeParse({ ...base, axis: "Middle", interval: 10 }).success).toBe(false);
    expect(s.safeParse({ action: "profile_view_axis_set", style: "S", axis: "Top", labelText: "x" }).success).toBe(false);
    expect(s.safeParse({ ...base, labelText: "" }).success).toBe(true);
  });

  it("finds and replaces band label texts behind approval", async () => {
    sendCommandMock.mockResolvedValue({});
    const args = { bandType: "ProfileData", group: "majorStations", component: "Station", find: "<[Station Value(Uft|FS|P0|RN|AP|Sn|TP|B2|EN|W0|OF)]>", replaceWith: station, dryRun: true };
    await run("band_style_text_set", args);
    expect(sendCommandMock).toHaveBeenLastCalledWith("bandStyleTextSet", args);
    survivesExposure({ action: "band_style_text_set", ...args, style: "Stationing", regex: false });
    expect(approval("band_style_text_set")).toBe(true);
    const s = drawing.band_style_text_set.inputSchema;
    expect(s.safeParse({ action: "band_style_text_set", bandType: "ProfileData", replaceWith: "x" }).success).toBe(false);
    expect(s.safeParse({ action: "band_style_text_set", bandType: "ProfileData", find: "a" }).success).toBe(false);
    expect(s.safeParse({ action: "band_style_text_set", bandType: "Profile", find: "a", replaceWith: "b" }).success).toBe(false);
    expect(s.safeParse({ action: "band_style_text_set", bandType: "PipeNetwork", find: "a", replaceWith: "" }).success).toBe(true);
  });
});

describe("C91 Civil feature and command settings", () => {
  beforeEach(() => sendCommandMock.mockReset());

  it("lists and reads settings types without approval", async () => {
    sendCommandMock.mockResolvedValue({});
    await run("civil_settings_list", {});
    expect(sendCommandMock).toHaveBeenLastCalledWith("civilSettingsList", {});
    await run("civil_settings_list", { contains: "ProfileView", includeAmbient: false });
    expect(sendCommandMock).toHaveBeenLastCalledWith("civilSettingsList", { contains: "ProfileView", includeAmbient: false });
    const get = { settingsType: "SettingsCmdCreateProfileView", group: "Styles", includeAmbient: true };
    await run("civil_settings_get", get);
    expect(sendCommandMock).toHaveBeenLastCalledWith("civilSettingsGet", get);
    survivesExposure({ action: "civil_settings_get", ...get });
    expect(approval("civil_settings_list")).toBe(false);
    expect(approval("civil_settings_get")).toBe(false);
    expect(drawing.civil_settings_get.inputSchema.safeParse({ action: "civil_settings_get" }).success).toBe(false);
  });

  it("sets styles by name or by collection and name templates as strings behind approval", async () => {
    sendCommandMock.mockResolvedValue({});
    const args = {
      settingsType: "SettingsCmdCreateProfileView",
      values: { "Styles.ProfileViewStyleId": "1:4 vertical exaggeration", "Styles.ProfileViewBandStyleId": { collection: "ProfileViewBandSetStyles", name: "Gravity" } },
    };
    await run("civil_settings_set", args);
    expect(sendCommandMock).toHaveBeenLastCalledWith("civilSettingsSet", args);
    survivesExposure({ action: "civil_settings_set", ...args });
    const names = { settingsType: "SettingsCmdCreateNetwork", values: { "NameFormat.Structure": "S-NB-<[Next Counter(CP)]>", "Styles.PartsListId": "Gravity network", DefaultLayoutCommand: "PipesAndStrucutres" } };
    await run("civil_settings_set", names);
    expect(sendCommandMock).toHaveBeenLastCalledWith("civilSettingsSet", names);
    survivesExposure({ action: "civil_settings_set", ...names });
    expect(approval("civil_settings_set")).toBe(true);
    const s = drawing.civil_settings_set.inputSchema;
    expect(s.safeParse({ action: "civil_settings_set", settingsType: "X", values: {} }).success).toBe(false);
    expect(s.safeParse({ action: "civil_settings_set", settingsType: "X", values: { "Styles.Pipe.Id": "a" } }).success).toBe(false);
    expect(s.safeParse({ action: "civil_settings_set", settingsType: "X", values: { "Styles.PipeStyleId": 3 } }).success).toBe(false);
    expect(s.safeParse({ action: "civil_settings_set", settingsType: "X", values: { "Styles.PipeStyleId": { name: "a" } } }).success).toBe(false);
    expect(s.safeParse({ action: "civil_settings_set", settingsType: "X", values: { "Styles.PipeStyleId": { collection: "PipeStyles", name: "a", extra: 1 } } }).success).toBe(false);
    expect(s.safeParse({ action: "civil_settings_set", values: { "Styles.PipeStyleId": "a" } }).success).toBe(false);
  });

  it("still passes ambient and object layer values through the shared values field", () => {
    survivesExposure({ action: "settings_ambient_set", values: { "Station.Precision": "0" } });
    survivesExposure({ action: "object_layers_set", values: { Pipe: { layer: "C-PIPE" } } });
  });
});

describe("C92 Hv/Vv in structures", () => {
  beforeEach(() => sendCommandMock.mockReset());

  it("computes Hv/Vv and writes them to the structure descriptions behind approval", async () => {
    sendCommandMock.mockResolvedValue({});
    await run("structure_bend_angles", { networkName: "Network S" });
    expect(sendCommandMock).toHaveBeenLastCalledWith("structureBendAngles", { networkName: "Network S" });
    const args = { networkName: "Network S", structureNames: ["S-NB-1", "S-NB-2"], write: true, dryRun: true, decimals: 0, minHvDeg: 1, minVvDeg: 1, keepExisting: true };
    await run("structure_bend_angles", args);
    expect(sendCommandMock).toHaveBeenLastCalledWith("structureBendAngles", args);
    survivesExposure({ action: "structure_bend_angles", ...args });
    expect(approval("structure_bend_angles")).toBe(true);
    const s = drawing.structure_bend_angles.inputSchema;
    expect(s.safeParse({ action: "structure_bend_angles" }).success).toBe(false);
    expect(s.safeParse({ action: "structure_bend_angles", networkName: "N", structureNames: [] }).success).toBe(false);
    expect(s.safeParse({ action: "structure_bend_angles", networkName: "N", decimals: 4 }).success).toBe(false);
    expect(s.safeParse({ action: "structure_bend_angles", networkName: "N", decimals: 0.5 }).success).toBe(false);
    expect(s.safeParse({ action: "structure_bend_angles", networkName: "N", minHvDeg: -1 }).success).toBe(false);
    expect(s.safeParse({ action: "structure_bend_angles", networkName: "N", minVvDeg: 181 }).success).toBe(false);
  });
});

describe("C93 open drawings", () => {
  beforeEach(() => sendCommandMock.mockReset());
  const template = "C:/templates/company_template.dwt";

  it("lists the open drawings without approval", async () => {
    sendCommandMock.mockResolvedValue({ documents: [] });
    await run("documents_list", {});
    expect(sendCommandMock).toHaveBeenCalledWith("documentsList", {});
    expect(approval("documents_list")).toBe(false);
    expect(drawing.documents_list.requiresActiveDrawing).toBe(false);
  });

  it("opens a drawing, active and for editing by default, without approval", async () => {
    sendCommandMock.mockResolvedValue({});
    await run("document_open", { path: template });
    expect(sendCommandMock).toHaveBeenLastCalledWith("documentOpen", { path: template, readOnly: false, activate: true });
    const args = { path: "C:/work/project/T6_test.dwg", readOnly: true, activate: false };
    await run("document_open", args);
    expect(sendCommandMock).toHaveBeenLastCalledWith("documentOpen", args);
    survivesExposure({ action: "document_open", ...args });
    expect(approval("document_open")).toBe(false);
    expect(drawing.document_open.requiresActiveDrawing).toBe(false);
    const s = drawing.document_open.inputSchema;
    expect(s.safeParse({ action: "document_open" }).success).toBe(false);
    expect(s.safeParse({ action: "document_open", path: "C:/work/project/map.dxf" }).success).toBe(false);
    expect(s.safeParse({ action: "document_open", path: "C:/work/project/TEMPLATE.DWT" }).success).toBe(true);
  });

  it("activates a drawing by name or by path without approval", async () => {
    sendCommandMock.mockResolvedValue({});
    await run("document_activate", { name: "company_template.dwt" });
    expect(sendCommandMock).toHaveBeenLastCalledWith("documentActivate", { name: "company_template.dwt" });
    await run("document_activate", { path: template });
    expect(sendCommandMock).toHaveBeenLastCalledWith("documentActivate", { path: template });
    survivesExposure({ action: "document_activate", name: "Drawing2.dwg" });
    expect(approval("document_activate")).toBe(false);
    const s = drawing.document_activate.inputSchema;
    expect(s.safeParse({ action: "document_activate" }).success).toBe(false);
    expect(s.safeParse({ action: "document_activate", name: "A", path: template }).success).toBe(false);
    expect(s.safeParse({ action: "document_activate", name: "" }).success).toBe(false);
  });

  it("closes a drawing behind approval, neither saving nor discarding unless asked", async () => {
    sendCommandMock.mockResolvedValue({ closing: true });
    await run("document_close", { name: "Drawing2.dwg" });
    expect(sendCommandMock).toHaveBeenLastCalledWith("documentClose", { name: "Drawing2.dwg", save: false, discard: false });
    await run("document_close", { path: template, save: true });
    expect(sendCommandMock).toHaveBeenLastCalledWith("documentClose", { path: template, save: true, discard: false });
    const args = { name: "T6_test", discard: true };
    await run("document_close", args);
    expect(sendCommandMock).toHaveBeenLastCalledWith("documentClose", { ...args, save: false });
    survivesExposure({ action: "document_close", ...args, save: false });
    expect(approval("document_close")).toBe(true);
    expect(drawing.document_close.requiresActiveDrawing).toBe(false);
    const s = drawing.document_close.inputSchema;
    expect(s.safeParse({ action: "document_close", save: true }).success).toBe(false);
    expect(s.safeParse({ action: "document_close", name: "A", save: true, discard: true }).success).toBe(false);
    expect(s.safeParse({ action: "document_close", name: "A", path: template }).success).toBe(false);
  });

  it("still inserts a block at a height through the shared z field", () => {
    survivesExposure({ action: "block_insert", blockName: "North arrow", x: 1, y: 2, z: 3 });
  });

  it("drawing info shows which drawing it ran in; a drawing never saved has no file path (C93c)", async () => {
    const answer = { fileName: "Drawing2.dwg", filePath: null, templatePath: template, drawingName: "Drawing2.dwg", activeDocument: "Drawing2.dwg", contextDocument: "Drawing2.dwg" };
    sendCommandMock.mockResolvedValue(answer);
    const result = await run("info", {});
    expect(sendCommandMock).toHaveBeenLastCalledWith("getDrawingInfo", {});
    const parsed = drawing.info.responseSchema!.parse(result) as Record<string, unknown>;
    expect(parsed).toMatchObject(answer);
    expect(drawing.info.responseSchema!.parse({ fileName: "company_template.dwt", filePath: template }).filePath).toBe(template);
  });

  it("still creates a drawing from a template behind approval", async () => {
    sendCommandMock.mockResolvedValue({ drawingName: "Drawing2.dwg", active: true });
    await run("new", { templatePath: template });
    expect(sendCommandMock).toHaveBeenLastCalledWith("newDrawing", { templatePath: template });
    expect(approval("new")).toBe(true);
  });
});

describe("C93b plot transparency and style names", () => {
  beforeEach(() => sendCommandMock.mockReset());

  it("turns plot transparency on in a page setup behind approval", async () => {
    sendCommandMock.mockResolvedValue({ pageSetup: { plotTransparency: true } });
    await run("page_setup_set", { name: "A1 monochrome 1:1", plotTransparency: true });
    expect(sendCommandMock).toHaveBeenLastCalledWith("setPageSetup", { name: "A1 monochrome 1:1", plotTransparency: true });
    survivesExposure({ action: "page_setup_set", name: "A1 monochrome 1:1", plotTransparency: false });
    expect(approval("page_setup_set")).toBe(true);
  });

  it("refuses a label style copy named with a slash before Civil 3D sees it", () => {
    const parsed = drawing.label_style_copy.inputSchema.safeParse({ action: "label_style_copy", collection: "StructureLabelStyles.LabelStyles", style: "Manhole name", newName: "Manhole Hv/Vv" });
    expect(parsed.success).toBe(false);
  });
});

describe("C95 drawing references", () => {
  beforeEach(() => sendCommandMock.mockReset());
  const baseMap = "C:/work/project/Base data/SWEREF_99_1330 base map 241106 (transformed).dwg";

  it("lists the drawing references without approval", async () => {
    sendCommandMock.mockResolvedValue({ xrefs: [] });
    await run("xrefs_list", {});
    expect(sendCommandMock).toHaveBeenLastCalledWith("xrefsList", {});
    expect(approval("xrefs_list")).toBe(false);
  });

  it("attaches a drawing as an overlay on layer XREF with a relative path by default, behind approval", async () => {
    sendCommandMock.mockResolvedValue({ status: "loaded" });
    await run("xref_attach", { path: baseMap });
    expect(sendCommandMock).toHaveBeenLastCalledWith("xrefAttach", { path: baseMap, overlay: true, layer: "XREF", relativePath: true });
    const args = { path: "C:/work/project/Base data/existing_utilities.dwg", name: "existing_utilities", overlay: false, x: 0, y: 0, z: 0, scale: 1, rotationDegrees: 0, layer: "XREF-UTILITIES", relativePath: false };
    await run("xref_attach", args);
    expect(sendCommandMock).toHaveBeenLastCalledWith("xrefAttach", args);
    survivesExposure({ action: "xref_attach", ...args });
    expect(approval("xref_attach")).toBe(true);
  });

  it("validates the file, the names and the scale", () => {
    const s = drawing.xref_attach.inputSchema;
    expect(s.safeParse({ action: "xref_attach" }).success).toBe(false);
    expect(s.safeParse({ action: "xref_attach", path: "C:/work/project/map.dxf" }).success).toBe(false);
    expect(s.safeParse({ action: "xref_attach", path: baseMap, name: "base/map" }).success).toBe(false);
    expect(s.safeParse({ action: "xref_attach", path: baseMap, layer: "X|REF" }).success).toBe(false);
    expect(s.safeParse({ action: "xref_attach", path: baseMap, scale: 0 }).success).toBe(false);
    expect(s.safeParse({ action: "xref_attach", path: baseMap, name: "SWEREF_99_1330 base map 241106 (transformed)" }).success).toBe(true);
  });
});

describe("C97 detach, unload and reload drawing references", () => {
  beforeEach(() => sendCommandMock.mockReset());

  it("detaches a reference by name behind approval", async () => {
    sendCommandMock.mockResolvedValue({ detached: true });
    await run("xref_detach", { name: "Existing_Utilities" });
    expect(sendCommandMock).toHaveBeenLastCalledWith("xrefDetach", { name: "Existing_Utilities" });
    survivesExposure({ action: "xref_detach", name: "Existing_Utilities" });
    expect(approval("xref_detach")).toBe(true);
    expect(drawing.xref_detach.inputSchema.safeParse({ action: "xref_detach" }).success).toBe(false);
    expect(drawing.xref_detach.inputSchema.safeParse({ action: "xref_detach", name: "" }).success).toBe(false);
  });

  it("unloads and reloads a reference behind approval", async () => {
    sendCommandMock.mockResolvedValue({});
    await run("xref_unload", { name: "BaseMap" });
    expect(sendCommandMock).toHaveBeenLastCalledWith("xrefUnload", { name: "BaseMap" });
    await run("xref_reload", { name: "BaseMap" });
    expect(sendCommandMock).toHaveBeenLastCalledWith("xrefReload", { name: "BaseMap" });
    expect(approval("xref_unload")).toBe(true);
    expect(approval("xref_reload")).toBe(true);
  });
});

describe("C94 drawing transformation", () => {
  beforeEach(() => sendCommandMock.mockReset());
  const sourcePath = "C:/work/project/source/base_map_1500.dwg";
  const outputPath = "C:/work/project/base_map_1330.dwg";
  const helmert = { a: 0.99976, b: -0.02618, tx: 163250.12, ty: -2735.48 };

  it("writes a transformed copy with a Helmert behind approval, never overwriting unless asked", async () => {
    sendCommandMock.mockResolvedValue({ written: true });
    await run("drawing_transform", { sourcePath, outputPath, transform: helmert });
    expect(sendCommandMock).toHaveBeenLastCalledWith("drawingTransform", { sourcePath, outputPath, transform: helmert, overwrite: false });
    const args = { sourcePath, outputPath, transform: helmert, z: { scale: 1, offset: -0.12 }, overwrite: true, dryRun: true, includeCivilObjects: false };
    await run("drawing_transform", args);
    expect(sendCommandMock).toHaveBeenLastCalledWith("drawingTransform", args);
    survivesExposure({ action: "drawing_transform", ...args });
    expect(approval("drawing_transform")).toBe(true);
  });

  it("takes a full affine instead", async () => {
    sendCommandMock.mockResolvedValue({});
    const affine = { m11: 0.99976, m12: 0.02618, m13: 163250.12, m21: -0.02618, m22: 0.99976, m23: -2735.48 };
    await run("drawing_transform", { sourcePath, outputPath, transform: affine });
    expect(sendCommandMock).toHaveBeenLastCalledWith("drawingTransform", { sourcePath, outputPath, transform: affine, overwrite: false });
    survivesExposure({ action: "drawing_transform", sourcePath, outputPath, transform: affine });
  });

  it("validates paths, transform and heights", () => {
    const s = drawing.drawing_transform.inputSchema;
    const base = { action: "drawing_transform", sourcePath, outputPath, transform: helmert };
    expect(s.safeParse(base).success).toBe(true);
    expect(s.safeParse({ ...base, outputPath: sourcePath.replace(/\//g, "\\").toUpperCase() }).success).toBe(false);
    expect(s.safeParse({ ...base, outputPath: "C:/work/project/map.dxf" }).success).toBe(false);
    expect(s.safeParse({ ...base, sourcePath: "C:/work/project/template.dwt" }).success).toBe(false);
    expect(s.safeParse({ ...base, transform: { a: 1, b: 0, tx: 0 } }).success).toBe(false);
    expect(s.safeParse({ ...base, transform: { ...helmert, m11: 1 } }).success).toBe(false);
    expect(s.safeParse({ ...base, transform: { m11: 1, m12: 0, m13: 0, m21: 0, m22: 1 } }).success).toBe(false);
    expect(s.safeParse({ ...base, z: { scale: 0 } }).success).toBe(false);
    expect(s.safeParse({ ...base, z: { offset: 1, shift: 2 } }).success).toBe(false);
    expect(s.safeParse({ ...base, z: 5 }).success).toBe(false);
    const { transform: _transform, ...noTransform } = base;
    expect(s.safeParse(noTransform).success).toBe(false);
  });

  it("can run as a job for a large base map", () => {
    const start = JOB_DOMAIN_DEFINITION.actions.start.inputSchema;
    expect(start.safeParse({ action: "start", operation: "drawing_transform", parameters: { sourcePath, outputPath, transform: helmert } }).success).toBe(true);
  });
});

describe("C98b label sets and label style general properties", () => {
  beforeEach(() => sendCommandMock.mockReset());

  it("lists label sets and reads a label style's general properties without approval", async () => {
    sendCommandMock.mockResolvedValue({});
    await run("label_sets_list", {});
    expect(sendCommandMock).toHaveBeenLastCalledWith("labelSetsList", {});
    await run("label_sets_list", { kind: "Alignment", name: "No radius-TP" });
    expect(sendCommandMock).toHaveBeenLastCalledWith("labelSetsList", { kind: "Alignment", name: "No radius-TP" });
    survivesExposure({ action: "label_sets_list", kind: "Profile", contains: "VA" });
    const get = { collection: "AlignmentLabelStyles.MajorStationLabelStyles", style: "Station 0/000" };
    await run("label_style_properties_get", get);
    expect(sendCommandMock).toHaveBeenLastCalledWith("labelStylePropertiesGet", get);
    expect(approval("label_sets_list")).toBe(false);
    expect(approval("label_style_properties_get")).toBe(false);
    expect(drawing.label_sets_list.inputSchema.safeParse({ action: "label_sets_list", kind: "Corridor" }).success).toBe(false);
    expect(drawing.label_style_properties_get.inputSchema.safeParse({ action: "label_style_properties_get", collection: "X" }).success).toBe(false);
  });

  it("sets general properties as Group.Property with booleans, numbers and names behind approval", async () => {
    sendCommandMock.mockResolvedValue({});
    const args = {
      collection: "AlignmentLabelStyles.MajorStationLabelStyles", style: "Station 0/000",
      values: { "PlanReadability.PlanReadable": true, "PlanReadability.PlanReadableBias": 90, "Behavior.OrientationReference": "View", "Label.Layer": "C-ANNO" },
      dryRun: true,
    };
    await run("label_style_properties_set", args);
    expect(sendCommandMock).toHaveBeenLastCalledWith("labelStylePropertiesSet", args);
    survivesExposure({ action: "label_style_properties_set", ...args });
    expect(approval("label_style_properties_set")).toBe(true);
    const s = drawing.label_style_properties_set.inputSchema;
    const base = { action: "label_style_properties_set", collection: "C", style: "S" };
    expect(s.safeParse({ ...base, values: { "PlanReadability.FlipAnchorsWithText": "false" } }).success).toBe(true);
    expect(s.safeParse(base).success).toBe(false);
    expect(s.safeParse({ ...base, values: {} }).success).toBe(false);
    expect(s.safeParse({ ...base, values: { PlanReadable: true } }).success).toBe(false);
    expect(s.safeParse({ ...base, values: { "Label.Layer.Name": "0" } }).success).toBe(false);
    expect(s.safeParse({ ...base, values: { "Label.Layer": { name: "0" } } }).success).toBe(false);
    expect(s.safeParse({ action: "label_style_properties_set", collection: "C", values: { "Label.Visibility": true } }).success).toBe(false);
  });

  it("still passes the earlier string values through the widened shared values field", () => {
    survivesExposure({ action: "civil_settings_set", settingsType: "SettingsPipeNetwork", values: { "Styles.PipeStyleId": "VA" } });
    survivesExposure({ action: "block_attributes_set", blockName: "Stamp", values: { PROJNR: "123" } });
  });
});

describe("C98c label expressions", () => {
  beforeEach(() => sendCommandMock.mockReset());
  const collection = "PipeLabelStyles.PlanProfileLabelStyles";

  it("lists the expressions of a label style collection without approval", async () => {
    sendCommandMock.mockResolvedValue({ expressions: [] });
    await run("label_expressions_list", { collection });
    expect(sendCommandMock).toHaveBeenLastCalledWith("labelExpressionsList", { collection });
    survivesExposure({ action: "label_expressions_list", collection, contains: "diameter" });
    expect(approval("label_expressions_list")).toBe(false);
    expect(drawing.label_expressions_list.inputSchema.safeParse({ action: "label_expressions_list" }).success).toBe(false);
  });

  it("creates or changes an expression behind approval", async () => {
    sendCommandMock.mockResolvedValue({});
    const outer = { collection, name: "Outer diameter", expression: "({Inner Pipe Diameter}+2*{Wall Thickness})*1000", description: "Outer diameter in mm", formatResultAs: "Double", dryRun: true };
    await run("label_expression_set", outer);
    expect(sendCommandMock).toHaveBeenLastCalledWith("labelExpressionSet", outer);
    survivesExposure({ action: "label_expression_set", ...outer });
    await run("label_expression_set", { collection, name: " SDR ", formatResultAs: "Double" });
    expect(sendCommandMock).toHaveBeenLastCalledWith("labelExpressionSet", { collection, name: "SDR", formatResultAs: "Double" });
    expect(approval("label_expression_set")).toBe(true);
    const s = drawing.label_expression_set.inputSchema;
    const base = { action: "label_expression_set", collection, name: "SDR" };
    expect(s.safeParse(base).success).toBe(false);
    expect(s.safeParse({ ...base, expression: "  " }).success).toBe(false);
    expect(s.safeParse({ ...base, formatResultAs: "Millimetre" }).success).toBe(false);
    for (const bad of ["SDR (PE)", "A{B}", "A|B", "A<B", "A\"B", "[x]", ""])
      expect(s.safeParse({ ...base, name: bad, expression: "1" }).success).toBe(false);
    expect(s.safeParse({ ...base, description: "" }).success).toBe(true);
  });

  it("deletes an expression behind approval, force only on request", async () => {
    sendCommandMock.mockResolvedValue({});
    await run("label_expression_delete", { collection, name: "SDR" });
    expect(sendCommandMock).toHaveBeenLastCalledWith("labelExpressionDelete", { collection, name: "SDR" });
    const args = { collection, name: "SDR", force: true, dryRun: true };
    await run("label_expression_delete", args);
    expect(sendCommandMock).toHaveBeenLastCalledWith("labelExpressionDelete", args);
    survivesExposure({ action: "label_expression_delete", ...args });
    expect(approval("label_expression_delete")).toBe(true);
    expect(drawing.label_expression_delete.inputSchema.safeParse({ action: "label_expression_delete", collection }).success).toBe(false);
  });
});

describe("C98e display components of styles", () => {
  beforeEach(() => sendCommandMock.mockReset());

  it("reads a style's display components and markers without approval, profile styles by default", async () => {
    sendCommandMock.mockResolvedValue({ views: {} });
    await run("style_display_get", { style: "Design - utilities" });
    expect(sendCommandMock).toHaveBeenLastCalledWith("styleDisplayGet", { style: "Design - utilities" });
    await run("style_display_get", { collection: "AlignmentStyles", style: "Utility line" });
    expect(sendCommandMock).toHaveBeenLastCalledWith("styleDisplayGet", { collection: "AlignmentStyles", style: "Utility line" });
    survivesExposure({ action: "style_display_get", collection: "ProfileStyles", style: "Design - utilities" });
    expect(approval("style_display_get")).toBe(false);
    expect(drawing.style_display_get.inputSchema.safeParse({ action: "style_display_get" }).success).toBe(false);
  });

  it("changes display components and marker styles behind approval", async () => {
    sendCommandMock.mockResolvedValue({});
    const args = {
      style: "Design - utilities",
      display: {
        Profile: { Line: { visible: true, layer: "0", color: "ByLayer", linetype: "ByLayer", lineweight: 35, plotStyle: "ByLayer" }, Curve: { visible: true }, LineExtension: { visible: false } },
        Model: { Chain3D: { color: 3, linetypeScale: 1 } },
      },
      markers: { VIntersectionPoint: "PVI marker" },
      dryRun: true,
    };
    await run("style_display_set", args);
    expect(sendCommandMock).toHaveBeenLastCalledWith("styleDisplaySet", args);
    survivesExposure({ action: "style_display_set", collection: "ProfileStyles", ...args });
    expect(approval("style_display_set")).toBe(true);
    const s = drawing.style_display_set.inputSchema;
    const base = { action: "style_display_set", style: "S" };
    expect(s.safeParse(base).success).toBe(false);
    expect(s.safeParse({ ...base, display: {} }).success).toBe(false);
    expect(s.safeParse({ ...base, display: { Profile: { Line: {} } } }).success).toBe(false);
    expect(s.safeParse({ ...base, display: { Profile: { Line: { visble: true } } } }).success).toBe(false);
    expect(s.safeParse({ ...base, display: { Profile: { Line: { lineweight: 33 } } } }).success).toBe(false);
    expect(s.safeParse({ ...base, display: { Profile: { Line: { lineweight: "Default" } } } }).success).toBe(true);
    expect(s.safeParse({ ...base, display: { Profile: { Line: { color: 300 } } } }).success).toBe(false);
    expect(s.safeParse({ ...base, display: { Profile: { Line: { linetypeScale: 0 } } } }).success).toBe(false);
    expect(s.safeParse({ ...base, markers: { BeginPoint: "" } }).success).toBe(false);
    expect(s.safeParse({ ...base, markers: { BeginPoint: "Cross" } }).success).toBe(true);
  });
});
