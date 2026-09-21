// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { ImageJobList } from "@/features/images/components/ImageJobList";
import type { ImageJobView } from "@/features/images/models/ImageModels";
import { installJsdomEnvironmentMocks, testMantineTheme } from "@/test/MantineTestRender";

// The card's live timeline rides the hub; this file is about the pager, so the transport is driven directly.
vi.mock("@/features/images/hooks/useImageJobHub", () => ({ useImageJobProgress: () => null }));

function job(id: string): ImageJobView {
	return {
		id,
		modelName: "sd-1.5",
		prompt: "a watercolor fox",
		negativePrompt: null,
		status: "Succeeded",
		seed: 42,
		width: 512,
		height: 512,
		steps: 20,
		sampler: "euler_a",
		cfgScale: 7,
		createdAtUtc: 1_700_000_000_000,
		startedAtUtc: 1_700_000_000_000,
		completedAtUtc: 1_700_000_005_000,
		durationMs: 5000,
		imageId: null,
		sanitizedError: null,
	};
}

interface ListProps {
	jobs?: readonly ImageJobView[];
	totalCount: number;
	page: number;
	pageSize?: number;
	onPageChange?: (page: number) => void;
}

function renderList({ jobs = [job("job-1")], totalCount, page, pageSize = 10, onPageChange = vi.fn() }: ListProps) {
	const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
	const view = render(
		<QueryClientProvider client={queryClient}>
			<MantineProvider env="test" theme={testMantineTheme}>
				<ImageJobList
					jobs={jobs}
					totalCount={totalCount}
					page={page}
					pageSize={pageSize}
					isLoading={false}
					cancellingJobId={null}
					onCancel={vi.fn()}
					onPageChange={onPageChange}
					onPageSizeChange={vi.fn()}
				/>
			</MantineProvider>
		</QueryClientProvider>,
	);
	return { ...view, onPageChange };
}

describe("ImageJobList pagination", () => {
	beforeEach(installJsdomEnvironmentMocks);
	afterEach(cleanup);

	// The range is computed from the SERVER's total and the page bounds, never from the array length on screen —
	// which is what lets a bounded window be numbered at all.
	it("numbers the range from the node's total rather than from the page on screen", () => {
		renderList({ totalCount: 42, page: 1 });

		expect(screen.getByTestId("image-job-list-pagination-range").textContent).toContain("1–10 of 42");
	});

	it("renders exactly the page it was given, never re-slicing it", () => {
		renderList({ jobs: [job("job-1"), job("job-2")], totalCount: 42, page: 3 });

		expect(screen.getAllByTestId("image-job-card")).toHaveLength(2);
	});

	// Deleting the last row of the last page: the total shrinks past the active page, which no longer exists. The
	// pager must ask for the last page that does, which re-issues the query — leaving the operator on an empty page
	// is the bug this guards.
	it("steps back when the active page no longer exists after a delete", async () => {
		const onPageChange = vi.fn();
		renderList({ totalCount: 10, page: 2, onPageChange });

		await waitFor(() => {
			expect(onPageChange).toHaveBeenCalledWith(1);
		});
	});

	it("shows the empty state when the node holds no jobs at all", () => {
		renderList({ jobs: [], totalCount: 0, page: 1 });

		expect(screen.queryByTestId("image-job-list-empty")).not.toBeNull();
		expect(screen.queryByTestId("image-job-list-pagination")).toBeNull();
	});
});
