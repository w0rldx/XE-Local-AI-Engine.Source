import { describe, expect, it } from "vitest";

import { formatInvocationDuration, getInvocationStatusColor, isInvocationActive, sortInvocationHistory } from "@/features/invocations/models/InvocationMonitorModel";

describe("invocation monitor model", () => {
  it("formats durations", () => {
    expect(formatInvocationDuration(250)).toBe("250 ms");
    expect(formatInvocationDuration(1200)).toBe("1.2 s");
    expect(formatInvocationDuration(120_000)).toBe("2.0 min");
  });

  it("maps status colors and active state", () => {
    expect(getInvocationStatusColor("Running")).toBe("blue");
    expect(getInvocationStatusColor("Completed")).toBe("green");
    expect(getInvocationStatusColor("Failed")).toBe("red");
    expect(isInvocationActive("Assigned")).toBe(true);
    expect(isInvocationActive("Completed")).toBe(false);
  });

  it("sorts history by newest completion first", () => {
    const sorted = sortInvocationHistory([
      createHistory("old", "2026-05-25T10:00:00Z"),
      createHistory("new", "2026-05-25T10:05:00Z"),
    ]);

    expect(sorted.map((entry) => entry.invocationId)).toEqual(["new", "old"]);
  });
});

function createHistory(invocationId: string, completedAt: string) {
  return {
    invocationId,
    conversationId: "conversation",
    status: "Completed" as const,
    modelUsed: "qwen3:8b",
    startedAt: "2026-05-25T09:59:00Z",
    completedAt,
    durationMs: 60_000,
    error: null,
    failureCategory: null,
    streamedChunkCount: 1,
    streamedThinkingChunkCount: 0,
  };
}
