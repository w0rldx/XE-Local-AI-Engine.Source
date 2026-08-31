// @vitest-environment jsdom

// The events tab is anchored on the NEWEST end of the log (R-C4), which makes "there is nothing on screen" and "there
// is nothing in this run" two different states. Conflating them strands the operator: the anchored window is a range of
// SEQUENCE numbers, the run's counter is shared with node-runs and artifacts, and a wide fan-out can therefore leave a
// tail window holding no events while the log behind it is full.

import { cleanup, fireEvent, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { DevWorkflowEventsTab } from "@/features/devWorkflows/components/DevWorkflowEventsTab";
import type { DevWorkflowEventsAnchor } from "@/features/devWorkflows/queries/useDevWorkflows";
import { devWorkflowRunEvent } from "@/features/devWorkflows/test/DevWorkflowFixtures";
import { renderWithProviders } from "@/test/RenderWithProviders";

function renderTab(
	options: {
		readonly events?: readonly ReturnType<typeof devWorkflowRunEvent>[];
		readonly anchor?: DevWorkflowEventsAnchor;
		readonly hasMore?: boolean;
		readonly onAnchorChange?: (anchor: DevWorkflowEventsAnchor) => void;
	} = {},
) {
	const onAnchorChange = options.onAnchorChange ?? vi.fn();
	renderWithProviders(
		<DevWorkflowEventsTab
			events={options.events ?? []}
			labelByNodeRunId={new Map()}
			hasMore={options.hasMore ?? false}
			isLoadingMore={false}
			anchor={options.anchor ?? "newest"}
			onAnchorChange={onAnchorChange}
			onLoadMore={vi.fn()}
			onSelectNode={vi.fn()}
		/>,
	);
	return { onAnchorChange };
}

describe("DevWorkflowEventsTab", () => {
	afterEach(() => {
		cleanup();
	});

	it("keeps the anchor controls reachable when the anchored window holds no events", () => {
		// Without this the empty state replaced the whole tab, and the one control that could reach the rest of the log
		// went with it — a blank page one click away from a full log, with the click removed.
		const { onAnchorChange } = renderTab({ events: [], anchor: "newest" });

		expect(screen.getByTestId("dev-workflow-events-empty")).toBeDefined();
		expect(screen.getByTestId("dev-workflow-events-jump-oldest")).toBeDefined();

		fireEvent.click(screen.getByTestId("dev-workflow-events-jump-oldest"));

		expect(onAnchorChange).toHaveBeenCalledWith("oldest");
	});

	it("does not claim nothing has happened when it is only THIS window that is empty", () => {
		renderTab({ events: [], anchor: "newest" });

		// The window is a range of sequence numbers, not a count of events, so an empty one says nothing about the run.
		expect(screen.getByTestId("dev-workflow-events-empty").textContent).toContain("most recent part");
	});

	it("does say nothing has happened when the feed is anchored at the start of the log", () => {
		// Anchored on the oldest end, an empty feed IS an empty run: there is nothing before sequence zero.
		renderTab({ events: [], anchor: "oldest" });

		expect(screen.getByTestId("dev-workflow-events-empty").textContent).toContain("Nothing has happened");
	});

	it("names the direction the load button walks, which is backward while anchored on the newest end", () => {
		renderTab({ events: [devWorkflowRunEvent({ sequence: 9 })], anchor: "newest", hasMore: true });

		expect(screen.getByTestId("dev-workflow-events-load-more").textContent).toBe("Load older");
	});
});
