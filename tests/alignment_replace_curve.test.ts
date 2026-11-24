import { readFileSync, readdirSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { z } from "zod";

const { sendCommandMock } = vi.hoisted(() => ({ sendCommandMock: vi.fn() }));

vi.mock("../src/utils/ConnectionManager.js", () => ({
  withApplicationConnection: async <T>(
    operation: (client: { sendCommand: typeof sendCommandMock }) => Promise<T>,
  ) => await operation({ sendCommand: sendCommandMock }),
}));

import { isApprovalRequired } from "../src/tools/approvalPolicy.js";
import { ALIGNMENT_DOMAIN_DEFINITION } from "../src/tools/domains/alignmentDomain.js";
import { GENERATED_TOOL_CATALOG_ENTRIES } from "../src/tools/toolManifest.js";

const alignment = ALIGNMENT_DOMAIN_DEFINITION.actions;
const shape = z.object(ALIGNMENT_DOMAIN_DEFINITION.exposures.find((e) => e.toolName === "civil3d_alignment")!.inputShape);

describe("C48 replace a curve with lines, entity chain, raw/nominal stations", () => {
  beforeEach(() => sendCommandMock.mockReset());

  it("replaces a curve by index with one straight line (no points)", async () => {
    sendCommandMock.mockResolvedValue({});
    const args = { action: "replace_curve_with_lines", name: "Trench_CL", entityIndex: 33 };
    await alignment.replace_curve_with_lines.execute(alignment.replace_curve_with_lines.inputSchema.parse(args));
    expect(sendCommandMock).toHaveBeenCalledWith("alignmentReplaceCurveWithLines", {
      alignmentName: "Trench_CL", entityIndex: 33, station: undefined, points: [],
    });
    expect(Object.keys(shape.parse(args)).sort()).toEqual(Object.keys(args).sort());
  });

  it("replaces the curve at a station with lines through corner points", async () => {
    sendCommandMock.mockResolvedValue({});
    const args = { action: "replace_curve_with_lines", name: "Trench_CL", station: 335.2, points: [{ x: 10, y: 20 }] };
    await alignment.replace_curve_with_lines.execute(alignment.replace_curve_with_lines.inputSchema.parse(args));
    expect(sendCommandMock).toHaveBeenCalledWith("alignmentReplaceCurveWithLines", expect.objectContaining({
      station: 335.2, entityIndex: undefined, points: [{ x: 10, y: 20 }],
    }));
    expect(Object.keys(shape.parse(args)).sort()).toEqual(Object.keys(args).sort());
  });

  it("needs exactly one of entityIndex and station, and approval", () => {
    const s = alignment.replace_curve_with_lines.inputSchema;
    const base = { action: "replace_curve_with_lines", name: "A" };
    expect(s.safeParse(base).success).toBe(false);
    expect(s.safeParse({ ...base, entityIndex: 3, station: 10 }).success).toBe(false);
    expect(s.safeParse({ ...base, entityIndex: -1 }).success).toBe(false);
    expect(s.safeParse({ ...base, entityIndex: 3, points: [{ x: 1 }] }).success).toBe(false);
    const target = { toolName: "civil3d_alignment", action: "replace_curve_with_lines", capabilities: alignment.replace_curve_with_lines.capabilities, safeForRetry: alignment.replace_curve_with_lines.safeForRetry };
    expect(isApprovalRequired(target)).toBe(true);
    const entry = GENERATED_TOOL_CATALOG_ENTRIES.find((e) => e.toolName === "civil3d_alignment");
    expect(entry!.operations).toEqual(expect.arrayContaining(["replace_curve_with_lines", "entities_get"]));
    expect(entry!.pluginMethods).toEqual(expect.arrayContaining(["alignmentReplaceCurveWithLines", "alignmentEntitiesGet"]));
  });

  it("reads the entity chain without approval", async () => {
    sendCommandMock.mockResolvedValue({ entities: [] });
    const args = { action: "entities_get", name: "Trench_CL" };
    await alignment.entities_get.execute(alignment.entities_get.inputSchema.parse(args));
    expect(sendCommandMock).toHaveBeenCalledWith("alignmentEntitiesGet", { name: "Trench_CL" });
    expect(isApprovalRequired({ toolName: "civil3d_alignment", action: "entities_get", capabilities: alignment.entities_get.capabilities, safeForRetry: alignment.entities_get.safeForRetry })).toBe(false);
  });

  it("station_to_point defaults to raw stations and passes nominal through", async () => {
    sendCommandMock.mockResolvedValue({ x: 1, y: 2, station: 120, offset: 0, units: "meters", stationMode: "nominal", rawStation: 20, stationWithEquations: "0+120.00" });
    await alignment.station_to_point.execute(alignment.station_to_point.inputSchema.parse({ action: "station_to_point", name: "A", station: 120 }));
    expect(sendCommandMock).toHaveBeenLastCalledWith("alignmentStationToPoint", { name: "A", station: 120, offset: 0, stationMode: "raw" });
    const args = { action: "station_to_point", name: "A", station: 120, stationMode: "nominal" };
    await alignment.station_to_point.execute(alignment.station_to_point.inputSchema.parse(args));
    expect(sendCommandMock).toHaveBeenLastCalledWith("alignmentStationToPoint", expect.objectContaining({ stationMode: "nominal" }));
    expect(Object.keys(shape.parse(args)).sort()).toEqual(Object.keys(args).sort());
    const parsed = alignment.station_to_point.responseSchema!.parse(await sendCommandMock.mock.results[1].value);
    expect(parsed).toMatchObject({ rawStation: 20, stationMode: "nominal", stationWithEquations: "0+120.00" });
    expect(alignment.station_to_point.inputSchema.safeParse({ ...args, stationMode: "equation" }).success).toBe(false);
  });

  it("set_station_equation sends an explicit equation type, default increasing", async () => {
    sendCommandMock.mockResolvedValue({});
    await alignment.set_station_equation.execute(alignment.set_station_equation.inputSchema.parse({ action: "set_station_equation", name: "A", rawStation: 80, nominalStation: 0 }));
    expect(sendCommandMock).toHaveBeenLastCalledWith("alignmentSetStationEquation", { alignmentName: "A", rawStation: 80, nominalStation: 0, equationType: "increasing" });
    const args = { action: "set_station_equation", name: "A", rawStation: 80, nominalStation: 500, equationType: "decreasing" };
    await alignment.set_station_equation.execute(alignment.set_station_equation.inputSchema.parse(args));
    expect(sendCommandMock).toHaveBeenLastCalledWith("alignmentSetStationEquation", expect.objectContaining({ equationType: "decreasing" }));
    expect(Object.keys(shape.parse(args)).sort()).toEqual(Object.keys(args).sort());
  });
});

// C48b: Civil 3D throws EntityNotFoundException (NoNextAlignmentEntity) for EntityAfter on the last entity and
// EntityBefore on the first, so the links and the chain ends may only be read inside a try in AlignmentChain.cs.
describe("C48b alignment entity links are read guarded", () => {
  it("reads EntityBefore/EntityAfter/FirstEntity/LastEntity only inside a try in AlignmentChain.cs", () => {
    const pluginDirectory = fileURLToPath(new URL("../Civil3D-MCP-Plugin/", import.meta.url));
    const access = /\.(?:EntityBefore|EntityAfter|FirstEntity|LastEntity)\b/;
    const unguarded: string[] = [];
    for (const fileName of readdirSync(pluginDirectory).filter((name) => name.endsWith(".cs"))) {
      const lines = readFileSync(`${pluginDirectory}/${fileName}`, "utf8").split(/\r?\n/);
      lines.forEach((line, index) => {
        const code = line.replace(/\/\/.*$/, "");
        if (!access.test(code)) return;
        if (fileName === "AlignmentChain.cs" && /\btry\b/.test(code)) return;
        unguarded.push(`${fileName}:${index + 1}: ${line.trim()}`);
      });
    }

    expect(unguarded).toEqual([]);
  });
});

describe("C96 create an alignment exactly as given (bulges, no added curves, start station)", () => {
  beforeEach(() => sendCommandMock.mockReset());
  const points = [{ x: 147500, y: 6236100 }, { x: 147560, y: 6236140 }, { x: 147610, y: 6236150 }];

  it("forwards bulges, addCurvesBetweenTangents and startStation behind approval", async () => {
    sendCommandMock.mockResolvedValue({ entityCount: 2 });
    const args = { action: "create", name: "Main_St_CL", points, bulges: [0, -0.087], addCurvesBetweenTangents: false, startStation: 1000, layer: "C-ALIGN" };
    await alignment.create.execute(alignment.create.inputSchema.parse(args));
    expect(sendCommandMock).toHaveBeenLastCalledWith("createAlignment", expect.objectContaining({
      name: "Main_St_CL", points, bulges: [0, -0.087], addCurvesBetweenTangents: false, startStation: 1000, layer: "C-ALIGN",
    }));
    expect(Object.keys(shape.parse(args)).sort()).toEqual(Object.keys(args).sort());
    expect(isApprovalRequired({ toolName: "civil3d_alignment", action: "create", capabilities: alignment.create.capabilities, safeForRetry: alignment.create.safeForRetry })).toBe(true);
  });

  it("keeps the old call without the new fields", async () => {
    sendCommandMock.mockResolvedValue({});
    await alignment.create.execute(alignment.create.inputSchema.parse({ action: "create", name: "A", points }));
    const sent = sendCommandMock.mock.calls.at(-1)![1] as Record<string, unknown>;
    expect(sent.bulges).toBeUndefined();
    expect(sent.addCurvesBetweenTangents).toBeUndefined();
    expect(sent.startStation).toBeUndefined();
  });

  it("refuses bulges of the wrong count or of a half circle or more", () => {
    const s = alignment.create.inputSchema;
    expect(s.safeParse({ action: "create", name: "A", points, bulges: [0] }).success).toBe(false);
    expect(s.safeParse({ action: "create", name: "A", points, bulges: [0, 1] }).success).toBe(false);
    expect(s.safeParse({ action: "create", name: "A", points, bulges: [-1, 0] }).success).toBe(false);
    expect(s.safeParse({ action: "create", name: "A", points, bulges: [0.999, -0.5] }).success).toBe(true);
  });
});
