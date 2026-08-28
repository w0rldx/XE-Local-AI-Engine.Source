import { describe, expect, it } from "vitest";

import type { ModelOption } from "@/features/chat/models/ChatModels";
import { deriveModelDisplay, deriveModelIdDisplay, middleEllipsis } from "@/features/chat/models/ModelDisplay";

function option(overrides: Partial<ModelOption>): ModelOption {
	return { value: "llama3:8b", label: "llama3:8b", isAvailable: true, ...overrides };
}

describe("deriveModelIdDisplay", () => {
	it("drops the publisher prefix so the clamped name is the model, not the org", () => {
		expect(deriveModelIdDisplay("unsloth/Qwen3-8B-Instruct").primary).toBe("Qwen3-8B-Instruct");
	});

	it("moves the quantization tag off the name and onto the second line", () => {
		const display = deriveModelIdDisplay("unsloth/Qwen3-8B-Instruct-GGUF:Q4_K_M");

		expect(display.primary).toBe("Qwen3-8B-Instruct");
		expect(display.secondary).toBe("Q4_K_M");
	});

	it("reads a dash-separated quantization tag as well as a colon-separated one", () => {
		expect(deriveModelIdDisplay("gemma-4-12b-it-Q6_K").secondary).toBe("Q6_K");
		expect(deriveModelIdDisplay("gpt-oss-20b-MXFP4").secondary).toBe("MXFP4");
		expect(deriveModelIdDisplay("phi-4-IQ3_XXS").secondary).toBe("IQ3_XXS");
	});

	it("keeps an Ollama parameter tag in the name — it is not a quantization marker", () => {
		const display = deriveModelIdDisplay("llama3:8b");

		expect(display.primary).toBe("llama3:8b");
		expect(display.secondary).toBeUndefined();
	});

	it("strips the -GGUF repository marker, which every entry in this list carries", () => {
		expect(deriveModelIdDisplay("bartowski/Mistral-Small-GGUF").primary).toBe("Mistral-Small");
	});

	it("shortens an external id to the model, dropping its ext: connection namespace", () => {
		expect(deriveModelIdDisplay("ext:workstation/qwen3-coder-30b").primary).toBe("qwen3-coder-30b");
	});

	it("keeps the raw id as the full form, whatever it stripped for the name", () => {
		expect(deriveModelIdDisplay("unsloth/Qwen3-8B-GGUF:Q4_K_M").full).toBe("unsloth/Qwen3-8B-GGUF:Q4_K_M");
	});

	it("falls back to the un-stripped id when stripping would leave nothing", () => {
		expect(deriveModelIdDisplay("unsloth/").primary).toBe("unsloth/");
	});

	it("returns empty strings for a blank id rather than inventing a name", () => {
		expect(deriveModelIdDisplay("   ")).toEqual({ primary: "", full: "" });
	});

	it("middle-ellipsises only what structure could not shorten", () => {
		const display = deriveModelIdDisplay("a-very-long-single-segment-model-identifier-with-no-structure");

		expect(display.primary).toContain("…");
		expect(display.primary.length).toBe(30);
		expect(display.primary.startsWith("a-very-long-sin")).toBe(true);
		expect(display.primary.endsWith("structure")).toBe(true);
	});
});

describe("middleEllipsis", () => {
	it("leaves a value that already fits untouched", () => {
		expect(middleEllipsis("Qwen3-8B", 30)).toBe("Qwen3-8B");
	});

	it("keeps both ends of an over-long value", () => {
		expect(middleEllipsis("abcdefghij", 5)).toBe("ab…ij");
	});
});

describe("deriveModelDisplay", () => {
	it("prefers the operator's own label over the raw id", () => {
		const display = deriveModelDisplay(option({ value: "ext:box/qwen3-27b", displayName: "Qwen3 27B" }), "Select model");

		expect(display.primary).toBe("Qwen3 27B");
	});

	it("shortens the id when no operator label exists", () => {
		expect(
			deriveModelDisplay(option({ value: "unsloth/Qwen3-8B-GGUF:Q4_K_M", label: "unsloth/Qwen3-8B-GGUF:Q4_K_M" }), "").primary,
		).toBe("Qwen3-8B");
	});

	it("names the serving connection on line two for an external model", () => {
		const display = deriveModelDisplay(
			option({
				value: "ext:box/qwen3-27b",
				displayName: "Qwen3 27B",
				provider: "external",
				externalConnectionName: "Workstation",
				declaredLocality: "local",
			}),
			"",
		);

		expect(display.secondary).toBe("Workstation");
	});

	it("names the cloud provider on line two for a Codex or Azure option", () => {
		expect(deriveModelDisplay(option({ value: "gpt-5.5", provider: "CodexOAuth", isCloud: true }), "").secondary).toBe(
			"OpenAI Codex",
		);
		expect(deriveModelDisplay(option({ value: "gpt-4o", provider: "AzureFoundry", isCloud: true }), "").secondary).toBe(
			"Azure Foundry",
		);
	});

	it("uses the catalog's size · quant status line for a local model", () => {
		expect(deriveModelDisplay(option({ statusLabel: "8B · Q4_K_M" }), "").secondary).toBe("8B · Q4_K_M");
	});

	it("falls back to the quantization stripped from the id when the catalog reported no status line", () => {
		expect(deriveModelDisplay(option({ value: "unsloth/Qwen3-8B-GGUF:Q4_K_M", label: "x" }), "").secondary).toBe("Q4_K_M");
	});

	it("carries label, raw id and connection in the full form, without repeating any of them", () => {
		const display = deriveModelDisplay(
			option({
				value: "ext:box/qwen3-27b",
				displayName: "Qwen3 27B",
				provider: "external",
				externalConnectionName: "Workstation",
			}),
			"",
		);

		expect(display.full).toBe("Qwen3 27B · ext:box/qwen3-27b · Workstation");
	});

	it("does not repeat a label that is identical to the id (cloud options set both)", () => {
		expect(
			deriveModelDisplay(option({ value: "gpt-5.5", label: "gpt-5.5", displayName: "gpt-5.5", provider: "CodexOAuth" }), "").full,
		).toBe("gpt-5.5 · OpenAI Codex");
	});

	it("renders the caller's placeholder when nothing is selected", () => {
		expect(deriveModelDisplay(undefined, "Select model")).toEqual({ primary: "Select model", full: "Select model" });
	});
});
