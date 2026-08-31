// @vitest-environment jsdom

// The event feed is the one unbounded feed, so it is the one that must page by its WATERMARK. Growing `limit` instead
// walks into the server's 500-row clamp, and every event past the 500th of a run becomes permanently unreachable — an
// audit log with a silent ceiling. These tests pin the cursor itself (`sinceSeq` is an EXCLUSIVE lower bound), the
// merge across a page boundary, and the fact that a hub-driven invalidation re-reads the loaded pages without
// double-rendering a row.

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, renderHook, waitFor } from "@testing-library/react";
import { http, HttpResponse } from "msw";
import type { ReactNode } from "react";
import { describe, expect, it } from "vitest";

import {
	devWorkflowEventsPageSize,
	devWorkflowInvalidationKey,
	devWorkflowQueryIds,
	useDevWorkflowRunEvents,
} from "@/features/devWorkflows/queries/useDevWorkflows";
import { devWorkflowRunEvent, devWorkflowTestIds } from "@/features/devWorkflows/test/DevWorkflowFixtures";
import { localApiPath } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { setupMswServer } from "@/test/UseMswServer";

const runId = devWorkflowTestIds.run;

interface FeedPage {
	readonly sequences: readonly number[];
	readonly hasMore: boolean;
}

interface FeedRequest {
	readonly sinceSeq: string | null;
	readonly limit: string | null;
}

/**
 * The feed served BY CURSOR, keyed on the `sinceSeq` the client sends, recording every request. `lastSequence` is the
 * highest sequence of the page — exactly what the endpoint reports — so the recorded cursors are the client's own.
 */
function eventFeed(pages: Readonly<Record<string, FeedPage>>): FeedRequest[] {
	const requests: FeedRequest[] = [];
	server.use(
		http.get(localApiPath(`development-workflows/runs/${runId}/events`), ({ request }) => {
			const params = new URL(request.url).searchParams;
			requests.push({ sinceSeq: params.get("sinceSeq"), limit: params.get("limit") });
			const page = pages[params.get("sinceSeq") ?? ""] ?? { sequences: [], hasMore: false };
			return HttpResponse.json({
				items: page.sequences.map((sequence) => devWorkflowRunEvent({ id: eventId(sequence), sequence })),
				lastSequence: page.sequences.at(-1) ?? 0,
				hasMore: page.hasMore,
			});
		}),
	);
	return requests;
}

/** The response schema types an event id as a GUID, so the rows have to carry one to survive response validation. */
function eventId(sequence: number): string {
	return `${String(sequence).padStart(8, "0")}-0000-4000-8000-000000000000`;
}

function harness(): { queryClient: QueryClient; wrapper: ({ children }: { children: ReactNode }) => ReactNode } {
	const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
	return {
		queryClient,
		wrapper: ({ children }) => <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>,
	};
}

/** `fetchNextPage` settles React state, so it belongs inside `act` the same way a hub emit does. */
async function loadMore(result: { readonly current: { readonly fetchNextPage: () => Promise<unknown> } }): Promise<void> {
	await act(async () => {
		await result.current.fetchNextPage();
	});
}

function sequences(events: readonly { readonly sequence?: number }[] | undefined): number[] {
	return (events ?? []).map((event) => event.sequence ?? 0);
}

setupMswServer();

describe("useDevWorkflowRunEvents", () => {
	it("merges the pages in ascending order across a NON-CONTIGUOUS sequence boundary", async () => {
		// 12 → 19 is a gap, not a loss: the run's counter is shared with node-runs and artifacts.
		eventFeed({ "0": { sequences: [7, 12], hasMore: true }, "12": { sequences: [19, 40], hasMore: false } });
		const { wrapper } = harness();
		const { result } = renderHook(() => useDevWorkflowRunEvents(runId, undefined), { wrapper });

		await waitFor(() => expect(result.current.hasNextPage).toBe(true));
		await loadMore(result);

		await waitFor(() => expect(sequences(result.current.data)).toEqual([7, 12, 19, 40]));
		expect(result.current.hasNextPage).toBe(false);
	});

	it("asks for the next page from the WATERMARK, never a grown limit", async () => {
		const requests = eventFeed({ "0": { sequences: [7, 12], hasMore: true }, "12": { sequences: [19], hasMore: false } });
		const { wrapper } = harness();
		const { result } = renderHook(() => useDevWorkflowRunEvents(runId, undefined), { wrapper });

		await waitFor(() => expect(result.current.hasNextPage).toBe(true));
		await loadMore(result);
		await waitFor(() => expect(sequences(result.current.data)).toEqual([7, 12, 19]));

		expect(requests.map((request) => request.sinceSeq)).toEqual(["0", "12"]);
		// The page size is FIXED. A second request with a bigger limit is the bug this replaced.
		expect(requests.map((request) => request.limit)).toEqual([`${devWorkflowEventsPageSize}`, `${devWorkflowEventsPageSize}`]);
	});

	it("deduplicates on the sequence when a page overlaps the one before it", async () => {
		// The boundary row served twice — what a refetch racing a `fetchNextPage` (or a bound read one row too wide)
		// produces. Un-deduplicated it is a repeated React key and one event rendered twice in an audit log.
		eventFeed({ "0": { sequences: [7, 12], hasMore: true }, "12": { sequences: [12, 19], hasMore: false } });
		const { wrapper } = harness();
		const { result } = renderHook(() => useDevWorkflowRunEvents(runId, undefined), { wrapper });

		await waitFor(() => expect(result.current.hasNextPage).toBe(true));
		await loadMore(result);

		await waitFor(() => expect(sequences(result.current.data)).toEqual([7, 12, 19]));
	});

	it("opens on the NEWEST events by computing the cursor from the run's last sequence", async () => {
		// A fan-out run is exactly the run this matters for: opening at sequence 1 pinned the feed to the run's first
		// minute and put current activity a dozen clicks away. The cursor is quantized to a page boundary — 903 sits in
		// the window starting at 800, and the tail cursor is one window below that — so it changes once per page rather
		// than on every event, which is what keeps loaded older pages from being thrown away.
		const requests = eventFeed({ "600": { sequences: [780, 903], hasMore: false } });
		const { wrapper } = harness();
		const { result } = renderHook(() => useDevWorkflowRunEvents(runId, 903), { wrapper });

		await waitFor(() => expect(sequences(result.current.data)).toEqual([780, 903]));
		expect(requests.map((request) => request.sinceSeq)).toEqual(["600"]);
		// Two windows wide, so the page spans the boundary all the way to the run's actual last sequence.
		expect(requests.map((request) => request.limit)).toEqual([`${devWorkflowEventsPageSize * 2}`]);
	});

	it("walks BACKWARD one window at a time from the tail, and stops at the start of the log", async () => {
		const requests = eventFeed({
			"600": { sequences: [780, 903], hasMore: false },
			"400": { sequences: [455], hasMore: true },
			"200": { sequences: [300], hasMore: true },
			"0": { sequences: [7], hasMore: true },
		});
		const { wrapper } = harness();
		const { result } = renderHook(() => useDevWorkflowRunEvents(runId, 903), { wrapper });

		await waitFor(() => expect(result.current.hasNextPage).toBe(true));
		await loadMore(result);
		await loadMore(result);
		await loadMore(result);

		await waitFor(() => expect(sequences(result.current.data)).toEqual([7, 300, 455, 780, 903]));
		expect(requests.map((request) => request.sinceSeq)).toEqual(["600", "400", "200", "0"]);
		// A window is one page WIDE and run sequences are unique, so a page-sized limit cannot skip a row inside it. At
		// cursor 0 there is nothing older, so the walk ends there even though the server still reports `hasMore`.
		expect(result.current.hasNextPage).toBe(false);
	});

	it("reads a run from its start when the operator anchors on the oldest end", async () => {
		const requests = eventFeed({ "0": { sequences: [7, 12], hasMore: true }, "12": { sequences: [19], hasMore: false } });
		const { wrapper } = harness();
		const { result } = renderHook(() => useDevWorkflowRunEvents(runId, 903, { anchor: "oldest" }), { wrapper });

		await waitFor(() => expect(result.current.hasNextPage).toBe(true));
		await loadMore(result);

		await waitFor(() => expect(sequences(result.current.data)).toEqual([7, 12, 19]));
		expect(requests.map((request) => request.sinceSeq)).toEqual(["0", "12"]);
	});

	it("re-reads every loaded page on a hub-driven invalidation, keeping the merged list intact", async () => {
		const requests = eventFeed({ "0": { sequences: [7, 12], hasMore: true }, "12": { sequences: [19], hasMore: false } });
		const { queryClient, wrapper } = harness();
		const { result } = renderHook(() => useDevWorkflowRunEvents(runId, undefined), { wrapper });

		await waitFor(() => expect(result.current.hasNextPage).toBe(true));
		await loadMore(result);
		await waitFor(() => expect(sequences(result.current.data)).toEqual([7, 12, 19]));

		// Exactly the key every hub ping invalidates.
		await queryClient.invalidateQueries({ queryKey: devWorkflowInvalidationKey(devWorkflowQueryIds.events, { runId }) });

		await waitFor(() => expect(requests.map((request) => request.sinceSeq)).toEqual(["0", "12", "0", "12"]));
		expect(sequences(result.current.data)).toEqual([7, 12, 19]);
	});
});
