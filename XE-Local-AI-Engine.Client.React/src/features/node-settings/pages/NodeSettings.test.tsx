// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import type { SaveNodeSettingsResponse } from "@/core/api/generated";

const settingsResponse = {
	maxMessageRequestTimeoutSeconds: 600,
	minMessageRequestTimeoutSeconds: 5,
	maxAllowedMessageRequestTimeoutSeconds: 3600,
};

const { generatedMock } = vi.hoisted(() => ({
	generatedMock: {
		getNodeSettingsOptions: vi.fn(),
		getNodeSettingsQueryKey: vi.fn(() => ["getNodeSettings"]),
		saveNodeSettingsMutation: vi.fn(),
		saveFn: vi.fn(),
		// Local-runtime cards (llama.cpp + HF token) relocated from the model-fit advisor.
		getLlamaCppVersionOptions: vi.fn(),
		ensureLlamaCppBinaryMutation: vi.fn(),
		getHfTokenStatusOptions: vi.fn(),
		setHfTokenMutation: vi.fn(),
		ensureFn: vi.fn(),
		setTokenFn: vi.fn(),
	},
}));

// Centralizes the `_id` discriminator literal (which trips biome's naming-convention rule) in one suppressed spot.
function fakeQueryKey(operationId: string): unknown {
	// biome-ignore lint/style/useNamingConvention: `_id` is the generated hey-api query-key discriminator field.
	return [{ _id: operationId }];
}

vi.mock("@/core/api/generated/@tanstack/react-query.gen", () => ({
	getNodeSettingsOptions: generatedMock.getNodeSettingsOptions,
	getNodeSettingsQueryKey: generatedMock.getNodeSettingsQueryKey,
	saveNodeSettingsMutation: generatedMock.saveNodeSettingsMutation,
	getLlamaCppVersionOptions: generatedMock.getLlamaCppVersionOptions,
	ensureLlamaCppBinaryMutation: generatedMock.ensureLlamaCppBinaryMutation,
	getHfTokenStatusOptions: generatedMock.getHfTokenStatusOptions,
	setHfTokenMutation: generatedMock.setHfTokenMutation,
}));

import { NodeSettings } from "@/features/node-settings/pages/NodeSettings";
import { useHfTokenStore } from "@/features/node-settings/stores/HfTokenStore";

function installJsdomEnvironmentMocks(): void {
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
}

function renderPage(): void {
	const queryClient = new QueryClient({
		defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
	});

	const wrapper = ({ children }: { children: ReactNode }) => (
		<QueryClientProvider client={queryClient}>
			<MantineProvider>{children}</MantineProvider>
		</QueryClientProvider>
	);

	render(<NodeSettings />, { wrapper });
}

describe("NodeSettings (generated hey-api data layer)", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
		generatedMock.getNodeSettingsOptions.mockReturnValue({
			queryKey: ["getNodeSettings"],
			queryFn: async () => settingsResponse,
		});
		generatedMock.saveFn.mockResolvedValue(settingsResponse as SaveNodeSettingsResponse);
		generatedMock.saveNodeSettingsMutation.mockReturnValue({ mutationFn: generatedMock.saveFn });

		// Local-runtime card defaults. The llama.cpp version query is DISABLED until the operator checks it (the GET can
		// trigger a binary download), so its queryFn never runs on mount; the HF token status returns "no token".
		generatedMock.getLlamaCppVersionOptions.mockReturnValue({
			queryKey: fakeQueryKey("getLlamaCppVersion"),
			queryFn: async () => ({ version: "b1234", variant: "cuda", isPinnedFallback: false, pinnedTag: "b1000" }),
		});
		generatedMock.getHfTokenStatusOptions.mockReturnValue({
			queryKey: fakeQueryKey("getHfTokenStatus"),
			queryFn: async () => ({ hasToken: false }),
		});
		generatedMock.ensureFn.mockResolvedValue({ version: "b1234", variant: "cpu" });
		generatedMock.ensureLlamaCppBinaryMutation.mockReturnValue({ mutationFn: generatedMock.ensureFn });
		generatedMock.setTokenFn.mockResolvedValue({});
		generatedMock.setHfTokenMutation.mockReturnValue({ mutationFn: generatedMock.setTokenFn });
		// The HF token draft lives in a store that survives a remount — reset it so each test starts blank.
		useHfTokenStore.setState({ tokenDraft: "" });
	});

	afterEach(() => {
		cleanup();
		vi.clearAllMocks();
	});

	it("loads settings through the generated query options", async () => {
		renderPage();

		expect(generatedMock.getNodeSettingsOptions).toHaveBeenCalled();
		expect(await screen.findByDisplayValue(/600/)).toBeTruthy();
	});

	it("saves through the generated mutation with the timeout body", async () => {
		renderPage();
		await screen.findByDisplayValue(/600/);

		fireEvent.click(screen.getByRole("button", { name: /save settings/i }));

		await waitFor(() => {
			// TanStack passes a second context arg to mutationFn; assert only the request variables.
			expect(generatedMock.saveFn.mock.calls[0]?.[0]).toEqual({
				body: { maxMessageRequestTimeoutSeconds: 600 },
			});
		});
	});

	// Local-runtime cards relocated from the model-fit advisor.
	it("renders the llama.cpp runtime and Hugging Face token cards", async () => {
		renderPage();

		expect(await screen.findByTestId("model-fit-llamacpp-card")).toBeTruthy();
		expect(screen.getByTestId("model-fit-hf-token-card")).toBeTruthy();
	});

	it("does not show the llama.cpp version until the operator explicitly checks it (avoids a mount-time download)", async () => {
		renderPage();

		// On mount the version probe is idle: the panel shows its idle hint and renders no version.
		expect(await screen.findByTestId("model-fit-llamacpp-idle")).toBeTruthy();
		expect(screen.queryByTestId("model-fit-llamacpp-version")).toBeNull();

		// Triggering the probe reveals the resolved version.
		fireEvent.click(screen.getByTestId("model-fit-llamacpp-check-button"));
		await waitFor(() => expect(screen.getByTestId("model-fit-llamacpp-version").textContent).toContain("b1234"));
	});

	it("ensures the selected llama.cpp variant through the generated mutation", async () => {
		renderPage();
		await screen.findByTestId("model-fit-llamacpp-card");

		fireEvent.click(screen.getByTestId("model-fit-llamacpp-ensure-button"));

		// The select defaults to "cpu" until the operator changes it.
		await waitFor(() => expect(generatedMock.ensureFn.mock.calls[0]?.[0]).toEqual({ body: { variant: "cpu" } }));
	});

	it("renders the HF token panel with a masked input and never the token value", async () => {
		generatedMock.getHfTokenStatusOptions.mockReturnValue({
			queryKey: fakeQueryKey("getHfTokenStatus"),
			queryFn: async () => ({ hasToken: true }),
		});

		renderPage();

		const input = (await screen.findByTestId("model-fit-hf-token-input")) as HTMLInputElement;
		// PasswordInput renders a type=password field — the value is masked, never plain text.
		expect(input.type).toBe("password");
		await waitFor(() => expect(screen.getByTestId("model-fit-hf-token-status").textContent).toContain("Token configured"));
	});

	it("saves the HF token draft through the generated mutation", async () => {
		useHfTokenStore.setState({ tokenDraft: "hf_secret" });

		renderPage();
		await screen.findByTestId("model-fit-hf-token-card");

		fireEvent.click(screen.getByTestId("model-fit-hf-token-save"));

		// An empty draft clears the token (null body); a non-empty draft is sent verbatim.
		await waitFor(() => expect(generatedMock.setTokenFn.mock.calls[0]?.[0]).toEqual({ body: { token: "hf_secret" } }));
	});
});
