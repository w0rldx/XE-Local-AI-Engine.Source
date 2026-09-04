// @vitest-environment jsdom

// The server-side half of the sessions filters (ruling R3-12). Both the trigger and the status filter must reach the
// request: a status narrowed in the browser would hide sessions that match it but fall outside the bounded window.

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { renderHook, waitFor } from "@testing-library/react";
import { http, HttpResponse } from "msw";
import type { ReactNode } from "react";
import { describe, expect, it } from "vitest";

import {
	useDeleteIntegrationSession,
	useIntegrationSessions,
} from "@/features/integrations/queries/useIntegrationSessions";
import { localApiPath } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { setupMswServer } from "@/test/UseMswServer";

setupMswServer();

const sessionId = "44444444-4444-4444-8444-444444444444";
const triggerId = "55555555-5555-4555-8555-555555555555";
const agentDefinitionId = "66666666-6666-4666-8666-666666666666";
const principalId = "77777777-7777-4777-8777-777777777777";

function listRoute(): URLSearchParams[] {
	const requests: URLSearchParams[] = [];
	server.use(
		http.get(localApiPath("integrations/sessions"), ({ request }) => {
			requests.push(new URL(request.url).searchParams);
			return HttpResponse.json({
				items: [
					{
						id: sessionId,
						triggerId,
						triggerName: "Sensor hub",
						principalId,
						agentDefinitionId,
						status: "Active",
						createdAtUtc: 1_700_000_000_000,
						lastActivityUtc: 1_700_000_010_000,
						executionCount: 3,
					},
				],
				totalCount: 7,
			});
		}),
	);
	return requests;
}

/** The row id one page of {@link deferredListRoute} carries, which is how a test tells the pages apart. */
function sessionIdAtOffset(offset: number): string {
	return `44444444-4444-4444-8444-${String(offset).padStart(12, "0")}`;
}

/**
 * The list served BY PAGE, with page two held until the test releases it. `limit`/`offset` are part of the query key,
 * so page two is a cache entry with no data of its own — the window in which the pager used to read a total of 0.
 */
function deferredListRoute(): { requests: URLSearchParams[]; releasePageTwo: () => void } {
	const requests: URLSearchParams[] = [];
	// `Promise.withResolvers` would say this in one line, but the project's lib target is below es2024.
	let releasePageTwo!: () => void;
	const pageTwo = new Promise<void>((resolve) => {
		releasePageTwo = resolve;
	});
	server.use(
		http.get(localApiPath("integrations/sessions"), async ({ request }) => {
			const params = new URL(request.url).searchParams;
			requests.push(params);
			const offset = Number(params.get("offset") ?? "0");
			if (offset > 0) {
				await pageTwo;
			}
			return HttpResponse.json({
				items: [
					{
						id: sessionIdAtOffset(offset),
						triggerId,
						triggerName: "Sensor hub",
						principalId,
						agentDefinitionId,
						status: "Active",
						createdAtUtc: 1_700_000_000_000,
						lastActivityUtc: 1_700_000_010_000,
						executionCount: 3,
					},
				],
				totalCount: 130,
			});
		}),
	);
	return { requests, releasePageTwo };
}

/** The query string of the first recorded request, as a total value so the assertions need no non-null dance. */
function firstQuery(requests: readonly URLSearchParams[]): URLSearchParams {
	return requests.at(0) ?? new URLSearchParams();
}

function harness(): { wrapper: ({ children }: { children: ReactNode }) => ReactNode } {
	const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
	return { wrapper: ({ children }) => <QueryClientProvider client={queryClient}>{children}</QueryClientProvider> };
}

describe("useIntegrationSessions", () => {
	it("maps the list DTO to the domain view-model", async () => {
		listRoute();
		const { wrapper } = harness();

		const { result } = renderHook(() => useIntegrationSessions(), { wrapper });

		await waitFor(() => {
			expect(result.current.data).toBeDefined();
		});
		expect(result.current.data?.items[0]).toEqual({
			id: sessionId,
			triggerId,
			triggerName: "Sensor hub",
			principalId,
			agentDefinitionId,
			status: "Active",
			createdAtUtc: 1_700_000_000_000,
			lastActivityUtc: 1_700_000_010_000,
			executionCount: 3,
		});
	});

	it("surfaces the server's total, which is what a pager can honestly number", async () => {
		listRoute();
		const { wrapper } = harness();

		const { result } = renderHook(() => useIntegrationSessions(), { wrapper });

		await waitFor(() => {
			expect(result.current.data).toBeDefined();
		});
		expect(result.current.data?.totalCount).toBe(7);
	});

	it("sends the default page and neither filter on the default read", async () => {
		const requests = listRoute();
		const { wrapper } = harness();

		renderHook(() => useIntegrationSessions(), { wrapper });

		await waitFor(() => {
			expect(requests).toHaveLength(1);
		});
		expect(firstQuery(requests).get("limit")).toBe("50");
		expect(firstQuery(requests).get("offset")).toBe("0");
		expect(firstQuery(requests).get("triggerId")).toBeNull();
		expect(firstQuery(requests).get("status")).toBeNull();
	});

	it("sends both filters as query parameters once they are selected", async () => {
		const requests = listRoute();
		const { wrapper } = harness();

		renderHook(() => useIntegrationSessions({ triggerId, status: "Closed" }), { wrapper });

		await waitFor(() => {
			expect(requests).toHaveLength(1);
		});
		expect(firstQuery(requests).get("triggerId")).toBe(triggerId);
		expect(firstQuery(requests).get("status")).toBe("Closed");
	});

	it("asks the server for the page it was given, not the first one", async () => {
		const requests = listRoute();
		const { wrapper } = harness();

		renderHook(() => useIntegrationSessions({}, { limit: 25, offset: 50 }), { wrapper });

		await waitFor(() => {
			expect(requests).toHaveLength(1);
		});
		expect(firstQuery(requests).get("limit")).toBe("25");
		expect(firstQuery(requests).get("offset")).toBe("50");
	});

	// The pager bounce: page two's cache entry starts empty, so a hook that answered `undefined` there let the page
	// read a total of 0, compute one page, and clamp back to page one while the offset-50 request was in flight.
	it("holds the previous page's rows and total while the next page loads", async () => {
		const { requests, releasePageTwo } = deferredListRoute();
		const { wrapper } = harness();

		const { result, rerender } = renderHook(
			({ offset }: { offset: number }) => useIntegrationSessions({}, { limit: 50, offset }),
			{ wrapper, initialProps: { offset: 0 } },
		);

		await waitFor(() => {
			expect(result.current.data?.items.at(0)?.id).toBe(sessionIdAtOffset(0));
		});

		rerender({ offset: 50 });

		await waitFor(() => {
			expect(requests).toHaveLength(2);
		});
		expect(requests.at(1)?.get("offset")).toBe("50");
		expect(result.current.data?.totalCount).toBe(130);
		expect(result.current.data?.items.at(0)?.id).toBe(sessionIdAtOffset(0));

		releasePageTwo();

		await waitFor(() => {
			expect(result.current.data?.items.at(0)?.id).toBe(sessionIdAtOffset(50));
		});
		expect(result.current.data?.totalCount).toBe(130);
	});

	it("refetches the list after a session is deleted", async () => {
		const requests = listRoute();
		server.use(http.delete(localApiPath(`integrations/sessions/${sessionId}`), () => new HttpResponse(null, { status: 204 })));
		const { wrapper } = harness();

		const { result } = renderHook(
			() => ({ list: useIntegrationSessions(), remove: useDeleteIntegrationSession() }),
			{ wrapper },
		);

		await waitFor(() => {
			expect(requests).toHaveLength(1);
		});

		result.current.remove.mutate({ path: { sessionId } });

		await waitFor(() => {
			expect(requests.length).toBeGreaterThan(1);
		});
	});
});
