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

vi.mock("@/core/dev-tools/stores/DeveloperModeStore", () => ({
	useDeveloperModeStore: (selector: (state: { developerMode: boolean }) => unknown) => selector({ developerMode: true }),
}));

vi.mock("@/core/ui/notifications/Toast", () => ({ toast: { error: toastError, success: vi.fn() } }));

vi.mock("react-i18next", () => ({
	useTranslation: () => ({ t: (key: string) => key, i18n: { language: "en" } }),
}));

import { VoiceSettingsCard } from "@/features/voice/components/VoiceSettingsCard";

function renderCard(): { queryClient: QueryClient } {
	const queryClient = new QueryClient({
		defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
	});
	const ui: ReactElement = (
		<QueryClientProvider client={queryClient}>
			<MantineProvider>
				<VoiceSettingsCard />
			</MantineProvider>
		</QueryClientProvider>
	);
	render(ui);
	return { queryClient };
}

describe("VoiceSettingsCard operator controls", () => {
	beforeEach(() => {
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
