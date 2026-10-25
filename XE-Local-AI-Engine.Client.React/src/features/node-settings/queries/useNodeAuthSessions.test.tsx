// @vitest-environment jsdom

import { QueryClient } from "@tanstack/react-query";
import { renderHook, waitFor } from "@testing-library/react";
import { HttpResponse, http } from "msw";
import { describe, expect, it } from "vitest";

import { listNodeAuthSessionsQueryKey } from "@/core/api/generated/@tanstack/react-query.gen";
import { useNodeAuthSessions } from "@/features/node-settings/queries/useNodeAuthSessions";
import { localApiPath } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { createProvidersWrapper } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

setupMswServer();

function session(id: string) {
	return { id, clientLabel: id, createdAtUtc: "2026-10-25T10:00:00Z", lastUsedAtUtc: "2026-10-25T10:00:00Z", current: true };
}

describe("useNodeAuthSessions", () => {
	it("drops the list when the card unmounts, so the next sign-in never sees the previous session's rows", async () => {
		let served = [session("previous-sign-in")];
		server.use(http.get(localApiPath("auth/sessions"), () => HttpResponse.json(served)));
		// The app's defaults (30 s stale, default gcTime), not the test client's gcTime 0, which would hide the bug.
		const { wrapper, queryClient } = createProvidersWrapper({
			queryClient: new QueryClient({ defaultOptions: { queries: { retry: false, staleTime: 30_000 } } }),
		});

		const first = renderHook(() => useNodeAuthSessions(), { wrapper });
		await waitFor(() => expect(first.result.current.data?.[0]?.id).toBe("previous-sign-in"));
		first.unmount();
		await waitFor(() => expect(queryClient.getQueryData(listNodeAuthSessionsQueryKey())).toBeUndefined());

		served = [session("new-sign-in")];
		const second = renderHook(() => useNodeAuthSessions(), { wrapper });
		expect(second.result.current.data).toBeUndefined();
		await waitFor(() => expect(second.result.current.data?.[0]?.id).toBe("new-sign-in"));
	});
});
