import { describe, expect, it } from "vitest";

import type { XeLocalAiEngineClientEndpointsNodeSettingsV1NodeSettingsResponse as NodeSettingsResponse } from "@/core/api/generated";
import {
	applyExternalAccessPreset,
	buildNodeSettingsRequest,
	featureSwitchFields,
	isExternalAccessBooleanField,
	lowersSandboxSecurityProfile,
	newUsageRateRow,
	nodeSettingsFieldDefaults,
	kvCacheTypeSelectValues,
	nodeSettingsDisplayScale,
	nodeSettingsScaleOf,
	restartGatedNodeSettingsFields,
	shippedDefaults,
	speculativeModeSelectValues,
	summarizePendingChanges,
	toNodeSettingsFieldBounds,
	toNodeSettingsFieldsForm,
	touchesRestartGatedField,
	toUsageRateRows,
	turnsOnExecutionPreviews,
	type UsageRateRow,
	validateOptionalHttpUrl,
	validateToolCapableModels,
	validateUsageRates,
} from "@/features/node-settings/models/NodeSettingsFieldsModel";

function rateRow(overrides: Partial<UsageRateRow>): UsageRateRow {
	return { id: "row-1", modelName: "gpt-5", inputPer1M: 1.25, outputPer1M: 10, ...overrides };
}

describe("NodeSettingsFieldsModel validators", () => {
	it("accepts http/https Ollama endpoints and rejects non-URLs", () => {
		expect(validateOptionalHttpUrl("http://127.0.0.1:11434")).toEqual({ value: "http://127.0.0.1:11434" });
		expect(validateOptionalHttpUrl("https://ollama.local")).toEqual({ value: "https://ollama.local" });
		expect(validateOptionalHttpUrl("")).toEqual({});
		expect(validateOptionalHttpUrl("not-a-url")).toEqual({ error: "url" });
		expect(validateOptionalHttpUrl("ftp://host")).toEqual({ error: "url" });
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
		// Absent in the response -> seed default (StoredNodeSettings.DefaultToolRelevanceEnabled, off).
		expect(form.toolRelevanceEnabled).toBe(false);
		expect(form.toolCapableModels).toEqual(["qwen3:8b"]);
		expect(form.llamaMaxLoadedProcesses).toBe(5);
		// 600 s on the wire, edited in minutes.
		expect(form.llamaIdleTimeToLiveSeconds).toBe(10);
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
		expect(nodeSettingsFieldDefaults.toolRelevanceEnabled).toBe(false); // DefaultToolRelevanceEnabled
		expect(nodeSettingsFieldDefaults.webAccessEnabled).toBe(false); // DefaultWebAccessEnabled
		expect(nodeSettingsFieldDefaults.llamaMaxLoadedProcesses).toBe(3);
		// Scaled fields are in their display unit: 900 s, 900 s, 300 s, 536870912 B, 52428800 B on the wire.
		expect(nodeSettingsFieldDefaults.llamaIdleTimeToLiveSeconds).toBe(15);
		expect(nodeSettingsFieldDefaults.keepModelWarmEnabled).toBe(false);
		expect(nodeSettingsFieldDefaults.keepModelWarmModelName).toBe("");
		expect(nodeSettingsFieldDefaults.keepModelWarmIntervalSeconds).toBe(300);
		expect(nodeSettingsFieldDefaults.maxResponseSizeMb).toBe(10);
		expect(nodeSettingsFieldDefaults.orchestrationIdleTimeoutSeconds).toBe(120); // DefaultOrchestrationIdleTimeoutSeconds
		expect(nodeSettingsFieldDefaults.agentHomePrepareTimeoutSeconds).toBe(15); // DefaultAgentHomePrepareTimeoutSeconds
		expect(nodeSettingsFieldDefaults.agentHomeCommandTimeoutSeconds).toBe(5); // DefaultAgentHomeCommandTimeoutSeconds
		expect(nodeSettingsFieldDefaults.agentHomeMaxSelectedFolderBytes).toBe(512);
		expect(nodeSettingsFieldDefaults.agentHomeMaxPatchBytes).toBe(50);
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

	it("sends the tool-relevance switch only when toggled", () => {
		const form = { ...baseline, toolRelevanceEnabled: true };
		const { body, errors } = buildNodeSettingsRequest(form, baseline, bounds, false);
		expect(errors).toEqual({});
		expect(body).toEqual({ toolRelevanceEnabled: true });
	});

	it("sends a cleared Ollama endpoint as an empty string, the explicit clear back to the default", () => {
		const configured = { ...baseline, ollamaEndpoint: "http://127.0.0.1:11500" };
		const { body, errors } = buildNodeSettingsRequest({ ...configured, ollamaEndpoint: "  " }, configured, bounds, false);
		expect(errors).toEqual({});
		expect(body).toEqual({ ollamaEndpoint: "" });
	});

	it("sends the web-access switch only when toggled, and never restart-gates it", () => {
		const { body, errors } = buildNodeSettingsRequest({ ...baseline, webAccessEnabled: true }, baseline, bounds, false);
		expect(errors).toEqual({});
		expect(body).toEqual({ webAccessEnabled: true });
		expect(restartGatedNodeSettingsFields.has("webAccessEnabled")).toBe(false);
		expect(restartGatedNodeSettingsFields.has("webSearchSearxngUrl")).toBe(false);
	});

	it("sends a trimmed absolute SearXNG URL and rejects a relative one", () => {
		const valid = buildNodeSettingsRequest(
			{ ...baseline, webSearchSearxngUrl: " http://localhost:8888 " },
			baseline,
			bounds,
			false,
		);
		expect(valid.errors).toEqual({});
		expect(valid.body).toEqual({ webSearchSearxngUrl: "http://localhost:8888" });

		const relative = buildNodeSettingsRequest({ ...baseline, webSearchSearxngUrl: "/search" }, baseline, bounds, false);
		expect(relative.errors).toEqual({ webSearchSearxngUrl: "url" });
		expect(relative.body.webSearchSearxngUrl).toBeUndefined();
	});

	it("sends an empty string to clear the SearXNG URL, because null would keep the stored one", () => {
		const stored = { ...baseline, webSearchSearxngUrl: "https://searx.example" };
		const { body, errors } = buildNodeSettingsRequest({ ...stored, webSearchSearxngUrl: "  " }, stored, bounds, false);
		expect(errors).toEqual({});
		expect(body).toEqual({ webSearchSearxngUrl: "" });
	});

	it("omits the tool-relevance switch when it is unchanged", () => {
		const { body } = buildNodeSettingsRequest({ ...baseline }, baseline, bounds, false);
		expect(body.toolRelevanceEnabled).toBeUndefined();
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
			// 2 minutes = 120 s, the same as the warm interval.
			llamaIdleTimeToLiveSeconds: 2,
			keepModelWarmEnabled: true,
			keepModelWarmModelName: "qwen3:8b",
			keepModelWarmIntervalSeconds: 120,
		};

		const { errors } = buildNodeSettingsRequest(form, baseline, bounds, false);

		expect(errors["keepModelWarmIntervalSeconds"]).toBe("belowIdleTtl");
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

		const withDraft = {
			...baseline,
			speculativeMode: mode,
			speculativeDraftModelName: "my-draft",
			speculativeDraftMaxTokens: 15,
		};
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
		expect(toNodeSettingsFieldsForm({ maxMessageRequestTimeoutSeconds: 300 } as NodeSettingsResponse).detachedGraceSeconds).toBe(
			300,
		);
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
			"toolRelevanceEnabled",
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
			"uiMode",
			"voiceFeatureEnabled",
			"defaultVoiceProfile",
		] as const) {
			expect(restartGatedNodeSettingsFields.has(live)).toBe(false);
		}
	});
});

describe("external access", () => {
	const baseline = toNodeSettingsFieldsForm(undefined);
	const bounds = toNodeSettingsFieldBounds(undefined);

	it("seeds the external-access booleans from the response and defaults them to true when absent", () => {
		const seeded = toNodeSettingsFieldsForm({
			externalAccessProfile: "offline",
			autoCheckApplicationUpdates: false,
			autoCheckRuntimeUpdates: false,
			autoProvisionFirstRunModel: false,
		} as NodeSettingsResponse);
		expect(seeded.externalAccessProfile).toBe("offline");
		expect(seeded.autoCheckApplicationUpdates).toBe(false);
		expect(seeded.autoCheckRuntimeUpdates).toBe(false);
		expect(seeded.autoProvisionFirstRunModel).toBe(false);

		const absent = toNodeSettingsFieldsForm({} as NodeSettingsResponse);
		expect(absent.autoCheckApplicationUpdates).toBe(true);
		expect(absent.autoCheckRuntimeUpdates).toBe(true);
		expect(absent.autoProvisionFirstRunModel).toBe(true);
	});

	it("renders an absent external-access profile as an empty form value", () => {
		// A corrupted settings file reads back as null; the form must not claim a profile the node never chose. The
		// seed default is the same sentinel, so a response-less form and an empty response agree on "undecided".
		expect(toNodeSettingsFieldsForm({} as NodeSettingsResponse).externalAccessProfile).toBe("");
		expect(nodeSettingsFieldDefaults.externalAccessProfile).toBe("");
		expect(toNodeSettingsFieldsForm(undefined).externalAccessProfile).toBe("");
	});

	it("displays a server-written custom profile without rewriting it", () => {
		const seeded = toNodeSettingsFieldsForm({ externalAccessProfile: "custom" } as NodeSettingsResponse);
		expect(seeded.externalAccessProfile).toBe("custom");
	});

	it("applies the recommended preset as profile plus all three booleans true", () => {
		const offline = {
			...baseline,
			autoCheckApplicationUpdates: false,
			autoCheckRuntimeUpdates: false,
			autoProvisionFirstRunModel: false,
		};
		const applied = applyExternalAccessPreset(offline, "recommended");
		expect(applied.externalAccessProfile).toBe("recommended");
		expect(applied.autoCheckApplicationUpdates).toBe(true);
		expect(applied.autoCheckRuntimeUpdates).toBe(true);
		expect(applied.autoProvisionFirstRunModel).toBe(true);
	});

	it("applies the offline preset as profile plus all three booleans false", () => {
		const applied = applyExternalAccessPreset(baseline, "offline");
		expect(applied.externalAccessProfile).toBe("offline");
		expect(applied.autoCheckApplicationUpdates).toBe(false);
		expect(applied.autoCheckRuntimeUpdates).toBe(false);
		expect(applied.autoProvisionFirstRunModel).toBe(false);
	});

	it("sends only the changed boolean and never a client-computed custom profile", () => {
		const form = { ...baseline, autoCheckRuntimeUpdates: false };
		const { body, errors } = buildNodeSettingsRequest(form, baseline, bounds, false);
		expect(errors).toEqual({});
		expect(body).toEqual({ autoCheckRuntimeUpdates: false });
		expect(body.externalAccessProfile).toBeUndefined();
	});

	it("sends only the profile name when a preset is pending", () => {
		const form = applyExternalAccessPreset(baseline, "offline");
		const { body, errors } = buildNodeSettingsRequest(form, baseline, bounds, false, "offline");
		expect(errors).toEqual({});
		expect(body).toEqual({ externalAccessProfile: "offline" });
	});

	it("sends booleans only when a switch is edited after a preset click", () => {
		// Offline node -> click Recommended -> turn provisioning back off. Sending the profile too would let the server
		// honour the preset and discard the operator's switch, which is the failing case this branch exists to prevent.
		const offlineBaseline = {
			...baseline,
			externalAccessProfile: "offline",
			autoCheckApplicationUpdates: false,
			autoCheckRuntimeUpdates: false,
			autoProvisionFirstRunModel: false,
		};
		const afterPreset = applyExternalAccessPreset(offlineBaseline, "recommended");
		const afterHandEdit = { ...afterPreset, autoProvisionFirstRunModel: false };

		const { body, errors } = buildNodeSettingsRequest(afterHandEdit, offlineBaseline, bounds, false, null);

		expect(errors).toEqual({});
		// Only the two switches that actually differ from the stored state travel; provisioning was already off, so it
		// needs no wire member to stay off. What matters is that NO profile goes with them — the server then stamps
		// "custom" and the operator's hand edit survives instead of being overwritten by the preset's triple.
		expect(body).toEqual({
			autoCheckApplicationUpdates: true,
			autoCheckRuntimeUpdates: true,
		});
		expect(body.externalAccessProfile).toBeUndefined();
		expect(afterHandEdit.autoProvisionFirstRunModel).toBe(false);
	});

	it("never lists an external-access field as restart-gated", () => {
		// Both check services read the setting live; the ACTION runs once per process, which is why the copy says "at
		// startup" and why nothing here joins the restart set.
		for (const field of [
			"externalAccessProfile",
			"autoCheckApplicationUpdates",
			"autoCheckRuntimeUpdates",
			"autoProvisionFirstRunModel",
		] as const) {
			expect(restartGatedNodeSettingsFields.has(field)).toBe(false);
		}
		expect(
			touchesRestartGatedField({
				externalAccessProfile: "offline",
				autoCheckApplicationUpdates: false,
				autoCheckRuntimeUpdates: false,
				autoProvisionFirstRunModel: false,
			}),
		).toBe(false);
	});
});

describe("display units", () => {
	const bounds = toNodeSettingsFieldBounds(undefined);

	it("round-trips seconds shown as minutes and bytes shown as MB back to the unchanged wire values", () => {
		const response = {
			llamaIdleTimeToLiveSeconds: 45,
			agentHomePrepareTimeoutSeconds: 100,
			agentHomeCommandTimeoutSeconds: 300,
			agentHomeMaxSelectedFolderBytes: 1_000_000,
			agentHomeMaxPatchBytes: 52_428_800,
			llamaChatHttpTimeoutSeconds: 90,
			imageIdleTimeToLiveSeconds: 900,
			agentHomeMaxRunSeconds: 610,
			huggingFaceDiskMarginBytes: 1_500_000_000,
			benchmarkKldCacheMaxBytes: 3_000_000_000,
			agentHomeRunRetentionMaxTotalBytes: 5_000_000_000,
		} satisfies NodeSettingsResponse;
		const form = toNodeSettingsFieldsForm(response);

		expect(form.llamaIdleTimeToLiveSeconds).toBe(0.75);
		expect(form.agentHomeCommandTimeoutSeconds).toBe(5);
		expect(form.agentHomeMaxPatchBytes).toBe(50);
		// An untouched field is never sent, so a value that is not a whole display unit survives unchanged.
		expect(buildNodeSettingsRequest(form, form, bounds, true).body).toEqual({});

		// Each display value multiplied back is the original wire value.
		for (const [field, scale] of Object.entries(nodeSettingsDisplayScale)) {
			const key = field as keyof typeof nodeSettingsDisplayScale;
			expect(Math.round(Number(form[key]) * scale)).toBe(response[key]);
		}
	});

	it("sends an edited display value in wire units, rounded to a whole unit", () => {
		const baseline = toNodeSettingsFieldsForm(undefined);
		const form = {
			...baseline,
			llamaIdleTimeToLiveSeconds: 20,
			agentHomePrepareTimeoutSeconds: 1.67,
			agentHomeMaxSelectedFolderBytes: 1024,
			agentHomeMaxPatchBytes: "0.5",
		};

		const { body, errors } = buildNodeSettingsRequest(form, baseline, bounds, true);

		expect(errors).toEqual({});
		expect(body).toMatchObject({
			llamaIdleTimeToLiveSeconds: 1200,
			agentHomePrepareTimeoutSeconds: 100,
			agentHomeMaxSelectedFolderBytes: 1_073_741_824,
			agentHomeMaxPatchBytes: 524_288,
		});
	});

	it("validates the converted value against the wire bounds and never turns a blank into zero", () => {
		const baseline = toNodeSettingsFieldsForm(undefined);
		// 0.25 min = 15 s, below the 30 s floor.
		const tooShort = buildNodeSettingsRequest({ ...baseline, llamaIdleTimeToLiveSeconds: 0.25 }, baseline, bounds, false);
		expect(tooShort.errors["llamaIdleTimeToLiveSeconds"]).toBe("range");

		const blank = buildNodeSettingsRequest({ ...baseline, agentHomeMaxPatchBytes: "" }, baseline, bounds, true);
		expect(blank.errors["agentHomeMaxPatchBytes"]).toBe("positive");
	});
});

describe("sandbox security profile", () => {
	const bounds = toNodeSettingsFieldBounds(undefined);
	const high = toNodeSettingsFieldsForm({ sandboxSecurityProfile: "high" } as NodeSettingsResponse);
	const low = toNodeSettingsFieldsForm({ sandboxSecurityProfile: "low" } as NodeSettingsResponse);

	it("seeds the profile from the response and leaves an absent one undecided", () => {
		expect(high.sandboxSecurityProfile).toBe("high");
		expect(toNodeSettingsFieldsForm({} as NodeSettingsResponse).sandboxSecurityProfile).toBe("");
	});

	it("flags only the high-to-low change for confirmation", () => {
		const lowered = buildNodeSettingsRequest({ ...high, sandboxSecurityProfile: "low" }, high, bounds, false);
		expect(lowered.body).toEqual({ sandboxSecurityProfile: "low" });
		expect(lowersSandboxSecurityProfile(lowered.body)).toBe(true);

		const raised = buildNodeSettingsRequest({ ...low, sandboxSecurityProfile: "high" }, low, bounds, false);
		expect(raised.body).toEqual({ sandboxSecurityProfile: "high" });
		expect(lowersSandboxSecurityProfile(raised.body)).toBe(false);

		// Already low and untouched: the body does not carry it, so an unrelated save is not asked about it.
		expect(
			lowersSandboxSecurityProfile(buildNodeSettingsRequest({ ...low, computeEnabled: true }, low, bounds, false).body),
		).toBe(false);
	});

	it("never sends a literal other than low or high", () => {
		const pending = toNodeSettingsFieldsForm({ sandboxSecurityProfile: "pending" } as NodeSettingsResponse);
		expect(buildNodeSettingsRequest({ ...high, sandboxSecurityProfile: "pending" }, high, bounds, false).body).toEqual({});
		expect(buildNodeSettingsRequest({ ...pending, sandboxSecurityProfile: "" }, pending, bounds, false).body).toEqual({});
	});

	it("is read live, so it never asks for a restart", () => {
		expect(restartGatedNodeSettingsFields.has("sandboxSecurityProfile")).toBe(false);
	});
});

describe("unified node fields and the pending-change summary", () => {
	const baseline = toNodeSettingsFieldsForm({ uiMode: "advanced", voiceFeatureEnabled: false, defaultVoiceProfile: "a" });
	const bounds = toNodeSettingsFieldBounds(undefined);

	it("sends uiMode and the voice fields through the one save body", () => {
		const form = { ...baseline, uiMode: "simple", voiceFeatureEnabled: true, defaultVoiceProfile: "b" };
		const { body } = buildNodeSettingsRequest(form, baseline, bounds, false);
		expect(body).toEqual({ uiMode: "simple", voiceFeatureEnabled: true, defaultVoiceProfile: "b" });
		expect(touchesRestartGatedField(body)).toBe(false);
	});

	it("counts sendable changes, invalid edits and the restart-gated subset", () => {
		const form = { ...baseline, uiMode: "simple", chatCacheReuse: 512, llamaMaxLoadedProcesses: "abc" };
		const result = buildNodeSettingsRequest(form, baseline, bounds, false);

		const summary = summarizePendingChanges(result, form, baseline);

		expect([...summary.changed].sort()).toEqual(["chatCacheReuse", "llamaMaxLoadedProcesses", "uiMode"]);
		expect([...summary.restartRequired].sort()).toEqual(["chatCacheReuse", "llamaMaxLoadedProcesses"]);
	});

	it("reports nothing for an untouched draft", () => {
		const summary = summarizePendingChanges(buildNodeSettingsRequest(baseline, baseline, bounds, true), baseline, baseline);
		expect(summary.changed).toEqual([]);
		expect(summary.restartRequired).toEqual([]);
	});
});

describe("curated tunables", () => {
	const bounds = toNodeSettingsFieldBounds(undefined);

	it("maps the prompt-cache RAM to Automatic / Off / Custom and back, resetting to automatic with -1", () => {
		const auto = toNodeSettingsFieldsForm({ llamaChatCacheRamMiB: null });
		const off = toNodeSettingsFieldsForm({ llamaChatCacheRamMiB: 0 });
		const custom = toNodeSettingsFieldsForm({ llamaChatCacheRamMiB: 2048 });
		expect(auto.llamaChatCacheRamMode).toBe("auto");
		expect(off.llamaChatCacheRamMode).toBe("off");
		expect(custom).toMatchObject({ llamaChatCacheRamMode: "custom", llamaChatCacheRamMiB: 2048 });

		// null on the wire means "keep", so returning to automatic must send the -1 reset sentinel.
		expect(buildNodeSettingsRequest({ ...custom, llamaChatCacheRamMode: "auto" }, custom, bounds, false).body).toEqual({
			llamaChatCacheRamMiB: -1,
		});
		expect(buildNodeSettingsRequest({ ...auto, llamaChatCacheRamMode: "off" }, auto, bounds, false).body).toEqual({
			llamaChatCacheRamMiB: 0,
		});
		expect(
			buildNodeSettingsRequest({ ...off, llamaChatCacheRamMode: "custom", llamaChatCacheRamMiB: 4096 }, off, bounds, false).body,
		).toEqual({ llamaChatCacheRamMiB: 4096 });
		// Touching the mode and returning to the same meaning sends nothing.
		expect(buildNodeSettingsRequest({ ...custom }, custom, bounds, false).body).toEqual({});
		expect(touchesRestartGatedField({ llamaChatCacheRamMiB: -1 })).toBe(true);
	});

	it("refuses a custom prompt-cache size that is blank or zero", () => {
		const auto = toNodeSettingsFieldsForm({ llamaChatCacheRamMiB: null });
		for (const size of ["", 0]) {
			const { body, errors } = buildNodeSettingsRequest(
				{ ...auto, llamaChatCacheRamMode: "custom", llamaChatCacheRamMiB: size },
				auto,
				bounds,
				false,
			);
			expect(errors["llamaChatCacheRamMiB"]).toBe("range");
			expect(body.llamaChatCacheRamMiB).toBeUndefined();
		}
	});

	it("rejects a default knowledge result count above the maximum", () => {
		const baseline = toNodeSettingsFieldsForm(undefined);
		const invalid = buildNodeSettingsRequest(
			{ ...baseline, knowledgeSearchDefaultResults: 12, knowledgeSearchMaxResults: 10 },
			baseline,
			bounds,
			false,
		);
		expect(invalid.errors["knowledgeSearchDefaultResults"]).toBe("defaultAboveMax");

		const valid = buildNodeSettingsRequest({ ...baseline, knowledgeSearchMaxResults: 10 }, baseline, bounds, false);
		expect(valid.errors).toEqual({});
		expect(valid.body).toEqual({ knowledgeSearchMaxResults: 10 });
	});

	it("keeps the AgentHome run limit at or above the command timeout, developer-gated", () => {
		const baseline = toNodeSettingsFieldsForm(undefined);
		// 4 minutes run limit < 5 minute command timeout.
		const form = { ...baseline, agentHomeMaxRunSeconds: 4 };
		expect(buildNodeSettingsRequest(form, baseline, bounds, true).errors["agentHomeMaxRunSeconds"]).toBe("belowCommandTimeout");
		expect(buildNodeSettingsRequest(form, baseline, bounds, false).errors).toEqual({});
		expect(buildNodeSettingsRequest({ ...baseline, agentHomeMaxRunSeconds: 20 }, baseline, bounds, true).body).toEqual({
			agentHomeMaxRunSeconds: 1200,
		});
	});

	it("sends each curated tunable in wire units and classifies restart versus live", () => {
		const baseline = toNodeSettingsFieldsForm(undefined);
		const form = {
			...baseline,
			llamaChatHttpTimeoutSeconds: 30,
			imageIdleTimeToLiveSeconds: 5,
			webFetchTimeoutSeconds: 40,
			huggingFaceDiskMarginBytes: 2,
			containerRuntimeSelection: "docker",
			speculativeDraftGpuLayers: 99,
		};

		const { body, errors } = buildNodeSettingsRequest(form, baseline, bounds, false);

		expect(errors).toEqual({});
		expect(body).toEqual({
			llamaChatHttpTimeoutSeconds: 1800,
			imageIdleTimeToLiveSeconds: 300,
			webFetchTimeoutSeconds: 40,
			huggingFaceDiskMarginBytes: 2 * 1024 * 1024 * 1024,
			containerRuntimeSelection: "docker",
			speculativeDraftGpuLayers: 99,
		});
		expect(restartGatedNodeSettingsFields.has("llamaChatHttpTimeoutSeconds")).toBe(true);
		expect(restartGatedNodeSettingsFields.has("huggingFaceDiskMarginBytes")).toBe(true);
		for (const live of [
			"modelFitSafetyMarginPercent",
			"customToolMaxTimeoutSeconds",
			"webFetchTimeoutSeconds",
			"webFetchMaxContentChars",
			"knowledgeSearchDefaultResults",
			"knowledgeSearchMaxResults",
			"agentHomeMaxRunSeconds",
			"containerRuntimeSelection",
		] as const) {
			expect(restartGatedNodeSettingsFields.has(live)).toBe(false);
		}
	});

	it("sends the reasoning budgets and the unspecified-effort rung live, within the shared token range", () => {
		const baseline = toNodeSettingsFieldsForm(undefined);
		// Operator decision 3 (model-matrix 2026-10-04): these mirror ReasoningBudgets.Default in the backend.
		expect(shippedDefaults).toMatchObject({
			reasoningBudgetMinimalTokens: 1024,
			reasoningBudgetLowTokens: 2048,
			reasoningBudgetMediumTokens: 8192,
			reasoningBudgetHighTokens: 24576,
			defaultReasoningEffort: "low",
		});
		// Unset renders blank: the field shows the shipped default instead of claiming a stored value.
		expect(baseline.reasoningBudgetMinimalTokens).toBe("");
		expect(baseline.defaultReasoningEffort).toBe("");

		const form = { ...baseline, reasoningBudgetMediumTokens: 4096, defaultReasoningEffort: "minimal" };
		expect(buildNodeSettingsRequest(form, baseline, bounds, false)).toEqual({
			body: { reasoningBudgetMediumTokens: 4096, defaultReasoningEffort: "minimal" },
			errors: {},
		});
		expect(
			buildNodeSettingsRequest({ ...baseline, reasoningBudgetHighTokens: 64 }, baseline, bounds, false).errors[
				"reasoningBudgetHighTokens"
			],
		).toBe("range");
		expect(
			toNodeSettingsFieldBounds({ minReasoningBudgetTokens: 256, maxAllowedReasoningBudgetTokens: 65536 }).tunables
				.reasoningBudgetLowTokens,
		).toEqual({ min: 256, max: 65536 });
		expect(toNodeSettingsFieldsForm({ defaultReasoningEffort: "high", reasoningBudgetLowTokens: 512 })).toMatchObject({
			defaultReasoningEffort: "high",
			reasoningBudgetLowTokens: 512,
		});
		for (const live of [
			"reasoningBudgetMinimalTokens",
			"reasoningBudgetLowTokens",
			"reasoningBudgetMediumTokens",
			"reasoningBudgetHighTokens",
			"defaultReasoningEffort",
		] as const) {
			expect(restartGatedNodeSettingsFields.has(live)).toBe(false);
		}
	});

	it("sends the chat output cap live, its ceiling within its own range", () => {
		const baseline = toNodeSettingsFieldsForm(undefined);
		// Operator decision 4 (model-matrix 2026-10-04): cap plus notice, at most 16384 tokens.
		expect(shippedDefaults.chatOutputCapMode).toBe("cap");
		expect(shippedDefaults.chatOutputCapMaxTokens).toBe(16384);
		expect(baseline.chatOutputCapMode).toBe("");
		expect(baseline.chatOutputCapMaxTokens).toBe("");

		const form = { ...baseline, chatOutputCapMode: "notice", chatOutputCapMaxTokens: 8192 };
		expect(buildNodeSettingsRequest(form, baseline, bounds, false)).toEqual({
			body: { chatOutputCapMode: "notice", chatOutputCapMaxTokens: 8192 },
			errors: {},
		});
		expect(
			buildNodeSettingsRequest({ ...baseline, chatOutputCapMaxTokens: 100 }, baseline, bounds, false).errors[
				"chatOutputCapMaxTokens"
			],
		).toBe("range");
		expect(
			toNodeSettingsFieldBounds({ minChatOutputCapMaxTokens: 512, maxAllowedChatOutputCapMaxTokens: 65536 }).tunables
				.chatOutputCapMaxTokens,
		).toEqual({ min: 512, max: 65536 });
		expect(toNodeSettingsFieldsForm({ chatOutputCapMode: "off", chatOutputCapMaxTokens: 2048 })).toMatchObject({
			chatOutputCapMode: "off",
			chatOutputCapMaxTokens: 2048,
		});
		expect(restartGatedNodeSettingsFields.has("chatOutputCapMode")).toBe(false);
		expect(restartGatedNodeSettingsFields.has("chatOutputCapMaxTokens")).toBe(false);
	});

	it("resets stored thinking budgets and output-cap fields to the default with the sentinels, and leaves unset ones alone", () => {
		const stored = toNodeSettingsFieldsForm({
			reasoningBudgetMinimalTokens: 512,
			reasoningBudgetLowTokens: 1024,
			reasoningBudgetMediumTokens: 4096,
			reasoningBudgetHighTokens: 16384,
			defaultReasoningEffort: "high",
			chatOutputCapMode: "off",
			chatOutputCapMaxTokens: 2048,
		});
		const reset = {
			...stored,
			reasoningBudgetMinimalTokens: "",
			reasoningBudgetLowTokens: "",
			reasoningBudgetMediumTokens: "",
			reasoningBudgetHighTokens: "",
			defaultReasoningEffort: "",
			chatOutputCapMode: "",
			chatOutputCapMaxTokens: "",
		};

		expect(buildNodeSettingsRequest(reset, stored, bounds, false)).toEqual({
			body: {
				reasoningBudgetMinimalTokens: -1,
				reasoningBudgetLowTokens: -1,
				reasoningBudgetMediumTokens: -1,
				reasoningBudgetHighTokens: -1,
				defaultReasoningEffort: "",
				chatOutputCapMode: "",
				chatOutputCapMaxTokens: -1,
			},
			errors: {},
		});
		// Already unset on both sides: nothing to send, and blank is not a range error.
		const unset = toNodeSettingsFieldsForm({});
		expect(buildNodeSettingsRequest(unset, unset, bounds, false)).toEqual({ body: {}, errors: {} });
	});

	it("takes the new bounds from the response, the knowledge counts sharing one range", () => {
		const fromServer = toNodeSettingsFieldBounds({
			minWebFetchTimeoutSeconds: 7,
			maxAllowedWebFetchTimeoutSeconds: 99,
			minKnowledgeSearchResults: 2,
			maxAllowedKnowledgeSearchResults: 15,
		});
		expect(fromServer.tunables.webFetchTimeoutSeconds).toEqual({ min: 7, max: 99 });
		expect(fromServer.tunables.knowledgeSearchDefaultResults).toEqual({ min: 2, max: 15 });
		expect(fromServer.tunables.knowledgeSearchMaxResults).toEqual({ min: 2, max: 15 });
		expect(bounds.tunables.llamaChatHttpTimeoutSeconds).toEqual({ min: 60, max: 86400 });
	});

	it("bounds the disk margin at 1 TiB, preferring the server's range", () => {
		const baseline = toNodeSettingsFieldsForm(undefined);
		expect(bounds.huggingFaceDiskMarginBytes).toEqual({ min: 1, max: 1024 ** 4 });
		expect(buildNodeSettingsRequest({ ...baseline, huggingFaceDiskMarginBytes: 1024 }, baseline, bounds, false).body).toEqual({
			huggingFaceDiskMarginBytes: 1024 ** 4,
		});
		expect(
			buildNodeSettingsRequest({ ...baseline, huggingFaceDiskMarginBytes: 1025 }, baseline, bounds, false).errors[
				"huggingFaceDiskMarginBytes"
			],
		).toBe("range");

		const fromServer = toNodeSettingsFieldBounds({
			minHuggingFaceDiskMarginBytes: 1,
			maxAllowedHuggingFaceDiskMarginBytes: 4 * 1024 ** 3,
		});
		expect(
			buildNodeSettingsRequest({ ...baseline, huggingFaceDiskMarginBytes: 5 }, baseline, fromServer, false).errors[
				"huggingFaceDiskMarginBytes"
			],
		).toBe("range");
	});
});

describe("chat knobs", () => {
	const bounds = toNodeSettingsFieldBounds(undefined);
	const baseline = toNodeSettingsFieldsForm(undefined);
	const restartGated = [
		"toolPipelineMaxIterationsPerRequest",
		"toolPipelineMaxToolResultChars",
		"toolPipelineMaxConsecutiveInvalidToolCalls",
	] as const;
	const live = [
		"defaultContextTokens",
		"providerBudgetRecentMessagesToKeep",
		"providerBudgetMaxCumulativeInputTokens",
		"contextBudgetRecentTurnKeepCount",
		"compactionAutoEnabled",
		"compactionAutoCompactPercent",
		"compactionRecentMessagesVerbatim",
		"compactionDistillEnabled",
		"maxInlinedAttachmentChars",
		"knowledgeChatTopK",
		"providerRetryEnabled",
		"providerMaxRetries",
		"spawnMaxConcurrent",
		"spawnMaxCloud",
		"spawnQueueWaitSeconds",
	] as const;

	it("seed defaults mirror the backend StoredNodeSettings Default* consts", () => {
		expect(nodeSettingsFieldDefaults).toMatchObject({
			toolPipelineMaxIterationsPerRequest: 40,
			toolPipelineMaxToolResultChars: 65536,
			toolPipelineMaxConsecutiveInvalidToolCalls: 3,
			defaultContextTokens: 8192,
			providerBudgetRecentMessagesToKeep: 6,
			providerBudgetMaxCumulativeInputTokens: 4_000_000,
			contextBudgetRecentTurnKeepCount: 4,
			compactionAutoEnabled: true,
			compactionAutoCompactPercent: 75,
			compactionRecentMessagesVerbatim: 8,
			compactionDistillEnabled: true,
			maxInlinedAttachmentChars: 48000,
			knowledgeChatTopK: 5,
			providerRetryEnabled: true,
			providerMaxRetries: 2,
			spawnMaxConcurrent: 3,
			spawnMaxCloud: 3,
			spawnQueueWaitSeconds: 120,
		});
	});

	it("maps stored values, and absent switches to their on default rather than a spurious off", () => {
		const form = toNodeSettingsFieldsForm({
			spawnMaxConcurrent: 1,
			compactionAutoCompactPercent: 60,
			providerRetryEnabled: false,
		});
		expect(form.spawnMaxConcurrent).toBe(1);
		expect(form.compactionAutoCompactPercent).toBe(60);
		expect(form.providerRetryEnabled).toBe(false);
		expect(form.compactionAutoEnabled).toBe(true);
		expect(form.compactionDistillEnabled).toBe(true);
	});

	it("sends changed knobs in wire units, an explicit false switch, and 0 where 0 is meaningful", () => {
		const form = {
			...baseline,
			defaultContextTokens: 16384,
			compactionAutoCompactPercent: 50,
			spawnMaxCloud: 0,
			spawnQueueWaitSeconds: 0,
			providerMaxRetries: 0,
			compactionAutoEnabled: false,
			providerRetryEnabled: false,
		};

		const { body, errors } = buildNodeSettingsRequest(form, baseline, bounds, false);

		expect(errors).toEqual({});
		expect(body).toEqual({
			defaultContextTokens: 16384,
			compactionAutoCompactPercent: 50,
			spawnMaxCloud: 0,
			spawnQueueWaitSeconds: 0,
			providerMaxRetries: 0,
			compactionAutoEnabled: false,
			providerRetryEnabled: false,
		});
		// No developer gate: the same body goes out with developer mode on.
		expect(buildNodeSettingsRequest(form, baseline, bounds, true).body).toEqual(body);
	});

	it("rejects an out-of-range knob with a range error and never sends it", () => {
		const { body, errors } = buildNodeSettingsRequest(
			{ ...baseline, compactionAutoCompactPercent: 96, spawnMaxConcurrent: 0 },
			baseline,
			bounds,
			false,
		);
		expect(errors).toMatchObject({ compactionAutoCompactPercent: "range", spawnMaxConcurrent: "range" });
		expect(body).toEqual({});
	});

	it("takes the bounds from the response and falls back to the backend ranges", () => {
		expect(bounds.tunables.defaultContextTokens).toEqual({ min: 1024, max: 1048576 });
		expect(bounds.tunables.compactionAutoCompactPercent).toEqual({ min: 30, max: 95 });
		expect(bounds.tunables.spawnMaxCloud).toEqual({ min: 0, max: 32 });
		expect(
			toNodeSettingsFieldBounds({ minKnowledgeChatTopK: 2, maxAllowedKnowledgeChatTopK: 9 }).tunables.knowledgeChatTopK,
		).toEqual({ min: 2, max: 9 });
	});

	it("restart-gates only the three tool-pipeline limits", () => {
		for (const field of restartGated) {
			expect(restartGatedNodeSettingsFields.has(field)).toBe(true);
		}
		for (const field of live) {
			expect(restartGatedNodeSettingsFields.has(field)).toBe(false);
		}
		expect(touchesRestartGatedField({ toolPipelineMaxIterationsPerRequest: 80 })).toBe(true);
		expect(touchesRestartGatedField({ spawnMaxConcurrent: 1, compactionAutoEnabled: false })).toBe(false);
	});

	it("shows every knob in its own unit, so none is display-scaled", () => {
		for (const field of [...restartGated, ...live]) {
			expect(nodeSettingsScaleOf(field)).toBe(1);
		}
	});
});

describe("knowledge, privacy and usage knobs", () => {
	const bounds = toNodeSettingsFieldBounds(undefined);
	const baseline = toNodeSettingsFieldsForm(undefined);
	const restartGated = ["knowledgeScheduledReindexEnabled", "knowledgeScheduledReindexIntervalMinutes"] as const;
	const live = [
		"knowledgeAdaptiveRerankingEnabled",
		"knowledgeRetrievalLatencyBudgetMs",
		"knowledgeAgentToolsEnabled",
		"allowCloudModelAccess",
		"allowCloudModelUnattendedRuns",
		"allowCloudModelWebTools",
		"allowCloudModelMcpTools",
		"allowCloudModelSubAgents",
		"playbookAnalysisModelName",
		"playbookEvalModelName",
		"memoryExtractionModelName",
		"chatRetentionEnabled",
		"chatRetentionDays",
		"agentExecutionLogRetentionEnabled",
		"agentExecutionLogRetentionDays",
		"nodeDbBackupRetainCount",
		"benchmarkKldCacheMaxBytes",
		"schedulerHistoryRetentionDays",
	] as const;

	it("seed defaults mirror the backend StoredNodeSettings Default* consts", () => {
		expect(nodeSettingsFieldDefaults).toMatchObject({
			knowledgeAdaptiveRerankingEnabled: true,
			knowledgeRetrievalLatencyBudgetMs: 500,
			knowledgeScheduledReindexEnabled: true,
			knowledgeScheduledReindexIntervalMinutes: 60,
			knowledgeAgentToolsEnabled: true,
			allowCloudModelAccess: false,
			allowCloudModelUnattendedRuns: false,
			allowCloudModelWebTools: false,
			allowCloudModelMcpTools: false,
			allowCloudModelSubAgents: false,
			playbookAnalysisModelName: "",
			playbookEvalModelName: "",
			memoryExtractionModelName: "",
			chatRetentionEnabled: false,
			chatRetentionDays: 30,
			agentExecutionLogRetentionEnabled: true,
			agentExecutionLogRetentionDays: 30,
			nodeDbBackupRetainCount: 3,
			// 64 GiB, shown in GB.
			benchmarkKldCacheMaxBytes: 64,
			schedulerHistoryRetentionDays: 30,
		});
	});

	it("maps stored values, and absent switches to their defaults rather than a spurious value", () => {
		const form = toNodeSettingsFieldsForm({
			allowCloudModelAccess: true,
			chatRetentionDays: 7,
			benchmarkKldCacheMaxBytes: 8 * 1024 ** 3,
			memoryExtractionModelName: "qwen3:8b",
		});
		expect(form.allowCloudModelAccess).toBe(true);
		expect(form.chatRetentionDays).toBe(7);
		expect(form.benchmarkKldCacheMaxBytes).toBe(8);
		expect(form.memoryExtractionModelName).toBe("qwen3:8b");
		expect(form.playbookEvalModelName).toBe("");
		expect(form.chatRetentionEnabled).toBe(false);
		expect(form.knowledgeAgentToolsEnabled).toBe(true);
		expect(form.agentExecutionLogRetentionEnabled).toBe(true);
	});

	it("sends changed knobs in wire units, explicit false switches, and a cleared model as an empty string", () => {
		const stored = toNodeSettingsFieldsForm({ playbookEvalModelName: "qwen3:8b" });
		const form = {
			...stored,
			knowledgeAgentToolsEnabled: false,
			allowCloudModelAccess: true,
			chatRetentionEnabled: true,
			chatRetentionDays: 90,
			benchmarkKldCacheMaxBytes: 2,
			playbookAnalysisModelName: "  repo/model:Q4_K_M ",
			playbookEvalModelName: "",
		};

		const { body, errors } = buildNodeSettingsRequest(form, stored, bounds, false);

		expect(errors).toEqual({});
		expect(body).toEqual({
			knowledgeAgentToolsEnabled: false,
			allowCloudModelAccess: true,
			chatRetentionEnabled: true,
			chatRetentionDays: 90,
			benchmarkKldCacheMaxBytes: 2 * 1024 ** 3,
			playbookAnalysisModelName: "repo/model:Q4_K_M",
			playbookEvalModelName: "",
		});
	});

	it("takes a seeded-true switch the server reports as the baseline, so turning it off sends an explicit false", () => {
		// The server reports the effective value (stored, else the appsettings seed) for an unsaved switch.
		const stored = toNodeSettingsFieldsForm({ allowCloudModelAccess: true, chatRetentionEnabled: true });
		expect(stored.allowCloudModelAccess).toBe(true);
		expect(stored.chatRetentionEnabled).toBe(true);

		const { body, errors } = buildNodeSettingsRequest(
			{ ...stored, allowCloudModelAccess: false, chatRetentionEnabled: false },
			stored,
			bounds,
			false,
		);

		expect(errors).toEqual({});
		expect(body).toEqual({ allowCloudModelAccess: false, chatRetentionEnabled: false });
	});

	it("takes a seeded retention window the server reports as the baseline, so only a real change is sent", () => {
		// The server reports the effective value (stored, else the appsettings seed) for an unsaved window.
		const seeded = toNodeSettingsFieldsForm({ chatRetentionDays: 1, agentHomeRunRetentionDays: 3 });
		expect(seeded.chatRetentionDays).toBe(1);
		expect(seeded.agentHomeRunRetentionDays).toBe(3);

		expect(buildNodeSettingsRequest(seeded, seeded, bounds, false).body).toEqual({});

		const { body, errors } = buildNodeSettingsRequest({ ...seeded, chatRetentionDays: 7 }, seeded, bounds, false);
		expect(errors).toEqual({});
		expect(body).toEqual({ chatRetentionDays: 7 });
	});

	it("keeps the cloud opt-in out of the external-access preset", () => {
		expect(isExternalAccessBooleanField("allowCloudModelAccess")).toBe(false);
		const preset = applyExternalAccessPreset({ ...baseline, allowCloudModelAccess: true }, "offline");
		expect(preset.allowCloudModelAccess).toBe(true);
	});

	describe("the four cloud-model permissions", () => {
		const cloudSwitches = [
			"allowCloudModelUnattendedRuns",
			"allowCloudModelWebTools",
			"allowCloudModelMcpTools",
			"allowCloudModelSubAgents",
		] as const;

		it("map a reported true, and an absent value to off", () => {
			const reported = toNodeSettingsFieldsForm({
				allowCloudModelUnattendedRuns: true,
				allowCloudModelWebTools: true,
				allowCloudModelMcpTools: true,
				allowCloudModelSubAgents: true,
			});
			for (const field of cloudSwitches) {
				expect(reported[field]).toBe(true);
				expect(baseline[field]).toBe(false);
			}
		});

		it("send only the switches that changed", () => {
			const { body, errors } = buildNodeSettingsRequest(
				{ ...baseline, allowCloudModelWebTools: true, allowCloudModelSubAgents: true },
				baseline,
				bounds,
				false,
			);
			expect(errors).toEqual({});
			expect(body).toEqual({ allowCloudModelWebTools: true, allowCloudModelSubAgents: true });

			const stored = toNodeSettingsFieldsForm({ allowCloudModelMcpTools: true });
			expect(buildNodeSettingsRequest({ ...stored, allowCloudModelMcpTools: false }, stored, bounds, false).body).toEqual({
				allowCloudModelMcpTools: false,
			});
		});

		it("are left alone by every external-access preset, like the read-local-data switch", () => {
			const allOn = {
				...baseline,
				allowCloudModelAccess: true,
				allowCloudModelUnattendedRuns: true,
				allowCloudModelWebTools: true,
				allowCloudModelMcpTools: true,
				allowCloudModelSubAgents: true,
			};
			for (const field of ["allowCloudModelAccess", ...cloudSwitches] as const) {
				expect(isExternalAccessBooleanField(field)).toBe(false);
				expect(applyExternalAccessPreset(allOn, "offline")[field]).toBe(true);
				expect(applyExternalAccessPreset(baseline, "recommended")[field]).toBe(false);
			}
		});
	});

	it("rejects an out-of-range knob with a range error and never sends it", () => {
		const { body, errors } = buildNodeSettingsRequest(
			{ ...baseline, chatRetentionDays: 0, knowledgeRetrievalLatencyBudgetMs: 49, benchmarkKldCacheMaxBytes: 0.5 },
			baseline,
			bounds,
			false,
		);
		expect(errors).toMatchObject({
			chatRetentionDays: "range",
			knowledgeRetrievalLatencyBudgetMs: "range",
			benchmarkKldCacheMaxBytes: "range",
		});
		expect(body).toEqual({});
	});

	it("takes the bounds from the response, the three retention windows sharing one pair", () => {
		expect(bounds.tunables.knowledgeScheduledReindexIntervalMinutes).toEqual({ min: 5, max: 10080 });
		expect(bounds.tunables.benchmarkKldCacheMaxBytes).toEqual({ min: 1024 ** 3, max: 4 * 1024 ** 4 });
		const shared = toNodeSettingsFieldBounds({ minRetentionDays: 2, maxAllowedRetentionDays: 400 }).tunables;
		expect(shared.chatRetentionDays).toEqual({ min: 2, max: 400 });
		expect(shared.agentExecutionLogRetentionDays).toEqual({ min: 2, max: 400 });
		expect(shared.schedulerHistoryRetentionDays).toEqual({ min: 2, max: 400 });
	});

	it("restart-gates only the scheduled reindex pair", () => {
		for (const field of restartGated) {
			expect(restartGatedNodeSettingsFields.has(field)).toBe(true);
		}
		for (const field of live) {
			expect(restartGatedNodeSettingsFields.has(field)).toBe(false);
		}
		expect(touchesRestartGatedField({ knowledgeScheduledReindexIntervalMinutes: 30 })).toBe(true);
		expect(touchesRestartGatedField({ allowCloudModelAccess: true, chatRetentionEnabled: true })).toBe(false);
	});

	it("shows only the benchmark cache in a scaled unit", () => {
		expect(nodeSettingsScaleOf("benchmarkKldCacheMaxBytes")).toBe(1024 ** 3);
		for (const field of [...restartGated, ...live].filter((field) => field !== "benchmarkKldCacheMaxBytes")) {
			expect(nodeSettingsScaleOf(field)).toBe(1);
		}
	});
});

describe("runtime and workspace knobs", () => {
	const bounds = toNodeSettingsFieldBounds(undefined);
	const baseline = toNodeSettingsFieldsForm(undefined);
	const restartGated = [
		"imageMaxLoadedProcesses",
		"imageTextEncoderOnGpu",
		"graphWorkflowMaxConcurrentRuns",
		"graphWorkflowDefaultNodeTimeoutSeconds",
		"workSessionMaxStepsPerRun",
		"workSessionMaxConcurrentSessions",
		"developmentMaxAttemptDurationSeconds",
		"developmentMaxToolCalls",
		"developmentMaxOutputTokens",
	] as const;
	const live = [
		"agentHomeMaxInnerToolCalls",
		"agentHomePatchApplyTimeoutSeconds",
		"agentHomeRunRetentionDays",
		"agentHomeRunRetentionMaxRuns",
		"agentHomeRunRetentionMaxTotalBytes",
	] as const;

	it("seed defaults mirror the backend StoredNodeSettings Default* consts", () => {
		expect(nodeSettingsFieldDefaults).toMatchObject({
			imageMaxLoadedProcesses: 1,
			imageTextEncoderOnGpu: false,
			graphWorkflowMaxConcurrentRuns: 4,
			graphWorkflowDefaultNodeTimeoutSeconds: 600,
			workSessionMaxStepsPerRun: 25,
			workSessionMaxConcurrentSessions: 1,
			developmentMaxAttemptDurationSeconds: 1800,
			developmentMaxToolCalls: 64,
			developmentMaxOutputTokens: 32768,
			agentHomeMaxInnerToolCalls: 24,
			agentHomePatchApplyTimeoutSeconds: 120,
			agentHomeRunRetentionMaxRuns: 200,
			// 2 GiB, shown in GB.
			agentHomeRunRetentionMaxTotalBytes: 2,
		});
	});

	it("sends the changed workspace limits and the image switch for every user, in wire units", () => {
		const form = {
			...baseline,
			imageMaxLoadedProcesses: 2,
			imageTextEncoderOnGpu: true,
			graphWorkflowDefaultNodeTimeoutSeconds: 900,
			developmentMaxOutputTokens: 65536,
		};

		const { body, errors } = buildNodeSettingsRequest(form, baseline, bounds, false);

		expect(errors).toEqual({});
		expect(body).toEqual({
			imageMaxLoadedProcesses: 2,
			imageTextEncoderOnGpu: true,
			graphWorkflowDefaultNodeTimeoutSeconds: 900,
			developmentMaxOutputTokens: 65536,
		});
	});

	it("sends the AgentHome knobs only with developer fields, the byte cap in wire units and 0 as a real value", () => {
		const form = {
			...baseline,
			agentHomeMaxInnerToolCalls: 12,
			agentHomePatchApplyTimeoutSeconds: 300,
			agentHomeRunRetentionMaxRuns: 0,
			agentHomeRunRetentionMaxTotalBytes: 4,
		};

		expect(buildNodeSettingsRequest(form, baseline, bounds, false).body).toEqual({});
		const { body, errors } = buildNodeSettingsRequest(form, baseline, bounds, true);
		expect(errors).toEqual({});
		expect(body).toEqual({
			agentHomeMaxInnerToolCalls: 12,
			agentHomePatchApplyTimeoutSeconds: 300,
			agentHomeRunRetentionMaxRuns: 0,
			agentHomeRunRetentionMaxTotalBytes: 4 * 1024 ** 3,
		});
	});

	it("rejects an out-of-range knob with a range error and never sends it", () => {
		const { body, errors } = buildNodeSettingsRequest(
			{ ...baseline, imageMaxLoadedProcesses: 5, developmentMaxOutputTokens: 255, agentHomeRunRetentionMaxTotalBytes: 1025 },
			baseline,
			bounds,
			true,
		);
		expect(errors).toMatchObject({
			imageMaxLoadedProcesses: "range",
			developmentMaxOutputTokens: "range",
			agentHomeRunRetentionMaxTotalBytes: "range",
		});
		expect(body).toEqual({});
	});

	it("takes the AgentHome bounds from the response", () => {
		const fromResponse = toNodeSettingsFieldBounds({
			minAgentHomeRunRetentionMaxRuns: 0,
			maxAllowedAgentHomeRunRetentionMaxRuns: 500,
		});
		expect(fromResponse.agentHomeRunRetentionMaxRuns).toEqual({ min: 0, max: 500 });
		expect(bounds.agentHomeRunRetentionMaxTotalBytes).toEqual({ min: 0, max: 1024 ** 4 });
		expect(bounds.tunables.graphWorkflowDefaultNodeTimeoutSeconds).toEqual({ min: 30, max: 86400 });
	});

	it("restart-gates the image pair and the workspace limits, and the AgentHome retention window is live now", () => {
		for (const field of restartGated) {
			expect(restartGatedNodeSettingsFields.has(field)).toBe(true);
		}
		for (const field of live) {
			expect(restartGatedNodeSettingsFields.has(field)).toBe(false);
		}
		expect(touchesRestartGatedField({ workSessionMaxConcurrentSessions: 2 })).toBe(true);
		expect(touchesRestartGatedField({ agentHomeRunRetentionDays: 7, agentHomeMaxInnerToolCalls: 12 })).toBe(false);
	});

	it("shows only the AgentHome byte cap in a scaled unit", () => {
		expect(nodeSettingsScaleOf("agentHomeRunRetentionMaxTotalBytes")).toBe(1024 ** 3);
		for (const field of [...restartGated, ...live].filter((field) => field !== "agentHomeRunRetentionMaxTotalBytes")) {
			expect(nodeSettingsScaleOf(field)).toBe(1);
		}
	});
});

describe("feature switches", () => {
	const bounds = toNodeSettingsFieldBounds(undefined);
	const baseline = toNodeSettingsFieldsForm(undefined);
	const restartGated = ["developmentEnabled", "schedulerEnabled"] as const;
	const live = [
		"workSessionsEnabled",
		"devWorkflowsEnabled",
		"graphWorkflowsEnabled",
		"agentHomeEnabled",
		"computeEnabled",
		"externalAppsEnabled",
		"transcriptionEnabled",
		"executionPreviewsEnabled",
	] as const;

	it("defaults mirror the C# code defaults of the BoolSeed calls", () => {
		expect(nodeSettingsFieldDefaults).toMatchObject({
			developmentEnabled: true,
			workSessionsEnabled: false,
			graphWorkflowsEnabled: true,
			transcriptionEnabled: true,
			externalAppsEnabled: false,
			computeEnabled: false,
			agentHomeEnabled: false,
			schedulerEnabled: true,
			devWorkflowsEnabled: false,
			executionPreviewsEnabled: false,
		});
	});

	it("flags only the off-to-on change of execution previews for confirmation", () => {
		const turnedOn = buildNodeSettingsRequest({ ...baseline, executionPreviewsEnabled: true }, baseline, bounds, false);
		expect(turnedOn.body).toEqual({ executionPreviewsEnabled: true });
		expect(turnsOnExecutionPreviews(turnedOn.body)).toBe(true);

		const loaded = { ...baseline, executionPreviewsEnabled: true };
		const turnedOff = buildNodeSettingsRequest({ ...loaded, executionPreviewsEnabled: false }, loaded, bounds, false);
		expect(turnedOff.body).toEqual({ executionPreviewsEnabled: false });
		expect(turnsOnExecutionPreviews(turnedOff.body)).toBe(false);
		// Already on and untouched: the body does not carry it, so a later unrelated save is not asked about it again.
		expect(
			turnsOnExecutionPreviews(buildNodeSettingsRequest({ ...loaded, computeEnabled: true }, loaded, bounds, false).body),
		).toBe(false);
	});

	it("takes the effective value from the response, false included, and the default only when absent", () => {
		const form = toNodeSettingsFieldsForm({ developmentEnabled: false, workSessionsEnabled: true, agentHomeEnabled: true });
		expect(form.developmentEnabled).toBe(false);
		expect(form.workSessionsEnabled).toBe(true);
		expect(form.agentHomeEnabled).toBe(true);
		expect(form.schedulerEnabled).toBe(true);
		expect(form.computeEnabled).toBe(false);
	});

	it("sends only the changed switches, an explicit false included", () => {
		const loaded = { ...baseline, workSessionsEnabled: true };
		const form = { ...loaded, developmentEnabled: false, computeEnabled: true, transcriptionEnabled: false };

		const { body, errors } = buildNodeSettingsRequest(form, loaded, bounds, false);

		expect(errors).toEqual({});
		expect(body).toEqual({ developmentEnabled: false, computeEnabled: true, transcriptionEnabled: false });
		expect(buildNodeSettingsRequest(loaded, loaded, bounds, false).body).toEqual({});
	});

	it("restart-gates only Development and Scheduler", () => {
		expect([...featureSwitchFields].sort()).toEqual([...restartGated, ...live].sort());
		for (const field of restartGated) {
			expect(restartGatedNodeSettingsFields.has(field)).toBe(true);
		}
		for (const field of live) {
			expect(restartGatedNodeSettingsFields.has(field)).toBe(false);
		}
		expect(touchesRestartGatedField({ schedulerEnabled: false })).toBe(true);
		expect(touchesRestartGatedField({ workSessionsEnabled: false, externalAppsEnabled: true })).toBe(false);
	});

	it("needs a restart for Development on, but not for Development off", () => {
		expect(touchesRestartGatedField({ developmentEnabled: true })).toBe(true);
		expect(touchesRestartGatedField({ developmentEnabled: false })).toBe(false);

		const loaded = { ...baseline, developmentEnabled: true };
		const off = { ...loaded, developmentEnabled: false };
		expect(summarizePendingChanges(buildNodeSettingsRequest(off, loaded, bounds, false), off, loaded).restartRequired).toEqual(
			[],
		);
		const on = { ...baseline, developmentEnabled: true };
		const onBaseline = { ...baseline, developmentEnabled: false };
		expect(
			summarizePendingChanges(buildNodeSettingsRequest(on, onBaseline, bounds, false), on, onBaseline).restartRequired,
		).toEqual(["developmentEnabled"]);
	});

	it("refuses development workflows without work sessions, blamed on the switch being turned on", () => {
		const { body, errors } = buildNodeSettingsRequest({ ...baseline, devWorkflowsEnabled: true }, baseline, bounds, false);
		expect(errors).toEqual({ devWorkflowsEnabled: "requiresWorkSessions" });
		expect(body.devWorkflowsEnabled).toBe(true);

		expect(
			buildNodeSettingsRequest({ ...baseline, devWorkflowsEnabled: true, workSessionsEnabled: true }, baseline, bounds, false)
				.errors,
		).toEqual({});
	});

	it("blames work sessions when they are turned off under running development workflows", () => {
		const loaded = { ...baseline, workSessionsEnabled: true, devWorkflowsEnabled: true };
		const { errors } = buildNodeSettingsRequest({ ...loaded, workSessionsEnabled: false }, loaded, bounds, false);
		expect(errors).toEqual({ workSessionsEnabled: "requiresWorkSessions" });
	});

	it("refuses AgentHome only when the effective tool-capable list is known to be empty", () => {
		const form = { ...baseline, agentHomeEnabled: true };
		expect(buildNodeSettingsRequest(form, baseline, bounds, false, null, []).errors).toEqual({
			agentHomeEnabled: "requiresToolCapableModels",
		});
		// Unknown, or a seeded list behind the empty stored one, is not refused: the server falls back to it.
		expect(buildNodeSettingsRequest(form, baseline, bounds, false, null, undefined).errors).toEqual({});
		expect(buildNodeSettingsRequest(form, baseline, bounds, false, null, ["qwen3:8b"]).errors).toEqual({});
		// A model added in the same save satisfies it.
		expect(
			buildNodeSettingsRequest({ ...form, toolCapableModels: ["qwen3:8b"] }, baseline, bounds, false, null, []).errors,
		).toEqual({});
	});

	it("leaves clearing a stored tool-capable list to the server, which alone knows the seed behind it", () => {
		const loaded = { ...baseline, agentHomeEnabled: true, toolCapableModels: ["qwen3:8b"] };
		const { body, errors } = buildNodeSettingsRequest({ ...loaded, toolCapableModels: [] }, loaded, bounds, false, null, [
			"qwen3:8b",
		]);
		expect(errors).toEqual({});
		expect(body.toolCapableModels).toEqual([]);
	});

	it("blames the tool-capable list when AgentHome was already on", () => {
		const loaded = { ...baseline, agentHomeEnabled: true };
		const { errors } = buildNodeSettingsRequest({ ...loaded, computeEnabled: true }, loaded, bounds, false, null, []);
		expect(errors).toEqual({ toolCapableModels: "requiresToolCapableModels" });
	});
});
