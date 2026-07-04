import { describe, expect, it } from "vitest";

import type {
	XeLocalAiEngineClientEndpointsNodeSettingsV1NodeSettingsResponse as NodeSettingsResponse,
} from "@/core/api/generated";
import {
	buildNodeSettingsRequest,
	nodeSettingsFieldDefaults,
	toNodeSettingsFieldBounds,
	toNodeSettingsFieldsForm,
	validateLlamaCppTag,
	validateOllamaEndpoint,
	validateToolCapableModels,
} from "@/features/node-settings/models/NodeSettingsFieldsModel";

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

describe("NodeSettingsFieldsModel mapping", () => {
	const response: NodeSettingsResponse = {
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
	};

	it("maps a response into the form, falling back to seed defaults for absent fields", () => {
		const form = toNodeSettingsFieldsForm(response);
		expect(form.defaultModelName).toBe("qwen3:8b");
		// Absent in the response -> reranking off (empty string).
		expect(form.rerankerModelName).toBe("");
		expect(form.enableTools).toBe(false);
		expect(form.toolCapableModels).toEqual(["qwen3:8b"]);
		expect(form.llamaMaxLoadedProcesses).toBe(5);
		// Absent in the response -> seed default (StoredNodeSettings.DefaultMaxPendingToolCallAgeMinutes).
		expect(form.maxPendingToolCallAgeMinutes).toBe(10);
	});

	it("seed defaults mirror the backend StoredNodeSettings Default* consts", () => {
		// Each value must equal the corresponding C# const so a stale-server render shows the real defaults and the
		// byte-cap fallbacks pass the backend `> 0` validator if ever saved.
		expect(nodeSettingsFieldDefaults.enableTools).toBe(true); // DefaultEnableTools
		expect(nodeSettingsFieldDefaults.llamaMaxLoadedProcesses).toBe(3); // DefaultLlamaMaxLoadedProcesses
		expect(nodeSettingsFieldDefaults.llamaIdleTimeToLiveSeconds).toBe(900); // DefaultLlamaIdleTimeToLiveSeconds
		expect(nodeSettingsFieldDefaults.maxResponseSizeMb).toBe(10); // DefaultMaxResponseSizeMb
		expect(nodeSettingsFieldDefaults.orchestrationIdleTimeoutSeconds).toBe(120); // DefaultOrchestrationIdleTimeoutSeconds
		expect(nodeSettingsFieldDefaults.agentHomePrepareTimeoutSeconds).toBe(900); // DefaultAgentHomePrepareTimeoutSeconds
		expect(nodeSettingsFieldDefaults.agentHomeCommandTimeoutSeconds).toBe(300); // DefaultAgentHomeCommandTimeoutSeconds
		expect(nodeSettingsFieldDefaults.agentHomeMaxSelectedFolderBytes).toBe(536870912); // DefaultAgentHomeMaxSelectedFolderBytes (512 MiB)
		expect(nodeSettingsFieldDefaults.agentHomeMaxPatchBytes).toBe(52428800); // DefaultAgentHomeMaxPatchBytes (50 MiB)
		expect(nodeSettingsFieldDefaults.maxPendingToolCallAgeMinutes).toBe(10); // DefaultMaxPendingToolCallAgeMinutes
	});

	it("resolves bounds from the response with a hardcoded fallback", () => {
		const bounds = toNodeSettingsFieldBounds(response);
		expect(bounds.llamaMaxLoadedProcesses).toEqual({ min: 1, max: 16 });
		// Absent server bound -> hardcoded fallback range.
		expect(bounds.maxResponseSizeMb).toEqual({ min: 1, max: 100 });
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

	it("rejects an out-of-range bounded number with a range error", () => {
		const form = { ...baseline, llamaMaxLoadedProcesses: 999 };
		const { body, errors } = buildNodeSettingsRequest(form, baseline, bounds, false);
		expect(errors["llamaMaxLoadedProcesses"]).toBe("range");
		expect(body.llamaMaxLoadedProcesses).toBeUndefined();
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

	it("rejects an unknown speculative mode", () => {
		const form = { ...baseline, speculativeMode: "totally-bogus" };
		const { errors } = buildNodeSettingsRequest(form, baseline, bounds, false);
		expect(errors["speculativeMode"]).toBe("mode");
	});

	it("requires a draft model for a draft-* mode", () => {
		const form = { ...baseline, speculativeMode: "draft-simple", speculativeDraftModelName: "" };
		const { body, errors } = buildNodeSettingsRequest(form, baseline, bounds, false);
		expect(errors["speculativeDraftModelName"]).toBe("required");
		// The mode itself is still valid, so no mode error — only the missing draft model blocks the save.
		expect(errors["speculativeMode"]).toBeUndefined();
		expect(body.speculativeDraftModelName).toBeUndefined();
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
});
