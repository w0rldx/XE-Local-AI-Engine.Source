// @vitest-environment jsdom

import { cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

// The panel is one query and one mutation over the generated SDK; mocking the two SDK functions keeps the test on the
// component's own rules (when it asks, and what it says about the answer) with no network and no msw handler set.
const { estimateMock, clearCacheMock } = vi.hoisted(() => ({ estimateMock: vi.fn(), clearCacheMock: vi.fn() }));

vi.mock("@/core/api/generated", async (importOriginal) => ({
	...(await importOriginal<typeof import("@/core/api/generated")>()),
	getBenchmarkKldDiskEstimate: estimateMock,
	clearBenchmarkFidelityCache: clearCacheMock,
}));

import { BenchmarkFidelityPanel } from "@/features/benchmarks/components/BenchmarkFidelityPanel";
import { renderWithProviders } from "@/test/RenderWithProviders";

const estimate = {
	estimatedBytes: 25_300_000_000,
	freeDiskBytes: 400_000_000_000,
	cachedBytes: 0,
	chunks: 200,
	contextTokens: 512,
	vocabSize: 151_936,
	formula: "200 chunks × 512 tokens × 151936 vocab × 1.75 B/logit",
	fitsOnDisk: true,
};

describe("BenchmarkFidelityPanel", () => {
	beforeEach(() => {
		vi.clearAllMocks();
		estimateMock.mockResolvedValue({ data: estimate });
		clearCacheMock.mockResolvedValue({ data: undefined });
	});
	afterEach(cleanup);

	// The estimate is a disk probe on the node. Asking for it on every project view would probe the disk for an operator
	// who never opens the section, and the answer moves whenever the disk does — so it is read on open, not on mount.
	it("does not ask the node for an estimate until the section is opened", async () => {
		renderWithProviders(<BenchmarkFidelityPanel projectId="project-1" />);

		expect(estimateMock).not.toHaveBeenCalled();

		fireEvent.click(screen.getByTestId("benchmark-fidelity-toggle"));

		await waitFor(() => expect(estimateMock).toHaveBeenCalledOnce());
	});

	// 25 GB is not a number to discover after the write has started, so it is stated with its arithmetic rather than
	// asserted: the operator can check the formula against the size.
	it("states the cache size and the arithmetic behind it", async () => {
		renderWithProviders(<BenchmarkFidelityPanel projectId="project-1" />);
		fireEvent.click(screen.getByTestId("benchmark-fidelity-toggle"));

		const block = await screen.findByTestId("benchmark-kld-estimate");
		expect(block.textContent).toContain("23.6 GB");
		expect(screen.getByTestId("benchmark-kld-estimate-formula").textContent).toBe(estimate.formula);
		expect(block.textContent).toContain("151936");
	});

	it("warns when the node says the reservation does not fit", async () => {
		estimateMock.mockResolvedValue({ data: { ...estimate, fitsOnDisk: false } });
		renderWithProviders(<BenchmarkFidelityPanel projectId="project-1" />);
		fireEvent.click(screen.getByTestId("benchmark-fidelity-toggle"));

		expect(await screen.findByTestId("benchmark-kld-estimate-too-large")).toBeTruthy();
	});

	it("does not warn when it fits", async () => {
		renderWithProviders(<BenchmarkFidelityPanel projectId="project-1" />);
		fireEvent.click(screen.getByTestId("benchmark-fidelity-toggle"));

		await screen.findByTestId("benchmark-kld-estimate");
		expect(screen.queryByTestId("benchmark-kld-estimate-too-large")).toBeNull();
	});

	it("clears the cached base logits for the project it was given", async () => {
		renderWithProviders(<BenchmarkFidelityPanel projectId="project-1" />);
		fireEvent.click(screen.getByTestId("benchmark-fidelity-toggle"));

		fireEvent.click(await screen.findByTestId("benchmark-fidelity-clear-cache"));

		await waitFor(() =>
			expect(clearCacheMock).toHaveBeenCalledWith(expect.objectContaining({ path: { projectId: "project-1" } })),
		);
	});
});
