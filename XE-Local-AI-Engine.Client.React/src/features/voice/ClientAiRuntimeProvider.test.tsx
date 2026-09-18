// @vitest-environment jsdom

import type { QueryClient } from "@tanstack/react-query";
import { act, renderHook, waitFor } from "@testing-library/react";
import { http, HttpResponse } from "msw";
import type { ReactNode } from "react";
import { beforeEach, describe, expect, it } from "vitest";

import { getNodeSettingsQueryKey } from "@/core/api/generated/@tanstack/react-query.gen";
import { useNodeAuthStore } from "@/core/auth/stores/NodeAuthStore";
import { useDeveloperModeStore } from "@/core/dev-tools/stores/DeveloperModeStore";
import { ClientAiRuntimeProvider } from "@/features/voice/ClientAiRuntimeProvider";
import { useVoiceRuntime } from "@/features/voice/VoiceRuntimeContext";
import { localApiPath } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { createProvidersWrapper } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

setupMswServer();

// The node-settings read is served over MSW rather than mocked away, because half of what is under test is whether
// the request reaches the wire at all. The provider mounts at the app root, OUTSIDE the `_layout` auth guard, and the
// access token lives in memory only — so an ungated query fires on every fresh page load, for every user, including
// on the login and setup screens where no session exists, and eats a 401 + interceptor refresh + retry each time.

/** Serves the node-settings GET out of a fixed gate value and counts reads, so "never requested" is observable. */
function nodeSettingsRoute(voiceFeatureEnabled: boolean, defaultVoiceProfile?: string): { reads: number } {
	const state = { reads: 0 };
	server.use(
		http.get(localApiPath("node-settings"), () => {
			state.reads += 1;
			return HttpResponse.json({ voiceFeatureEnabled, defaultVoiceProfile });
		}),
	);

	return state;
}

function renderVoiceRuntime(): { result: { current: ReturnType<typeof useVoiceRuntime> }; queryClient: QueryClient } {
	const { wrapper: Providers, queryClient } = createProvidersWrapper();
	const { result } = renderHook(() => useVoiceRuntime(), {
		wrapper: ({ children }: { children: ReactNode }) => (
			<Providers>
				<ClientAiRuntimeProvider>{children}</ClientAiRuntimeProvider>
			</Providers>
		),
	});

	return { result, queryClient };
}

describe("ClientAiRuntimeProvider voice gating", () => {
	beforeEach(() => {
		// Developer Mode stays OFF throughout: voice is a browser Web Speech wrapper with no model download, so the
		// operator's node setting is the only gate it may have.
		useDeveloperModeStore.setState({ developerMode: false });
		useNodeAuthStore.setState({ accessToken: undefined });
	});

	it("enables voice from the operator node setting alone, with Developer Mode off", async () => {
		nodeSettingsRoute(true);
		useNodeAuthStore.setState({ accessToken: "test-access-token" });

		const { result } = renderVoiceRuntime();

		await waitFor(() => expect(result.current.enabled).toBe(true));
		expect(useDeveloperModeStore.getState().developerMode).toBe(false);
	});

	it("leaves voice disabled when the operator turned the node setting off", async () => {
		const route = nodeSettingsRoute(false);
		useNodeAuthStore.setState({ accessToken: "test-access-token" });

		const { result } = renderVoiceRuntime();

		await waitFor(() => expect(route.reads).toBe(1));
		expect(result.current.enabled).toBe(false);
	});

	it("issues no node-settings read without an access token, then exactly one once a token appears", async () => {
		const route = nodeSettingsRoute(true);

		const { result, queryClient } = renderVoiceRuntime();

		// `fetchStatus` is set synchronously when an observer mounts, so an "idle" here means the query was never
		// started — not merely that its response has not come back yet. The read count backs that up.
		expect(queryClient.getQueryState(getNodeSettingsQueryKey())?.fetchStatus ?? "idle").toBe("idle");
		expect(route.reads).toBe(0);
		expect(result.current.enabled).toBe(false);

		act(() => {
			useNodeAuthStore.setState({ accessToken: "test-access-token" });
		});

		await waitFor(() => expect(result.current.enabled).toBe(true));
		expect(route.reads).toBe(1);
	});

	it("disables voice again the moment the session is cleared, without believing the cached node setting", async () => {
		// Logout clears the auth store only (`useNodeLogout` → `clearAuth`). TanStack Query keeps a cache entry's
		// `data` when `enabled` flips to false — it stops refetching, it does not forget — so a node gate that was
		// `true` before logout would otherwise still read `true` on the login screen, for as long as the entry lives.
		nodeSettingsRoute(true, "os-voice-de");
		useNodeAuthStore.setState({ accessToken: "test-access-token" });

		const { result } = renderVoiceRuntime();
		await waitFor(() => expect(result.current.enabled).toBe(true));
		expect(result.current.defaultVoiceProfile).toBe("os-voice-de");

		act(() => {
			useNodeAuthStore.setState({ accessToken: undefined });
		});

		await waitFor(() => expect(result.current.enabled).toBe(false));
		expect(result.current.defaultVoiceProfile).toBeUndefined();
	});
});
