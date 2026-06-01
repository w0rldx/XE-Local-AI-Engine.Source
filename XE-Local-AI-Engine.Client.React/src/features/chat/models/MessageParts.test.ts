import { describe, expect, it } from "vitest";

import { buildMessageParts, type ReasoningSegmentInput, type ToolEntryInput } from "@/features/chat/models/MessageParts";

describe("buildMessageParts", () => {
	it("orders reasoning and tool parts by wire sequence", () => {
		const reasoning: ReasoningSegmentInput[] = [
			{ id: "m:0", sequence: 0, text: "first thoughts" },
			{ id: "m:2", sequence: 2, text: "second thoughts" },
		];
		const tools: ToolEntryInput[] = [{ id: "call-1", sequence: 1, name: "get_time", state: "received", result: "12:00" }];

		const parts = buildMessageParts(reasoning, tools);

		expect(parts.map((part) => [part.kind, part.sequence])).toEqual([
			["reasoning", 0],
			["tool", 1],
			["reasoning", 2],
		]);
	});

	it("drops empty reasoning segments so a freshly opened block never renders blank", () => {
		const parts = buildMessageParts(
			[
				{ id: "m:0", sequence: 0, text: "kept" },
				{ id: "m:2", sequence: 2, text: "   " },
			],
			[],
		);

		expect(parts).toHaveLength(1);
		expect(parts[0]).toMatchObject({ kind: "reasoning", text: "kept" });
	});

	it("interleaves optional text segments by sequence", () => {
		const parts = buildMessageParts(
			[{ id: "m:0", sequence: 0, text: "reason" }],
			[{ id: "call-1", sequence: 1, name: "t", state: "received" }],
			[{ id: "m:2", sequence: 2, text: "narration" }],
		);

		expect(parts.map((part) => part.kind)).toEqual(["reasoning", "tool", "text"]);
	});

	it("does not mutate its inputs (purity)", () => {
		const reasoning: ReasoningSegmentInput[] = [{ id: "m:0", sequence: 0, text: "a" }];
		const tools: ToolEntryInput[] = [{ id: "call-1", sequence: 1, name: "t", state: "received" }];
		const reasoningSnapshot = JSON.stringify(reasoning);
		const toolsSnapshot = JSON.stringify(tools);

		buildMessageParts(reasoning, tools);

		expect(JSON.stringify(reasoning)).toBe(reasoningSnapshot);
		expect(JSON.stringify(tools)).toBe(toolsSnapshot);
	});

	it("returns an empty array when there is nothing to render", () => {
		expect(buildMessageParts([], [])).toEqual([]);
	});
});
