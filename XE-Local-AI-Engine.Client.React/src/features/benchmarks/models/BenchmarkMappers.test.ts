import { describe, expect, it } from "vitest";

import {
	toBenchmarkEligibleModel,
	toBenchmarkProjectDetail,
	toBenchmarkProjectSummary,
	toBenchmarkRankCohort,
	toBenchmarkRubric,
	toBenchmarkRunDetail,
	toBenchmarkRunFidelity,
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
			// Null, not 0: an absent budget means context-limited, and 0 is a budget the node refuses. The reasoning
			// budget reads the same way — absent is "as much as the window allows", never "no thinking".
			maxOutputTokens: null,
			reasoningBudgetTokens: null,
			invocationTimeoutSeconds: null,
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
		const mapped = toBenchmarkProjectSummary(
			partial({ name: "X", judgeEnabled: true, isFrozen: true, runCount: 4, maxOutputTokens: 2048, reasoningBudgetTokens: 1024 }),
		);

		expect(mapped).toMatchObject({
			judgeEnabled: true,
			isFrozen: true,
			runCount: 4,
			maxOutputTokens: 2048,
			reasoningBudgetTokens: 1024,
		});
	});
});

describe("toBenchmarkProjectDetail", () => {
	it("extends the summary and reports a disabled judge for a project that does not judge", () => {
		const mapped = toBenchmarkProjectDetail(partial({ name: "X", coreTask: "Summarise the text." }));

		expect(mapped).toMatchObject({ name: "X", coreTask: "Summarise the text." });
		expect(mapped.judge).toEqual({
			enabled: false,
			// An absent mode is the node's own default, which is pointwise — never "unknown".
			mode: "pointwise",
			policyRevision: null,
			policyHash: null,
			modelName: null,
			requestedContextTokens: null,
			rubric: null,
			referenceAnswer: null,
			cohortGeneration: null,
			referenceExecutionKey: null,
			promptVersionOutdated: false,
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
			criteria: [{ id: "accuracy", title: "Accuracy", description: "Facts", weight: 50, kind: null, config: null }],
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

		const cancelled = toBenchmarkRunSummary(
			partial({
				primaryModelName: "m",
				modelContentFingerprint: "v1",
				agentName: "a",
				rankExclusionReason: "judge-cancelled",
			}),
		);

		const truncated = toBenchmarkRunSummary(
			partial({
				primaryModelName: "m",
				modelContentFingerprint: "v1",
				agentName: "a",
				rankExclusionReason: "truncated",
				primaryStopReason: "length",
			}),
		);

		expect(excluded).toMatchObject({ qualityScore: null, qualityScoreSource: "none", rankExclusionReason: "policy-outdated" });
		expect(unknownReason.rankExclusionReason).toBeNull();
		expect(cancelled.rankExclusionReason).toBe("judge-cancelled");
		expect(truncated).toMatchObject({ rankExclusionReason: "truncated", primaryStopReason: "length" });
		// An absent stop reason must stay null: a legacy run was never measured, and "stop" would be a claim.
		expect(unknownReason.primaryStopReason).toBeNull();
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
		// Same rule for the pp/tg split: a runtime that timed nothing leaves six nulls, not six zeros.
		expect(mapped.throughput).toEqual({
			ttftMs: null,
			promptTokens: null,
			promptTokensPerSecond: null,
			generationTokens: null,
			generationTokensPerSecond: null,
			cachedPromptTokens: null,
			segmentCount: null,
		});
	});

	// The node derives both rates from the counts and durations it measured, so the mapper carries them through rather
	// than recomputing — a second computation here could disagree with the one the API served.
	it("carries the throughput split through verbatim", () => {
		const mapped = toBenchmarkRunSummary(
			partial({
				primaryModelName: "m",
				modelContentFingerprint: "v1",
				agentName: "a",
				ttftMs: 180.25,
				promptTokens: 123,
				promptTokensPerSecond: 269.4,
				generationTokens: 89,
				generationTokensPerSecond: 88,
				cachedPromptTokens: 7,
				segmentCount: 2,
			}),
		);

		expect(mapped.throughput).toEqual({
			ttftMs: 180.25,
			promptTokens: 123,
			promptTokensPerSecond: 269.4,
			generationTokens: 89,
			generationTokensPerSecond: 88,
			cachedPromptTokens: 7,
			segmentCount: 2,
		});
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
				primaryLaunchIdentitySchemeOutdated: false,
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
			launchIdentitySchemeOutdated: false,
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

// The KLD gate lives on the node, but the mapper is where a contract addition would first leak a figure the reader
// must not compare — so the state is carried through verbatim rather than collapsed to a boolean.
describe("toBenchmarkRunFidelity", () => {
	it("is null for a run the node reports no fidelity row for", () => {
		expect(toBenchmarkRunFidelity(null)).toBeNull();
		expect(toBenchmarkRunFidelity(undefined)).toBeNull();
	});

	it("maps a succeeded perplexity measurement with its comparability facts", () => {
		const fidelity = toBenchmarkRunFidelity({
			status: "succeeded",
			attemptId: "attempt-1",
			perplexityMean: 6.7977,
			perplexityStdErr: 0.07405,
			perplexityChunks: 200,
			perplexityContextTokens: 512,
			perplexityCorpusId: "wikitext2-raw-test@abc123def456",
			kldState: "none",
		});

		expect(fidelity).toMatchObject({
			status: "succeeded",
			perplexityMean: 6.7977,
			perplexityChunks: 200,
			perplexityContextTokens: 512,
			kldState: "none",
			kldMean: null,
		});
	});

	it("fails closed on an unrecognized kldState, so an unknown answer never reads as comparable", () => {
		expect(toBenchmarkRunFidelity({ status: "succeeded", kldState: "something-new" })?.kldState).toBe("none");
	});

	it("falls back to the node's own default for an unrecognized status", () => {
		expect(toBenchmarkRunFidelity({ status: "reticulating", kldState: "none" })?.status).toBe("queued");
	});
});

// The editor sends the mapped rubric straight back on save, so anything the mapper drops is silently deleted from the
// project. `kind` and `config` decide whether a criterion is checked deterministically or read by an LLM — losing
// them on a round-trip would change what the project measures without the operator touching that criterion.
describe("toBenchmarkRubric criterion verifier members", () => {
	it("preserves a criterion's kind and config", () => {
		const rubric = toBenchmarkRubric({
			version: 1,
			criteria: [{ id: "answer", title: "Answer", description: "", weight: 50, kind: "regex", config: '{"pattern":"^42$"}' }],
		});

		expect(rubric?.criteria[0]).toMatchObject({ kind: "regex", config: '{"pattern":"^42$"}' });
	});

	it("reads an absent kind as null, which is the LLM judge", () => {
		const rubric = toBenchmarkRubric({ version: 1, criteria: [{ id: "answer", title: "Answer", description: "", weight: 50 }] });

		expect(rubric?.criteria[0]).toMatchObject({ kind: null, config: null });
	});
});
