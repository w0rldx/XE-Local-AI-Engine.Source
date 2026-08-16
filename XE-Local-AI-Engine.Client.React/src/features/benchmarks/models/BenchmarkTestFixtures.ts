import type {
	BenchmarkRunDetail,
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

export function benchmarkRunSummaryFixture(overrides: Partial<BenchmarkRunSummary> = {}): BenchmarkRunSummary {
	return {
		id: "run-1",
		projectId: "project-1",
		primaryModelName: "model.gguf",
		primaryModelOrigin: null,
		modelContentFingerprint: "v1:test",
		modelGroupKey: "v1:test",
		agentName: "agent",
		agentVersion: 1,
		requestedContextTokens: 4096,
		primaryStatus: "Succeeded",
		judge: noBenchmarkRunJudge,
		qualityScore: null,
		qualityScoreSource: "none",
		rank: null,
		rankExclusionReason: null,
		effectiveContextTokens: 4096,
		durationMs: 1250,
		totalTokens: 30,
		tokensPerSecond: 24,
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
		...overrides,
	};
}
