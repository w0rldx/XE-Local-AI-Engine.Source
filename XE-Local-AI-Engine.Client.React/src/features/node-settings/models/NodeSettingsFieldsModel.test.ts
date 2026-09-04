import { describe, expect, it } from "vitest";

import type { XeLocalAiEngineClientEndpointsNodeSettingsV1NodeSettingsResponse as NodeSettingsResponse } from "@/core/api/generated";
import {
	buildNodeSettingsRequest,
	newUsageRateRow,
	nodeSettingsFieldDefaults,
	kvCacheTypeSelectValues,
	restartGatedNodeSettingsFields,
	speculativeModeSelectValues,
	toNodeSettingsFieldBounds,
	toNodeSettingsFieldsForm,
	touchesRestartGatedField,
	toUsageRateRows,
	type UsageRateRow,
	validateLlamaCppTag,
	validateOllamaEndpoint,
	validateToolCapableModels,
	validateUsageRates,
} from "@/features/node-settings/models/NodeSettingsFieldsModel";

function rateRow(overrides: Partial<UsageRateRow>): UsageRateRow {
	return { id: "row-1", modelName: "gpt-5", inputPer1M: 1.25, outputPer1M: 10, ...overrides };
}

describe("NodeSettingsFieldsModel validators", () => {
	it("accepts a well-formed llama.cpp tag and rejects everything else", () => {
		expect(validateLlamaCppTag("b9692")).toEqual({ value: "b9692" });
		expect(validateLlamaCppTag("  b1  ")).toEqual({ value: "b1" });
		// Empty resolves to "leave unchanged" (no value, no error).
		expect(validateLlamaCppTag("")).toEqual({});
		expect(validateLlamaCppTag("9692")).toEqual({ error: "format" });
		expect(validateLlamaCppTag("b96.92")).toEqual({ error: "format" });
		expect(validateLlamaCppTag("../etc")).toEqual({ error: "format" });
	});

	it("accepts http/https Ollama endpoints and rejects non-URLs", () => {
		expect(validateOllamaEndpoint("http://127.0.0.1:11434")).toEqual({ value: "http://127.0.0.1:11434" });
		expect(validateOllamaEndpoint("https://ollama.local")).toEqual({ value: "https://ollama.local" });
		expect(validateOllamaEndpoint("")).toEqual({});
		expect(validateOllamaEndpoint("not-a-url")).toEqual({ error: "url" });
		expect(validateOllamaEndpoint("ftp://host")).toEqual({ error: "url" });
	});

	it("cleans tool-capable model lists and flags invalid entries", () => {
		expect(validateToolCapableModels(["qwen3:8b", "  ", "llama3"])).toEqual({
			value: ["qwen3:8b", "llama3"],
			hasInvalid: false,
		});
		// A non-empty name containing a control character (an escaped tab) is a hard validation error.
		expect(validateToolCapableModels(["bad\tname"]).hasInvalid).toBe(true);
	});
});

describe("validateUsageRates", () => {
	it("reduces valid rows into the keyed rate map", () => {
		const result = validateUsageRates([
			rateRow({ id: "a", modelName: "gpt-5", inputPer1M: 1.25, outputPer1M: 10 }),
			rateRow({ id: "b", modelName: "  claude  ", inputPer1M: "3", outputPer1M: "15" }),
		]);
		expect(result.hasInvalid).toBe(false);
		expect(result.map).toEqual({
			"gpt-5": { inputPer1M: 1.25, outputPer1M: 10 },
			// The name is trimmed and string cells are coerced to numbers.
			claude: { inputPer1M: 3, outputPer1M: 15 },
		});
	});

	it("silently drops a fully-empty row and keeps a valid one", () => {
		const result = validateUsageRates([
			rateRow({ id: "blank", modelName: "  ", inputPer1M: "", outputPer1M: "" }),
			rateRow({ id: "ok", modelName: "gpt-5", inputPer1M: 2, outputPer1M: 4 }),
		]);
		expect(result.hasInvalid).toBe(false);
		expect(result.map).toEqual({ "gpt-5": { inputPer1M: 2, outputPer1M: 4 } });
	});

	it("accepts a zero rate but rejects negatives", () => {
		expect(validateUsageRates([rateRow({ inputPer1M: 0, outputPer1M: 0 })])).toEqual({
			map: { "gpt-5": { inputPer1M: 0, outputPer1M: 0 } },
			hasInvalid: false,
		});
		expect(validateUsageRates([rateRow({ inputPer1M: -1, outputPer1M: 5 })]).hasInvalid).toBe(true);
	});

	it("rejects a row with a rate but no model name, and a row with a name but an empty rate", () => {
		expect(validateUsageRates([rateRow({ modelName: "", inputPer1M: 5, outputPer1M: 5 })]).hasInvalid).toBe(true);
		expect(validateUsageRates([rateRow({ modelName: "gpt-5", inputPer1M: "", outputPer1M: 5 })]).hasInvalid).toBe(true);
	});

	it("maps an empty table to a null map (clear signal)", () => {
		expect(validateUsageRates([]).map).toBeNull();
		expect(validateUsageRates([rateRow({ modelName: "", inputPer1M: "", outputPer1M: "" })]).map).toBeNull();
	});

	it("round-trips through toUsageRateRows preserving values (with a client id)", () => {
		const rows = toUsageRateRows({ "gpt-5": { inputPer1M: 1.25, outputPer1M: 10 } });
		expect(rows).toHaveLength(1);
		expect(rows[0]?.modelName).toBe("gpt-5");
		expect(rows[0]?.inputPer1M).toBe(1.25);
		expect(typeof rows[0]?.id).toBe("string");
		// A fresh add row carries its own id and empty cells.
		expect(newUsageRateRow()).toMatchObject({ modelName: "", inputPer1M: "", outputPer1M: "" });
	});

	it("maps incomplete server usage rates to editable empty values", () => {
		const rows = toUsageRateRows({ "gpt-5": {} });

		expect(rows[0]).toMatchObject({ modelName: "gpt-5", inputPer1M: "", outputPer1M: "" });
	});
});

describe("NodeSettingsFieldsModel mapping", () => {
	const response = {
		defaultModelName: "qwen3:8b",
		enableTools: false,
		toolCapableModels: ["qwen3:8b"],
		ollamaEndpoint: "http://127.0.0.1:11434",
		huggingFaceDefaultQuant: "Q4_K_M",
		llamaMaxLoadedProcesses: 5,
		minLlamaMaxLoadedProcesses: 1,
		maxAllowedLlamaMaxLoadedProcesses: 16,
		llamaIdleTimeToLiveSeconds: 600,
		maxResponseSizeMb: 12,
		recommendedLlamaCppTag: "b9692",
		keepModelWarmEnabled: true,
		keepModelWarmModelName: "qwen3:8b",
		keepModelWarmIntervalSeconds: 120,
		minKeepModelWarmIntervalSeconds: 10,
		maxAllowedKeepModelWarmIntervalSeconds: 1800,
	} satisfies NodeSettingsResponse;

	it("maps a response into the form, falling back to seed defaults for absent fields", () => {
		const form = toNodeSettingsFieldsForm(response);
		expect(form.defaultModelName).toBe("qwen3:8b");
		// Absent in the response -> reranking off (empty string).
		expect(form.rerankerModelName).toBe("");
		expect(form.enableTools).toBe(false);
		// Absent in the response -> seed default (StoredNodeSettings.DefaultCustomToolsEnabled, off).
		expect(form.customToolsEnabled).toBe(false);
		expect(form.toolCapableModels).toEqual(["qwen3:8b"]);
		expect(form.llamaMaxLoadedProcesses).toBe(5);
		expect(form.keepModelWarmEnabled).toBe(true);
		expect(form.keepModelWarmModelName).toBe("qwen3:8b");
		expect(form.keepModelWarmIntervalSeconds).toBe(120);
		// Absent in the response -> seed default (StoredNodeSettings.DefaultMaxPendingToolCallAgeMinutes).
		expect(form.maxPendingToolCallAgeMinutes).toBe(10);
	});

	it("seed defaults mirror the backend StoredNodeSettings Default* consts", () => {
		// Each value must equal the corresponding C# const so a stale-server render shows the real defaults and the
		// byte-cap fallbacks pass the backend `> 0` validator if ever saved.
		expect(nodeSettingsFieldDefaults.enableTools).toBe(true);
		expect(nodeSettingsFieldDefaults.customToolsEnabled).toBe(false);
		expect(nodeSettingsFieldDefaults.llamaMaxLoadedProcesses).toBe(3);
		expect(nodeSettingsFieldDefaults.llamaIdleTimeToLiveSeconds).toBe(900);
		expect(nodeSettingsFieldDefaults.keepModelWarmEnabled).toBe(false);
		expect(nodeSettingsFieldDefaults.keepModelWarmModelName).toBe("");
		expect(nodeSettingsFieldDefaults.keepModelWarmIntervalSeconds).toBe(300);
		expect(nodeSettingsFieldDefaults.maxResponseSizeMb).toBe(10);
		expect(nodeSettingsFieldDefaults.orchestrationIdleTimeoutSeconds).toBe(120); // DefaultOrchestrationIdleTimeoutSeconds
		expect(nodeSettingsFieldDefaults.agentHomePrepareTimeoutSeconds).toBe(900); // DefaultAgentHomePrepareTimeoutSeconds
		expect(nodeSettingsFieldDefaults.agentHomeCommandTimeoutSeconds).toBe(300); // DefaultAgentHomeCommandTimeoutSeconds
		expect(nodeSettingsFieldDefaults.agentHomeMaxSelectedFolderBytes).toBe(536870912);
		expect(nodeSettingsFieldDefaults.agentHomeMaxPatchBytes).toBe(52428800);
		expect(nodeSettingsFieldDefaults.maxPendingToolCallAgeMinutes).toBe(10); // DefaultMaxPendingToolCallAgeMinutes
		expect(nodeSettingsFieldDefaults.detachedGraceSeconds).toBe(300); // DefaultDetachedGraceSeconds
	});

	it("resolves bounds from the response with a hardcoded fallback", () => {
		const bounds = toNodeSettingsFieldBounds(response);
		expect(bounds.llamaMaxLoadedProcesses).toEqual({ min: 1, max: 16 });
		expect(bounds.keepModelWarmIntervalSeconds).toEqual({ min: 10, max: 1800 });
		// Absent server bound -> hardcoded fallback range.
		expect(bounds.maxResponseSizeMb).toEqual({ min: 1, max: 100 });
		expect(toNodeSettingsFieldBounds(undefined).keepModelWarmIntervalSeconds).toEqual({ min: 5, max: 3600 });
	});
});

describe("buildNodeSettingsRequest", () => {
	const baseline = toNodeSettingsFieldsForm(undefined);
	const bounds = toNodeSettingsFieldBounds(undefined);

	it("sends only changed fields", () => {
		const form = { ...baseline, defaultModelName: "qwen3:8b" };
		const { body, errors } = buildNodeSettingsRequest(form, baseline, bounds, false);
		expect(errors).toEqual({});
		expect(body).toEqual({ defaultModelName: "qwen3:8b" });
	});

	it("sends the custom-tools kill-switch only when toggled", () => {
		const form = { ...baseline, customToolsEnabled: true };
		const { body, errors } = buildNodeSettingsRequest(form, baseline, bounds, false);
		expect(errors).toEqual({});
		expect(body).toEqual({ customToolsEnabled: true });
	});

	it("rejects an out-of-range bounded number with a range error", () => {
		const form = { ...baseline, llamaMaxLoadedProcesses: 999 };
		const { body, errors } = buildNodeSettingsRequest(form, baseline, bounds, false);
		expect(errors["llamaMaxLoadedProcesses"]).toBe("range");
		expect(body.llamaMaxLoadedProcesses).toBeUndefined();
	});

	it("enables keep-warm with a trimmed model and bounded interval", () => {
		const form = {
			...baseline,
			keepModelWarmEnabled: true,
			keepModelWarmModelName: "  qwen3:8b  ",
			keepModelWarmIntervalSeconds: 120,
		};

		const { body, errors } = buildNodeSettingsRequest(form, baseline, bounds, false);

		expect(errors).toEqual({});
		expect(body).toEqual({
			keepModelWarmEnabled: true,
			keepModelWarmModelName: "qwen3:8b",
			keepModelWarmIntervalSeconds: 120,
		});
	});

	it("requires a selected model when keep-warm is enabled", () => {
		const form = { ...baseline, keepModelWarmEnabled: true, keepModelWarmModelName: "  " };

		const { body, errors } = buildNodeSettingsRequest(form, baseline, bounds, false);

		expect(errors["keepModelWarmModelName"]).toBe("requiredKeepWarmModel");
		expect(body.keepModelWarmModelName).toBeUndefined();
	});

	it("emits explicit false and no unchanged keep-warm fields when disabling", () => {
		const enabledBaseline = {
			...baseline,
			keepModelWarmEnabled: true,
			keepModelWarmModelName: "qwen3:8b",
			keepModelWarmIntervalSeconds: 120,
		};

		const { body, errors } = buildNodeSettingsRequest(
			{ ...enabledBaseline, keepModelWarmEnabled: false },
			enabledBaseline,
			bounds,
			false,
		);

		expect(errors).toEqual({});
		expect(body).toEqual({ keepModelWarmEnabled: false });
	});

	it("lets disabling win over an invalid interval draft", () => {
		const enabledBaseline = {
			...baseline,
			keepModelWarmEnabled: true,
			keepModelWarmModelName: "qwen3:8b",
			keepModelWarmIntervalSeconds: 120,
		};

		const { body, errors } = buildNodeSettingsRequest(
			{ ...enabledBaseline, keepModelWarmEnabled: false, keepModelWarmIntervalSeconds: "" },
			enabledBaseline,
			bounds,
			false,
		);

		expect(errors).toEqual({});
		expect(body).toEqual({ keepModelWarmEnabled: false });
	});

	it("uses an empty string to explicitly clear the selected keep-warm model while disabled", () => {
		const enabledBaseline = {
			...baseline,
			keepModelWarmEnabled: false,
			keepModelWarmModelName: "qwen3:8b",
		};

		const { body, errors } = buildNodeSettingsRequest(
			{ ...enabledBaseline, keepModelWarmModelName: "" },
			enabledBaseline,
			bounds,
			false,
		);

		expect(errors).toEqual({});
		expect(body.keepModelWarmModelName).toBe("");
	});

	it("rejects an out-of-range keep-warm interval", () => {
		const form = {
			...baseline,
			keepModelWarmEnabled: true,
			keepModelWarmModelName: "qwen3:8b",
			keepModelWarmIntervalSeconds: 3601,
		};

		const { body, errors } = buildNodeSettingsRequest(form, baseline, bounds, false);

		expect(errors["keepModelWarmIntervalSeconds"]).toBe("range");
		expect(body.keepModelWarmIntervalSeconds).toBeUndefined();
	});

	it("rejects keep-warm when the configured process cap leaves no non-pinned slot", () => {
		const form = {
			...baseline,
			llamaMaxLoadedProcesses: 1,
			keepModelWarmEnabled: true,
			keepModelWarmModelName: "qwen3:8b",
		};

		const { errors } = buildNodeSettingsRequest(form, baseline, bounds, false);

		expect(errors["llamaMaxLoadedProcesses"]).toBe("keepWarmCapacity");
	});

	it("rejects a keep-warm interval that is not below the idle TTL", () => {
		const form = {
			...baseline,
			llamaIdleTimeToLiveSeconds: 120,
			keepModelWarmEnabled: true,
			keepModelWarmModelName: "qwen3:8b",
			keepModelWarmIntervalSeconds: 120,
		};

		const { errors } = buildNodeSettingsRequest(form, baseline, bounds, false);

		expect(errors["keepModelWarmIntervalSeconds"]).toBe("belowIdleTtl");
	});

	it("rejects a malformed recommended tag", () => {
		const form = { ...baseline, recommendedLlamaCppTag: "not-a-tag" };
		const { errors } = buildNodeSettingsRequest(form, baseline, bounds, false);
		expect(errors["recommendedLlamaCppTag"]).toBe("format");
	});

	it("excludes developer-only fields when developer mode is off", () => {
		const form = { ...baseline, orchestrationIdleTimeoutSeconds: 99 };
		const offResult = buildNodeSettingsRequest(form, baseline, bounds, false);
		expect(offResult.body.orchestrationIdleTimeoutSeconds).toBeUndefined();
		const onResult = buildNodeSettingsRequest(form, baseline, bounds, true);
		expect(onResult.body.orchestrationIdleTimeoutSeconds).toBe(99);
	});

	it("sends a changed speculative mode and prompt-cache reuse (always shown, no developer gate)", () => {
		const form = { ...baseline, speculativeMode: "ngram-mod", chatCacheReuse: 512 };
		const { body, errors } = buildNodeSettingsRequest(form, baseline, bounds, false);
		expect(errors).toEqual({});
		expect(body.speculativeMode).toBe("ngram-mod");
		expect(body.chatCacheReuse).toBe(512);
	});

	it("accepts 0 as the disable value for prompt-cache reuse", () => {
		const form = { ...baseline, chatCacheReuse: 0 };
		const { body, errors } = buildNodeSettingsRequest(form, baseline, bounds, false);
		expect(errors["chatCacheReuse"]).toBeUndefined();
		expect(body.chatCacheReuse).toBe(0);
	});

	it("sends a changed KV cache type and rejects an unknown one", () => {
		const changed = buildNodeSettingsRequest({ ...baseline, kvCacheType: "q4_0" }, baseline, bounds, false);
		expect(changed.errors).toEqual({});
		expect(changed.body.kvCacheType).toBe("q4_0");

		// Unset means "leave it alone": an unchanged field is never sent, so the seeded options stay the provider default.
		const unchanged = buildNodeSettingsRequest({ ...baseline }, baseline, bounds, false);
		expect(unchanged.body.kvCacheType).toBeUndefined();

		const bogus = buildNodeSettingsRequest({ ...baseline, kvCacheType: "q5_1" }, baseline, bounds, false);
		expect(bogus.errors["kvCacheType"]).toBe("type");
		expect(bogus.body.kvCacheType).toBeUndefined();
	});

	it("marks the KV cache type as restart-gated", () => {
		// The seeded LlamaServerLaunchPolicyOptions is built once at host build, so a save needs a node restart.
		expect(restartGatedNodeSettingsFields.has("kvCacheType")).toBe(true);
		expect(kvCacheTypeSelectValues).toEqual(["f16", "q8_0", "q4_0"]);
		expect(nodeSettingsFieldDefaults.kvCacheType).toBe("q8_0");
	});

	it("rejects an unknown speculative mode", () => {
		const form = { ...baseline, speculativeMode: "totally-bogus" };
		const { errors } = buildNodeSettingsRequest(form, baseline, bounds, false);
		expect(errors["speculativeMode"]).toBe("mode");
	});

	it("requires a draft model for an external-draft mode", () => {
		const form = { ...baseline, speculativeMode: "draft-simple", speculativeDraftModelName: "" };
		const { body, errors } = buildNodeSettingsRequest(form, baseline, bounds, false);
		expect(errors["speculativeDraftModelName"]).toBe("required");
		// The mode itself is still valid, so no mode error — only the missing draft model blocks the save.
		expect(errors["speculativeMode"]).toBeUndefined();
		expect(body.speculativeDraftModelName).toBeUndefined();
	});

	it.each(["draft-dflash", "draft-dspark"])("offers %s as an external-draft mode that needs a draft model", (mode) => {
		const withoutDraft = { ...baseline, speculativeMode: mode, speculativeDraftModelName: "" };
		const missing = buildNodeSettingsRequest(withoutDraft, baseline, bounds, false);
		// The mode itself is accepted; only the missing second GGUF blocks the save.
		expect(missing.errors["speculativeMode"]).toBeUndefined();
		expect(missing.errors["speculativeDraftModelName"]).toBe("required");

		const withDraft = { ...baseline, speculativeMode: mode, speculativeDraftModelName: "my-draft", speculativeDraftMaxTokens: 15 };
		const saved = buildNodeSettingsRequest(withDraft, baseline, bounds, false);
		expect(saved.errors).toEqual({});
		expect(saved.body.speculativeMode).toBe(mode);
		expect(saved.body.speculativeDraftMaxTokens).toBe(15);
		expect(speculativeModeSelectValues).toContain(mode);
	});

	it("does NOT require a draft model for draft-mtp, whose drafter lives in the main model", () => {
		const form = { ...baseline, speculativeMode: "draft-mtp", speculativeDraftModelName: "" };
		const { body, errors } = buildNodeSettingsRequest(form, baseline, bounds, false);
		expect(errors).toEqual({});
		expect(body.speculativeMode).toBe("draft-mtp");
	});

	it("does NOT require a draft model for an ngram-* mode with no draft model", () => {
		const form = { ...baseline, speculativeMode: "ngram-mod", speculativeDraftModelName: "" };
		const { body, errors } = buildNodeSettingsRequest(form, baseline, bounds, false);
		expect(errors["speculativeDraftModelName"]).toBeUndefined();
		expect(body.speculativeMode).toBe("ngram-mod");
	});

	it("sends the draft model name and draft tokens for a draft-* mode", () => {
		const form = {
			...baseline,
			speculativeMode: "draft-simple",
			speculativeDraftModelName: "my-draft",
			speculativeDraftMaxTokens: 5,
		};
		const { body, errors } = buildNodeSettingsRequest(form, baseline, bounds, false);
		expect(errors).toEqual({});
		expect(body.speculativeMode).toBe("draft-simple");
		expect(body.speculativeDraftModelName).toBe("my-draft");
		expect(body.speculativeDraftMaxTokens).toBe(5);
	});

	it("rejects out-of-range draft tokens", () => {
		const form = { ...baseline, speculativeMode: "draft-simple", speculativeDraftModelName: "d", speculativeDraftMaxTokens: 99 };
		const { errors } = buildNodeSettingsRequest(form, baseline, bounds, false);
		expect(errors["speculativeDraftMaxTokens"]).toBe("range");
	});

	it("sends a changed reranker model name (free string, no developer gate)", () => {
		const form = { ...baseline, rerankerModelName: "  bge-reranker-v2-m3  " };
		const { body, errors } = buildNodeSettingsRequest(form, baseline, bounds, false);
		expect(errors).toEqual({});
		expect(body.rerankerModelName).toBe("bge-reranker-v2-m3");
	});

	it("sends an empty string when the reranker is switched Off", () => {
		const withModel = { ...baseline, rerankerModelName: "bge-reranker-v2-m3" };
		const off = { ...withModel, rerankerModelName: "" };
		const { body, errors } = buildNodeSettingsRequest(off, withModel, bounds, false);
		expect(errors).toEqual({});
		expect(body.rerankerModelName).toBe("");
	});

	it("sends a changed fast model for automatic reasoning effort, and an empty string for Off", () => {
		const form = { ...baseline, autoEffortFastModelName: "  qwen3-1.7b  " };
		const { body, errors } = buildNodeSettingsRequest(form, baseline, bounds, false);
		expect(errors).toEqual({});
		expect(body.autoEffortFastModelName).toBe("qwen3-1.7b");

		const withModel = { ...baseline, autoEffortFastModelName: "qwen3-1.7b" };
		const off = { ...withModel, autoEffortFastModelName: "" };
		expect(buildNodeSettingsRequest(off, withModel, bounds, false).body.autoEffortFastModelName).toBe("");
	});

	it("does not restart-gate the fast model for automatic reasoning effort", () => {
		// The dispatcher reads it per send, so a save applies to the very next turn — telling the operator to restart
		// would be wrong, and would train them to ignore the hint on the fields that do need one.
		expect(restartGatedNodeSettingsFields.has("autoEffortFastModelName")).toBe(false);
		expect(restartGatedNodeSettingsFields.has("rerankerModelName")).toBe(true);
	});

	it("sends the usage-rate map when a rate row is added (no developer gate)", () => {
		const form = { ...baseline, usageRates: [rateRow({ id: "a", modelName: "gpt-5", inputPer1M: 1.25, outputPer1M: 10 })] };
		const { body, errors } = buildNodeSettingsRequest(form, baseline, bounds, false);
		expect(errors).toEqual({});
		expect(body.usageRates).toEqual({ "gpt-5": { inputPer1M: 1.25, outputPer1M: 10 } });
	});

	it("sends null when the rate table is emptied (null-preserving clear)", () => {
		const withRates = { ...baseline, usageRates: [rateRow({ id: "a", modelName: "gpt-5", inputPer1M: 1, outputPer1M: 2 })] };
		const cleared = { ...withRates, usageRates: [] as UsageRateRow[] };
		const { body, errors } = buildNodeSettingsRequest(cleared, withRates, bounds, false);
		expect(errors).toEqual({});
		expect(body.usageRates).toBeNull();
	});

	it("does not send usageRates when unchanged (ignoring row order and client ids)", () => {
		const rowsA = [
			rateRow({ id: "a", modelName: "gpt-5", inputPer1M: 1, outputPer1M: 2 }),
			rateRow({ id: "b", modelName: "claude", inputPer1M: 3, outputPer1M: 4 }),
		];
		// Same rates, reversed order and different client ids -> no change.
		const rowsB = [
			rateRow({ id: "x", modelName: "claude", inputPer1M: 3, outputPer1M: 4 }),
			rateRow({ id: "y", modelName: "gpt-5", inputPer1M: 1, outputPer1M: 2 }),
		];
		const { body } = buildNodeSettingsRequest(
			{ ...baseline, usageRates: rowsB },
			{ ...baseline, usageRates: rowsA },
			bounds,
			false,
		);
		expect(body.usageRates).toBeUndefined();
	});

	it("rejects an invalid rate row with a rate error and never sends the map", () => {
		const form = { ...baseline, usageRates: [rateRow({ id: "a", modelName: "gpt-5", inputPer1M: -5, outputPer1M: 2 })] };
		const { body, errors } = buildNodeSettingsRequest(form, baseline, bounds, false);
		expect(errors["usageRates"]).toBe("rate");
		expect(body.usageRates).toBeUndefined();
	});
});

describe("detachedGraceSeconds", () => {
	const baseline = toNodeSettingsFieldsForm(undefined);
	const bounds = toNodeSettingsFieldBounds(undefined);

	it("round-trips through the form and back into the request body", () => {
		const form = { ...baseline, detachedGraceSeconds: 120 };
		const { body, errors } = buildNodeSettingsRequest(form, baseline, bounds, true);
		expect(errors).toEqual({});
		expect(body.detachedGraceSeconds).toBe(120);
	});

	it("sends an explicit 0 rather than treating it as unset", () => {
		// 0 is the "never cancel" sentinel, so it must survive the changed-fields filter as a real edit.
		const form = { ...baseline, detachedGraceSeconds: 0 };
		const { body, errors } = buildNodeSettingsRequest(form, baseline, bounds, true);
		expect(errors).toEqual({});
		expect(body.detachedGraceSeconds).toBe(0);
	});

	it("reads the value and its bounds off the response, falling back to the seed default", () => {
		expect(toNodeSettingsFieldsForm({ maxMessageRequestTimeoutSeconds: 300 } as NodeSettingsResponse).detachedGraceSeconds).toBe(300);
		expect(
			toNodeSettingsFieldsForm({ maxMessageRequestTimeoutSeconds: 300, detachedGraceSeconds: 45 } as NodeSettingsResponse)
				.detachedGraceSeconds,
		).toBe(45);
		expect(bounds.detachedGraceSeconds).toEqual({ min: 0, max: 86400 });
		expect(
			toNodeSettingsFieldBounds({
				maxMessageRequestTimeoutSeconds: 300,
				minDetachedGraceSeconds: 10,
				maxAllowedDetachedGraceSeconds: 600,
			} as NodeSettingsResponse).detachedGraceSeconds,
		).toEqual({ min: 10, max: 600 });
	});

	it("rejects a negative grace with a range error", () => {
		const form = { ...baseline, detachedGraceSeconds: -1 };
		const { body, errors } = buildNodeSettingsRequest(form, baseline, bounds, true);
		expect(errors["detachedGraceSeconds"]).toBe("range");
		expect(body.detachedGraceSeconds).toBeUndefined();
	});

	it("is developer-gated like its MaxPendingToolCallAge sibling", () => {
		const form = { ...baseline, detachedGraceSeconds: 120 };
		expect(buildNodeSettingsRequest(form, baseline, bounds, false).body.detachedGraceSeconds).toBeUndefined();
	});
});

describe("restart-gated fields", () => {
	const baseline = { ...nodeSettingsFieldDefaults };
	const bounds = toNodeSettingsFieldBounds(undefined);

	it("flags a body that changed a seeded-once field", () => {
		// chatCacheReuse is read once into LlamaServerSupervisorOptions at composition.
		const { body } = buildNodeSettingsRequest({ ...baseline, chatCacheReuse: 512 }, baseline, bounds, false);
		expect(touchesRestartGatedField(body)).toBe(true);
	});

	it("does not flag a body that only changed live fields", () => {
		// enableTools + the AgentHome caps are re-read per call, so a save is effective immediately.
		const { body } = buildNodeSettingsRequest(
			{ ...baseline, enableTools: false, agentHomeMaxPatchBytes: 1024 },
			baseline,
			bounds,
			true,
		);
		expect(Object.keys(body).length).toBeGreaterThan(0);
		expect(touchesRestartGatedField(body)).toBe(false);
	});

	it("does not flag an empty (nothing changed) body", () => {
		expect(touchesRestartGatedField({})).toBe(false);
	});

	it("never lists a field that is read live on the backend", () => {
		// Regression guard for the labelling rule: mislabelling a live field would tell operators to restart for nothing.
		for (const live of [
			"enableTools",
			"customToolsEnabled",
			"toolCapableModels",
			"keepModelWarmEnabled",
			"keepModelWarmModelName",
			"keepModelWarmIntervalSeconds",
			"agentHomePrepareTimeoutSeconds",
			"agentHomeCommandTimeoutSeconds",
			"agentHomeMaxSelectedFolderBytes",
			"agentHomeMaxPatchBytes",
			"detachedGraceSeconds",
			"usageRates",
			"recommendedLlamaCppTag",
		] as const) {
			expect(restartGatedNodeSettingsFields.has(live)).toBe(false);
		}
	});
});
