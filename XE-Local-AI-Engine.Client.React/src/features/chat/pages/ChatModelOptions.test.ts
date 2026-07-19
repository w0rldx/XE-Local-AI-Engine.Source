import { describe, expect, it } from "vitest";

import type { XeLocalAiEngineClientEndpointsLocalModelsV1LocalModelResponse } from "@/core/api/generated";
import {
	resolveLocalDefaultModelCapabilities,
	resolveLocalDefaultModelName,
	toChatModelOptions,
	toModelOption,
} from "@/features/chat/pages/ChatModelOptions";

type LocalModelDto = XeLocalAiEngineClientEndpointsLocalModelsV1LocalModelResponse;

function model(overrides: Partial<LocalModelDto>): LocalModelDto {
	return {
		modelName: "qwen3:8b",
		kind: "Chat",
		detectedKind: "Chat",
		isSelected: false,
		capabilities: [],
		isReasoningCapable: false,
		isToolCapable: false,
		isOverridden: false,
		...overrides,
	};
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

describe("resolveLocalDefaultModelCapabilities", () => {
	it("uses the node default (isSelected) chat model's capabilities", () => {
		const capabilities = resolveLocalDefaultModelCapabilities([
			model({ modelName: "gemma:12b", isReasoningCapable: false, isToolCapable: false }),
			model({ modelName: "qwen3:8b", isSelected: true, isReasoningCapable: true, isToolCapable: true }),
		]);

		expect(capabilities).toEqual({ isReasoningModel: true, isToolCapable: true });
	});

	it("falls back to name-ascending order when no model is the node default and mod-times tie", () => {
		// Neither carries modifiedAtUtc, so the fallback's ThenBy(modelName) decides: "gemma:12b" < "qwen3:8b".
		const capabilities = resolveLocalDefaultModelCapabilities([
			model({ modelName: "qwen3:8b", isReasoningCapable: true, isToolCapable: false }),
			model({ modelName: "gemma:12b", isReasoningCapable: false, isToolCapable: true }),
		]);

		expect(capabilities).toEqual({ isReasoningModel: false, isToolCapable: true });
	});

	it("falls back to the most-recently-modified chat model, overriding name order", () => {
		// Name-ascending would pick "alpha", but modifiedAtUtc-descending must win: "zeta" is newer.
		const capabilities = resolveLocalDefaultModelCapabilities([
			model({ modelName: "alpha", modifiedAtUtc: 1000, isReasoningCapable: false, isToolCapable: false }),
			model({ modelName: "zeta", modifiedAtUtc: 2000, isReasoningCapable: true, isToolCapable: true }),
		]);

		expect(capabilities).toEqual({ isReasoningModel: true, isToolCapable: true });
	});

	it("ignores non-chat models when resolving the default", () => {
		const capabilities = resolveLocalDefaultModelCapabilities([
			model({ modelName: "embed", kind: "Embedding", isSelected: true, isReasoningCapable: true, isToolCapable: true }),
			model({ modelName: "qwen3:8b", isReasoningCapable: true, isToolCapable: false }),
		]);

		expect(capabilities).toEqual({ isReasoningModel: true, isToolCapable: false });
	});

	it("excludes CodexOAuth provider entries from the resolved default", () => {
		const capabilities = resolveLocalDefaultModelCapabilities([
			model({ modelName: "codex", provider: "CodexOAuth", isSelected: true, isReasoningCapable: true, isToolCapable: true }),
			model({ modelName: "gemma:12b", isReasoningCapable: false, isToolCapable: false }),
		]);

		expect(capabilities).toEqual({ isReasoningModel: false, isToolCapable: false });
	});

	it("returns false capabilities when there are no chat models", () => {
		const capabilities = resolveLocalDefaultModelCapabilities([]);

		expect(capabilities).toEqual({ isReasoningModel: false, isToolCapable: false });
	});
});

describe("resolveLocalDefaultModelName", () => {
	it("names the node default (isSelected) installed chat model", () => {
		const name = resolveLocalDefaultModelName([
			model({ modelName: "gemma:12b" }),
			model({ modelName: "qwen3:8b", isSelected: true }),
		]);

		expect(name).toBe("qwen3:8b");
	});

	it("names the newest-modified installed chat model when the store default is not installed", () => {
		// The configured/selected store name is NOT in this installed list — the resolver only ever names an
		// installed model (the backend runs one), so the details poll's installed-list gate can open.
		const name = resolveLocalDefaultModelName([
			model({ modelName: "alpha", modifiedAtUtc: 1000 }),
			model({ modelName: "zeta", modifiedAtUtc: 2000 }),
		]);

		expect(name).toBe("zeta");
	});

	it("returns undefined when no installed chat-capable model exists", () => {
		expect(resolveLocalDefaultModelName([])).toBeUndefined();
		expect(resolveLocalDefaultModelName([model({ modelName: "embed", kind: "Embedding" })])).toBeUndefined();
	});
});
