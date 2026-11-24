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

import { PROJECT_DOMAIN_DEFINITION } from "../src/tools/domains/projectDomain.js";

const project = PROJECT_DOMAIN_DEFINITION.actions;

describe("data shortcut project without dialogs (C61)", () => {
  beforeEach(() => {
    sendCommandMock.mockReset();
    sendCommandMock.mockResolvedValue({});
  });

  it("sets working folder and project, creating and associating", async () => {
    await project.data_shortcut_project_set.execute(project.data_shortcut_project_set.inputSchema.parse({
      action: "data_shortcut_project_set", workingFolder: "C:/work/projects", projectFolder: "P1001", create: true,
      associateDrawing: true,
    }));
    expect(sendCommandMock).toHaveBeenCalledWith("setDataShortcutProject", {
      workingFolder: "C:/work/projects", projectFolder: "P1001", create: true, description: null,
      templatePath: null, associateDrawing: true,
    });
  });

  it("publishes named items with optional type", async () => {
    await project.data_shortcut_publish.execute(project.data_shortcut_publish.inputSchema.parse({
      action: "data_shortcut_publish", items: ["Trench_datum", { type: "Alignment", name: "Trench_CL" }],
    }));
    expect(sendCommandMock).toHaveBeenCalledWith("publishDataShortcuts", {
      items: ["Trench_datum", { type: "Alignment", name: "Trench_CL" }], all: false, listOnly: false,
    });
  });
});

describe("external reference paths (C62)", () => {
  beforeEach(() => {
    sendCommandMock.mockReset();
    sendCommandMock.mockResolvedValue({});
  });

  it("sets a relative path by handle", async () => {
    await project.external_reference_set_path.execute(project.external_reference_set_path.inputSchema.parse({
      action: "external_reference_set_path", handle: "4A2B", path: "./Base data/Orthophoto/x.jpg",
    }));
    expect(sendCommandMock).toHaveBeenCalledWith("setExternalReferencePath", {
      handle: "4A2B", path: "./Base data/Orthophoto/x.jpg", makeRelative: false, requireExisting: true,
    });
  });

  it("needs exactly one of path and makeRelative", () => {
    const schema = project.external_reference_set_path.inputSchema;
    expect(() => schema.parse({ action: "external_reference_set_path", handle: "1" })).toThrow();
    expect(() => schema.parse({ action: "external_reference_set_path", handle: "1", path: "a", makeRelative: true })).toThrow();
    expect(schema.parse({ action: "external_reference_set_path", handle: "1", makeRelative: true }).makeRelative).toBe(true);
  });
});
