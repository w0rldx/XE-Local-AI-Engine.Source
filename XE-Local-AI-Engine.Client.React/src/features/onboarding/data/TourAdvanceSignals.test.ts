import type { QueryClient } from "@tanstack/react-query";
import { describe, expect, it } from "vitest";

import type { XeLocalAiEngineClientEndpointsLocalModelsV1LocalModelResponse as LocalModelResponse } from "@/core/api/generated/types.gen";
import type { ChatConversationModel } from "@/features/chat/models/ChatModels";
import {
	countVisibleAssistantReplies,
	hasChatCapableDefault,
	hasInstalledChatModel,
} from "@/features/onboarding/data/TourAdvanceSignals";

const chatModel: LocalModelResponse = {
	modelName: "qwen3:8b",
	provider: "llamacpp",
	kind: "Chat",
	detectedKind: "Chat",
	isSelected: false,
	capabilities: [],
	isReasoningCapable: false,
	isToolCapable: false,
	isOverridden: false,
};
const embeddingModel: LocalModelResponse = {
	modelName: "nomic-embed",
	provider: "llamacpp",
	kind: "Embedding",
	detectedKind: "Embedding",
	isSelected: false,
	capabilities: [],
	isReasoningCapable: false,
	isToolCapable: false,
	isOverridden: false,
};
const rerankerModel: LocalModelResponse = {
	modelName: "bge-reranker-v2-m3",
	provider: "llamacpp",
	kind: "Reranker",
	detectedKind: "Reranker",
	isSelected: false,
	capabilities: [],
	isReasoningCapable: false,
	isToolCapable: false,
	isOverridden: false,
};
const cloudChatModel: LocalModelResponse = {
	...chatModel,
	modelName: "gpt-5.3-codex",
	provider: "CodexOAuth",
};

describe("hasInstalledChatModel (install step advances on real state, not a timer)", () => {
	it("is false when no models are installed", () => {
		expect(hasInstalledChatModel(undefined)).toBe(false);
		expect(hasInstalledChatModel([])).toBe(false);
	});

	it("is false when only an embedding model is installed (not chat-capable)", () => {
		expect(hasInstalledChatModel([embeddingModel])).toBe(false);
	});

	it("is false when only a reranker model is installed (allowlist excludes non-chat kinds)", () => {
		// A reranker (cross-encoder) has no completion head; the tour must not treat it as an installed chat model.
		expect(hasInstalledChatModel([rerankerModel])).toBe(false);
	});

	it("is false when only a cloud chat model is listed", () => {
		expect(hasInstalledChatModel([cloudChatModel])).toBe(false);
	});

	it("flips to true only once a chat-capable model is actually installed", () => {
		// Pre-install state: only non-chat kinds present → the install step must NOT advance.
		expect(hasInstalledChatModel([embeddingModel, rerankerModel])).toBe(false);
		// State flips when the real download completes and the chat model appears in the list.
		expect(hasInstalledChatModel([embeddingModel, rerankerModel, chatModel])).toBe(true);
	});
});

describe("hasChatCapableDefault", () => {
	it("is false when no default is selected", () => {
		expect(hasChatCapableDefault([chatModel], null)).toBe(false);
		expect(hasChatCapableDefault([chatModel], undefined)).toBe(false);
	});

	it("is false when the selected default is an embedding model", () => {
		expect(hasChatCapableDefault([embeddingModel], "nomic-embed")).toBe(false);
	});

	it("is false when the selected default is a reranker model", () => {
		expect(hasChatCapableDefault([rerankerModel], "bge-reranker-v2-m3")).toBe(false);
	});

	it("is false when the selected default is a cloud model", () => {
		expect(hasChatCapableDefault([cloudChatModel], "gpt-5.3-codex")).toBe(false);
	});

	it("is true when the selected default names an installed chat-capable model", () => {
		expect(hasChatCapableDefault([chatModel], "qwen3:8b")).toBe(true);
	});
});

describe("countVisibleAssistantReplies", () => {
	function fakeClient(conversations: [unknown, ChatConversationModel | undefined][]): QueryClient {
		return {
			getQueriesData: () => conversations,
		} as unknown as QueryClient;
	}

	function conversation(messages: Array<{ role: string; content: string }>): ChatConversationModel {
		return { messages } as unknown as ChatConversationModel;
	}

	it("counts nothing when no assistant message has content yet", () => {
		const client = fakeClient([
			[
				["c1"],
				conversation([
					{ role: "user", content: "hi" },
					{ role: "assistant", content: "" },
				]),
			],
		]);
		expect(countVisibleAssistantReplies(client)).toBe(0);
	});

	it("counts an assistant message once it carries non-empty content", () => {
		const client = fakeClient([
			[
				["c1"],
				conversation([
					{ role: "user", content: "hi" },
					{ role: "assistant", content: "Hello!" },
				]),
			],
		]);
		expect(countVisibleAssistantReplies(client)).toBe(1);
	});

	it("counts visible replies so a later reply can be distinguished from existing conversation history", () => {
		const client = fakeClient([
			[["c1"], conversation([{ role: "assistant", content: "First" }])],
			[
				["c2"],
				conversation([
					{ role: "assistant", content: "Second" },
					{ role: "assistant", content: "   " },
				]),
			],
		]);

		expect(countVisibleAssistantReplies(client)).toBe(2);
	});

	it("counts nothing when there are no cached conversations", () => {
		expect(countVisibleAssistantReplies(fakeClient([]))).toBe(0);
	});
});
