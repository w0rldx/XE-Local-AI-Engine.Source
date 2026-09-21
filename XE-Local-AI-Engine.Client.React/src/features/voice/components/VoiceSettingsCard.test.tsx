// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import type { ReactElement } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

// Mock generated query/mutation factories to isolate the hook while retaining validation and mapping.
const { getNodeSettingsOptionsMock, saveMutationFn, toastError, nodeSettingsQueryKey } = vi.hoisted(() => ({
	getNodeSettingsOptionsMock: vi.fn(),
	saveMutationFn: vi.fn(),
	toastError: vi.fn(),
	nodeSettingsQueryKey: ["getNodeSettings"] as const,
}));

vi.mock("@/core/api/generated/@tanstack/react-query.gen", () => ({
	getNodeSettingsOptions: getNodeSettingsOptionsMock,
	getNodeSettingsQueryKey: () => nodeSettingsQueryKey,
	saveNodeSettingsMutation: () => ({ mutationFn: saveMutationFn }),
}));

vi.mock("@/core/ui/notifications/Toast", () => ({ toast: { error: toastError, success: vi.fn() } }));

vi.mock("react-i18next", () => ({
	useTranslation: () => ({ t: (key: string) => key, i18n: { language: "en" } }),
}));

import { useNodeAuthStore } from "@/core/auth/stores/NodeAuthStore";
import { useDeveloperModeStore } from "@/core/dev-tools/stores/DeveloperModeStore";
import { VoiceSettingsCard } from "@/features/voice/components/VoiceSettingsCard";
import { testMantineTheme } from "@/test/MantineTestRender";

function renderCard(): { queryClient: QueryClient } {
	const queryClient = new QueryClient({
		defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
	});
	const ui: ReactElement = (
		<QueryClientProvider client={queryClient}>
			<MantineProvider env="test" theme={testMantineTheme}>
				<VoiceSettingsCard />
			</MantineProvider>
		</QueryClientProvider>
	);
	render(ui);
	return { queryClient };
}

describe("VoiceSettingsCard operator controls", () => {
	beforeEach(() => {
		// Developer Mode is deliberately OFF for every case in this file: the card is gated on the operator's node
		// setting alone, so the real store's `false` must never hide it. The hook's own gate is the session, so the
		// card only reads the node settings once a token exists.
		useDeveloperModeStore.setState({ developerMode: false });
		useNodeAuthStore.setState({ accessToken: "test-access-token" });
		getNodeSettingsOptionsMock.mockReset();
		saveMutationFn.mockReset();
		toastError.mockReset();
		getNodeSettingsOptionsMock.mockReturnValue({
			queryKey: nodeSettingsQueryKey,
			queryFn: async () => ({ voiceFeatureEnabled: false, defaultVoiceProfile: undefined }),
		});
		saveMutationFn.mockResolvedValue({ voiceFeatureEnabled: true });
		Object.defineProperty(window, "matchMedia", {
			writable: true,
			value: vi.fn().mockImplementation((query: string) => ({
				matches: false,
				media: query,
				onchange: null,
				addEventListener: vi.fn(),
				removeEventListener: vi.fn(),
				dispatchEvent: vi.fn(),
			})),
		});
		Object.defineProperty(window, "ResizeObserver", {
			writable: true,
			value: class ResizeObserverMock {
				observe = vi.fn();

				unobserve = vi.fn();

				disconnect = vi.fn();
			},
		});
	});

	afterEach(() => {
		cleanup();
		useNodeAuthStore.setState({ accessToken: undefined });
	});

	it("renders the per-user controls with Developer Mode off once the node gate is on", async () => {
		getNodeSettingsOptionsMock.mockReturnValue({
			queryKey: nodeSettingsQueryKey,
			queryFn: async () => ({ voiceFeatureEnabled: true, defaultVoiceProfile: undefined }),
		});

		renderCard();

		expect(useDeveloperModeStore.getState().developerMode).toBe(false);
		expect(await screen.findByTestId("voice-settings-enable-switch")).toBeTruthy();
	});

	it("keeps the per-user controls behind the node gate, showing the operator block only, when the node gate is off", async () => {
		renderCard();

		expect(await screen.findByTestId("voice-settings-node-gate-switch")).toBeTruthy();
		expect(screen.queryByTestId("voice-settings-enable-switch")).toBeNull();
	});

	it("writes voiceFeatureEnabled=true through the node-settings mutation when the operator toggles the node gate", async () => {
		renderCard();

		const gate = (await screen.findByTestId("voice-settings-node-gate-switch")) as HTMLInputElement;
		await waitFor(() => expect(gate.disabled).toBe(false));

		fireEvent.click(gate);

		await waitFor(() => expect(saveMutationFn).toHaveBeenCalledTimes(1));
		expect(saveMutationFn).toHaveBeenCalledWith(
			expect.objectContaining({ body: { voiceFeatureEnabled: true } }),
			expect.anything(),
		);
	});

	it("invalidates the shared node-settings query after a successful node save", async () => {
		const { queryClient } = renderCard();
		const invalidateSpy = vi.spyOn(queryClient, "invalidateQueries");

		const gate = (await screen.findByTestId("voice-settings-node-gate-switch")) as HTMLInputElement;
		await waitFor(() => expect(gate.disabled).toBe(false));

		fireEvent.click(gate);

		await waitFor(() => expect(invalidateSpy).toHaveBeenCalledWith(expect.objectContaining({ queryKey: nodeSettingsQueryKey })));
	});
});
