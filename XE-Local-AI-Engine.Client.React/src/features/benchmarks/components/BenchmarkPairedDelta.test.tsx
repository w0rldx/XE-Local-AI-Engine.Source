// @vitest-environment jsdom

import { cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

const { compareMock } = vi.hoisted(() => ({ compareMock: vi.fn() }));

vi.mock("@/core/api/generated", async (importOriginal) => ({
	...(await importOriginal<typeof import("@/core/api/generated")>()),
	compareBenchmarkCells: compareMock,
}));

import { ApiError } from "@/core/api/errors/ApiError";
import { BenchmarkPairedDelta } from "@/features/benchmarks/components/BenchmarkPairedDelta";
import { benchmarkCellFixture } from "@/features/benchmarks/models/BenchmarkTestFixtures";

import { renderWithProviders } from "@/test/RenderWithProviders";

const cells = [
	benchmarkCellFixture({ cellKey: "cell:a", primaryModelName: "owner/Repo:Q4_K_M", kvCacheType: "q8_0" }),
	benchmarkCellFixture({ cellKey: "cell:b", primaryModelName: "owner/Repo:Q8_0", kvCacheType: "q8_0" }),
];

const answer = (delta: Record<string, unknown> | null) => ({
	data: {
		cells: [],
		rankCohort: { rankedCount: 2, totalScored: 2 },
		scorableItemCount: 5,
		pairedDeltas: delta === null ? [] : [delta],
	},
});

const pick = (a: string, b: string) => {
	fireEvent.click(screen.getByTestId("benchmark-paired-a"));
	fireEvent.click(screen.getByRole("option", { name: a }));
	fireEvent.click(screen.getByTestId("benchmark-paired-b"));
	fireEvent.click(screen.getByRole("option", { name: b }));
};

describe("BenchmarkPairedDelta", () => {
	beforeEach(() => vi.clearAllMocks());
	afterEach(cleanup);

	it("asks for nothing until two different combinations are picked", () => {
		renderWithProviders(<BenchmarkPairedDelta projectId="project-1" cells={cells} />);

		expect(screen.getByTestId("benchmark-paired-hint")).toBeTruthy();
		expect(compareMock).not.toHaveBeenCalled();
	});

	// The interval IS the finding. A point estimate without one is the reading this panel exists to replace.
	it("states the difference with its interval and the node's own separated flag", async () => {
		compareMock.mockResolvedValue(
			answer({ aCellKey: "cell:a", bCellKey: "cell:b", sharedItemCount: 5, delta: 6.2, ciLow: 1.4, ciHigh: 13.9, separated: true }),
		);
		renderWithProviders(<BenchmarkPairedDelta projectId="project-1" cells={cells} />);

		pick("owner/Repo:Q4_K_M · q8_0", "owner/Repo:Q8_0 · q8_0");

		expect((await screen.findByTestId("benchmark-paired-value")).textContent).toBe("A − B = +6.2 [+1.4, +13.9]");
		expect(screen.getByTestId("benchmark-paired-separated").textContent).toContain("separated");
		expect(screen.getByTestId("benchmark-paired-detail").textContent).toContain("5 scored items");
	});

	// Rendered from the flag, never re-derived from the bounds — and the sentence is the whole point of the panel.
	it("says the suite does not separate them when zero is inside the interval", async () => {
		compareMock.mockResolvedValue(
			answer({ aCellKey: "cell:a", bCellKey: "cell:b", sharedItemCount: 5, delta: 6.2, ciLow: -1.4, ciHigh: 13.9, separated: false }),
		);
		renderWithProviders(<BenchmarkPairedDelta projectId="project-1" cells={cells} />);

		pick("owner/Repo:Q4_K_M · q8_0", "owner/Repo:Q8_0 · q8_0");

		expect((await screen.findByTestId("benchmark-paired-value")).textContent).toBe("A − B = +6.2 [−1.4, +13.9]");
		expect(screen.getByTestId("benchmark-paired-detail").textContent).toContain("does not separate them");
	});

	// The node reports one entry per UNORDERED pair, so picking B first has to flip the sign rather than show A − B
	// under a B − A heading.
	it("flips the sign when the pair is picked the other way round", async () => {
		compareMock.mockResolvedValue(
			answer({ aCellKey: "cell:a", bCellKey: "cell:b", sharedItemCount: 4, delta: 6.2, ciLow: 1.4, ciHigh: 13.9, separated: true }),
		);
		renderWithProviders(<BenchmarkPairedDelta projectId="project-1" cells={cells} />);

		pick("owner/Repo:Q8_0 · q8_0", "owner/Repo:Q4_K_M · q8_0");

		expect((await screen.findByTestId("benchmark-paired-value")).textContent).toBe("A − B = −6.2 [−13.9, −1.4]");
	});

	// An absent entry means "fewer than three shared items", which is a gap in the measurement and never a tie.
	it("says the suite cannot answer it when the two share too few items", async () => {
		compareMock.mockResolvedValue(answer(null));
		renderWithProviders(<BenchmarkPairedDelta projectId="project-1" cells={cells} />);

		pick("owner/Repo:Q4_K_M · q8_0", "owner/Repo:Q8_0 · q8_0");

		await waitFor(() =>
			expect(screen.getByTestId("benchmark-paired-insufficient").textContent).toContain("not a tie"),
		);
	});

	// A failed request reports NOTHING about how many items the two share. Reading its empty result as "fewer than three
	// shared items" states a finding about the measurement that the node never made, and buries the only actionable
	// thing the operator has.
	it("shows the node's own failure instead of calling it too few shared items", async () => {
		compareMock.mockRejectedValue(
			new ApiError(500, { type: "", title: "Internal Server Error", status: 500, detail: "The comparison cache is rebuilding." }),
		);
		renderWithProviders(<BenchmarkPairedDelta projectId="project-1" cells={cells} />);

		pick("owner/Repo:Q4_K_M · q8_0", "owner/Repo:Q8_0 · q8_0");

		const error = await screen.findByTestId("benchmark-paired-error");
		expect(error.textContent).toContain("The comparison cache is rebuilding.");
		expect(error.textContent).toContain("500");
		expect(screen.queryByTestId("benchmark-paired-insufficient")).toBeNull();
	});

	it("retries the comparison on demand", async () => {
		compareMock.mockRejectedValueOnce(new ApiError(503, { type: "", title: "Unavailable", status: 503, detail: "Busy." }));
		renderWithProviders(<BenchmarkPairedDelta projectId="project-1" cells={cells} />);

		pick("owner/Repo:Q4_K_M · q8_0", "owner/Repo:Q8_0 · q8_0");
		compareMock.mockResolvedValue(
			answer({ aCellKey: "cell:a", bCellKey: "cell:b", sharedItemCount: 5, delta: 6.2, ciLow: 1.4, ciHigh: 13.9, separated: true }),
		);
		fireEvent.click(await screen.findByTestId("benchmark-paired-retry"));

		expect((await screen.findByTestId("benchmark-paired-value")).textContent).toBe("A − B = +6.2 [+1.4, +13.9]");
	});

	it("asks the node for exactly the two cells that were picked", async () => {
		compareMock.mockResolvedValue(answer(null));
		renderWithProviders(<BenchmarkPairedDelta projectId="project-1" cells={cells} />);

		pick("owner/Repo:Q4_K_M · q8_0", "owner/Repo:Q8_0 · q8_0");

		await waitFor(() =>
			expect(compareMock).toHaveBeenCalledWith(
				expect.objectContaining({ path: { projectId: "project-1" }, query: { cellKeys: ["cell:a", "cell:b"] } }),
			),
		);
	});
});
