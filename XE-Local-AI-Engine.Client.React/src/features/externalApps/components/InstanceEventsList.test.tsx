// @vitest-environment jsdom

// The feed pages FORWARD. `afterSequence` is an exclusive LOWER bound, so "load more" advances it to the last
// sequence returned; decreasing it would hand back the page just read, forever. The other assertion that matters is
// the unknown kind: a newer server's event must read as "Unknown event", never as a raw identifier.

import { fireEvent, screen, waitFor } from "@testing-library/react";
import { HttpResponse, http } from "msw";
import { describe, expect, it } from "vitest";

import { InstanceEventsList } from "@/features/externalApps/components/InstanceEventsList";
import type { ExternalAppInstanceEventView } from "@/features/externalApps/models/ExternalAppModels";
import {
	externalAppInstanceEvent,
	externalAppInstanceEventsResponse,
	externalAppTestIds,
} from "@/features/externalApps/test/ExternalAppFixtures";
import { localApiPath } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { renderWithProviders } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

setupMswServer();

const instanceId = externalAppTestIds.instance;
const eventsPath = `external-apps/instances/${instanceId}/events`;

interface FeedQuery {
	readonly afterSequence: string | null;
	readonly limit: string | null;
}

/**
 * Answers each read with its own page and records the bounds it was asked for. A page's rows are typed as the wire
 * event view, so a fixture that drifts from the generated type fails tsc rather than rendering as blank cells.
 */
function feed(pages: readonly { items: readonly ExternalAppInstanceEventView[]; hasMore: boolean }[]): FeedQuery[] {
	const queries: FeedQuery[] = [];
	server.use(
		http.get(localApiPath(eventsPath), ({ request }) => {
			const url = new URL(request.url);
			queries.push({ afterSequence: url.searchParams.get("afterSequence"), limit: url.searchParams.get("limit") });
			const page = pages[Math.min(queries.length - 1, pages.length - 1)];
			return HttpResponse.json(
				externalAppInstanceEventsResponse({
					items: [...(page?.items ?? [])],
					highestSequence: 0,
					hasMore: page?.hasMore ?? false,
				}),
			);
		}),
	);
	return queries;
}

describe("InstanceEventsList", () => {
	it("reads ascending from zero with the full page size and renders the newest row last", async () => {
		const queries = feed([
			{
				items: [
					externalAppInstanceEvent({ sequence: 1, kind: "Installed" }),
					externalAppInstanceEvent({ sequence: 2, kind: "Started" }),
				],
				hasMore: false,
			},
		]);
		renderWithProviders(<InstanceEventsList instanceId={instanceId} />);

		await waitFor(() => expect(screen.getByTestId("external-app-event-2")).toBeDefined());
		expect(queries[0]).toEqual({ afterSequence: "0", limit: "200" });
		const rows = screen.getByTestId("external-app-events").querySelectorAll("tbody tr");
		expect(rows[0]?.textContent).toContain("Installed");
		expect(rows[1]?.textContent).toContain("Started");
	});

	it("advances afterSequence to the last sequence returned and appends without duplicates", async () => {
		const queries = feed([
			{ items: [externalAppInstanceEvent({ sequence: 1 }), externalAppInstanceEvent({ sequence: 4 })], hasMore: true },
			{ items: [externalAppInstanceEvent({ sequence: 9, kind: "Started" })], hasMore: false },
		]);
		renderWithProviders(<InstanceEventsList instanceId={instanceId} />);
		await waitFor(() => expect(screen.getByTestId("external-app-events-load-more")).toBeDefined());

		fireEvent.click(screen.getByTestId("external-app-events-load-more"));

		await waitFor(() => expect(screen.getByTestId("external-app-event-9")).toBeDefined());
		// 4, the LAST sequence of the page just read — sequences are not contiguous, so a count would be wrong.
		expect(queries[1]?.afterSequence).toBe("4");
		expect(screen.getByTestId("external-app-events").querySelectorAll("tbody tr")).toHaveLength(3);
		expect(screen.queryByTestId("external-app-events-load-more")).toBeNull();
	});

	it("renders an unrecognised kind as the unknown label, never the raw token", async () => {
		feed([{ items: [externalAppInstanceEvent({ sequence: 1, kind: "somethingNewerServersDo" })], hasMore: false }]);
		renderWithProviders(<InstanceEventsList instanceId={instanceId} />);

		await waitFor(() => expect(screen.getByTestId("external-app-event-1")).toBeDefined());
		expect(screen.getByTestId("external-app-event-1").textContent).toContain("Unknown event");
		expect(screen.getByTestId("external-app-event-1").textContent).not.toContain("somethingNewerServersDo");
	});

	it("renders the empty state for an instance nothing has happened to yet", async () => {
		feed([{ items: [], hasMore: false }]);
		renderWithProviders(<InstanceEventsList instanceId={instanceId} />);

		await waitFor(() => expect(screen.getByTestId("external-app-events-empty")).toBeDefined());
	});
	// "No history yet" is a claim about the node. Asserting it while the first read is still in flight makes the panel
	// flash a wrong answer before the real one arrives, which is what the page-level loading gates exist to prevent.
	it("waits for the first read instead of claiming the history is empty", async () => {
		feed([{ items: [externalAppInstanceEvent()], hasMore: false }]);
		renderWithProviders(<InstanceEventsList instanceId={instanceId} />);

		expect(screen.getByTestId("external-app-events-loading")).toBeDefined();
		expect(screen.queryByTestId("external-app-events-empty")).toBeNull();

		await waitFor(() => expect(screen.getByTestId("external-app-event-1")).toBeDefined());
		expect(screen.queryByTestId("external-app-events-loading")).toBeNull();
	});
});
