// @vitest-environment jsdom

import { cleanup, fireEvent, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { BenchmarkBatchProgressAlert } from "@/features/benchmarks/components/BenchmarkBatchProgressAlert";
import { renderWithProviders } from "@/test/RenderWithProviders";

// A matrix launch scatters its runs across a table ranked by score, so "is my batch done" is otherwise unanswerable
// without hunting for its rows. The line states every bucket, including the failures it would be tempting to hide.

describe("BenchmarkBatchProgressAlert", () => {
	afterEach(cleanup);

	it("states every bucket of the launch", () => {
		renderWithProviders(
			<BenchmarkBatchProgressAlert
				progress={{ total: 12, done: 5, running: 2, queued: 5, failed: 1 }}
				onDismiss={vi.fn()}
			/>,
		);

		expect(screen.getByTestId("benchmark-batch-progress").textContent).toContain(
			"Batch: 5 of 12 done (2 running, 5 queued, 1 failed)",
		);
	});

	it("can be dismissed while the batch is still running", () => {
		const onDismiss = vi.fn();
		renderWithProviders(
			<BenchmarkBatchProgressAlert progress={{ total: 4, done: 0, running: 1, queued: 3, failed: 0 }} onDismiss={onDismiss} />,
		);

		fireEvent.click(screen.getByLabelText("Close"));

		expect(onDismiss).toHaveBeenCalledTimes(1);
	});
});
