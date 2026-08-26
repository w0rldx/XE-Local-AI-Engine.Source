// @vitest-environment jsdom

import { QueryClient } from "@tanstack/react-query";
import { cleanup, screen } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";

import { BenchmarkLaunchCompare } from "@/features/benchmarks/components/BenchmarkLaunchCompare";
import type { BenchmarkRunDetail } from "@/features/benchmarks/models/BenchmarkModels";
import { noBenchmarkLaunchFacts } from "@/features/benchmarks/models/BenchmarkModels";
import { benchmarkFidelityFixture, benchmarkRunDetailFixture } from "@/features/benchmarks/models/BenchmarkTestFixtures";
import { renderWithProviders } from "@/test/RenderWithProviders";

// Two runs of the same task are only worth reading side by side if the operator can see how their launches differed.
// The alerts state the difference as a fact and never claim the runs are (or are not) comparable — that judgement is
// deliberately not made here. Differences that a hash equality check alone would hide are asserted field-by-field.

afterEach(cleanup);

const leftId = "aaaaaaaa-0000-4000-8000-000000000001";
const rightId = "bbbbbbbb-0000-4000-8000-000000000002";

function detail(id: string, overrides: Partial<BenchmarkRunDetail> = {}): BenchmarkRunDetail {
	return benchmarkRunDetailFixture({
		id,
		primaryModelName: `model-${id.slice(0, 1)}.gguf`,
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
		primaryLaunchReceipt: { placement: { outcome: "Full", offloadedLayers: 32, totalLayers: 32 } },
		primaryEnvironmentFacts: { llamaRuntime: { version: "b10201" } },
		...overrides,
	});
}

// The runs are already in the pane's cache by the time the compare block renders, so the test seeds that cache and
// keeps it fresh rather than re-serving the reads.
function renderCompare(...runs: BenchmarkRunDetail[]) {
	const queryClient = new QueryClient({
		defaultOptions: { queries: { retry: false, gcTime: 0, staleTime: Number.POSITIVE_INFINITY } },
	});
	for (const run of runs) {
		queryClient.setQueryData(["benchmarks", "runs", run.id], run);
	}
	return renderWithProviders(<BenchmarkLaunchCompare runIds={runs.map((run) => run.id)} />, { queryClient });
}

describe("BenchmarkLaunchCompare", () => {
	it("shows one launch line per run and reports nothing when both launched identically", () => {
		renderCompare(detail(leftId), detail(rightId));

		expect(screen.getAllByText("KV q8_0 (auto)")).toHaveLength(2);
		expect(screen.getAllByText("CUDA 32/32 layers")).toHaveLength(2);
		expect(screen.queryByTestId("benchmark-primary-launch-differs")).toBeNull();
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

	// Neither hash covers the freeze-side facts, so a KV source that changed between the two runs would leave a
	// hash-driven check silent while the table below it already shows the difference.
	it("reports a difference neither hash covers", () => {
		const right = detail(rightId, {
			primaryLaunch: { ...detail(rightId).primaryLaunch, kvCacheTypeSource: "explicit" },
		});
		renderCompare(detail(leftId), right);

		expect(screen.getByTestId("benchmark-primary-launch-differs").textContent).toContain("launch.kvCacheTypeSource");
	});

	// Every run stamps its own capture time, so a run pair that is otherwise identical must not raise a banner.
	it("raises no banner when the runs differ only by their environment capture time", () => {
		const right = detail(rightId, {
			primaryEnvironmentFacts: { llamaRuntime: { version: "b10201" }, capturedAtUtc: 222 },
		});
		renderCompare(
			detail(leftId, {
				primaryEnvironmentFacts: { llamaRuntime: { version: "b10201" }, capturedAtUtc: 111 },
			}),
			right,
		);

		expect(screen.queryByTestId("benchmark-primary-launch-differs")).toBeNull();
	});
});

// The cap is the caller's, but the table has to render N columns honestly — one per run, in the order asked for,
// with a row flagged when ANY of them disagrees rather than only the first pair.
describe("BenchmarkLaunchCompare across more than two runs", () => {
	afterEach(cleanup);

	const thirdId = "cccccccc-0000-4000-8000-000000000003";

	it("renders one column per compared run and says how many are being compared", () => {
		renderCompare(detail(leftId), detail(rightId), detail(thirdId));

		expect(screen.getByTestId("benchmark-compare-count").textContent).toContain("3");
		const header = screen.getByTestId("benchmark-launch-compare");
		expect(header.querySelectorAll('[data-testid^="benchmark-launch-line-"]')).toHaveLength(3);
	});

	it("flags a field the third run alone disagrees on", () => {
		const third = detail(thirdId, {
			primaryLaunch: { ...detail(thirdId).primaryLaunch, effectiveBackend: "vulkan" },
		});
		renderCompare(detail(leftId), detail(rightId), third);

		const table = screen.getByTestId("benchmark-primary-launch-diff");
		const differing = [...table.querySelectorAll('tr[data-differs="true"]')].map((row) => row.textContent ?? "");
		expect(differing.some((row) => row.includes("launch.effectiveBackend"))).toBe(true);
		// Four cells: the field name plus one value per run. A pairwise table would render three.
		const backendRow = [...table.querySelectorAll("tr")].find((row) => row.textContent?.startsWith("launch.effectiveBackend"));
		expect(backendRow?.querySelectorAll("td")).toHaveLength(4);
	});

	it("renders nothing until every selected run has arrived, so an absent column cannot read as a difference", () => {
		const queryClient = new QueryClient({
			defaultOptions: { queries: { retry: false, gcTime: 0, staleTime: Number.POSITIVE_INFINITY } },
		});
		queryClient.setQueryData(["benchmarks", "runs", leftId], detail(leftId));
		queryClient.setQueryData(["benchmarks", "runs", rightId], detail(rightId));
		renderWithProviders(<BenchmarkLaunchCompare runIds={[leftId, rightId, thirdId]} />, { queryClient });

		expect(screen.queryByTestId("benchmark-launch-compare")).toBeNull();
	});

	// Fidelity rides the same diff engine as launch and throughput. Its one extra rule: a KLD the node marked stale is
	// never rendered as a figure, and a compare table is exactly where a stale figure would do its damage.
	it("compares perplexity and withholds a stale KLD figure", () => {
		renderCompare(
			detail(leftId, { fidelity: benchmarkFidelityFixture({ perplexityMean: 6.7977 }) }),
			detail(rightId, {
				fidelity: benchmarkFidelityFixture({ perplexityMean: 6.9497, kldState: "kld-stale", kldMean: 0.0123 }),
			}),
		);

		const table = screen.getByTestId("benchmark-fidelity-diff");
		expect(table.textContent).toContain("6.7977");
		expect(table.textContent).toContain("6.9497");
		expect(table.textContent).not.toContain("0.0123");
		const kldRow = [...table.querySelectorAll("tr")].find((row) => row.textContent?.startsWith("fidelity.kldMean"));
		expect(kldRow?.textContent).toContain("—");
	});
});
