// @vitest-environment jsdom

// The executions surface is filtered, paged and ordered ENTIRELY by the server, so what these tests pin is the wire:
// which query parameters leave the browser and which stay off it. A filter that narrowed rows without producing a new
// request would hide executions that match it but fall outside the bounded window.

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { renderHook, waitFor } from "@testing-library/react";
import { http, HttpResponse } from "msw";
import type { ReactNode } from "react";
import { describe, expect, it } from "vitest";

import {
	useCancelIntegrationExecution,
	useIntegrationExecutionEvents,
	useIntegrationExecutions,
} from "@/features/integrations/queries/useIntegrationExecutions";
import { localApiPath } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { setupMswServer } from "@/test/UseMswServer";

setupMswServer();

const executionId = "11111111-1111-4111-8111-111111111111";
const triggerId = "22222222-2222-4222-8222-222222222222";
const sessionId = "33333333-3333-4333-8333-333333333333";

/** Records the query string of every executions list request and answers with one row. */
function listRoute(): URLSearchParams[] {
	const requests: URLSearchParams[] = [];
	server.use(
		http.get(localApiPath("integrations/executions"), ({ request }) => {
			requests.push(new URL(request.url).searchParams);
			return HttpResponse.json({
				items: [
					{
						id: executionId,
						triggerId,
						sessionId,
						status: "Failed",
						receivedAtUtc: 1_700_000_000_000,
						startedAtUtc: null,
						endedAtUtc: 1_700_000_002_000,
						failureCategory: "queue-timeout",
						failureSummary: "Waited too long.",
						outputCount: 0,
					},
				],
				totalCount: 412,
			});
		}),
	);
	return requests;
}

/** The row id one page of {@link deferredListRoute} carries, which is how a test tells the pages apart. */
function executionIdAtOffset(offset: number): string {
	return `44444444-4444-4444-8444-${String(offset).padStart(12, "0")}`;
}

/**
 * The list served BY PAGE, with page two held until the test releases it. `limit`/`offset` are part of the query key,
 * so page two is a cache entry with no data of its own — this is the window in which the pager used to read a total
 * of 0 and clamp the operator back to page one.
 */
function deferredListRoute(): { requests: URLSearchParams[]; releasePageTwo: () => void } {
	const requests: URLSearchParams[] = [];
	// `Promise.withResolvers` would say this in one line, but the project's lib target is below es2024.
	let releasePageTwo!: () => void;
	const pageTwo = new Promise<void>((resolve) => {
		releasePageTwo = resolve;
	});
	server.use(
		http.get(localApiPath("integrations/executions"), async ({ request }) => {
			const params = new URL(request.url).searchParams;
			requests.push(params);
			const offset = Number(params.get("offset") ?? "0");
			if (offset > 0) {
				await pageTwo;
			}
			// The id is a guid because the response schema validates it as one; the offset rides in its last block so
			// each page is identifiable.
			return HttpResponse.json({
				items: [
					{
						id: executionIdAtOffset(offset),
						triggerId,
						sessionId,
						status: "Completed",
						receivedAtUtc: 1_700_000_000_000,
						startedAtUtc: null,
						endedAtUtc: null,
						failureCategory: null,
						failureSummary: null,
						outputCount: 0,
					},
				],
				totalCount: 412,
			});
		}),
	);
	return { requests, releasePageTwo };
}

function eventRow(sequence: number, eventType = "execution.accepted"): Record<string, unknown> {
	return { executionId, sequence, eventType, detailJson: null, occurredAtUtc: 1_700_000_000_000 };
}

function eventsRoute(): URLSearchParams[] {
	const requests: URLSearchParams[] = [];
	server.use(
		http.get(localApiPath(`integrations/executions/${executionId}/events`), ({ request }) => {
			requests.push(new URL(request.url).searchParams);
			return HttpResponse.json({ items: [eventRow(1)] });
		}),
	);
	return requests;
}

/**
 * The feed served BY WATERMARK, the way the endpoint documents it: `sinceSeq` is EXCLUSIVE, rows ascend, and a page
 * shorter than the limit means "caught up". 600 events therefore need two requests, and the terminal event is in the
 * second one — the row a single 500-row read used to drop.
 */
function pagedEventsRoute(total: number): URLSearchParams[] {
	const requests: URLSearchParams[] = [];
	server.use(
		http.get(localApiPath(`integrations/executions/${executionId}/events`), ({ request }) => {
			const params = new URL(request.url).searchParams;
			requests.push(params);
			const sinceSeq = Number(params.get("sinceSeq") ?? "0");
			const limit = Number(params.get("limit") ?? "500");
			const items = Array.from({ length: total }, (_unused, index) => index + 1)
				.filter((sequence) => sequence > sinceSeq)
				.slice(0, limit)
				.map((sequence) => eventRow(sequence, sequence === total ? "execution.completed" : "execution.accepted"));
			return HttpResponse.json({ items });
		}),
	);
	return requests;
}

/** The query string of the first recorded request, as a total value so the assertions need no non-null dance. */
function firstQuery(requests: readonly URLSearchParams[]): URLSearchParams {
	return requests.at(0) ?? new URLSearchParams();
}

function harness(): { queryClient: QueryClient; wrapper: ({ children }: { children: ReactNode }) => ReactNode } {
	const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
	return {
		queryClient,
		wrapper: ({ children }) => <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>,
	};
}

describe("useIntegrationExecutions", () => {
	it("maps the list DTO to the domain view-model", async () => {
		listRoute();
		const { wrapper } = harness();

		const { result } = renderHook(() => useIntegrationExecutions(), { wrapper });

		await waitFor(() => {
			expect(result.current.data).toBeDefined();
		});
		expect(result.current.data?.items[0]).toEqual({
			id: executionId,
			triggerId,
			sessionId,
			status: "Failed",
			receivedAtUtc: 1_700_000_000_000,
			startedAtUtc: null,
			endedAtUtc: 1_700_000_002_000,
			failureCategory: "queue-timeout",
			failureSummary: "Waited too long.",
			outputCount: 0,
		});
	});

	it("surfaces the server's total, which is what a pager can honestly number", async () => {
		listRoute();
		const { wrapper } = harness();

		const { result } = renderHook(() => useIntegrationExecutions(), { wrapper });

		await waitFor(() => {
			expect(result.current.data).toBeDefined();
		});
		expect(result.current.data?.totalCount).toBe(412);
	});

	it("sends the default page and no filter on the default read", async () => {
		const requests = listRoute();
		const { wrapper } = harness();

		renderHook(() => useIntegrationExecutions(), { wrapper });

		await waitFor(() => {
			expect(requests).toHaveLength(1);
		});
		expect(firstQuery(requests).get("limit")).toBe("50");
		expect(firstQuery(requests).get("offset")).toBe("0");
		expect(firstQuery(requests).get("status")).toBeNull();
		expect(firstQuery(requests).get("triggerId")).toBeNull();
		expect(firstQuery(requests).get("sessionId")).toBeNull();
	});

	it("sends every filter it was given as a query parameter", async () => {
		const requests = listRoute();
		const { wrapper } = harness();

		renderHook(() => useIntegrationExecutions({ triggerId, sessionId, status: ["Running"] }), { wrapper });

		await waitFor(() => {
			expect(requests).toHaveLength(1);
		});
		expect(firstQuery(requests).get("triggerId")).toBe(triggerId);
		expect(firstQuery(requests).get("sessionId")).toBe(sessionId);
		expect(firstQuery(requests).get("status")).toBe("Running");
	});

	// The Active chip: three states in ONE read, as a repeated parameter, rather than a union assembled in the browser
	// out of pages that would each have their own count.
	it("sends a status SET as a repeated query parameter", async () => {
		const requests = listRoute();
		const { wrapper } = harness();

		renderHook(() => useIntegrationExecutions({ status: ["Accepted", "Queued", "Running"] }), { wrapper });

		await waitFor(() => {
			expect(requests).toHaveLength(1);
		});
		expect(firstQuery(requests).getAll("status")).toEqual(["Accepted", "Queued", "Running"]);
	});

	it("asks the server for the page it was given, not the first one", async () => {
		const requests = listRoute();
		const { wrapper } = harness();

		renderHook(() => useIntegrationExecutions({}, { limit: 25, offset: 75 }), { wrapper });

		await waitFor(() => {
			expect(requests).toHaveLength(1);
		});
		expect(firstQuery(requests).get("limit")).toBe("25");
		expect(firstQuery(requests).get("offset")).toBe("75");
	});

	it("reads a short event page in one request, from sequence zero, at the endpoint's maximum page size", async () => {
		const requests = eventsRoute();
		const { wrapper } = harness();

		const { result } = renderHook(() => useIntegrationExecutionEvents(executionId), { wrapper });

		await waitFor(() => {
			expect(result.current.data).toHaveLength(1);
		});
		expect(firstQuery(requests).get("sinceSeq")).toBe("0");
		expect(firstQuery(requests).get("limit")).toBe("500");
		expect(result.current.data?.[0]).toEqual({
			sequence: 1,
			eventType: "execution.accepted",
			detailJson: null,
			occurredAtUtc: 1_700_000_000_000,
		});
	});

	// The regression F-24 named: an execution with more events than one page holds lost its tail, and events ascend,
	// so the row lost first was the terminal one.
	it("pages the event list on the watermark until a short page and returns every event in order", async () => {
		const requests = pagedEventsRoute(600);
		const { wrapper } = harness();

		const { result } = renderHook(() => useIntegrationExecutionEvents(executionId), { wrapper });

		await waitFor(() => {
			expect(result.current.data).toHaveLength(600);
		});
		expect(requests.map((request) => request.get("sinceSeq"))).toEqual(["0", "500"]);
		expect(requests.map((request) => request.get("limit"))).toEqual(["500", "500"]);
		expect(result.current.data?.map((event) => event.sequence)).toEqual(
			Array.from({ length: 600 }, (_unused, index) => index + 1),
		);
		expect(result.current.data?.at(-1)).toEqual({
			sequence: 600,
			eventType: "execution.completed",
			detailJson: null,
			occurredAtUtc: 1_700_000_000_000,
		});
	});

	// Every refetch re-pages from the start: the cache entry is the WHOLE log, so a poll that resumed from the last
	// watermark would replace it with just the tail.
	// Driven by an explicit refetch rather than `refetchInterval`: the timer version leaves a poll running past the
	// test and its late request lands in the NEXT test's recorder.
	it("re-pages from sequence zero on every refetch", async () => {
		const requests = pagedEventsRoute(600);
		const { wrapper } = harness();

		const { result } = renderHook(() => useIntegrationExecutionEvents(executionId), { wrapper });

		await waitFor(() => {
			expect(result.current.data).toHaveLength(600);
		});

		await result.current.refetch();

		expect(requests.map((request) => request.get("sinceSeq"))).toEqual(["0", "500", "0", "500"]);
		expect(result.current.data).toHaveLength(600);
	});

	it("does not read events until an execution is selected", async () => {
		const requests = eventsRoute();
		const { wrapper } = harness();

		const { result } = renderHook(() => useIntegrationExecutionEvents(null), { wrapper });

		await waitFor(() => {
			expect(result.current.isLoading).toBe(false);
		});
		expect(requests).toHaveLength(0);
	});

	// The pager bounce: page two's cache entry starts empty, so a hook that answered `undefined` there let the page
	// read a total of 0, compute one page, and clamp back to page one while the offset-50 request was in flight.
	it("holds the previous page's rows and total while the next page loads", async () => {
		const { requests, releasePageTwo } = deferredListRoute();
		const { wrapper } = harness();

		const { result, rerender } = renderHook(
			({ offset }: { offset: number }) => useIntegrationExecutions({}, { limit: 50, offset }),
			{ wrapper, initialProps: { offset: 0 } },
		);

		await waitFor(() => {
			expect(result.current.data?.items.at(0)?.id).toBe(executionIdAtOffset(0));
		});

		rerender({ offset: 50 });

		await waitFor(() => {
			expect(requests).toHaveLength(2);
		});
		expect(requests.at(1)?.get("offset")).toBe("50");
		expect(result.current.data?.totalCount).toBe(412);
		expect(result.current.data?.items.at(0)?.id).toBe(executionIdAtOffset(0));

		releasePageTwo();

		await waitFor(() => {
			expect(result.current.data?.items.at(0)?.id).toBe(executionIdAtOffset(50));
		});
		expect(result.current.data?.totalCount).toBe(412);
	});

	it("refetches the list after a cancellation is accepted", async () => {
		const requests = listRoute();
		server.use(http.post(localApiPath(`integrations/executions/${executionId}/cancel`), () => new HttpResponse(null, { status: 202 })));
		const { wrapper } = harness();

		const { result } = renderHook(
			() => ({ list: useIntegrationExecutions(), cancel: useCancelIntegrationExecution() }),
			{ wrapper },
		);

		await waitFor(() => {
			expect(requests).toHaveLength(1);
		});

		result.current.cancel.mutate({ path: { executionId } });

		await waitFor(() => {
			expect(requests.length).toBeGreaterThan(1);
		});
	});

	// A 409 says the run finished first, so the row that offered the cancel button is the stale one — it has to be
	// re-read for exactly the same reason an accepted request does.
	it("refetches the list after a cancellation is refused as already finished", async () => {
		const requests = listRoute();
		server.use(
			http.post(localApiPath(`integrations/executions/${executionId}/cancel`), () =>
				HttpResponse.json(
					{
						status: 409,
						title: "One or more errors occurred!",
						errors: [{ name: "generalErrors", reason: "The execution has already finished." }],
					},
					{ status: 409 },
				),
			),
		);
		const { wrapper } = harness();

		const { result } = renderHook(
			() => ({ list: useIntegrationExecutions(), cancel: useCancelIntegrationExecution() }),
			{ wrapper },
		);

		await waitFor(() => {
			expect(requests).toHaveLength(1);
		});

		result.current.cancel.mutate({ path: { executionId } });

		await waitFor(() => {
			expect(result.current.cancel.isError).toBe(true);
		});
		await waitFor(() => {
			expect(requests.length).toBeGreaterThan(1);
		});
	});
});
