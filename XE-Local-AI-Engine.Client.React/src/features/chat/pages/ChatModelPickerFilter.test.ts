import { describe, expect, it } from "vitest";

import type { XeLocalAiEngineClientEndpointsLocalModelsV1LocalModelResponse } from "@/core/api/generated";
import { toChatModelOptions, toDraftModelOptions } from "@/features/chat/pages/ChatModelOptions";

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

	it("hides a speculative-decoding draft model", () => {
		// A downloaded MTP drafter used to be classified Chat and sat in the picker as a 0.4 GB twin of the real
		// model it drafts for. It has no standalone chat use at all.
		const models = [model("unsloth/gemma-4-12b-it-GGUF:MTP-Q8_0", "Draft"), model("unsloth/gemma-4-12b-it-GGUF:Q8_0", "Chat")];

		expect(toChatModelOptions(models, true).map((option) => option.value)).toEqual(["unsloth/gemma-4-12b-it-GGUF:Q8_0"]);
	});

	it("propagates node availability onto each chat option", () => {
		const options = toChatModelOptions([model("llama3:8b", "Chat")], false);

		expect(options[0]?.isAvailable).toBe(false);
	});
});

describe("speculative draft model picker filter", () => {
	it("offers drafts and chat models, and nothing else", () => {
		// The Node Settings draft slot is the ONLY surface an MTP drafter is usable on, so it must appear here —
		// alongside any small chat model, which is the ordinary draft-simple setup.
		const models = [
			model("unsloth/gemma-4-12b-it-GGUF:MTP-Q8_0", "Draft"),
			model("llama3:1b", "Chat"),
			model("nomic-embed-text", "Embedding"),
			model("bge-reranker-v2-m3", "Reranker"),
			model("mystery", "Unknown"),
		];

		expect(toDraftModelOptions(models, true).map((option) => option.value)).toEqual([
			"unsloth/gemma-4-12b-it-GGUF:MTP-Q8_0",
			"llama3:1b",
		]);
	});
});
