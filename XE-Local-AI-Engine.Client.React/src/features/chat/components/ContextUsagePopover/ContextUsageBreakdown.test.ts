import { describe, expect, it } from "vitest";

import {
	computeContextSections,
	contextSource,
	groupTools,
	isSnapshotForModel,
	remainingTokens,
} from "@/features/chat/components/ContextUsagePopover/ContextUsageBreakdown";
import type { ContextWindowSnapshot } from "@/features/chat/models/ContextWindowModels";

function snapshot(overrides: Partial<ContextWindowSnapshot> = {}): ContextWindowSnapshot {
	return {
		kind: "LastRound",
		windowTokens: 10_000,
		reservedOutputTokens: 1_000,
		usableWindowTokens: 8_000,
		safetyMarginTokens: 1_000,
		providerInputTokens: 3_000,
		estimated: {
			systemPromptTokens: 500,
			instructionsTokens: 0,
			toolSchemaTokens: 800,
			toolTemplatePreambleTokens: 200,
			knowledgeTokens: 0,
			attachmentTokens: 0,
			compactionTokens: 0,
			conversationTokens: 1_400,
			totalTokens: 2_900,
		},
		tools: [],
		toolsWithheldCount: 0,
		...overrides,
	};
}

describe("context usage breakdown", () => {
	it("orders the estimated categories, folds the tool preamble into tools and omits zero sections", () => {
		expect(
			computeContextSections(snapshot()).map((section) => [section.key, section.tokens, Math.round(section.percent)]),
		).toEqual([
			["systemPrompt", 500, 5],
			["tools", 1_000, 10],
			["conversation", 1_400, 14],
			["reservedOutput", 1_000, 10],
			["safetyMargin", 1_000, 10],
			["free", 5_100, 51],
		]);
	});

	it("fills exactly the window with an estimate, whatever the provider reported", () => {
		for (const providerInputTokens of [3_000, 1_000, null]) {
			const total = computeContextSections(snapshot({ providerInputTokens })).reduce((sum, section) => sum + section.tokens, 0);
			expect(total).toBe(10_000);
		}
	});

	it("falls back to one reported section when the snapshot carries no estimate", () => {
		expect(computeContextSections(snapshot({ estimated: null })).map((section) => section.key)).toEqual([
			"used",
			"reservedOutput",
			"safetyMargin",
			"free",
		]);
	});

	it("computes remaining from the reported input, else the estimate, and never below zero", () => {
		expect(remainingTokens(snapshot())).toBe(5_000);
		expect(remainingTokens(snapshot({ providerInputTokens: null }))).toBe(5_100);
		expect(remainingTokens(snapshot({ providerInputTokens: 9_000 }))).toBe(0);
	});

	it("treats a zero window as unknown capacity", () => {
		const unknown = snapshot({ windowTokens: 0, usableWindowTokens: 0, reservedOutputTokens: 0, safetyMarginTokens: 0 });
		expect(remainingTokens(unknown)).toBeUndefined();
		expect(computeContextSections(unknown).every((section) => section.percent === 0)).toBe(true);
	});

	it("labels the source by what the snapshot carries", () => {
		expect(contextSource(snapshot())).toBe("reported");
		expect(contextSource(snapshot({ providerInputTokens: null }))).toBe("estimated");
		expect(contextSource(snapshot({ providerInputTokens: null, estimated: null }))).toBe("unknown");
		expect(contextSource(undefined)).toBe("unknown");
	});

	it("groups tools as built-in, MCP per server slug, custom, then skills", () => {
		const groups = groupTools([
			{ name: "load_skill", tokens: 30 },
			{ name: "mcp__weather__forecast", tokens: 50 },
			{ name: "custom__ticket_lookup", tokens: 40 },
			{ name: "web_search", tokens: 20 },
			{ name: "mcp__docs__search", tokens: 10 },
			{ name: "mcp__weather__alerts", tokens: 5 },
		]);

		expect(groups.map((group) => [group.kind, group.server, group.tokens])).toEqual([
			["builtIn", undefined, 20],
			["mcp", "docs", 10],
			["mcp", "weather", 55],
			["custom", undefined, 40],
			["skills", undefined, 30],
		]);
		expect(groups[2]?.tools.map((tool) => tool.displayName)).toEqual(["forecast", "alerts"]);
		expect(groups[3]?.tools[0]?.displayName).toBe("ticket_lookup");
	});

	it("matches a snapshot to the selected model", () => {
		const served = (modelId: string | null) => snapshot({ modelId });

		expect(isSnapshotForModel(served("qwen3-4b"), "")).toBe(true);
		expect(isSnapshotForModel(served(null), "llama-3-8b")).toBe(true);
		expect(isSnapshotForModel(served("qwen3-4b"), "qwen3-4b")).toBe(true);
		expect(isSnapshotForModel(served("Qwen3-4B"), "qwen3-4b")).toBe(true);
		expect(isSnapshotForModel(served("qwen3-27b"), "ext:workstation/qwen3-27b")).toBe(true);
		expect(isSnapshotForModel(served("ext:workstation/qwen3-27b"), "workstation/qwen3-27b")).toBe(true);
		expect(isSnapshotForModel(served("qwen3-4b"), "llama-3-8b")).toBe(false);
		expect(isSnapshotForModel(served("qwen3:4b"), "llama:4b")).toBe(false);
		expect(isSnapshotForModel(served("27b"), "ext:workstation/qwen3-27b")).toBe(false);
	});
});
