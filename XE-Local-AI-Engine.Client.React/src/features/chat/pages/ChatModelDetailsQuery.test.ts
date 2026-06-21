import { describe, expect, it } from "vitest";

import type { ModelOption } from "@/features/chat/models/ChatModels";
import { shouldFetchLocalModelDetails } from "@/features/chat/pages/ChatModelDetailsQuery";

function option(overrides: Partial<ModelOption>): ModelOption {
	return { value: "model", label: "model", isAvailable: true, ...overrides };
}

describe("shouldFetchLocalModelDetails", () => {
	it("does not fetch when the concrete model name is empty", () => {
		expect(shouldFetchLocalModelDetails("", option({}), false)).toBe(false);
	});

	it("does not fetch for a cloud selection (no local details)", () => {
		expect(shouldFetchLocalModelDetails("gpt-5.5", option({ isCloud: true }), true)).toBe(false);
	});

	it("does not fetch when the matched option is explicitly unavailable", () => {
		expect(shouldFetchLocalModelDetails("llama3:8b", option({ isAvailable: false }), false)).toBe(false);
	});

	it("fetches for an available GGUF (llamacpp) selection — CL-4 serves its details as a 200", () => {
		expect(shouldFetchLocalModelDetails("Qwen2.5-0.5B-Instruct-GGUF:Q4_K_M", option({ provider: "llamacpp", isAvailable: true }), false)).toBe(
			true,
		);
	});

	it("fetches for an available Ollama selection", () => {
		expect(shouldFetchLocalModelDetails("llama3:8b", option({ provider: "Ollama", isAvailable: true }), false)).toBe(true);
	});

	it("fetches when no option is matched (default sentinel resolving to a concrete name)", () => {
		expect(shouldFetchLocalModelDetails("llama3:8b", undefined, false)).toBe(true);
	});
});
