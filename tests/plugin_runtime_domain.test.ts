import { describe, expect, it } from "vitest";
import { PLUGIN_DOMAIN_DEFINITION } from "../src/tools/domains/pluginDomain.js";

describe("plugin runtime domain", () => {
  it("preserves queue, job, and file-log observability fields", () => {
    const health = PLUGIN_DOMAIN_DEFINITION.actions.health;
    const response = health.responseSchema.parse({
      connected: true,
      civil3dVersion: "24.3s",
      pluginVersion: "1.2.1.0",
      drawingLoaded: true,
      operationInProgress: false,
      currentOperation: null,
      queueDepth: 0,
      queueCapacity: 64,
      currentOperationStartedAtUnixMs: null,
      currentRequestId: null,
      currentOperationDurationMs: null,
      memoryUsageMb: 512,
      logFilePath: "C:\\logs\\Civil3DMcpPlugin\\plugin.log",
      fileLoggingHealthy: true,
      fileLoggingError: null,
      jobs: {
        total: 1,
        running: 0,
        completed: 1,
        failed: 0,
        cancelled: 0,
        capacity: 256,
        terminalRetentionMinutes: 1440,
      },
    });

    expect(response.fileLoggingHealthy).toBe(true);
    expect(response.logFilePath).toContain("plugin.log");
    expect(response.queueCapacity).toBe(64);
    expect(response.jobs.capacity).toBe(256);
  });

  it("keeps the active drawing and the number of open drawings (C93c)", () => {
    const health = PLUGIN_DOMAIN_DEFINITION.actions.health;
    const base = {
      connected: true, drawingLoaded: true, operationInProgress: false, currentOperation: null, queueDepth: 0, queueCapacity: 64,
      currentOperationStartedAtUnixMs: null, currentRequestId: null, currentOperationDurationMs: null, memoryUsageMb: 512,
      logFilePath: "plugin.log", fileLoggingHealthy: true, fileLoggingError: null,
      jobs: { total: 0, running: 0, completed: 0, failed: 0, cancelled: 0, capacity: 256, terminalRetentionMinutes: 1440 },
    };
    const response = health.responseSchema.parse({ ...base, activeDocument: "Drawing2.dwg", documentCount: 2 }) as Record<string, unknown>;
    expect(response.activeDocument).toBe("Drawing2.dwg");
    expect(response.documentCount).toBe(2);
    expect((health.responseSchema.parse({ ...base, drawingLoaded: false, activeDocument: null, documentCount: 0 }) as Record<string, unknown>).activeDocument).toBeNull();
  });
});
