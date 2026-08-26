// @vitest-environment jsdom

import { cleanup, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

const { estimateMock } = vi.hoisted(() => ({ estimateMock: vi.fn() }));

vi.mock("@/core/api/generated", async (importOriginal) => ({
	...(await importOriginal<typeof import("@/core/api/generated")>()),
	getBenchmarkPairwiseEstimate: estimateMock,
}));

import { BenchmarkPairwiseEstimateNote } from "@/features/benchmarks/components/BenchmarkPairwiseEstimateNote";
import { formatEstimatedDuration } from "@/features/benchmarks/models/BenchmarkPairwise";
import { renderWithProviders } from "@/test/RenderWithProviders";

const wire = (overrides = {}) => ({
	data: { eligibleRuns: 12, pairedRuns: 12, cappedRuns: 0, judgeCalls: 132, estimatedSeconds: 600, warn: false, maximumRuns: 16, ...overrides },
});

describe("formatEstimatedDuration", () => {
	it("reads in the unit the number belongs to", () => {
		expect(formatEstimatedDuration(45)).toBe("45 s");
		expect(formatEstimatedDuration(600)).toBe("10 min");
		expect(formatEstimatedDuration(7800)).toBe("2 h 10 min");
	});
});

describe("BenchmarkPairwiseEstimateNote", () => {
	beforeEach(() => vi.clearAllMocks());
	afterEach(cleanup);

	// Pairwise is quadratic and judged both ways: 12 runs is 132 calls. The number belongs next to the control that
	// causes it, not in a log an hour later.
	it("states the call count the cohort will produce", async () => {
		estimateMock.mockResolvedValue(wire());
		renderWithProviders(<BenchmarkPairwiseEstimateNote projectId="p1" />);

		const note = await screen.findByTestId("benchmark-pairwise-estimate");
		expect(note.textContent).toContain("132");
		expect(note.textContent).toContain("10 min");
	});

	// "0 s" would read as instant; an absent estimate must read as absent.
	it("omits the duration entirely when the node cannot estimate one", async () => {
		estimateMock.mockResolvedValue(wire({ estimatedSeconds: null }));
		renderWithProviders(<BenchmarkPairwiseEstimateNote projectId="p1" />);

		const note = await screen.findByTestId("benchmark-pairwise-estimate");
		expect(note.textContent).toContain("132");
		expect(note.textContent).not.toContain("Roughly");
		expect(note.textContent).not.toContain("0 s");
	});

	it("names the runs the cohort cap leaves out, and what they will rank as", async () => {
		estimateMock.mockResolvedValue(wire({ cappedRuns: 4, maximumRuns: 16, warn: true }));
		renderWithProviders(<BenchmarkPairwiseEstimateNote projectId="p1" />);

		expect((await screen.findByTestId("benchmark-pairwise-estimate-capped")).textContent).toContain("pairwise-cap");
	});

	it("says nothing about a cap that leaves nothing out", async () => {
		estimateMock.mockResolvedValue(wire());
		renderWithProviders(<BenchmarkPairwiseEstimateNote projectId="p1" />);

		await screen.findByTestId("benchmark-pairwise-estimate");
		expect(screen.queryByTestId("benchmark-pairwise-estimate-capped")).toBeNull();
	});
});
