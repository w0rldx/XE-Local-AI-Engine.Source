import { describe, expect, it } from "vitest";

import { answerNowMessageId } from "@/features/chat/models/ChatAnswerNow";
import type { ChatStreamingState, ModelOption } from "@/features/chat/models/ChatModels";

const controllable: ModelOption = {
	value: "qwen3",
	label: "qwen3",
	isAvailable: true,
	isReasoningModel: true,
	isReasoningControllable: true,
};

function stream(overrides: Partial<ChatStreamingState> = {}): ChatStreamingState {
	return {
		conversationId: "conversation-1",
		messageId: "assistant-1",
		content: "",
		isActive: true,
		parts: [{ kind: "reasoning", id: "assistant-1:1", sequence: 1, text: "Thinking." }],
		...overrides,
	};
}

describe("answerNowMessageId", () => {
	it("targets the streaming message while its trailing reasoning segment streams on a controllable model", () => {
		expect(answerNowMessageId(stream(), controllable, "medium")).toBe("assistant-1");
	});

	it("falls back to the flat reasoning when the stream has no parts yet", () => {
		expect(answerNowMessageId(stream({ parts: undefined, reasoning: "Thinking." }), controllable, "high")).toBe("assistant-1");
	});

	it.each<[string, ChatStreamingState | undefined, ModelOption | undefined, "none" | "medium"]>([
		["no stream", undefined, controllable, "medium"],
		["a finished stream", stream({ isActive: false }), controllable, "medium"],
		["the answer has started", stream({ content: "The answer" }), controllable, "medium"],
		[
			"a tool card is the trailing part",
			stream({ parts: [{ kind: "tool", id: "t", sequence: 2, name: "search", state: "waiting" }] }),
			controllable,
			"medium",
		],
		["reasoning is off", stream(), controllable, "none"],
		["a model without reasoning control", stream(), { ...controllable, isReasoningControllable: undefined }, "medium"],
	])("hides the button with %s", (_case, state, model, effort) => {
		expect(answerNowMessageId(state, model, effort)).toBeUndefined();
	});
});
