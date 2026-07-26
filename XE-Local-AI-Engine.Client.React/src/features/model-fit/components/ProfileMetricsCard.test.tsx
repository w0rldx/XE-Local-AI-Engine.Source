// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, render, screen } from "@testing-library/react";
import type { ReactElement } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("react-i18next", () => ({
	useTranslation: () => ({
		t: (key: string, fallback?: string) => fallback ?? key,
	}),
}));

import { ProfileMetricsCard } from "@/features/model-fit/components/ProfileMetricsCard";
import type { InferenceBenchmarkMetrics } from "@/features/model-fit/models/InferenceProfileModels";

function makeMetrics(overrides: Partial<InferenceBenchmarkMetrics> = {}): InferenceBenchmarkMetrics {
	return {
		role: null,
		tokensPerSecond: null,
		ppTokensPerSecond: null,
		ttftMs: null,
		totalLatencyMs: null,
		cacheHitRate: null,
		toolLoopMs: null,
		itemsPerSecond: null,
		inputTokensPerSecond: null,
		p50LatencyMs: null,
		p95LatencyMs: null,
		batchSize: null,
		outputDimension: null,
		valuesFinite: null,
		deterministicOutput: null,
		vramLoadBytes: null,
		vramAfterBytes: null,
		globalFreeVramLoadBytes: null,
		globalFreeVramAfterBytes: null,
		processBudgetVramLoadBytes: null,
		processBudgetVramAfterBytes: null,
		externalPressureDetected: false,
		runs: null,
		...overrides,
	};
}

function renderCard(ui: ReactElement) {
	return render(<MantineProvider>{ui}</MantineProvider>);
}

describe("ProfileMetricsCard", () => {
	beforeEach(() => {
		Object.defineProperty(window, "matchMedia", {
			writable: true,
			value: vi.fn().mockImplementation((query: string) => ({
				matches: false,
				media: query,
				onchange: null,
				addEventListener: vi.fn(),
				removeEventListener: vi.fn(),
				dispatchEvent: vi.fn(),
			})),
		});
		Object.defineProperty(window, "ResizeObserver", {
			writable: true,
			value: class ResizeObserverMock {
				observe = vi.fn();

				unobserve = vi.fn();

				disconnect = vi.fn();
			},
		});
	});

	afterEach(() => {
		cleanup();
	});

	it("renders each present metric with its formatted value", () => {
		const metrics = makeMetrics({
			tokensPerSecond: 42,
			ppTokensPerSecond: 310,
			ttftMs: 120,
			cacheHitRate: 0.75,
			toolLoopMs: 850,
			vramLoadBytes: 6_442_450_944,
			vramAfterBytes: 6_979_321_856,
		});

		renderCard(<ProfileMetricsCard metrics={metrics} testIdSuffix="p1" />);

		// TTFT 120 ms, generation 42.0 tok/s, cache-hit 75 %, VRAM at load 6.0 GB.
		expect(screen.getByTestId("inference-profile-metric-ttft-p1").textContent).toContain("120 ms");
		expect(screen.getByTestId("inference-profile-metric-genTps-p1").textContent).toContain("42.0 tok/s");
		expect(screen.getByTestId("inference-profile-metric-promptTps-p1").textContent).toContain("310.0 tok/s");
		expect(screen.getByTestId("inference-profile-metric-cacheHit-p1").textContent).toContain("75 %");
		expect(screen.getByTestId("inference-profile-metric-toolLoop-p1").textContent).toContain("850 ms");
		expect(screen.getByTestId("inference-profile-metric-vramLoad-p1").textContent).toContain("6.0 GB");
		expect(screen.getByTestId("inference-profile-metric-vramAfter-p1").textContent).toContain("6.5 GB");
	});

	it("omits a metric whose value is null rather than showing a dash", () => {
		const metrics = makeMetrics({ tokensPerSecond: 30, ttftMs: null, cacheHitRate: null });

		renderCard(<ProfileMetricsCard metrics={metrics} testIdSuffix="p2" />);

		// Present metric renders; null ones are absent entirely.
		expect(screen.getByTestId("inference-profile-metric-genTps-p2")).toBeTruthy();
		expect(screen.queryByTestId("inference-profile-metric-ttft-p2")).toBeNull();
		expect(screen.queryByTestId("inference-profile-metric-cacheHit-p2")).toBeNull();
		expect(screen.queryByTestId("inference-profile-metric-vramLoad-p2")).toBeNull();
	});

	it("renders embedding correctness, latency, and explicit VRAM semantics", () => {
		const metrics = makeMetrics({
			role: "Embedding",
			itemsPerSecond: 125.25,
			inputTokensPerSecond: 501,
			p50LatencyMs: 18,
			p95LatencyMs: 29,
			batchSize: 4,
			outputDimension: 768,
			valuesFinite: true,
			deterministicOutput: true,
			vramLoadBytes: 7_516_192_768,
			globalFreeVramLoadBytes: 6_442_450_944,
			globalFreeVramAfterBytes: 5_905_580_032,
			processBudgetVramLoadBytes: 7_516_192_768,
			processBudgetVramAfterBytes: 6_979_321_856,
		});

		renderCard(<ProfileMetricsCard metrics={metrics} testIdSuffix="embedding" />);

		expect(screen.getByTestId("inference-profile-metric-role-embedding").textContent).toContain("Embedding");
		expect(screen.getByTestId("inference-profile-metric-itemsPerSecond-embedding").textContent).toContain("125.3 items/s");
		expect(screen.getByTestId("inference-profile-metric-p95Latency-embedding").textContent).toContain("29 ms");
		expect(screen.getByTestId("inference-profile-metric-outputDimension-embedding").textContent).toContain("768");
		expect(screen.getByTestId("inference-profile-metric-valuesFinite-embedding").textContent).toContain("Yes");
		expect(screen.getByTestId("inference-profile-metric-globalFreeVramLoad-embedding").textContent).toContain("6.0 GB");
		expect(screen.getByTestId("inference-profile-metric-processBudgetVramLoad-embedding").textContent).toContain("7.0 GB");
		expect(screen.queryByTestId("inference-profile-metric-vramLoad-embedding")).toBeNull();
	});

	it("warns when divergent VRAM evidence marks the benchmark invalid", () => {
		renderCard(<ProfileMetricsCard metrics={makeMetrics({ externalPressureDetected: true })} testIdSuffix="pressure" />);

		expect(screen.getByTestId("inference-profile-external-pressure-pressure").textContent).toContain("benchmark is invalid");
	});

	it("shows an empty note when the run reported no metrics", () => {
		renderCard(<ProfileMetricsCard metrics={makeMetrics()} testIdSuffix="p3" />);

		expect(screen.getByTestId("inference-profile-metrics-empty-p3")).toBeTruthy();
	});
});
