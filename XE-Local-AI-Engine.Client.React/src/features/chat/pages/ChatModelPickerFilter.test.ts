import { describe, expect, it } from "vitest";

import type { XeLocalAiEngineClientEndpointsLocalModelsV1LocalModelResponse } from "@/core/api/generated";
import { toChatModelOptions } from "@/features/chat/pages/ChatModelOptions";

type LocalModelDto = XeLocalAiEngineClientEndpointsLocalModelsV1LocalModelResponse;

function model(modelName: string, kind: string): LocalModelDto {
	return {
		modelName,
		sizeBytes: null,
		modifiedAtUtc: null,
		family: null,
		parameterSize: null,
		quantizationLevel: null,
		isSelected: false,
		kind,
		detectedKind: kind,
		capabilities: [],
		isReasoningCapable: false,
		isToolCapable: false,
		isOverridden: false,
	};
}

describe("chat model picker filter", () => {
	it("keeps only chat-capable models and hides embedding, reranker and unknown ones", () => {
		const models = [
			model("llama3:8b", "Chat"),
			model("nomic-embed-text", "Embedding"),
			model("bge-reranker-v2-m3", "Reranker"),
			model("mystery-model", "Unknown"),
			model("mistral", "Chat"),
		];

		const options = toChatModelOptions(models, true);

		expect(options.map((option) => option.value)).toEqual(["llama3:8b", "mistral"]);
	});

	it("returns no options when every model is non-chat", () => {
		const options = toChatModelOptions(
			[model("nomic-embed-text", "Embedding"), model("bge-reranker-v2-m3", "Reranker"), model("mystery", "Unknown")],
			true,
		);

		expect(options).toHaveLength(0);
	});

	it("propagates node availability onto each chat option", () => {
		const options = toChatModelOptions([model("llama3:8b", "Chat")], false);

		expect(options[0]?.isAvailable).toBe(false);
	});
});
