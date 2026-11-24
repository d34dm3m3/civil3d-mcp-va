import { beforeEach, describe, expect, it, vi } from "vitest";

const { sendCommandMock } = vi.hoisted(() => ({
  sendCommandMock: vi.fn(),
}));

vi.mock("../src/utils/ConnectionManager.js", () => ({
  withApplicationConnection: async <T>(
    operation: (client: { sendCommand: typeof sendCommandMock }) => Promise<T>,
  ) => await operation({
    sendCommand: sendCommandMock,
  }),
}));

import { CORRIDOR_DOMAIN_DEFINITION } from "../src/tools/domains/corridorDomain.js";

const corridor = CORRIDOR_DOMAIN_DEFINITION.actions;

describe("corridor get_feature_lines with geometry (C60)", () => {
  beforeEach(() => {
    sendCommandMock.mockReset();
  });

  it("keeps the old call shape by default", async () => {
    sendCommandMock.mockResolvedValue({ corridorName: "Schakt_HK", featureLines: [{ name: "Trench_Bottom" }] });
    await corridor.get_feature_lines.execute(corridor.get_feature_lines.inputSchema.parse({ action: "get_feature_lines", name: "Schakt_HK" }));
    expect(sendCommandMock).toHaveBeenCalledWith("getCorridorFeatureLines", {
      name: "Schakt_HK", includeGeometry: false, codes: null, decimals: 3,
    });
  });

  it("passes codes and keeps the 3D lines in the response", async () => {
    const line = { baseline: "BL", code: "Trench_Daylight", side: "left", medianOffset: -2.6, startStation: 0, endStation: 10,
      points: [[1, 2, 3], [4, 5, 6]] };
    sendCommandMock.mockResolvedValue({ corridorName: "Schakt_HK", featureLines: [{ name: "Trench_Daylight" }], lines: [line] });
    const args = corridor.get_feature_lines.inputSchema.parse({
      action: "get_feature_lines", name: "Schakt_HK", includeGeometry: true, codes: ["Trench_Daylight"],
    });
    const result = corridor.get_feature_lines.responseSchema.parse(await corridor.get_feature_lines.execute(args));
    expect(sendCommandMock).toHaveBeenCalledWith("getCorridorFeatureLines", {
      name: "Schakt_HK", includeGeometry: true, codes: ["Trench_Daylight"], decimals: 3,
    });
    expect(result.lines?.[0].points[1]).toEqual([4, 5, 6]);
  });
});
