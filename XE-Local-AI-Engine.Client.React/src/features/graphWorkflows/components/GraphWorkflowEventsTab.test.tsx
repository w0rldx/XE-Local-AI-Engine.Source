// @vitest-environment jsdom

// The one component in the feature that owns its query, so its test drives the real hook over MSW rather than a stub:
// what the tab has to prove is that a trail cut at the replay cap SAYS so, and a mocked hook would let the banner and
// the flag drift apart.

import { cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { http, HttpResponse } from "msw";
import { afterEach, describe, expect, it } from "vitest";

import { GraphWorkflowEventsTab } from "@/features/graphWorkflows/components/GraphWorkflowEventsTab";
import { graphWorkflowTestIds } from "@/features/graphWorkflows/test/GraphWorkflowFixtures";
import { localApiPath } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { renderWithProviders } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

setupMswServer();

const runId = graphWorkflowTestIds.run;

/** The response schema types an event id as a GUID, so a served row has to carry one. */
function eventId(seq: number): string {
	return `${String(seq).padStart(8, "0")}-0000-4000-8000-000000000000`;
}

interface ServedEvent {
	readonly seq: number;
	readonly eventType: string;
	readonly nodeKey?: string | null;
	readonly detail?: unknown;
}

function serveEvents(events: readonly ServedEvent[], replayTruncated = false): void {
	server.use(
		http.get(localApiPath(`graph-workflows/runs/${runId}/events`), () =>
			HttpResponse.json({
				events: events.map((event) => ({
					id: eventId(event.seq),
					seq: event.seq,
					eventType: event.eventType,
					nodeKey: event.nodeKey ?? null,
					detail: event.detail ?? null,
					createdAtUtc: 1_700_000_200_000 + event.seq,
				})),
				lastSeq: events.at(-1)?.seq ?? 0,
				replayTruncated,
			}),
		),
	);
}

describe("GraphWorkflowEventsTab", () => {
	afterEach(() => {
		cleanup();
	});

	it("labels each event from the eventType vocabulary and names the node it happened on", async () => {
		serveEvents([
			{ seq: 1, eventType: "run.created" },
			{ seq: 2, eventType: "node.retried", nodeKey: "lookup" },
		]);
		renderWithProviders(<GraphWorkflowEventsTab runId={runId} />);

		// Dotted tokens are keyed with underscores (i18next reads `.` as a separator); the raw token must not leak.
		await waitFor(() => expect(screen.getByTestId(`graph-workflow-event-type-${eventId(1)}`).textContent).toBe("Run created"));
		expect(screen.getByTestId(`graph-workflow-event-type-${eventId(2)}`).textContent).toBe("Node retried");
		expect(screen.getByTestId(`graph-workflow-event-node-${eventId(2)}`).textContent).toBe("lookup");
	});

	it("reads the trail newest first", async () => {
		serveEvents([
			{ seq: 1, eventType: "run.created" },
			{ seq: 2, eventType: "run.started" },
		]);
		renderWithProviders(<GraphWorkflowEventsTab runId={runId} />);

		await waitFor(() => expect(screen.getAllByTestId(/^graph-workflow-event-0/)).toHaveLength(2));
		expect(screen.getAllByTestId(/^graph-workflow-event-0/).map((row) => row.getAttribute("data-testid"))).toEqual([
			`graph-workflow-event-${eventId(2)}`,
			`graph-workflow-event-${eventId(1)}`,
		]);
	});

	it("says the trail is cut rather than presenting a silently short list as the whole run", async () => {
		serveEvents([{ seq: 1, eventType: "run.created" }], true);
		renderWithProviders(<GraphWorkflowEventsTab runId={runId} />);

		await waitFor(() => expect(screen.getByTestId("graph-workflow-events-truncated")).toBeDefined());
		expect(screen.getByTestId("graph-workflow-events-load-more")).toBeDefined();
	});

	it("keeps the whole trail on screen when nothing was truncated", async () => {
		serveEvents([{ seq: 1, eventType: "run.created" }]);
		renderWithProviders(<GraphWorkflowEventsTab runId={runId} />);

		await waitFor(() => expect(screen.getByTestId(`graph-workflow-event-${eventId(1)}`)).toBeDefined());
		expect(screen.queryByTestId("graph-workflow-events-truncated")).toBeNull();
		expect(screen.queryByTestId("graph-workflow-events-load-more")).toBeNull();
	});

	it("opens an event's detail document on request", async () => {
		serveEvents([{ seq: 5, eventType: "node.retried", nodeKey: "lookup", detail: { failureClass: "Timeout" } }]);
		renderWithProviders(<GraphWorkflowEventsTab runId={runId} />);

		await waitFor(() => expect(screen.getByTestId(`graph-workflow-event-toggle-${eventId(5)}`)).toBeDefined());
		const toggle = screen.getByTestId(`graph-workflow-event-toggle-${eventId(5)}`);
		expect(toggle.getAttribute("aria-expanded")).toBe("false");
		fireEvent.click(toggle);

		expect(toggle.getAttribute("aria-expanded")).toBe("true");
		expect(screen.getByTestId(`graph-workflow-event-detail-${eventId(5)}`).textContent).toContain("Timeout");
	});

	it("says nothing has happened rather than rendering an empty feed", async () => {
		serveEvents([]);
		renderWithProviders(<GraphWorkflowEventsTab runId={runId} />);

		await waitFor(() => expect(screen.getByTestId("graph-workflow-events-empty")).toBeDefined());
	});

	it("surfaces a failed feed with a retry rather than an empty run", async () => {
		server.use(
			http.get(localApiPath(`graph-workflows/runs/${runId}/events`), () =>
				HttpResponse.json({ detail: "The run is gone." }, { status: 404 }),
			),
		);
		renderWithProviders(<GraphWorkflowEventsTab runId={runId} />);

		await waitFor(() => expect(screen.getByTestId("graph-workflow-events-error")).toBeDefined());
		expect(screen.getByTestId("graph-workflow-events-retry")).toBeDefined();
	});
});
