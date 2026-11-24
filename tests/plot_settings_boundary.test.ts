import { readFileSync, readdirSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";

// PlotSettingsValidator writes to the PlotSettings it is given (it loads the plot device's paper list lazily). Given a
// layout or named page setup opened for read, AutoCAD aborts with "INTERNAL ERROR: !dbobji.cpp@8703: eNotOpenForWrite"
// and Civil 3D closes (live 2026-10-09: listPageSetups as the first plot call in a new session). So the validator may
// only be handed a scratch copy: a variable created with `new PlotSettings(` in the same file. Name based, so a
// database object must never be given such a name.
const pluginDirectory = fileURLToPath(new URL("../Civil3D-MCP-Plugin/", import.meta.url));
const validatorCall = /(?:\bvalidator|PlotSettingsValidator\.Current)\.(\w+)\(\s*([A-Za-z_]\w*)?/g;
const scratchDeclaration = /\b(\w+)\s*=\s*new PlotSettings\(/g;

describe("plot settings boundary", () => {
  it("hands PlotSettingsValidator only scratch PlotSettings, never database objects", () => {
    const violations: string[] = [];
    for (const fileName of readdirSync(pluginDirectory).filter((name) => name.endsWith(".cs"))) {
      const source = readFileSync(`${pluginDirectory}/${fileName}`, "utf8");
      const scratch = new Set([...source.matchAll(scratchDeclaration)].map((m) => m[1]));
      source.split(/\r?\n/).forEach((line, index) => {
        if (line.trim().startsWith("//")) return;
        for (const [, method, argument] of line.matchAll(validatorCall)) {
          if (!argument) continue;                               // GetPlotDeviceList(), GetPlotStyleSheetList()
          if (!scratch.has(argument)) violations.push(`${fileName}:${index + 1} ${method}(${argument}, …)`);
        }
      });
    }

    expect(violations).toEqual([]);
  });

  // C74: Papers() sets the device with no media, which puts the device's default paper on the object it is given.
  // Read through the page setup being edited it turned 16 page setups into ISO A4 (live 2026-10-09).
  it("reads the paper list through a probe, not through the page setup being edited", () => {
    const source = readFileSync(`${pluginDirectory}/PageSetupCommands.cs`, "utf8");
    expect(source).not.toMatch(/Papers\(\s*scratch\b/);
    expect(source).toMatch(/Papers\(\s*probe\s*,/);
  });
});
