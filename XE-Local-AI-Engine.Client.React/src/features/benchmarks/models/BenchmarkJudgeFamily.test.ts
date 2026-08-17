import { describe, expect, it } from "vitest";

import { benchmarkJudgeFamilyOverlap, benchmarkModelFamily } from "@/features/benchmarks/models/BenchmarkJudgeFamily";

// A judge preferring its own family is invisible in the score, so the heuristic that flags it has to be exact about
// what it does and does not claim: it groups by the leading name token, nothing more.

describe("benchmarkModelFamily", () => {
	it("reduces an owner-prefixed GGUF name to its leading family token", () => {
		expect(benchmarkModelFamily("unsloth/Qwen3-32B-GGUF")).toBe("qwen");
		expect(benchmarkModelFamily("unsloth/Qwen3.8-27B-GGUF")).toBe("qwen");
		expect(benchmarkModelFamily("google/gemma-3-12b-it-GGUF")).toBe("gemma");
		expect(benchmarkModelFamily("mistralai/Mistral-7B-Instruct-v0.3")).toBe("mistral");
	});

	it("ignores a quant tag and a repeated owner segment", () => {
		expect(benchmarkModelFamily("unsloth/Qwen3-32B-GGUF:Q4_K_M")).toBe("qwen");
		expect(benchmarkModelFamily("hf.co/unsloth/llama-3.2-3B-GGUF")).toBe("llama");
	});

	// The documented ceiling: a vendor-prefixed name yields the VENDOR, not the architecture. It still groups that
	// vendor's models together and still separates them from qwen, which is all the warning needs.
	it("yields the vendor token when the name leads with one", () => {
		expect(benchmarkModelFamily("bartowski/Meta-Llama-3.1-8B-Instruct-GGUF")).toBe("meta");
	});

	it("has no family for a name that does not start with letters", () => {
		expect(benchmarkModelFamily("owner/3b-model")).toBeNull();
		expect(benchmarkModelFamily("")).toBeNull();
		expect(benchmarkModelFamily(null)).toBeNull();
		expect(benchmarkModelFamily(undefined)).toBeNull();
	});
});

describe("benchmarkJudgeFamilyOverlap", () => {
	it("counts the primary runs that share the judge's family", () => {
		expect(
			benchmarkJudgeFamilyOverlap("unsloth/Qwen3-32B-GGUF", [
				"unsloth/Qwen3.8-27B-GGUF",
				"unsloth/Qwen3-8B-GGUF",
				"google/gemma-3-12b-it-GGUF",
			]),
		).toEqual({ family: "qwen", matchCount: 2 });
	});

	it("stays silent when nothing overlaps, when the judge has no family, and when there are no runs", () => {
		expect(benchmarkJudgeFamilyOverlap("unsloth/Qwen3-32B-GGUF", ["google/gemma-3-12b-it-GGUF"])).toBeNull();
		expect(benchmarkJudgeFamilyOverlap("owner/3b-model", ["owner/3b-model"])).toBeNull();
		expect(benchmarkJudgeFamilyOverlap(null, ["unsloth/Qwen3-32B-GGUF"])).toBeNull();
		expect(benchmarkJudgeFamilyOverlap("unsloth/Qwen3-32B-GGUF", [])).toBeNull();
	});
});
