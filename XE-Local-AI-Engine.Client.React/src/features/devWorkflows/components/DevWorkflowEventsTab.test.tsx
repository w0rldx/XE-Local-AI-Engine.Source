// @vitest-environment jsdom

// The events tab is anchored on the NEWEST end of the log (R-C4), which makes "there is nothing on screen" and "there
// is nothing in this run" two different states. Conflating them strands the operator: the anchored window is a range of
// SEQUENCE numbers, the run's counter is shared with node-runs and artifacts, and a wide fan-out can therefore leave a
// tail window holding no events while the log behind it is full.

import { MantineProvider } from "@mantine/core";
import { QueryClientProvider } from "@tanstack/react-query";
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
		readonly anchorParam?: number;
		readonly onAnchorChange?: (anchor: DevWorkflowEventsAnchor) => void;
		readonly onLoadMore?: () => void;
	} = {},
) {
	const onAnchorChange = options.onAnchorChange ?? vi.fn();
	const onLoadMore = options.onLoadMore ?? vi.fn();
	const view = renderWithProviders(
		<DevWorkflowEventsTab
			events={options.events ?? []}
			labelByNodeRunId={new Map()}
			hasMore={options.hasMore ?? false}
			isLoadingMore={false}
			anchor={options.anchor ?? "newest"}
			anchorParam={options.anchorParam ?? 0}
			onAnchorChange={onAnchorChange}
			onLoadMore={onLoadMore}
			onSelectNode={vi.fn()}
		/>,
	);
	return { onAnchorChange, onLoadMore, view };
}

/**
 * Re-renders the same tab with a new anchor cursor — what a live run crossing a page boundary looks like.
 *
 * The provider stack is repeated VERBATIM from `renderWithProviders`: Testing Library's `rerender` replaces the root
 * element, and a different root type would unmount the component and take the state under test with it.
 */
function rerenderWithAnchorParam(view: ReturnType<typeof renderWithProviders>, anchorParam: number, onLoadMore = vi.fn()) {
	view.rerender(
		<QueryClientProvider client={view.queryClient}>
			<MantineProvider>
				<DevWorkflowEventsTab
					events={[]}
					labelByNodeRunId={new Map()}
					hasMore={true}
					isLoadingMore={false}
					anchor="newest"
					anchorParam={anchorParam}
					onAnchorChange={vi.fn()}
					onLoadMore={onLoadMore}
					onSelectNode={vi.fn()}
				/>
			</MantineProvider>
		</QueryClientProvider>,
	);
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

	it("says so when a live run re-anchored the feed and took the loaded older pages with it", () => {
		const { view } = renderTab({ anchorParam: 200, hasMore: true });

		expect(screen.queryByTestId("dev-workflow-events-reanchored")).toBeNull();

		// Paging back is what puts history at risk; the anchored page itself is refetched either way.
		fireEvent.click(screen.getByTestId("dev-workflow-events-load-more"));
		rerenderWithAnchorParam(view, 400);

		expect(screen.getByTestId("dev-workflow-events-reanchored")).toBeDefined();
	});

	it("stays quiet when the re-anchor discarded nothing the operator had paged back to", () => {
		// A live run crosses a boundary every 200 sequences. On a feed nobody has paged back through, that reloads the
		// one page already on screen — announcing it would train the operator to ignore the notice that matters.
		const { view } = renderTab({ anchorParam: 200, hasMore: true });

		rerenderWithAnchorParam(view, 400);

		expect(screen.queryByTestId("dev-workflow-events-reanchored")).toBeNull();
	});

	it("stays quiet when the cursor first arrives — there was no loaded history to lose", () => {
		// A feed mounts before the run payload lands, so its cursor is 0 until `lastSequence` shows up. That first move
		// discards nothing, and announcing it would train the operator to ignore the notice that matters.
		const { view } = renderTab({ anchorParam: 0, hasMore: true });

		rerenderWithAnchorParam(view, 400);

		expect(screen.queryByTestId("dev-workflow-events-reanchored")).toBeNull();
	});

	it("clears the notice once the operator loads the older pages back", () => {
		const { view } = renderTab({ anchorParam: 200, hasMore: true });
		fireEvent.click(screen.getByTestId("dev-workflow-events-load-more"));
		rerenderWithAnchorParam(view, 400);

		fireEvent.click(screen.getByTestId("dev-workflow-events-load-more"));

		expect(screen.queryByTestId("dev-workflow-events-reanchored")).toBeNull();
	});

	it("clears the notice when the operator switches ends, because that jump is their own act", () => {
		const { view } = renderTab({ anchorParam: 200, hasMore: true });
		fireEvent.click(screen.getByTestId("dev-workflow-events-load-more"));
		rerenderWithAnchorParam(view, 400);
		expect(screen.getByTestId("dev-workflow-events-reanchored")).toBeDefined();

		fireEvent.click(screen.getByTestId("dev-workflow-events-jump-oldest"));

		expect(screen.queryByTestId("dev-workflow-events-reanchored")).toBeNull();
	});
});
