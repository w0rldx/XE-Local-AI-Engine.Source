import { describe, expect, it } from "vitest";

import type { ModelOption } from "@/features/chat/models/ChatModels";
import { resolveContextCapacityTokens, shouldFetchLocalModelDetails } from "@/features/chat/pages/ChatModelDetailsQuery";

function option(overrides: Partial<ModelOption>): ModelOption {
	return { value: "model", label: "model", isAvailable: true, ...overrides };
}

describe("shouldFetchLocalModelDetails", () => {
	it("does not fetch when the concrete model name is empty", () => {
		expect(shouldFetchLocalModelDetails("", option({}), false, false)).toBe(false);
	});

	it("does not fetch for a cloud selection (no local details)", () => {
		expect(shouldFetchLocalModelDetails("gpt-5.5", option({ isCloud: true }), true, true)).toBe(false);
	});

	it("does not fetch when the matched option is explicitly unavailable", () => {
		expect(shouldFetchLocalModelDetails("llama3:8b", option({ isAvailable: false }), false, true)).toBe(false);
	});

	it("fetches for an available, installed GGUF (llamacpp) selection — CL-4 serves its details as a 200", () => {
		expect(
			shouldFetchLocalModelDetails("Qwen2.5-0.5B-Instruct-GGUF:Q4_K_M", option({ provider: "llamacpp", isAvailable: true }), false, true),
		).toBe(true);
	});

	it("fetches for an available, installed Ollama selection", () => {
		expect(shouldFetchLocalModelDetails("llama3:8b", option({ provider: "Ollama", isAvailable: true }), false, true)).toBe(true);
	});

	it("fetches when no option is matched but the resolved default name is installed (default sentinel)", () => {
		expect(shouldFetchLocalModelDetails("llama3:8b", undefined, false, true)).toBe(true);
	});

	it("does not fetch a configured-but-not-installed starter model (resolved name absent from the installed list)", () => {
		// The local-default sentinel resolves to configuredDefaultModelName whose GGUF was never downloaded — GET
		// details would 404 forever. Terminal domain state, not a retry loop.
		expect(shouldFetchLocalModelDetails("Starter-Model-GGUF:Q4_K_M", undefined, false, false)).toBe(false);
	});
});

describe("resolveContextCapacityTokens", () => {
	it("prefers the running effective context window over the advertised ceiling (AUD4-02)", () => {
		expect(resolveContextCapacityTokens({ effectiveContextTokens: 16384, maxContextTokens: 262144 })).toBe(16384);
	});

	it("falls back to the advertised ceiling when no effective window is reported yet", () => {
		expect(resolveContextCapacityTokens({ effectiveContextTokens: null, maxContextTokens: 8192 })).toBe(8192);
		expect(resolveContextCapacityTokens({ maxContextTokens: 8192 })).toBe(8192);
	});

	it("returns undefined (meter shows unknown) when neither window is known", () => {
		expect(resolveContextCapacityTokens({ effectiveContextTokens: null, maxContextTokens: null })).toBeUndefined();
		expect(resolveContextCapacityTokens(undefined)).toBeUndefined();
	});
});
