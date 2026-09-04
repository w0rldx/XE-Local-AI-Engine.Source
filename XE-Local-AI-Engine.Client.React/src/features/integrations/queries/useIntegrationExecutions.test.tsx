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
			});
		}),
	);
	return requests;
}

function eventsRoute(): URLSearchParams[] {
	const requests: URLSearchParams[] = [];
	server.use(
		http.get(localApiPath(`integrations/executions/${executionId}/events`), ({ request }) => {
			requests.push(new URL(request.url).searchParams);
			return HttpResponse.json({
				items: [
					{
						executionId,
						sequence: 1,
						eventType: "execution.accepted",
						detailJson: null,
						occurredAtUtc: 1_700_000_000_000,
					},
				],
			});
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
		expect(result.current.data?.[0]).toEqual({
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

	it("sends the bounded window and no filter on the default read", async () => {
		const requests = listRoute();
		const { wrapper } = harness();

		renderHook(() => useIntegrationExecutions(), { wrapper });

		await waitFor(() => {
			expect(requests).toHaveLength(1);
		});
		expect(firstQuery(requests).get("limit")).toBe("200");
		expect(firstQuery(requests).get("offset")).toBe("0");
		expect(firstQuery(requests).get("status")).toBeNull();
		expect(firstQuery(requests).get("triggerId")).toBeNull();
		expect(firstQuery(requests).get("sessionId")).toBeNull();
	});

	it("sends every filter it was given as a query parameter", async () => {
		const requests = listRoute();
		const { wrapper } = harness();

		renderHook(() => useIntegrationExecutions({ triggerId, sessionId, status: "Running" }), { wrapper });

		await waitFor(() => {
			expect(requests).toHaveLength(1);
		});
		expect(firstQuery(requests).get("triggerId")).toBe(triggerId);
		expect(firstQuery(requests).get("sessionId")).toBe(sessionId);
		expect(firstQuery(requests).get("status")).toBe("Running");
	});

	it("reads the event list whole, from sequence zero, at the endpoint's maximum page", async () => {
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

	it("does not read events until an execution is selected", async () => {
		const requests = eventsRoute();
		const { wrapper } = harness();

		const { result } = renderHook(() => useIntegrationExecutionEvents(null), { wrapper });

		await waitFor(() => {
			expect(result.current.isLoading).toBe(false);
		});
		expect(requests).toHaveLength(0);
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
