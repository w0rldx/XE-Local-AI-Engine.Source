// A judge scoring a model from its own family is the one bias an operator cannot see in the numbers: the verdict looks
// like every other verdict. So the overlap is surfaced as a non-blocking warning next to the judge, never as a refusal
// or a score adjustment — self-preference is a reason to read a result carefully, not a reason to withhold it.

/**
 * The base model family of a model name, or null when the name carries none.
 *
 * `unsloth/Qwen3-32B-GGUF` and `unsloth/Qwen3.8-27B-GGUF` both reduce to `qwen`: the owner segment is dropped, the rest
 * is lowercased, and the leading run of letters before the first digit, dash, dot or colon is the family.
 *
 * ponytail: naive leading-token heuristic. A vendor-prefixed name reduces to the VENDOR
 * (`bartowski/Meta-Llama-3.1-8B` → `meta`, not `llama`), which still groups that vendor's models together and still
 * separates them from `qwen`, so it does its job — it just does not always name the architecture. The upgrade path is
 * the architecture string in the GGUF metadata, which the node already reads at import time; wire that through if the
 * warning ever needs to be exact rather than indicative.
 */
export function benchmarkModelFamily(modelName: string | null | undefined): string | null {
	if (typeof modelName !== "string") {
		return null;
	}
	const withoutOwner = modelName.slice(modelName.lastIndexOf("/") + 1).toLowerCase();
	const family = /^[a-z]+/.exec(withoutOwner)?.[0] ?? "";
	return family.length > 0 ? family : null;
}

/** The judge/primary family overlap of one project, or null when there is nothing to warn about. */
export interface BenchmarkJudgeFamilyOverlap {
	family: string;
	matchCount: number;
}

/**
 * How many of `primaryModelNames` share the judge model's family. Null when judging is off, the judge model has no
 * recognizable family, or no run matches — all three mean "no warning", so the caller never renders an empty alert.
 */
export function benchmarkJudgeFamilyOverlap(
	judgeModelName: string | null | undefined,
	primaryModelNames: readonly string[],
): BenchmarkJudgeFamilyOverlap | null {
	const family = benchmarkModelFamily(judgeModelName);
	if (family === null) {
		return null;
	}
	const matchCount = primaryModelNames.filter((name) => benchmarkModelFamily(name) === family).length;
	return matchCount > 0 ? { family, matchCount } : null;
}
