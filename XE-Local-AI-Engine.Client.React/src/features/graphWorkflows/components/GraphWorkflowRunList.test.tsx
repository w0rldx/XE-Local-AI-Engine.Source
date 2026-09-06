// @vitest-environment jsdom

import { cleanup, fireEvent, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { GraphWorkflowRunList } from "@/features/graphWorkflows/components/GraphWorkflowRunList";
import { graphWorkflowRunSummary } from "@/features/graphWorkflows/test/GraphWorkflowFixtures";
import { renderWithProviders } from "@/test/RenderWithProviders";

const older = graphWorkflowRunSummary({
	id: "11111111-1111-4111-8111-111111111111",
	createdAtUtc: 1_700_000_100_000,
	startedAtUtc: 1_700_000_100_000,
	completedAtUtc: 1_700_000_150_000,
	status: "Failed",
	failureClass: "NodeFailed",
});
const newer = graphWorkflowRunSummary({ id: "22222222-2222-4222-8222-222222222222", createdAtUtc: 1_700_000_900_000 });

describe("GraphWorkflowRunList", () => {
	afterEach(() => {
		cleanup();
	});

	it("lists the runs newest first, whatever order they arrived in", () => {
		renderWithProviders(<GraphWorkflowRunList runs={[older, newer]} onSelectRun={vi.fn()} />);

		expect(screen.getAllByTestId(/^graph-workflow-run-card-/).map((card) => card.getAttribute("data-testid"))).toEqual([
			`graph-workflow-run-card-${newer.id}`,
			`graph-workflow-run-card-${older.id}`,
		]);
	});

	it("carries the status, the definition version and a failure class onto the row", () => {
		renderWithProviders(<GraphWorkflowRunList runs={[older]} onSelectRun={vi.fn()} />);

		expect(screen.getByTestId(`graph-workflow-run-status-${older.id}`).textContent).toBe("Failed");
		expect(screen.getByTestId(`graph-workflow-run-version-${older.id}`).textContent).toBe("Version 1");
		expect(screen.getByTestId(`graph-workflow-run-failure-${older.id}`).textContent).toBe("The node failed");
		// `None` is "nothing went wrong": a failure word on a healthy run is noise.
		expect(screen.queryByTestId(`graph-workflow-run-failure-${newer.id}`)).toBeNull();
	});

	it("selects a run when its row is clicked", () => {
		const onSelectRun = vi.fn();
		renderWithProviders(<GraphWorkflowRunList runs={[older, newer]} onSelectRun={onSelectRun} />);

		fireEvent.click(screen.getByTestId(`graph-workflow-run-card-${older.id}`));

		expect(onSelectRun).toHaveBeenCalledWith(older.id);
	});

	it("shows the loading, error and empty states instead of a list", () => {
		const loading = renderWithProviders(<GraphWorkflowRunList runs={[]} isLoading={true} onSelectRun={vi.fn()} />);
		expect(screen.getByTestId("graph-workflow-run-list-loading")).toBeDefined();
		loading.unmount();

		const failed = renderWithProviders(
			<GraphWorkflowRunList runs={[]} error={new Error("gone")} onSelectRun={vi.fn()} />,
		);
		expect(screen.getByTestId("graph-workflow-run-list-error")).toBeDefined();
		failed.unmount();

		renderWithProviders(<GraphWorkflowRunList runs={[]} onSelectRun={vi.fn()} />);
		expect(screen.getByTestId("graph-workflow-run-list-empty")).toBeDefined();
		expect(screen.queryByTestId("graph-workflow-run-list")).toBeNull();
	});
});
