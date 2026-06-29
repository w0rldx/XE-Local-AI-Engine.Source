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
		ensureLlamaCppBinaryMutation: vi.fn(),
		getHfTokenStatusOptions: vi.fn(),
		setHfTokenMutation: vi.fn(),
		// llama.cpp runtime card: read-only status query + ensure + update mutations. The card owns its own data layer;
		// the page only mounts it. `getLlamaCppRuntimeOptions` returns the queryKey + queryFn for both the mount read and
		// the refresh-fetch / cache-seed path (the queryKey field is read by `useRefreshLlamaCppRuntime`).
		getLlamaCppRuntimeOptions: vi.fn(),
		updateLlamaCppRuntimeMutation: vi.fn(),
		ensureFn: vi.fn(),
		setTokenFn: vi.fn(),
		updateRuntimeFn: vi.fn(),
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
	ensureLlamaCppBinaryMutation: generatedMock.ensureLlamaCppBinaryMutation,
	getHfTokenStatusOptions: generatedMock.getHfTokenStatusOptions,
	setHfTokenMutation: generatedMock.setHfTokenMutation,
	getLlamaCppRuntimeOptions: generatedMock.getLlamaCppRuntimeOptions,
	updateLlamaCppRuntimeMutation: generatedMock.updateLlamaCppRuntimeMutation,
}));

// The CUDA build card owns its own data layer (CUDA-build SDK endpoints + a SignalR hub) and has its own dedicated
// test; stub it to null here so these page tests stay isolated to the settings/runtime/HF-token composition.
vi.mock("@/features/node-settings/components/CudaBuildCard", () => ({
	CudaBuildCard: () => null,
}));

// The runtime card renders a TanStack Router <Link> (eject-first notice). Stub it so the page mounts without a
// RouterProvider OR loading the generated route tree (which eval-fails outside a real router).
vi.mock("@tanstack/react-router", () => ({
	Link: ({ children, to, ...props }: { children: ReactNode; to: string; [key: string]: unknown }) => (
		<a href={to} {...props}>
			{children}
		</a>
	),
}));

import { useDeveloperModeStore } from "@/core/dev-tools/stores/DeveloperModeStore";
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

		// Local-runtime card defaults. The HF token status returns "no token".
		generatedMock.getHfTokenStatusOptions.mockReturnValue({
			queryKey: fakeQueryKey("getHfTokenStatus"),
			queryFn: async () => ({ hasToken: false }),
		});
		generatedMock.ensureFn.mockResolvedValue({ version: "b1234", variant: "cpu" });
		generatedMock.ensureLlamaCppBinaryMutation.mockReturnValue({ mutationFn: generatedMock.ensureFn });
		generatedMock.setTokenFn.mockResolvedValue({});
		generatedMock.setHfTokenMutation.mockReturnValue({ mutationFn: generatedMock.setTokenFn });
		// Runtime card: a read-only status query (safe on mount) and an update mutation. Default = up to date, nothing
		// running. `getLlamaCppRuntimeOptions` is called both with no args (mount + cache-seed key) and with
		// `{ query: { refresh: true } }` (manual recheck) — return the same shape for either call.
		generatedMock.getLlamaCppRuntimeOptions.mockReturnValue({
			queryKey: fakeQueryKey("getLlamaCppRuntime"),
			queryFn: async () => ({
				installed: { tag: "b1000", variant: "cpu", asset: "asset.tar.gz", installedAtUtc: 0 },
				recommendedTag: "b1000",
				upstreamLatestTag: null,
				updateAvailable: false,
				isOffline: false,
				runningProcessCount: 0,
			}),
		});
		generatedMock.updateRuntimeFn.mockResolvedValue({ version: "b1000", variant: "cpu" });
		generatedMock.updateLlamaCppRuntimeMutation.mockReturnValue({ mutationFn: generatedMock.updateRuntimeFn });
		// The HF token draft lives in a store that survives a remount — reset it so each test starts blank.
		useHfTokenStore.setState({ tokenDraft: "" });
		// Developer mode persists in a store across tests — reset to off so dev-gating tests start from a known state.
		useDeveloperModeStore.setState({ developerMode: false });
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

	// Local-runtime cards relocated from the model-fit advisor (llama.cpp now a single merged card).
	it("renders the merged llama.cpp runtime card and the Hugging Face token card", async () => {
		renderPage();

		// The merged runtime card shows the installed tag + variant on mount (no operator click / version probe).
		expect(await screen.findByTestId("llamacpp-updater-card")).toBeTruthy();
		await waitFor(() => expect(screen.getByTestId("llamacpp-updater-installed").textContent).toContain("b1000"));
		expect(screen.getByTestId("model-fit-hf-token-card")).toBeTruthy();
	});

	it("ensures the selected llama.cpp variant through the generated mutation", async () => {
		renderPage();
		// The ensure control only renders once the runtime status has resolved.
		const ensureButton = await screen.findByTestId("llamacpp-updater-ensure-button");

		fireEvent.click(ensureButton);

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

	// Migrated appsettings knobs: developer-mode gating and zod validation.
	it("hides developer-only fields when developer mode is off and reveals them when on", async () => {
		localStorage.setItem("xe-developer-mode", "false");
		useDeveloperModeStore.setState({ developerMode: false });
		renderPage();
		await screen.findByTestId("node-settings-local-chat-card");

		// Always-shown fields render; the developer-only advanced card does not.
		expect(screen.getByTestId("node-settings-default-model")).toBeTruthy();
		expect(screen.queryByTestId("node-settings-advanced-card")).toBeNull();

		// Flip developer mode on via the page switch -> the advanced card mounts. Mantine puts data-testid on the
		// checkbox input itself.
		const switchEl = screen.getByTestId("developer-mode-switch");
		fireEvent.click(switchEl.querySelector("input[type='checkbox']") ?? switchEl);
		await waitFor(() => expect(screen.getByTestId("node-settings-advanced-card")).toBeTruthy());
	});

	it("merges the migrated-fields card with the timeout and sends only changed fields on save", async () => {
		renderPage();
		// Wait for the loaded settings to sync into the form (timeout 600).
		await screen.findByDisplayValue(/600/);
		await screen.findByTestId("node-settings-runtime-card");

		// The migrated-fields card renders and its dedicated save button drives the same merged PUT as the timeout
		// card. With no migrated field edited, only the timeout is sent (optional-request semantics: omit = unchanged).
		fireEvent.click(screen.getByTestId("node-settings-fields-save-button"));

		await waitFor(() =>
			expect(generatedMock.saveFn.mock.calls[0]?.[0]).toEqual({ body: { maxMessageRequestTimeoutSeconds: 600 } }),
		);
	});
});
