import { describe, expect, it } from "vitest";

import type { XeLocalAiEngineClientEndpointsLocalModelsV1LocalModelResponse } from "@/core/api/generated";
import {
	groupExternalModelOptions,
	resolveLocalDefaultModelCapabilities,
	resolveLocalDefaultModelName,
	toChatModelOptions,
	toDraftModelOptions,
	toExternalModelOptions,
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
		expect(option.isNativeReasoningModel).toBe(false);
		expect(option.isToolCapable).toBe(false);
	});

	// A native-reasoning model (harmony/gpt-oss) reasons, but on a template-baked channel with no graded
	// switch. It must surface as its OWN capability and must NOT be folded into isReasoningModel — that flag drives
	// the graded think:<level> menu and the backend branch that writes `think` / `enable_thinking=false`.
	it("maps isNativeReasoningCapable to its own flag without setting isReasoningModel", () => {
		const option = toModelOption(model({ isNativeReasoningCapable: true, isReasoningCapable: false }), true);

		expect(option.isNativeReasoningModel).toBe(true);
		expect(option.isReasoningModel).toBe(false);
	});

	it("keeps a graded reasoning model out of the native flag", () => {
		const option = toModelOption(model({ isReasoningCapable: true, isNativeReasoningCapable: false }), true);

		expect(option.isReasoningModel).toBe(true);
		expect(option.isNativeReasoningModel).toBe(false);
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

		expect(capabilities).toEqual({
			isReasoningModel: true,
			isNativeReasoningModel: false,
			isToolCapable: true,
			isMultimodal: false,
		});
	});

	it("falls back to name-ascending order when no model is the node default and mod-times tie", () => {
		// Neither carries modifiedAtUtc, so the fallback's ThenBy(modelName) decides: "gemma:12b" < "qwen3:8b".
		const capabilities = resolveLocalDefaultModelCapabilities([
			model({ modelName: "qwen3:8b", isReasoningCapable: true, isToolCapable: false }),
			model({ modelName: "gemma:12b", isReasoningCapable: false, isToolCapable: true }),
		]);

		expect(capabilities).toEqual({
			isReasoningModel: false,
			isNativeReasoningModel: false,
			isToolCapable: true,
			isMultimodal: false,
		});
	});

	it("falls back to the most-recently-modified chat model, overriding name order", () => {
		// Name-ascending would pick "alpha", but modifiedAtUtc-descending must win: "zeta" is newer.
		const capabilities = resolveLocalDefaultModelCapabilities([
			model({ modelName: "alpha", modifiedAtUtc: 1000, isReasoningCapable: false, isToolCapable: false }),
			model({ modelName: "zeta", modifiedAtUtc: 2000, isReasoningCapable: true, isToolCapable: true }),
		]);

		expect(capabilities).toEqual({
			isReasoningModel: true,
			isNativeReasoningModel: false,
			isToolCapable: true,
			isMultimodal: false,
		});
	});

	it("ignores non-chat models when resolving the default", () => {
		const capabilities = resolveLocalDefaultModelCapabilities([
			model({ modelName: "embed", kind: "Embedding", isSelected: true, isReasoningCapable: true, isToolCapable: true }),
			model({ modelName: "qwen3:8b", isReasoningCapable: true, isToolCapable: false }),
		]);

		expect(capabilities).toEqual({
			isReasoningModel: true,
			isNativeReasoningModel: false,
			isToolCapable: false,
			isMultimodal: false,
		});
	});

	it("excludes CodexOAuth provider entries from the resolved default", () => {
		const capabilities = resolveLocalDefaultModelCapabilities([
			model({ modelName: "codex", provider: "CodexOAuth", isSelected: true, isReasoningCapable: true, isToolCapable: true }),
			model({ modelName: "gemma:12b", isReasoningCapable: false, isToolCapable: false }),
		]);

		expect(capabilities).toEqual({
			isReasoningModel: false,
			isNativeReasoningModel: false,
			isToolCapable: false,
			isMultimodal: false,
		});
	});

	it("returns false capabilities when there are no chat models", () => {
		const capabilities = resolveLocalDefaultModelCapabilities([]);

		expect(capabilities).toEqual({
			isReasoningModel: false,
			isNativeReasoningModel: false,
			isToolCapable: false,
			isMultimodal: false,
		});
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

describe("external-provider containment (D10)", () => {
	const externalModel = (overrides: Partial<LocalModelDto> = {}): LocalModelDto =>
		model({
			modelName: "ext:unsloth-box/qwen3-27b",
			provider: "external",
			externalConnectionId: "unsloth-box",
			externalConnectionName: "Unsloth box",
			declaredLocality: "local",
			...overrides,
		});

	it("keeps external models out of the local chat list", () => {
		const options = toChatModelOptions([model({ modelName: "qwen3:8b" }), externalModel()], true);

		expect(options.map((option) => option.value)).toEqual(["qwen3:8b"]);
	});

	it("keeps declared-cloud external models out of the local chat list too", () => {
		const options = toChatModelOptions([externalModel({ declaredLocality: "cloud" })], true);

		expect(options).toHaveLength(0);
	});

	it("never offers an external model as a speculative draft model", () => {
		const options = toDraftModelOptions([model({ modelName: "qwen3:1.7b", kind: "Draft" }), externalModel()], true);

		expect(options.map((option) => option.value)).toEqual(["qwen3:1.7b"]);
	});

	it("never resolves an external model as the synthetic local default, even when it is the node default", () => {
		const models = [externalModel({ isSelected: true }), model({ modelName: "qwen3:8b" })];

		expect(resolveLocalDefaultModelName(models)).toBe("qwen3:8b");
	});

	it("resolves no local default when the node has only external models", () => {
		expect(resolveLocalDefaultModelName([externalModel({ isSelected: true })])).toBeUndefined();
	});

	it("carries the connection identity and declared locality onto external options", () => {
		const [option] = toExternalModelOptions([externalModel({ displayLabel: "Qwen3 27B" })], true);

		expect(option?.provider).toBe("external");
		expect(option?.externalConnectionId).toBe("unsloth-box");
		expect(option?.externalConnectionName).toBe("Unsloth box");
		expect(option?.declaredLocality).toBe("local");
		expect(option?.displayName).toBe("Qwen3 27B");
	});

	it("carries a declared graded-effort capability, and leaves it undeclared for every other provider", () => {
		const [graded] = toExternalModelOptions([externalModel({ isReasoningCapable: true, isReasoningEffortCapable: true })], true);
		const [binaryOnly] = toExternalModelOptions(
			[externalModel({ isReasoningCapable: true, isReasoningEffortCapable: false })],
			true,
		);

		expect(graded?.isReasoningEffortCapable).toBe(true);
		expect(binaryOnly?.isReasoningEffortCapable).toBe(false);

		// Undefined, not false: the backend reports null for a local model, and reading that as "no graded effort"
		// would demote every thinking model to the binary control.
		expect(toModelOption(model({ isReasoningCapable: true }), true).isReasoningEffortCapable).toBeUndefined();
	});

	it("leaves isCloud unset on external options so they never get the Codex effort vocabulary", () => {
		const options = toExternalModelOptions([externalModel({ declaredLocality: "cloud" })], true);

		expect(options[0]?.isCloud).toBeUndefined();
	});

	it("takes only external chat models, ignoring every other provider", () => {
		const options = toExternalModelOptions([model({ modelName: "qwen3:8b" }), externalModel()], true);

		expect(options.map((option) => option.value)).toEqual(["ext:unsloth-box/qwen3-27b"]);
	});

	it("groups external options one section per connection, in first-seen order", () => {
		const options = toExternalModelOptions(
			[
				externalModel({ modelName: "ext:a/one", externalConnectionId: "a", externalConnectionName: "Box A" }),
				externalModel({
					modelName: "ext:b/one",
					externalConnectionId: "b",
					externalConnectionName: "Gateway B",
					declaredLocality: "cloud",
				}),
				externalModel({ modelName: "ext:a/two", externalConnectionId: "a", externalConnectionName: "Box A" }),
			],
			true,
		);

		const groups = groupExternalModelOptions(options);

		expect(groups.map((group) => group.connectionId)).toEqual(["a", "b"]);
		expect(groups[0]?.items.map((option) => option.value)).toEqual(["ext:a/one", "ext:a/two"]);
		expect(groups[0]?.isDeclaredCloud).toBe(false);
		expect(groups[1]?.connectionName).toBe("Gateway B");
		expect(groups[1]?.isDeclaredCloud).toBe(true);
	});

	it("treats a missing or unrecognized locality as cloud, matching the backend's fail-closed direction", () => {
		const options = toExternalModelOptions([externalModel({ declaredLocality: null })], true);

		expect(groupExternalModelOptions(options)[0]?.isDeclaredCloud).toBe(true);
	});
});
