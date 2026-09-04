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
						agentDefinitionId,
						status: "Active",
						createdAtUtc: 1_700_000_000_000,
						lastActivityUtc: 1_700_000_010_000,
						executionCount: 3,
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
		expect(result.current.data?.[0]).toEqual({
			id: sessionId,
			triggerId,
			triggerName: "Sensor hub",
			agentDefinitionId,
			status: "Active",
			createdAtUtc: 1_700_000_000_000,
			lastActivityUtc: 1_700_000_010_000,
			executionCount: 3,
		});
	});

	it("sends the validator's maximum window and neither filter on the default read", async () => {
		const requests = listRoute();
		const { wrapper } = harness();

		renderHook(() => useIntegrationSessions(), { wrapper });

		await waitFor(() => {
			expect(requests).toHaveLength(1);
		});
		expect(firstQuery(requests).get("limit")).toBe("200");
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
