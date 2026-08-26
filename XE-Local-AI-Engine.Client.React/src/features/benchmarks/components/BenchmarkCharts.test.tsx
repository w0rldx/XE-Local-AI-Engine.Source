// @vitest-environment jsdom

import { cleanup, screen } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";

import BenchmarkCharts from "@/features/benchmarks/components/BenchmarkCharts";
import { noBenchmarkLaunchFacts } from "@/features/benchmarks/models/BenchmarkModels";
import type { BenchmarkRunSummary } from "@/features/benchmarks/models/BenchmarkModels";
import {
	benchmarkFidelityFixture,
	benchmarkRunDetailFixture,
	benchmarkRunSummaryFixture,
} from "@/features/benchmarks/models/BenchmarkTestFixtures";
import { renderWithProviders } from "@/test/RenderWithProviders";

const run = (overrides: Partial<BenchmarkRunSummary> = {}): BenchmarkRunSummary =>
	benchmarkRunSummaryFixture({ primaryLaunch: { ...noBenchmarkLaunchFacts, kvCacheType: "q8_0" }, ...overrides });

describe("BenchmarkCharts", () => {
	afterEach(cleanup);

	it("says there is nothing to chart rather than drawing empty axes", () => {
		renderWithProviders(<BenchmarkCharts runs={[run({ primaryStatus: "Failed" })]} selectedRuns={[]} />);

		expect(screen.getByTestId("benchmark-charts").textContent).toContain("No measured runs to chart yet.");
		expect(screen.queryByTestId("benchmark-chart-throughput")).toBeNull();
	});

	it("draws the throughput and speed panels from measured runs", () => {
		renderWithProviders(
			<BenchmarkCharts runs={[run({ id: "a", tokensPerSecond: 24 }), run({ id: "b", tokensPerSecond: 26 })]} selectedRuns={[]} />,
		);

		expect(screen.getByTestId("benchmark-chart-throughput")).toBeTruthy();
		expect(screen.getByTestId("benchmark-chart-speed")).toBeTruthy();
		expect(screen.getByTestId("benchmark-chart-ttft")).toBeTruthy();
	});

	// Prefill/decode is tokens per second and time-to-first-token is milliseconds. They are two panels rather than
	// three bars on one axis, because two scales on one axis makes every comparison drawn on it meaningless.
	it("keeps latency on its own axis, not beside the tok/s bars", () => {
		renderWithProviders(<BenchmarkCharts runs={[run()]} selectedRuns={[]} />);

		expect(screen.getByTestId("benchmark-chart-speed")).not.toBe(screen.getByTestId("benchmark-chart-ttft"));
	});

	it("draws a perplexity panel once a model has two measured quants", () => {
		const quant = (name: string, mean: number) =>
			run({
				id: name,
				primaryModelName: `unsloth/model:${name}`,
				modelGroupKey: "unsloth/model",
				fidelity: benchmarkFidelityFixture({ perplexityMean: mean }),
			});
		renderWithProviders(<BenchmarkCharts runs={[quant("Q4_K_M", 6.7977), quant("Q3_K_XL", 6.9497)]} selectedRuns={[]} />);

		expect(screen.getByTestId("benchmark-chart-perplexity")).toBeTruthy();
		// Nothing measured KLD, so no KLD panel is drawn rather than one with empty bars.
		expect(screen.queryByTestId("benchmark-chart-kld")).toBeNull();
	});

	it("hides the reasoning-budget line while every selected run carries the same budget", () => {
		renderWithProviders(
			<BenchmarkCharts
				runs={[run()]}
				selectedRuns={[
					benchmarkRunDetailFixture({ id: "a", reasoningBudgetTokens: 2048, qualityScore: 70 }),
					benchmarkRunDetailFixture({ id: "b", reasoningBudgetTokens: 2048, qualityScore: 80 }),
				]}
			/>,
		);

		expect(screen.queryByTestId("benchmark-chart-reasoning-budget")).toBeNull();
	});

	it("draws the reasoning-budget line once the budget varies", () => {
		renderWithProviders(
			<BenchmarkCharts
				runs={[run()]}
				selectedRuns={[
					benchmarkRunDetailFixture({ id: "a", reasoningBudgetTokens: 1024, qualityScore: 60 }),
					benchmarkRunDetailFixture({ id: "b", reasoningBudgetTokens: 4096, qualityScore: 90 }),
				]}
			/>,
		);

		expect(screen.getByTestId("benchmark-chart-reasoning-budget")).toBeTruthy();
	});
});
