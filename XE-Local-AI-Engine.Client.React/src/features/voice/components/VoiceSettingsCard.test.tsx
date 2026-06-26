// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import type { ReactElement } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import type { VoiceManifest } from "@/core/runtime/VoiceManifest";

// Mock the generated TanStack factories so the card → useVoiceNodeSettings → real withResponseValidation bridge runs
// against owned queryFn/mutationFn (no network). The real hook still composes the mutation onSuccess invalidation, so
// this exercises the full read → toggle → save → invalidate path.
const { getNodeSettingsOptionsMock, saveMutationFn, toastError, nodeSettingsQueryKey, voiceManifestQueryKey } =
	vi.hoisted(() => ({
		getNodeSettingsOptionsMock: vi.fn(),
		saveMutationFn: vi.fn(),
		toastError: vi.fn(),
		nodeSettingsQueryKey: ["getNodeSettings"] as const,
		voiceManifestQueryKey: ["getVoiceManifest"] as const,
	}));

vi.mock("@/core/api/generated/@tanstack/react-query.gen", () => ({
	getNodeSettingsOptions: getNodeSettingsOptionsMock,
	getNodeSettingsQueryKey: () => nodeSettingsQueryKey,
	getVoiceManifestQueryKey: () => voiceManifestQueryKey,
	saveNodeSettingsMutation: () => ({ mutationFn: saveMutationFn }),
}));

vi.mock("@/core/dev-tools/stores/DeveloperModeStore", () => ({
	useDeveloperModeStore: (selector: (state: { developerMode: boolean }) => unknown) => selector({ developerMode: true }),
}));

let mockManifest: VoiceManifest | undefined;
vi.mock("@/features/voice/VoiceRuntimeContext", () => ({
	useVoiceRuntime: () => ({ manifest: mockManifest }),
}));

vi.mock("@/core/ui/notifications/Toast", () => ({ toast: { error: toastError, success: vi.fn() } }));

vi.mock("react-i18next", () => ({
	useTranslation: () => ({ t: (key: string) => key }),
}));

import { VoiceSettingsCard } from "@/features/voice/components/VoiceSettingsCard";

function manifestWithVoices(): VoiceManifest {
	return {
		enabled: false,
		models: [],
		voices: [
			{ id: "af_heart", name: "Heart", language: "en", gender: "female" },
			{ id: "am_michael", name: "Michael", language: "en", gender: "male" },
		],
		defaultVoiceId: "af_heart",
	};
}

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
		mockManifest = manifestWithVoices();
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

	it("invalidates the voice manifest query after a successful node save", async () => {
		const { queryClient } = renderCard();
		const invalidateSpy = vi.spyOn(queryClient, "invalidateQueries");

		const gate = (await screen.findByTestId("voice-settings-node-gate-switch")) as HTMLInputElement;
		await waitFor(() => expect(gate.disabled).toBe(false));

		fireEvent.click(gate);

		await waitFor(() =>
			expect(invalidateSpy).toHaveBeenCalledWith(expect.objectContaining({ queryKey: voiceManifestQueryKey })),
		);
		expect(invalidateSpy).toHaveBeenCalledWith(expect.objectContaining({ queryKey: nodeSettingsQueryKey }));
	});
});
