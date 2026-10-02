// @vitest-environment jsdom

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { renderHook, waitFor } from "@testing-library/react";
import { http, HttpResponse } from "msw";
import type { ReactNode } from "react";
import { describe, expect, it, vi } from "vitest";

import { getLogLevelQueryKey, getNodeInfoQueryKey } from "@/core/api/generated/@tanstack/react-query.gen";
import { useLogLevel, useSetLogLevel } from "@/features/diagnostics/queries/useLogLevel";
import { jsonRoute, localApiPath } from "@/test/msw/Handlers";
import { setupMswServer } from "@/test/UseMswServer";

const server = setupMswServer();

function wrapperFor(client: QueryClient) {
	return ({ children }: { children: ReactNode }) => <QueryClientProvider client={client}>{children}</QueryClientProvider>;
}

describe("useLogLevel", () => {
	it("reads the node's verbose flag through the generated client", async () => {
		server.use(jsonRoute("get", "diagnostics/log-level", { verbose: true }));

		const { result } = renderHook(() => useLogLevel(), { wrapper: wrapperFor(new QueryClient()) });

		await waitFor(() => expect(result.current.isSuccess).toBe(true));
		expect(result.current.data).toEqual({ verbose: true });
	});
});

describe("useSetLogLevel", () => {
	it("PUTs the flag and invalidates the log-level and node-info queries", async () => {
		const bodies: unknown[] = [];
		server.use(
			http.put(localApiPath("diagnostics/log-level"), async ({ request }) => {
				bodies.push(await request.json());
				return HttpResponse.json({ verbose: true });
			}),
		);
		const client = new QueryClient();
		const invalidate = vi.spyOn(client, "invalidateQueries");

		const { result } = renderHook(() => useSetLogLevel(), { wrapper: wrapperFor(client) });
		const response = await result.current.mutateAsync({ body: { verbose: true } });

		expect(response).toEqual({ verbose: true });
		expect(bodies).toEqual([{ verbose: true }]);
		expect(invalidate).toHaveBeenCalledWith({ queryKey: getLogLevelQueryKey() });
		expect(invalidate).toHaveBeenCalledWith({ queryKey: getNodeInfoQueryKey() });
	});
});
