import { describe, expect, it } from "vitest";

import type { XeLocalAiEngineClientEndpointsLocalModelsV1LocalModelResponse } from "@/core/api/generated";
import { toChatModelOptions, toModelOption } from "@/features/chat/pages/ChatModelOptions";

type LocalModelDto = XeLocalAiEngineClientEndpointsLocalModelsV1LocalModelResponse;

function model(overrides: Partial<LocalModelDto>): LocalModelDto {
	return { modelName: "qwen3:8b", kind: "Chat", ...overrides };
}

describe("toModelOption capability mapping", () => {
	it("maps isReasoningCapable to isReasoningModel and isToolCapable through", () => {
		const option = toModelOption(model({ isReasoningCapable: true, isToolCapable: true }), true);

		expect(option.isReasoningModel).toBe(true);
		expect(option.isToolCapable).toBe(true);
	});

	it("treats a model that lacks the capability flags as not reasoning/tool capable", () => {
		const option = toModelOption(model({ isReasoningCapable: false, isToolCapable: false }), true);

		expect(option.isReasoningModel).toBe(false);
		expect(option.isToolCapable).toBe(false);
	});

	it("coalesces undefined capability flags to false", () => {
		const option = toModelOption(model({ isReasoningCapable: undefined, isToolCapable: undefined }), true);

		expect(option.isReasoningModel).toBe(false);
		expect(option.isToolCapable).toBe(false);
	});
});

describe("toChatModelOptions capability mapping", () => {
	it("carries per-model capabilities through the chat-capable filter", () => {
		const options = toChatModelOptions(
			[
				model({ modelName: "qwen3:8b", isReasoningCapable: true, isToolCapable: true }),
				model({ modelName: "gemma:12b", isReasoningCapable: false, isToolCapable: false }),
			],
			true,
		);

		expect(options).toHaveLength(2);
		expect(options[0]).toMatchObject({ value: "qwen3:8b", isReasoningModel: true, isToolCapable: true });
		expect(options[1]).toMatchObject({ value: "gemma:12b", isReasoningModel: false, isToolCapable: false });
	});

	it("excludes non-chat models before mapping", () => {
		const options = toChatModelOptions(
			[model({ modelName: "embed", kind: "Embedding", isReasoningCapable: true, isToolCapable: true })],
			true,
		);

		expect(options).toHaveLength(0);
	});
});
