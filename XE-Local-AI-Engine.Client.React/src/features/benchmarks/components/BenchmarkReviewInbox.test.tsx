// @vitest-environment jsdom

import { cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

const { listReviewsMock } = vi.hoisted(() => ({ listReviewsMock: vi.fn() }));

vi.mock("@/core/api/generated", async (importOriginal) => ({
	...(await importOriginal<typeof import("@/core/api/generated")>()),
	listBenchmarkReviews: listReviewsMock,
}));

// The run pane has its own suites; here it only has to show WHICH run a row opened.
vi.mock("@/features/benchmarks/components/BenchmarkRunLivePane", () => ({
	BenchmarkRunLivePane: ({ runId }: { runId: string }) => <div data-testid="opened-run">{runId}</div>,
}));

import { BenchmarkReviewInbox } from "@/features/benchmarks/components/BenchmarkReviewInbox";
import type { BenchmarkProjectSummary } from "@/features/benchmarks/models/BenchmarkModels";
import { renderWithProviders } from "@/test/RenderWithProviders";

const project = (id: string, name: string): BenchmarkProjectSummary => ({
	id,
	name,
	contextTokens: 4096,
	maxOutputTokens: null,
	reasoningBudgetTokens: null,
	invocationTimeoutSeconds: null,
	agentDefinitionId: "agent",
	judgeEnabled: true,
	requiresReview: true,
	runCount: 1,
	isFrozen: true,
	version: 1,
	createdAtUtc: 1,
	updatedAtUtc: 1,
});

const projects = [project("p1", "Coding"), project("p2", "Maths")];

const row = (runId: string, state = "pending") => ({
	runId,
	projectId: "p1",
	runVersion: 4,
	primaryModelName: `model-${runId}`,
	taskItemId: "item-1",
	taskItemIndex: 0,
	primaryCompletedAtUtc: 1_700_000_000_000,
	state,
	reviewScore: null,
	reviewedAtUtc: null,
});

const lastQuery = () => listReviewsMock.mock.calls.at(-1)?.[0]?.query;

describe("BenchmarkReviewInbox", () => {
	beforeEach(() => {
		vi.clearAllMocks();
		listReviewsMock.mockResolvedValue({ data: { items: [row("r1"), row("r2")], totalCount: 120, page: 1, take: 50 } });
	});
	afterEach(cleanup);

	it("lists pending reviews across every review project by default", async () => {
		renderWithProviders(<BenchmarkReviewInbox projects={projects} />);

		expect((await screen.findByTestId("benchmark-review-row-r1")).textContent).toContain("Coding");
		expect(lastQuery()).toEqual({ state: "pending", page: 1, take: 50 });
	});

	it("filters by state and by project, starting again on the first page", async () => {
		renderWithProviders(<BenchmarkReviewInbox projects={projects} />);
		await screen.findByTestId("benchmark-review-row-r1");

		fireEvent.click(screen.getByRole("radio", { name: "approved" }));
		await waitFor(() => expect(lastQuery()).toEqual({ state: "approved", page: 1, take: 50 }));

		fireEvent.click(screen.getByTestId("benchmark-review-inbox-project"));
		fireEvent.click(await screen.findByRole("option", { name: "Maths", hidden: true }));
		await waitFor(() => expect(lastQuery()).toEqual({ state: "approved", projectId: "p2", page: 1, take: 50 }));
	});

	it("pages through the inbox", async () => {
		renderWithProviders(<BenchmarkReviewInbox projects={projects} />);
		await screen.findByTestId("benchmark-review-row-r1");

		// 120 rows at 50 a page.
		fireEvent.click(screen.getByRole("button", { name: "3" }));

		await waitFor(() => expect(lastQuery()).toEqual({ state: "pending", page: 3, take: 50 }));
	});

	it("opens the run a row names", async () => {
		renderWithProviders(<BenchmarkReviewInbox projects={projects} />);

		fireEvent.click(await screen.findByTestId("benchmark-review-open-r2"));

		expect(screen.getByTestId("opened-run").textContent).toBe("r2");
	});

	it("says so when nothing waits for review", async () => {
		listReviewsMock.mockResolvedValue({ data: { items: [], totalCount: 0, page: 1, take: 50 } });
		renderWithProviders(<BenchmarkReviewInbox projects={projects} />);

		expect((await screen.findByTestId("benchmark-review-inbox-empty")).textContent).toContain("No run is waiting for review.");
	});

	// Reviewing the last row of the last page leaves that page empty; the inbox steps back instead of stranding there.
	it("steps back to the last page when the current one empties", async () => {
		renderWithProviders(<BenchmarkReviewInbox projects={projects} />);
		await screen.findByTestId("benchmark-review-row-r1");
		listReviewsMock.mockImplementation(({ query }: { query: { page: number } }) =>
			Promise.resolve({
				data: { items: query.page === 3 ? [] : [row("r1")], totalCount: 100, page: query.page, take: 50 },
			}),
		);

		fireEvent.click(screen.getByRole("button", { name: "3" }));

		await waitFor(() => expect(lastQuery()).toEqual({ state: "pending", page: 2, take: 50 }));
		expect(await screen.findByTestId("benchmark-review-row-r1")).toBeTruthy();
	});
});
