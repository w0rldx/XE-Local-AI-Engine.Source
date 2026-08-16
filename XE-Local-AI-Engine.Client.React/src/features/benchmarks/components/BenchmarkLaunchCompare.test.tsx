// @vitest-environment jsdom

import { QueryClient } from "@tanstack/react-query";
import { cleanup, screen } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";

import { BenchmarkLaunchCompare } from "@/features/benchmarks/components/BenchmarkLaunchCompare";
import type { BenchmarkRunDetail } from "@/features/benchmarks/models/BenchmarkModels";
import { noBenchmarkLaunchFacts } from "@/features/benchmarks/models/BenchmarkModels";
import { renderWithProviders } from "@/test/RenderWithProviders";

// Two runs of the same task are only worth reading side by side if the operator can see how their launches differed.
// The alerts state the difference as a fact and never claim the runs are (or are not) comparable — that judgement is
// deliberately not made here. Differences that a hash equality check alone would hide are asserted field-by-field.

afterEach(cleanup);

const leftId = "aaaaaaaa-0000-4000-8000-000000000001";
const rightId = "bbbbbbbb-0000-4000-8000-000000000002";

function detail(id: string, overrides: Partial<BenchmarkRunDetail> = {}): BenchmarkRunDetail {
	return {
		id,
		projectId: "project-1",
		primaryModelName: `model-${id.slice(0, 1)}.gguf`,
		primaryModelOrigin: null,
		modelContentFingerprint: "v1:test",
		agentName: "agent",
		agentVersion: 1,
		requestedContextTokens: 4096,
		primaryStatus: "Succeeded",
		judgeStatus: "Succeeded",
		effectiveContextTokens: 4096,
		durationMs: 1000,
		totalTokens: 10,
		tokensPerSecond: 10,
		userScore: null,
		lastStreamSequence: 1,
		version: 1,
		createdAtUtc: 1,
		updatedAtUtc: 2,
		primaryLaunch: {
			...noBenchmarkLaunchFacts,
			kvCacheType: "q8_0",
			kvCacheTypeSource: "auto",
			flashAttentionMode: "on",
			effectiveBackend: "cuda",
			placementOffloaded: 32,
			placementTotal: 32,
			executableSha256: "a".repeat(64),
			receiptHash: "receipt-1",
			environmentFactsHash: "env-1",
		},
		judgeLaunch: { ...noBenchmarkLaunchFacts, kvCacheType: "f16", receiptHash: "judge-1", environmentFactsHash: "judge-env-1" },
		primaryLaunchReceipt: { placement: { outcome: "Full", offloadedLayers: 32, totalLayers: 32 } },
		judgeLaunchReceipt: { effectiveContextTokens: 4096 },
		primaryEnvironmentFacts: { llamaRuntime: { version: "b10201" } },
		judgeEnvironmentFacts: { llamaRuntime: { version: "b10201" } },
		outputParts: [],
		judgeResult: null,
		primaryErrorMessage: null,
		judgeErrorMessage: null,
		startedAtUtc: 1,
		primaryCompletedAtUtc: 2,
		judgeStartedAtUtc: null,
		judgeCompletedAtUtc: null,
		...overrides,
	};
}

// The two runs are already in the pane's cache by the time the compare block renders, so the test seeds that cache and
// keeps it fresh rather than re-serving the reads.
function renderCompare(left: BenchmarkRunDetail, right: BenchmarkRunDetail) {
	const queryClient = new QueryClient({
		defaultOptions: { queries: { retry: false, gcTime: 0, staleTime: Number.POSITIVE_INFINITY } },
	});
	queryClient.setQueryData(["benchmarks", "runs", left.id], left);
	queryClient.setQueryData(["benchmarks", "runs", right.id], right);
	return renderWithProviders(<BenchmarkLaunchCompare leftRunId={left.id} rightRunId={right.id} />, { queryClient });
}

describe("BenchmarkLaunchCompare", () => {
	it("shows one launch line per run and reports nothing when both launched identically", () => {
		renderCompare(detail(leftId), detail(rightId));

		expect(screen.getAllByText("KV q8_0 (auto)")).toHaveLength(2);
		expect(screen.getAllByText("CUDA 32/32 layers")).toHaveLength(2);
		expect(screen.queryByTestId("benchmark-primary-launch-differs")).toBeNull();
		expect(screen.queryByTestId("benchmark-judge-launch-differs")).toBeNull();
	});

	it("reports a differing primary launch with the differing fields and a per-field table", () => {
		const right = detail(rightId, {
			primaryLaunch: {
				...detail(rightId).primaryLaunch,
				executableSha256: "b".repeat(64),
				receiptHash: "receipt-2",
			},
			primaryEnvironmentFacts: { llamaRuntime: { version: "b10300" } },
		});
		renderCompare(detail(leftId), right);

		const alert = screen.getByTestId("benchmark-primary-launch-differs");
		expect(alert.textContent).toContain("Launch differs");
		expect(alert.textContent).toContain("launch.executableSha256");
		expect(alert.textContent).toContain("environment.llamaRuntime.version");

		const table = screen.getByTestId("benchmark-primary-launch-diff");
		const differing = table.querySelectorAll('tr[data-differs="true"]');
		const differingKeys = [...differing].map((row) => row.textContent ?? "");
		expect(differingKeys.some((row) => row.includes("launch.executableSha256"))).toBe(true);
		expect(differingKeys.some((row) => row.includes("environment.llamaRuntime.version"))).toBe(true);
		expect(differingKeys.some((row) => row.includes("receipt.placement.outcome"))).toBe(false);
	});

	it("warns when only the judge launch differs", () => {
		const right = detail(rightId, {
			judgeLaunch: {
				...noBenchmarkLaunchFacts,
				kvCacheType: "f16",
				receiptHash: "judge-2",
				environmentFactsHash: "judge-env-1",
			},
			judgeEnvironmentFacts: { llamaRuntime: { version: "b10300" } },
		});
		renderCompare(detail(leftId), right);

		expect(screen.queryByTestId("benchmark-primary-launch-differs")).toBeNull();
		const alert = screen.getByTestId("benchmark-judge-launch-differs");
		expect(alert.textContent).toContain("Judge launch/environment differs");
		expect(alert.textContent).toContain("environment.llamaRuntime.version");
	});

	it("renders a dash for evidence a legacy run never recorded", () => {
		const right = detail(rightId, {
			primaryLaunch: { ...noBenchmarkLaunchFacts },
			primaryLaunchReceipt: null,
			primaryEnvironmentFacts: null,
		});
		renderCompare(detail(leftId), right);

		const table = screen.getByTestId("benchmark-primary-launch-diff");
		const backendRow = [...table.querySelectorAll("tr")].find((row) => row.textContent?.startsWith("launch.effectiveBackend"));
		expect(backendRow?.textContent).toContain("—");
	});
	// The two hashes cover disjoint halves of the evidence, so an environment-only capture change (a rebuilt runtime
	// bundle, a driver update) must raise the same report as a receipt change — a receipt-only check would miss it.
	it("reports a difference carried only by the environment-facts hash", () => {
		const right = detail(rightId, {
			primaryLaunch: { ...detail(rightId).primaryLaunch, environmentFactsHash: "env-2" },
			primaryEnvironmentFacts: { llamaRuntime: { version: "b10300" } },
		});
		renderCompare(detail(leftId), right);

		const alert = screen.getByTestId("benchmark-primary-launch-differs");
		expect(alert.textContent).toContain("launch.environmentFactsHash");
		expect(alert.textContent).toContain("environment.llamaRuntime.version");
	});

	it("warns when only the judge environment-facts hash differs", () => {
		const right = detail(rightId, {
			judgeLaunch: { ...detail(rightId).judgeLaunch, environmentFactsHash: "judge-env-2" },
			judgeEnvironmentFacts: { llamaRuntime: { version: "b10300" } },
		});
		renderCompare(detail(leftId), right);

		expect(screen.queryByTestId("benchmark-primary-launch-differs")).toBeNull();
		expect(screen.getByTestId("benchmark-judge-launch-differs").textContent).toContain("launch.environmentFactsHash");
	});
});
