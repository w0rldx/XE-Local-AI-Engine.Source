import { describe, expect, it } from "vitest";

import {
	toBenchmarkEligibleModel,
	toBenchmarkProjectDetail,
	toBenchmarkProjectSummary,
	toBenchmarkRankCohort,
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
	it("extends the summary and reports a disabled judge for a project that does not judge", () => {
		const mapped = toBenchmarkProjectDetail(partial({ name: "X", coreTask: "Summarise the text." }));

		expect(mapped).toMatchObject({ name: "X", coreTask: "Summarise the text." });
		expect(mapped.judge).toEqual({
			enabled: false,
			policyRevision: null,
			policyHash: null,
			modelName: null,
			requestedContextTokens: null,
			rubric: null,
			referenceAnswer: null,
			cohortGeneration: null,
			referenceExecutionKey: null,
		});
	});

	// A rubric the node did not send means "the default", which is not the same as a rubric with no criteria.
	it("reads the whole judge policy, keeping an absent rubric absent", () => {
		const withRubric = toBenchmarkProjectDetail(
			partial({
				name: "X",
				coreTask: "T",
				judge: {
					enabled: true,
					policyRevision: 2,
					modelName: "judge.gguf",
					requestedContextTokens: 8192,
					referenceAnswer: "ideal",
					cohortGeneration: 3,
					rubric: { version: 1, criteria: [{ id: "accuracy", title: "Accuracy", description: "Facts", weight: 50 }] },
				},
			}),
		);

		expect(withRubric.judge).toMatchObject({
			enabled: true,
			policyRevision: 2,
			modelName: "judge.gguf",
			requestedContextTokens: 8192,
			referenceAnswer: "ideal",
			cohortGeneration: 3,
		});
		expect(withRubric.judge.rubric).toEqual({
			version: 1,
			criteria: [{ id: "accuracy", title: "Accuracy", description: "Facts", weight: 50 }],
		});

		const withoutRubric = toBenchmarkProjectDetail(partial({ name: "X", coreTask: "T", judge: { enabled: true } }));

		expect(withoutRubric.judge.rubric).toBeNull();
	});
});

describe("toBenchmarkRunSummary", () => {
	// Fail-closed: a run whose status did not survive the round-trip must not read as Succeeded.
	it("defaults a missing primary status to Failed and an absent judging to none", () => {
		const mapped = toBenchmarkRunSummary(partial({ primaryModelName: "m", modelContentFingerprint: "v1", agentName: "a" }));

		expect(mapped.primaryStatus).toBe("Failed");
		expect(mapped.judge.state).toBe("none");
	});

	// Fail-closed both ways: an unknown state is not a verdict, and absent currency flags are not "rankable".
	it("carries the judging through and refuses an unknown state", () => {
		const running = toBenchmarkRunSummary(
			partial({
				primaryModelName: "m",
				modelContentFingerprint: "v1",
				agentName: "a",
				judge: { state: "running", policyRevision: 2, attemptSequence: 3, cohortGeneration: 1, policyCurrent: true },
			}),
		);

		expect(running.judge).toMatchObject({
			state: "running",
			policyRevision: 2,
			attemptSequence: 3,
			cohortGeneration: 1,
			policyCurrent: true,
			executionCurrent: false,
		});

		const unknown = toBenchmarkRunSummary(
			partial({ primaryModelName: "m", modelContentFingerprint: "v1", agentName: "a", judge: { state: "pending" } }),
		);

		expect(unknown.judge.state).toBe("none");
	});

	// The quality score, its source, the rank and the exclusion reason are one story: a rank the node withheld must
	// arrive with the node's reason, and an unrecognised reason must not be invented into the UI's vocabulary.
	it("maps the ranking projection and rejects an unknown exclusion reason", () => {
		const ranked = toBenchmarkRunSummary(
			partial({
				primaryModelName: "m",
				modelContentFingerprint: "v1",
				agentName: "a",
				qualityScore: 72,
				qualityScoreSource: "judge",
				rank: 1,
				modelGroupKey: "v1:group",
			}),
		);

		expect(ranked).toMatchObject({
			qualityScore: 72,
			qualityScoreSource: "judge",
			rank: 1,
			rankExclusionReason: null,
			modelGroupKey: "v1:group",
		});

		const excluded = toBenchmarkRunSummary(
			partial({
				primaryModelName: "m",
				modelContentFingerprint: "v1",
				agentName: "a",
				qualityScoreSource: "weird",
				rankExclusionReason: "policy-outdated",
			}),
		);
		const unknownReason = toBenchmarkRunSummary(
			partial({
				primaryModelName: "m",
				modelContentFingerprint: "v1",
				agentName: "a",
				rankExclusionReason: "something-new",
			}),
		);

		expect(excluded).toMatchObject({ qualityScore: null, qualityScoreSource: "none", rankExclusionReason: "policy-outdated" });
		expect(unknownReason.rankExclusionReason).toBeNull();
	});

	// Grouping must never collapse two different models into one row because the key was missing.
	it("falls back to the content fingerprint when the group key is absent", () => {
		const mapped = toBenchmarkRunSummary(partial({ primaryModelName: "m", modelContentFingerprint: "v1:abc", agentName: "a" }));

		expect(mapped.modelGroupKey).toBe("v1:abc");
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
	it("defaults the detail-only fields", () => {
		const mapped = toBenchmarkRunDetail(partial({ primaryModelName: "m", modelContentFingerprint: "v1", agentName: "a" }));

		expect(mapped.outputParts).toEqual([]);
		expect(mapped.primaryErrorMessage).toBeNull();
		expect(mapped.judge.errorMessage).toBeNull();
		expect(mapped.judge.criteria).toEqual([]);
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

	// The detail projection is the only one carrying the per-criterion breakdown, and a criterion scored 0 is a real
	// verdict rather than a missing one.
	it("maps the judging's summary and every criterion score", () => {
		const mapped = toBenchmarkRunDetail(
			partial({
				primaryModelName: "m",
				modelContentFingerprint: "v1",
				agentName: "a",
				judge: {
					state: "succeeded",
					score: 73,
					summary: "Accurate but terse.",
					criteria: [
						{ id: "accuracy", score: 8, rationale: "Facts check out." },
						{ id: "clarity", score: 0, rationale: "Unreadable." },
					],
				},
			}),
		);

		expect(mapped.judge).toMatchObject({ state: "succeeded", score: 73, summary: "Accurate but terse." });
		expect(mapped.judge.criteria).toEqual([
			{ id: "accuracy", score: 8, rationale: "Facts check out." },
			{ id: "clarity", score: 0, rationale: "Unreadable." },
		]);
	});

	// A failed judging still carries its error; nothing about it touches the primary result.
	it("keeps a failed judging's error message", () => {
		const mapped = toBenchmarkRunDetail(
			partial({
				primaryModelName: "m",
				modelContentFingerprint: "v1",
				agentName: "a",
				judge: { state: "failed", errorMessage: "Judge output was invalid." },
			}),
		);

		expect(mapped.judge).toMatchObject({ state: "failed", score: null, errorMessage: "Judge output was invalid." });
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
// the UI must be able to tell that apart from a recorded value. Only the PRIMARY side is projected onto the run — the
// judge's evidence belongs to its attempt.
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
	});

	it("keeps a legacy run's launch facts null instead of inventing defaults", () => {
		const mapped = toBenchmarkRunSummary(partial({ primaryModelName: "m", modelContentFingerprint: "v1", agentName: "a" }));

		expect(mapped.primaryLaunch).toEqual(noBenchmarkLaunchFacts);
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
			}),
		);

		expect(mapped.primaryLaunchReceipt).toEqual({ variant: "cuda", placement: { outcome: "Full" } });
		expect(mapped.primaryEnvironmentFacts).toEqual({ llamaRuntime: { version: "b10201" } });
	});
});

// The cohort line is the honest half of "n of m ranked": absent counters are zero, but an absent policy revision is
// "no judge policy at all", which must not read as revision 0.
describe("toBenchmarkRankCohort", () => {
	it("defaults the counters and keeps an absent cohort identity absent", () => {
		expect(toBenchmarkRankCohort(undefined)).toEqual({
			policyRevision: null,
			executionKey: null,
			cohortGeneration: null,
			rankedCount: 0,
			totalScored: 0,
		});
	});

	it("carries the cohort identity and counts through", () => {
		expect(toBenchmarkRankCohort({ policyRevision: 2, executionKey: "key", cohortGeneration: 3, rankedCount: 2, totalScored: 5 })).toEqual({
			policyRevision: 2,
			executionKey: "key",
			cohortGeneration: 3,
			rankedCount: 2,
			totalScored: 5,
		});
	});
});
