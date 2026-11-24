import { describe, expect, it } from "vitest";
import { DRAWING_RUNTIME_DOMAIN_DEFINITION } from "../src/tools/domains/drawingRuntimeDomain.js";

describe("drawing runtime response contracts", () => {
  it("accepts a standalone drawing with no Civil 3D project", () => {
    const responseSchema = DRAWING_RUNTIME_DOMAIN_DEFINITION.actions.info.responseSchema;

    expect(responseSchema.safeParse({
      drawingName: "Drawing1.dwg",
      projectName: null,
      units: "feet",
    }).success).toBe(true);
  });

  it("accepts drawing settings whose default styles are null (template without a corridor default)", () => {
    const responseSchema = DRAWING_RUNTIME_DOMAIN_DEFINITION.actions.settings.responseSchema;

    expect(responseSchema.safeParse({
      coordinateSystem: "SWEREF-99-13-30",
      defaultLayer: "0",
      defaultStyles: { surface: "Standard", alignment: null, profile: "Standard", corridor: null, pipeNetwork: null },
    }).success).toBe(true);
  });
});
