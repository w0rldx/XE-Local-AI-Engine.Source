import { describe, expect, it } from "vitest";

import {
	toBenchmarkEligibleModel,
	toBenchmarkProjectDetail,
	toBenchmarkProjectSummary,
	toBenchmarkRunDetail,
	toBenchmarkRunSummary,
} from "@/features/benchmarks/models/BenchmarkMappers";
import { noBenchmarkLaunchFacts } from "@/features/benchmarks/models/BenchmarkModels";

// Boundary mappers: the generated OpenAPI shapes keep almost every member optional, so these supply the UI defaults.
// The rule they encode is that a DEFAULT must never erase a lifecycle or provenance distinction — a missing numeric
// metric stays null (nothing measured) rather than becoming 0 (measured as zero), a missing status fails closed, and
// an unrecognised origin degrades to "legacy/unknown" instead of being trusted through.

// The generated response types are all-optional; a cast keeps each case to the fields it is actually about.
// biome-ignore lint/suspicious/noExplicitAny: exercising the mappers' undefined-field defaults requires partial input.
const partial = (value: Record<string, unknown>) => value as any;

describe("toBenchmarkProjectSummary", () => {
	it("defaults every absent numeric and boolean field", () => {
		expect(toBenchmarkProjectSummary(partial({ name: "Summarisation" }))).toEqual({
			id: "",
			name: "Summarisation",
			contextTokens: 0,
			agentDefinitionId: "",
			judgeEnabled: false,
			runCount: 0,
			isFrozen: false,
			version: 0,
			createdAtUtc: 0,
			updatedAtUtc: 0,
		});
	});

	// `=== true` rather than truthiness: an absent flag must read as off, never as "unknown means on".
	it("carries the flags through when present", () => {
		const mapped = toBenchmarkProjectSummary(partial({ name: "X", judgeEnabled: true, isFrozen: true, runCount: 4 }));

		expect(mapped).toMatchObject({ judgeEnabled: true, isFrozen: true, runCount: 4 });
	});
});

describe("toBenchmarkProjectDetail", () => {
	it("extends the summary and defaults the judge prompt/schema versions to 1", () => {
		const mapped = toBenchmarkProjectDetail(partial({ name: "X", coreTask: "Summarise the text." }));

		expect(mapped).toMatchObject({
			name: "X",
			coreTask: "Summarise the text.",
			judgeModelName: null,
			judgeContextTokens: null,
			judgePromptVersion: 1,
			judgeOutputSchemaVersion: 1,
		});
	});

	it("keeps an explicit judge model and context", () => {
		const mapped = toBenchmarkProjectDetail(
			partial({ name: "X", coreTask: "T", judgeModelName: "judge.gguf", judgeContextTokens: 8192, judgePromptVersion: 2 }),
		);

		expect(mapped).toMatchObject({ judgeModelName: "judge.gguf", judgeContextTokens: 8192, judgePromptVersion: 2 });
	});
});

describe("toBenchmarkRunSummary", () => {
	// Fail-closed: a run whose status did not survive the round-trip must not read as Succeeded.
	it("defaults a missing primary and judge status to Failed", () => {
		const mapped = toBenchmarkRunSummary(partial({ primaryModelName: "m", modelContentFingerprint: "v1", agentName: "a" }));

		expect(mapped.primaryStatus).toBe("Failed");
		expect(mapped.judgeStatus).toBe("Failed");
	});

	// "Not measured" and "measured as zero" are different facts; the nullable metrics must not collapse into 0.
	it("keeps unmeasured metrics null rather than zero", () => {
		const mapped = toBenchmarkRunSummary(partial({ primaryModelName: "m", modelContentFingerprint: "v1", agentName: "a" }));

		expect(mapped.effectiveContextTokens).toBeNull();
		expect(mapped.durationMs).toBeNull();
		expect(mapped.totalTokens).toBeNull();
		expect(mapped.tokensPerSecond).toBeNull();
		expect(mapped.userScore).toBeNull();
	});

	it("preserves a measured zero", () => {
		const mapped = toBenchmarkRunSummary(
			partial({ primaryModelName: "m", modelContentFingerprint: "v1", agentName: "a", durationMs: 0, totalTokens: 0 }),
		);

		expect(mapped.durationMs).toBe(0);
		expect(mapped.totalTokens).toBe(0);
	});

	// Provenance is an allow-list: anything else is legacy/unknown, which the UI renders as such.
	it.each([
		["huggingface", "huggingface"],
		["imported", "imported"],
		[undefined, null],
		["something-new", null],
		["HuggingFace", null],
	])("maps origin %s to %s", (origin, expected) => {
		const mapped = toBenchmarkRunSummary(
			partial({ primaryModelName: "m", modelContentFingerprint: "v1", agentName: "a", primaryModelOrigin: origin }),
		);

		expect(mapped.primaryModelOrigin).toBe(expected);
	});
});

describe("toBenchmarkRunDetail", () => {
	it("defaults the detail-only fields and returns no judge result", () => {
		const mapped = toBenchmarkRunDetail(partial({ primaryModelName: "m", modelContentFingerprint: "v1", agentName: "a" }));

		expect(mapped.outputParts).toEqual([]);
		expect(mapped.judgeResult).toBeNull();
		expect(mapped.primaryErrorMessage).toBeNull();
		expect(mapped.judgeErrorMessage).toBeNull();
		expect(mapped.startedAtUtc).toBeNull();
	});

	// outputParts is untyped on the wire; anything without a `kind` discriminator cannot be rendered, so it is dropped.
	it("keeps only output parts carrying a kind discriminator", () => {
		const mapped = toBenchmarkRunDetail(
			partial({
				primaryModelName: "m",
				modelContentFingerprint: "v1",
				agentName: "a",
				outputParts: [{ kind: "output", content: "hi" }, { content: "no kind" }, null, "text", 7],
			}),
		);

		expect(mapped.outputParts).toEqual([{ kind: "output", content: "hi" }]);
	});

	it("drops a non-array outputParts payload entirely", () => {
		const mapped = toBenchmarkRunDetail(
			partial({ primaryModelName: "m", modelContentFingerprint: "v1", agentName: "a", outputParts: "not-an-array" }),
		);

		expect(mapped.outputParts).toEqual([]);
	});

	it("maps a judge result, defaulting its versions to 1 and its rationale to empty", () => {
		const mapped = toBenchmarkRunDetail(
			partial({
				primaryModelName: "m",
				modelContentFingerprint: "v1",
				agentName: "a",
				judgeResult: { score: 4 },
			}),
		);

		expect(mapped.judgeResult).toEqual({
			schemaVersion: 1,
			score: 4,
			rationale: "",
			judgeModelContentFingerprint: "",
			promptVersion: 1,
		});
	});
});

describe("toBenchmarkEligibleModel", () => {
	it("defaults the nullable context limits and fails tool support closed", () => {
		const mapped = toBenchmarkEligibleModel(partial({ modelName: "m.gguf", modelContentFingerprint: "v1:abc" }));

		expect(mapped).toEqual({
			modelName: "m.gguf",
			maxContextTokens: null,
			effectiveContextTokens: null,
			origin: null,
			modelContentFingerprint: "v1:abc",
			supportsTools: false,
		});
	});

	it("carries an explicit origin and tool support", () => {
		const mapped = toBenchmarkEligibleModel(
			partial({ modelName: "m.gguf", modelContentFingerprint: "v1:abc", origin: "imported", supportsTools: true }),
		);

		expect(mapped).toMatchObject({ origin: "imported", supportsTools: true });
	});
});

// Launch evidence is optional by contract: a run frozen before the receipt existed carries NULL in every column, and
// the UI must be able to tell that apart from a recorded value. The columns arrive per side under a primary…/judge…
// prefix, so the mapper is asserted to keep the two sides separate rather than reading one into both.
describe("launch evidence mapping", () => {
	it("maps every launch column of a run that recorded one", () => {
		const mapped = toBenchmarkRunSummary(
			partial({
				primaryModelName: "m",
				modelContentFingerprint: "v1",
				agentName: "a",
				primaryVariant: "cuda",
				primaryKvCacheType: "q8_0",
				primaryKvCacheTypeSource: "auto",
				primaryKvAutoReason: "manifest supports q8_0",
				primaryFlashAttentionMode: "on",
				primaryIntendedLaunchIdentity: "identity-1",
				primaryIntendedExecutableSha256: "sha-intended",
				primaryEffectiveLaunchIdentity: "identity-1",
				primaryEffectiveBackend: "cuda",
				primaryPlacementOffloaded: 32,
				primaryPlacementTotal: 32,
				primaryExecutableSha256: "sha-effective",
				primaryHasAuxAssets: false,
				primaryReceiptHash: "receipt-1",
				primaryEnvironmentFactsHash: "env-1",
				judgeKvCacheType: "f16",
				judgeKvCacheTypeSource: "auto",
				judgeReceiptHash: "judge-receipt-1",
			}),
		);

		expect(mapped.primaryLaunch).toEqual({
			variant: "cuda",
			kvCacheType: "q8_0",
			kvCacheTypeSource: "auto",
			kvAutoReason: "manifest supports q8_0",
			flashAttentionMode: "on",
			intendedLaunchIdentity: "identity-1",
			intendedExecutableSha256: "sha-intended",
			effectiveLaunchIdentity: "identity-1",
			effectiveBackend: "cuda",
			placementOffloaded: 32,
			placementTotal: 32,
			executableSha256: "sha-effective",
			hasAuxAssets: false,
			receiptHash: "receipt-1",
			environmentFactsHash: "env-1",
		});
		expect(mapped.judgeLaunch).toMatchObject({ kvCacheType: "f16", receiptHash: "judge-receipt-1", variant: null });
	});

	it("keeps a legacy run's launch facts null instead of inventing defaults", () => {
		const mapped = toBenchmarkRunSummary(partial({ primaryModelName: "m", modelContentFingerprint: "v1", agentName: "a" }));

		expect(mapped.primaryLaunch).toEqual(noBenchmarkLaunchFacts);
		expect(mapped.judgeLaunch).toEqual(noBenchmarkLaunchFacts);
	});

	// A placement of zero offloaded layers is a fact (the GPU took nothing), not an absent measurement.
	it("preserves a zero placement count and rejects an unknown source or flash-attention mode", () => {
		const mapped = toBenchmarkRunSummary(
			partial({
				primaryModelName: "m",
				modelContentFingerprint: "v1",
				agentName: "a",
				primaryPlacementOffloaded: 0,
				primaryPlacementTotal: 32,
				primaryKvCacheTypeSource: "guessed",
				primaryFlashAttentionMode: "off",
			}),
		);

		expect(mapped.primaryLaunch.placementOffloaded).toBe(0);
		expect(mapped.primaryLaunch.placementTotal).toBe(32);
		expect(mapped.primaryLaunch.kvCacheTypeSource).toBeNull();
		expect(mapped.primaryLaunch.flashAttentionMode).toBeNull();
	});

	it("carries the decoded receipt and environment objects through, and nulls a non-object payload", () => {
		const mapped = toBenchmarkRunDetail(
			partial({
				primaryModelName: "m",
				modelContentFingerprint: "v1",
				agentName: "a",
				primaryLaunchReceipt: { variant: "cuda", placement: { outcome: "Full" } },
				primaryEnvironmentFacts: { llamaRuntime: { version: "b10201" } },
				judgeLaunchReceipt: "not-an-object",
			}),
		);

		expect(mapped.primaryLaunchReceipt).toEqual({ variant: "cuda", placement: { outcome: "Full" } });
		expect(mapped.primaryEnvironmentFacts).toEqual({ llamaRuntime: { version: "b10201" } });
		expect(mapped.judgeLaunchReceipt).toBeNull();
		expect(mapped.judgeEnvironmentFacts).toBeNull();
	});
});
