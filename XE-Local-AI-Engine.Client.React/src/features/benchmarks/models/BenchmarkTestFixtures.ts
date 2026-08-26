import type {
	BenchmarkRunDetail,
	BenchmarkRunFidelity,
	BenchmarkRunJudge,
	BenchmarkRunSummary,
} from "@/features/benchmarks/models/BenchmarkModels";
import { noBenchmarkLaunchFacts, noBenchmarkRunJudge } from "@/features/benchmarks/models/BenchmarkModels";

// One neutral run per shape, so a contract change lands in a single fixture instead of in every benchmark suite.
// Deliberately a succeeded, unjudged, unranked run: every test states the facts it is actually about.

export const benchmarkJudgeFixture = (overrides: Partial<BenchmarkRunJudge> = {}): BenchmarkRunJudge => ({
	...noBenchmarkRunJudge,
	...overrides,
});

/** A succeeded perplexity-only measurement: KLD is opt-in, so `none` is the ordinary state, not a degraded one. */
export const benchmarkFidelityFixture = (overrides: Partial<BenchmarkRunFidelity> = {}): BenchmarkRunFidelity => ({
	status: "succeeded",
	attemptId: "attempt-1",
	perplexityMean: 6.7977,
	perplexityStdErr: 0.074_05,
	perplexityChunks: 200,
	perplexityContextTokens: 512,
	perplexityCorpusId: "wikitext2-raw-test@abc123def456",
	kldState: "none",
	kldMean: null,
	kldP99: null,
	topTokenAgreement: null,
	kldBaseFingerprint: null,
	errorMessage: null,
	...overrides,
});

export function benchmarkRunSummaryFixture(overrides: Partial<BenchmarkRunSummary> = {}): BenchmarkRunSummary {
	return {
		id: "run-1",
		projectId: "project-1",
		primaryModelName: "model.gguf",
		primaryModelOrigin: null,
		modelContentFingerprint: "v1:test",
		modelGroupKey: "v1:test",
		repeatGroupId: null,
		repeatIndex: null,
		isWarmup: false,
		repeatMode: "Throughput",
		samplingSeed: null,
		samplingTemperature: null,
		agentName: "agent",
		agentVersion: 1,
		requestedContextTokens: 4096,
		primaryStatus: "Succeeded",
		judge: noBenchmarkRunJudge,
		qualityScore: null,
		qualityScoreSource: "none",
		rank: null,
		rankExclusionReason: null,
		primaryStopReason: "stop",
		effectiveContextTokens: 4096,
		durationMs: 1250,
		totalTokens: 30,
		tokensPerSecond: 24,
		throughput: {
			ttftMs: 180,
			promptTokens: 512,
			promptTokensPerSecond: 640,
			generationTokens: 30,
			generationTokensPerSecond: 24,
			cachedPromptTokens: 0,
			segmentCount: 1,
		},
		fidelity: null,
		userScore: null,
		lastStreamSequence: 2,
		version: 3,
		createdAtUtc: 1,
		updatedAtUtc: 2,
		primaryLaunch: noBenchmarkLaunchFacts,
		...overrides,
	};
}

export function benchmarkRunDetailFixture(overrides: Partial<BenchmarkRunDetail> = {}): BenchmarkRunDetail {
	return {
		...benchmarkRunSummaryFixture(),
		primaryLaunchReceipt: null,
		primaryEnvironmentFacts: null,
		outputParts: [],
		primaryErrorMessage: null,
		startedAtUtc: 1,
		primaryCompletedAtUtc: 2,
		reasoningBudgetTokens: null,
		reasoningBudgetApplicable: null,
		...overrides,
	};
}
