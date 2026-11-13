// @vitest-environment jsdom

// The banner's link is the way back to the Graph Workflows run that drives the task, opened by its own id.

import { cleanup, fireEvent, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { DevelopmentWorkflowTaskBanner } from "@/features/development/components/DevelopmentWorkflowTaskBanner";
import { renderWithProviders } from "@/test/RenderWithProviders";

const navigate = vi.hoisted(() => vi.fn());

vi.mock("@tanstack/react-router", async (importOriginal) => ({
	...(await importOriginal<typeof import("@tanstack/react-router")>()),
	useNavigate: () => navigate,
}));

const runId = "cccccccc-cccc-4ccc-8ccc-cccccccccccc";

function renderBanner() {
	return renderWithProviders(
		<DevelopmentWorkflowTaskBanner
			workflowRunId={runId}
			runEnded={false}
			runUnreadable={false}
			onRetryStatus={() => undefined}
		/>,
	);
}

describe("DevelopmentWorkflowTaskBanner", () => {
	beforeEach(() => {
		navigate.mockClear();
	});

	afterEach(() => {
		cleanup();
	});

	it("opens a Graph Workflows run on the Graph Workflows page by its run id", () => {
		renderBanner();

		fireEvent.click(screen.getByTestId("development-workflow-link"));

		expect(navigate).toHaveBeenCalledWith({ to: "/graph-workflows", search: { runId } });
	});
});
