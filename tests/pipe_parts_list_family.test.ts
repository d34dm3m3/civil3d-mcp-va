import { beforeEach, describe, expect, it, vi } from "vitest";
import { z } from "zod";

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

import { isApprovalRequired } from "../src/tools/approvalPolicy.js";
import { PIPE_DOMAIN_DEFINITION } from "../src/tools/domains/pipeDomain.js";
import { GENERATED_TOOL_CATALOG_ENTRIES } from "../src/tools/toolManifest.js";

const pipe = PIPE_DOMAIN_DEFINITION.actions;

describe("parts list families", () => {
  beforeEach(() => sendCommandMock.mockReset());

  it("lists catalog families, optionally for one domain", async () => {
    sendCommandMock.mockResolvedValue({});
    await pipe.catalog_families.execute(pipe.catalog_families.inputSchema.parse({ action: "catalog_families", domain: "structure" }));
    expect(sendCommandMock).toHaveBeenCalledWith("listCatalogPartFamilies", { domain: "structure" });
    expect(pipe.catalog_families.inputSchema.safeParse({ action: "catalog_families", domain: "fitting" }).success).toBe(false);
  });

  it("adds a family copied from another parts list", async () => {
    sendCommandMock.mockResolvedValue({});
    await pipe.parts_list_family_add.execute(pipe.parts_list_family_add.inputSchema.parse({
      action: "parts_list_family_add", partsList: "Sewer", familyName: "Air valve", domain: "structure", sourcePartsList: "Water",
    }));
    expect(sendCommandMock).toHaveBeenCalledWith("addPartsListFamily", {
      partsList: "Sewer", familyName: "Air valve", domain: "structure", sourcePartsList: "Water", createPartsList: false,
    });
  });

  it("needs parts list, family and domain", () => {
    const base = { action: "parts_list_family_add", partsList: "P", familyName: "F", domain: "pipe" };
    expect(pipe.parts_list_family_add.inputSchema.safeParse(base).success).toBe(true);
    expect(pipe.parts_list_family_add.inputSchema.safeParse({ ...base, domain: undefined }).success).toBe(false);
    expect(pipe.parts_list_family_add.inputSchema.safeParse({ ...base, familyName: "" }).success).toBe(false);
  });

  it("only adding needs approval; both are on the canonical pipe tool", () => {
    const needs = (a: "catalog_families" | "parts_list_family_add") =>
      isApprovalRequired({ toolName: "civil3d_pipe", action: a, capabilities: pipe[a].capabilities, safeForRetry: pipe[a].safeForRetry });
    expect(needs("catalog_families")).toBe(false);
    expect(needs("parts_list_family_add")).toBe(true);
    const entry = GENERATED_TOOL_CATALOG_ENTRIES.find((e) => e.toolName === "civil3d_pipe");
    expect(entry!.operations).toEqual(expect.arrayContaining(["catalog_families", "parts_list_family_add"]));
  });
});

describe("C79 catalog sizes into a parts list family", () => {
  beforeEach(() => sendCommandMock.mockReset());

  it("adds given sizes or all, behind approval, on the canonical pipe tool", async () => {
    sendCommandMock.mockResolvedValue({});
    await pipe.parts_list_sizes_add.execute(pipe.parts_list_sizes_add.inputSchema.parse({
      action: "parts_list_sizes_add", partsList: "LPS", familyName: "PE100 pressure pipe PN16", domain: "pipe", sizes: ["32", 40], field: "PrtSN",
    }));
    expect(sendCommandMock).toHaveBeenCalledWith("addPartsListSizes", {
      partsList: "LPS", familyName: "PE100 pressure pipe PN16", domain: "pipe", sizes: ["32", "40"], all: undefined, field: "PrtSN",
    });
    const base = { action: "parts_list_sizes_add", partsList: "P", familyName: "F", domain: "pipe" };
    expect(pipe.parts_list_sizes_add.inputSchema.safeParse(base).success).toBe(false);
    expect(pipe.parts_list_sizes_add.inputSchema.safeParse({ ...base, all: true }).success).toBe(true);
    expect(isApprovalRequired({ toolName: "civil3d_pipe", action: "parts_list_sizes_add", capabilities: pipe.parts_list_sizes_add.capabilities, safeForRetry: pipe.parts_list_sizes_add.safeForRetry })).toBe(true);
    const entry = GENERATED_TOOL_CATALOG_ENTRIES.find((e) => e.toolName === "civil3d_pipe");
    expect(entry!.operations).toEqual(expect.arrayContaining(["parts_list_sizes_add"]));
  });
});

describe("C82 remove a family from a parts list", () => {
  beforeEach(() => sendCommandMock.mockReset());

  it("removes a family behind approval", async () => {
    sendCommandMock.mockResolvedValue({});
    await pipe.parts_list_family_remove.execute(pipe.parts_list_family_remove.inputSchema.parse({
      action: "parts_list_family_remove", partsList: "Standard", familyName: "PE pressure pipe", domain: "pipe",
    }));
    expect(sendCommandMock).toHaveBeenCalledWith("removePartsListFamily", { partsList: "Standard", familyName: "PE pressure pipe", domain: "pipe" });
    expect(isApprovalRequired({ toolName: "civil3d_pipe", action: "parts_list_family_remove", capabilities: pipe.parts_list_family_remove.capabilities, safeForRetry: pipe.parts_list_family_remove.safeForRetry })).toBe(true);
  });
});

describe("C98d size data of a parts list family", () => {
  beforeEach(() => sendCommandMock.mockReset());
  const family = "PE100 pressure pipe PN10";
  const needs = (a: "parts_list_size_data_get" | "parts_list_size_data_set") =>
    isApprovalRequired({ toolName: "civil3d_pipe", action: a, capabilities: pipe[a].capabilities, safeForRetry: pipe[a].safeForRetry });
  const survivesExposure = (args: Record<string, unknown>) => {
    const exposure = PIPE_DOMAIN_DEFINITION.exposures.find((e) => e.toolName === "civil3d_pipe")!;
    const parsed = z.object(exposure.inputShape).parse(args) as Record<string, unknown>;
    expect(Object.keys(parsed).sort()).toEqual(Object.keys(args).sort());
    expect(exposure.supportedActions).toContain(args.action);
  };

  it("reads families, sizes and their data fields without approval", async () => {
    sendCommandMock.mockResolvedValue({});
    const get = pipe.parts_list_size_data_get;
    await get.execute(get.inputSchema.parse({ action: "parts_list_size_data_get", partsList: "Sewer pressure" }));
    expect(sendCommandMock).toHaveBeenLastCalledWith("partsListSizeDataGet", { partsList: "Sewer pressure" });
    const args = { action: "parts_list_size_data_get", partsList: "Sewer pressure", familyName: family, domain: "pipe", sizes: [0, "110 PE100 SDR17"], field: "Material" };
    await get.execute(get.inputSchema.parse(args));
    expect(sendCommandMock).toHaveBeenLastCalledWith("partsListSizeDataGet", { partsList: "Sewer pressure", familyName: family, domain: "pipe", sizes: [0, "110 PE100 SDR17"], field: "Material" });
    survivesExposure(args);
    expect(needs("parts_list_size_data_get")).toBe(false);
    expect(get.inputSchema.safeParse({ action: "parts_list_size_data_get", partsList: "P", field: "Material" }).success).toBe(false);
    expect(get.inputSchema.safeParse({ action: "parts_list_size_data_get", partsList: "P", sizes: [0] }).success).toBe(false);
    expect(get.inputSchema.safeParse({ action: "parts_list_size_data_get", partsList: "P", familyName: "F", domain: "fitting" }).success).toBe(false);
  });

  it("writes a field such as Material behind approval", async () => {
    sendCommandMock.mockResolvedValue({});
    const set = pipe.parts_list_size_data_set;
    const args = { action: "parts_list_size_data_set", partsList: "Sewer pressure", familyName: family, values: { Material: "PE" }, dryRun: true };
    await set.execute(set.inputSchema.parse(args));
    expect(sendCommandMock).toHaveBeenLastCalledWith("partsListSizeDataSet", { partsList: "Sewer pressure", familyName: family, values: { Material: "PE" }, dryRun: true });
    survivesExposure({ ...args, sizes: [2, 3], domain: "pipe" });
    expect(needs("parts_list_size_data_set")).toBe(true);
    const base = { action: "parts_list_size_data_set", partsList: "P", familyName: "F" };
    expect(set.inputSchema.safeParse({ ...base, values: { Material: "PE" } }).success).toBe(true);
    expect(set.inputSchema.safeParse(base).success).toBe(false);
    expect(set.inputSchema.safeParse({ ...base, values: {} }).success).toBe(false);
    expect(set.inputSchema.safeParse({ ...base, values: { Material: { name: "PE" } } }).success).toBe(false);
    expect(set.inputSchema.safeParse({ ...base, values: { Material: "PE" }, sizes: [-1] }).success).toBe(false);
    expect(set.inputSchema.safeParse({ ...base, values: { Material: "PE" }, sizes: [1.5] }).success).toBe(false);
    expect(set.inputSchema.safeParse({ action: "parts_list_size_data_set", partsList: "P", values: { Material: "PE" } }).success).toBe(false);
  });
});
