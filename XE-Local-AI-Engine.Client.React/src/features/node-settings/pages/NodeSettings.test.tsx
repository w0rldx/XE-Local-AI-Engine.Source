// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import type { SaveNodeSettingsResponse } from "@/core/api/generated";

const settingsResponse = {
	maxMessageRequestTimeoutSeconds: 600,
	minMessageRequestTimeoutSeconds: 5,
	maxAllowedMessageRequestTimeoutSeconds: 3600,
};

const { toastMock } = vi.hoisted(() => ({
	toastMock: { success: vi.fn(), error: vi.fn(), info: vi.fn(), warn: vi.fn(), warning: vi.fn(), progress: vi.fn() },
}));

vi.mock("react-i18next", () => ({
	useTranslation: () => ({
		t: (key: string, fallbackOrVars?: string | Record<string, unknown>, explicitVars?: Record<string, unknown>) => {
			const text = typeof fallbackOrVars === "string" ? fallbackOrVars : key;
			const vars = typeof fallbackOrVars === "string" ? explicitVars : fallbackOrVars;
			if (vars === undefined) {
				return text;
			}
			return Object.entries(vars).reduce(
				(acc, [name, value]) => acc.replace(new RegExp(`{{${name}}}`, "g"), String(value)),
				text,
			);
		},
		i18n: { language: "en" },
	}),
}));

// The page asks the node whether the optional Ollama runtime is configured at all and threads the answer down to the
// runtime card. That probe reaches the generated SDK, and its axios interceptors pull in the app router - neither of
// which this file mounts (the SDK's react-query module is mocked wholesale below, so MSW never sees the call). This is
// the one seam left for the hook, and it doubles as the switch for the gate-off test: `undefined` is the fail-open
// answer every other test runs with, so the Ollama endpoint field renders exactly as it did before the probe existed.
const { ollamaProbe } = vi.hoisted(() => ({ ollamaProbe: { data: undefined as boolean | undefined } }));

vi.mock("@/core/runtime/hooks/useOllamaRuntimeConfigured", () => ({
	useOllamaRuntimeConfigured: () => ollamaProbe,
}));

const { generatedMock } = vi.hoisted(() => ({
	generatedMock: {
		getNodeSettingsOptions: vi.fn(),
		getNodeSettingsQueryKey: vi.fn(() => ["getNodeSettings"]),
		saveNodeSettingsMutation: vi.fn(),
		saveFn: vi.fn(),
		// Installed-models query feeding the speculative draft-model picker.
		listLocalModelsOptions: vi.fn(),
		// Local-runtime cards (llama.cpp + HF token) relocated from the model-fit advisor.
		ensureLlamaCppBinaryMutation: vi.fn(),
		getHfTokenStatusOptions: vi.fn(),
		setHfTokenMutation: vi.fn(),
		// llama.cpp runtime card: read-only status query + ensure + update mutations. The card owns its own data layer;
		// the page only mounts it. `getLlamaCppRuntimeOptions` returns the queryKey + queryFn for both the mount read and
		// the refresh-fetch / cache-seed path (the queryKey field is read by `useRefreshLlamaCppRuntime`).
		getLlamaCppRuntimeOptions: vi.fn(),
		getLlamaCppSourceBuildStatusOptions: vi.fn(),
		updateLlamaCppRuntimeMutation: vi.fn(),
		ensureFn: vi.fn(),
		setTokenFn: vi.fn(),
		updateRuntimeFn: vi.fn(),
		// One-click recommended-reranker download mutation.
		downloadRecommendedRerankerMutation: vi.fn(),
		downloadRerankerFn: vi.fn(),
		// One-click recommended-embedding download mutation.
		downloadRecommendedEmbeddingMutation: vi.fn(),
		downloadEmbeddingFn: vi.fn(),
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
	listLocalModelsOptions: generatedMock.listLocalModelsOptions,
	ensureLlamaCppBinaryMutation: generatedMock.ensureLlamaCppBinaryMutation,
	getHfTokenStatusOptions: generatedMock.getHfTokenStatusOptions,
	setHfTokenMutation: generatedMock.setHfTokenMutation,
	getLlamaCppRuntimeOptions: generatedMock.getLlamaCppRuntimeOptions,
	getLlamaCppSourceBuildStatusOptions: generatedMock.getLlamaCppSourceBuildStatusOptions,
	updateLlamaCppRuntimeMutation: generatedMock.updateLlamaCppRuntimeMutation,
	downloadRecommendedRerankerMutation: generatedMock.downloadRecommendedRerankerMutation,
	downloadRecommendedEmbeddingMutation: generatedMock.downloadRecommendedEmbeddingMutation,
}));

// The recommended-reranker download progress reuses the shared GgufDownload feed (SignalR hub + cancel mutation).
// Stub it so these page tests stay isolated and never open a real hub connection; the reranker download flow has its
// own dedicated test in NodeSettingsFieldsCard.test.tsx.
vi.mock("@/features/models/queries/useGgufDownload", () => ({
	useActiveGgufDownloads: () => new Map(),
	useCancelGgufDownload: () => ({ mutate: vi.fn(), isPending: false, variables: undefined }),
}));

// Toast is the mutation-result surface for the reranker download states — spy on it to assert already-installed vs.
// download-started notices.
vi.mock("@/core/ui/notifications/Toast", () => ({ toast: toastMock }));

// The source build card owns its own data layer (source-build SDK endpoints + a SignalR hub) and has its own dedicated
// test; stub it to null here so these page tests stay isolated to the settings/runtime/HF-token composition.
vi.mock("@/features/node-settings/components/SourceBuildCard", () => ({
	SourceBuildCard: () => null,
}));

// The MCP server-key panel owns its own data layer (the inbound-credential SDK endpoints) and has its own dedicated
// test; stub it to null here, matching the source-build cards above, so these page tests stay isolated.
vi.mock("@/features/node-settings/components/McpServerKeyPanel", () => ({
	McpServerKeyPanel: () => <div data-testid="mcp-server-key-panel" />,
}));

// The local-model-proxy key panel owns its own data layer (the inbound-credential SDK endpoints) and has its own
// dedicated test; stub it to null here, matching the MCP server-key panel above, so these page tests stay isolated.
vi.mock("@/features/node-settings/components/LocalModelProxyKeyPanel", () => ({
	LocalModelProxyKeyPanel: () => <div data-testid="local-model-proxy-key-panel" />,
}));

vi.mock("@/features/node-settings/components/McpWorkspaceAllowlistPanel", () => ({
	McpWorkspaceAllowlistPanel: () => <div data-testid="mcp-workspace-allowlist-panel" />,
}));

vi.mock("@/features/node-settings/components/ImageRuntimeSourceBuildCard", () => ({
	ImageRuntimeSourceBuildCard: () => null,
}));

vi.mock("@/features/node-settings/components/WhisperRuntimeSourceBuildCard", () => ({
	WhisperRuntimeSourceBuildCard: () => null,
}));

// The runtime card renders a TanStack Router <Link> (eject-first notice) and the change-password card calls
// useNavigate (a successful change is a sign-out that routes to /login). Stub both so the page mounts without a
// RouterProvider OR loading the generated route tree (which eval-fails outside a real router).
vi.mock("@tanstack/react-router", () => ({
	Link: ({ children, to, ...props }: { children: ReactNode; to: string; [key: string]: unknown }) => (
		<a href={to} {...props}>
			{children}
		</a>
	),
	useNavigate: () => vi.fn(),
}));

import { useDeveloperModeStore } from "@/core/dev-tools/stores/DeveloperModeStore";
import { useGgufBrowseStore } from "@/features/models/stores/GgufBrowseStore";
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
	// jsdom does not implement scrollIntoView; Mantine's Combobox calls it on a timer when a dropdown opens, which
	// surfaces as an unhandled error AFTER the test that opened it.
	Element.prototype.scrollIntoView = vi.fn();
}

// Returns the client so a test can drive a BACKGROUND refetch (an invalidation), which is a different contract from
// the operator clicking Reload.
function renderPage(cachedSettings?: unknown): QueryClient {
	const queryClient = new QueryClient({
		defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
	});
	// A pre-populated cache is the real mount shape after any earlier visit: the page paints the cached response and
	// the mount refetch supersedes it.
	if (cachedSettings !== undefined) {
		queryClient.setQueryData(["getNodeSettings"], cachedSettings);
	}

	const wrapper = ({ children }: { children: ReactNode }) => (
		<QueryClientProvider client={queryClient}>
			<MantineProvider>{children}</MantineProvider>
		</QueryClientProvider>
	);

	render(<NodeSettings />, { wrapper });
	return queryClient;
}

describe("NodeSettings (generated hey-api data layer)", () => {
	// Cleared BEFORE each test, not after: the global `afterEach(cleanup)` in `src/test/Cleanup.ts` runs after this
	// file's own hooks (Vitest stacks them), so clearing there would drop the unmount's calls into the next test.
	beforeEach(() => {
		vi.clearAllMocks();
		installJsdomEnvironmentMocks();
		generatedMock.getNodeSettingsOptions.mockReturnValue({
			queryKey: ["getNodeSettings"],
			queryFn: async () => settingsResponse,
		});
		generatedMock.saveFn.mockResolvedValue(settingsResponse as SaveNodeSettingsResponse);
		generatedMock.saveNodeSettingsMutation.mockReturnValue({ mutationFn: generatedMock.saveFn });
		// The draft-model picker's installed-models query resolves to an empty list by default (no draft models offered).
		generatedMock.listLocalModelsOptions.mockReturnValue({
			queryKey: fakeQueryKey("listLocalModels"),
			queryFn: async () => ({ items: [], isAvailable: false }),
		});
		generatedMock.getLlamaCppSourceBuildStatusOptions.mockReturnValue({
			queryKey: fakeQueryKey("getLlamaCppSourceBuildStatus"),
			queryFn: async () => ({ phase: "Idle", isRunning: false, terminal: false, logLines: [], currentBuild: null }),
		});

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
		// Recommended-reranker download: default resolves to a fresh start (not installed, not already in flight).
		generatedMock.downloadRerankerFn.mockResolvedValue({
			modelName: "bge-reranker-v2-m3",
			repoId: "BAAI/bge-reranker-v2-m3",
			quant: "Q4_K_M",
			alreadyInstalled: false,
			alreadyInFlight: false,
		});
		generatedMock.downloadRecommendedRerankerMutation.mockReturnValue({ mutationFn: generatedMock.downloadRerankerFn });
		// Recommended-embedding download: default resolves to a fresh start (not installed, not already in flight).
		generatedMock.downloadEmbeddingFn.mockResolvedValue({
			modelName: "nomic-embed-text-v1.5",
			repoId: "nomic-ai/nomic-embed-text-v1.5-GGUF",
			quant: "Q4_K_M",
			alreadyInstalled: false,
			alreadyInFlight: false,
		});
		generatedMock.downloadRecommendedEmbeddingMutation.mockReturnValue({ mutationFn: generatedMock.downloadEmbeddingFn });
		// The HF token draft lives in a store that survives a remount — reset it so each test starts blank.
		useHfTokenStore.setState({ tokenDraft: "" });
		// Developer mode persists in a store across tests — reset to off so dev-gating tests start from a known state.
		useDeveloperModeStore.setState({ developerMode: false });
		// The GGUF in-flight set is a shared session store; reset it so a reranker-download test starts with none.
		useGgufBrowseStore.setState({ inFlightDownloads: [] });
		// The probe holder is module state shared by the whole file; reset it to the fail-open answer.
		ollamaProbe.data = undefined;
	});

	it("hides the Ollama endpoint field when the node reports the runtime gated off", () => {
		ollamaProbe.data = false;

		renderPage();

		// Through the real page -> NodeSettingsFieldsCard -> NodeSettingsRuntimeCard path, so the prop is proven wired.
		expect(screen.queryByTestId("node-settings-ollama-endpoint")).toBeNull();
		expect(screen.getByTestId("node-settings-ollama-disabled")).toBeTruthy();
	});

	it("keeps the Ollama endpoint field while the runtime probe has not answered", () => {
		renderPage();

		// FAIL OPEN: `undefined` is a loading or failed probe, and must never take the setting away.
		expect(screen.getByTestId("node-settings-ollama-endpoint")).toBeTruthy();
		expect(screen.queryByTestId("node-settings-ollama-disabled")).toBeNull();
	});

	it("mounts workspace access directly below the inbound MCP key panel", () => {
		renderPage();

		const keyPanel = screen.getByTestId("mcp-server-key-panel");
		const workspacePanel = screen.getByTestId("mcp-workspace-allowlist-panel");
		expect(keyPanel.compareDocumentPosition(workspacePanel) & Node.DOCUMENT_POSITION_FOLLOWING).not.toBe(0);
	});

	afterEach(() => {
		// The developer-mode test writes `xe-developer-mode`, and localStorage is one jsdom object shared by the whole
		// file: left behind, it decides for every later test whether the developer-only cards mount.
		localStorage.clear();
	});

	// Both draft-seeding contracts share one server: the first response is what the operator sees, every later one
	// differs so a re-seed is unmistakable.
	function mockSecondLoadDiffers(): void {
		let fetches = 0;
		generatedMock.getNodeSettingsOptions.mockReturnValue({
			queryKey: ["getNodeSettings"],
			queryFn: async () => {
				fetches += 1;
				return fetches === 1
					? settingsResponse
					: {
							...settingsResponse,
							minMessageRequestTimeoutSeconds: 7,
							maxMessageRequestTimeoutSeconds: 900,
							llamaMaxLoadedProcesses: 9,
						};
			},
		});
	}

	// Regression: the editable draft used to be re-seeded by an effect on every `settings` identity, so any
	// background refetch (window focus, the post-save invalidation) silently replaced whatever the operator had typed
	// with the server's values.
	it("keeps in-progress edits when a background refetch returns different server values", async () => {
		mockSecondLoadDiffers();

		const queryClient = renderPage();
		await screen.findByDisplayValue(/600/);

		const timeout = screen.getByLabelText(/Maximum message request timeout/) as HTMLInputElement;
		const maxProcesses = screen.getByTestId("node-settings-llama-max-processes") as HTMLInputElement;
		fireEvent.change(timeout, { target: { value: "700" } });
		fireEvent.change(maxProcesses, { target: { value: "5" } });

		await queryClient.invalidateQueries({ queryKey: ["getNodeSettings"] });

		// The allowed-range description is rendered straight off the query data, so it only reads 7 once the second
		// response has actually landed in React — which is the moment the old effect would have clobbered the draft.
		await waitFor(() => expect(screen.getByText(/Allowed range: 7–3600 seconds\./)).toBeTruthy());
		// The NumberInput renders its suffix inside the value.
		expect(timeout.value).toBe("700 seconds");
		expect(maxProcesses.value).toBe("5");
	});

	// Seeding only on the first load was wrong the other way round: mounting against a cached response latched the
	// draft to the CACHE, so the mount refetch's fresher values never landed and a Save wrote the stale ones back.
	it("adopts a fresher server state on mount over a stale cache while the draft is untouched", async () => {
		generatedMock.getNodeSettingsOptions.mockReturnValue({
			queryKey: ["getNodeSettings"],
			queryFn: async () => ({ ...settingsResponse, maxMessageRequestTimeoutSeconds: 900, llamaMaxLoadedProcesses: 9 }),
		});

		renderPage({ ...settingsResponse, maxMessageRequestTimeoutSeconds: 120, llamaMaxLoadedProcesses: 2 });

		const maxProcesses = (await screen.findByTestId("node-settings-llama-max-processes")) as HTMLInputElement;
		await waitFor(() => expect(maxProcesses.value).toBe("9"));
		expect((screen.getByLabelText(/Maximum message request timeout/) as HTMLInputElement).value).toBe("900 seconds");

		fireEvent.click(screen.getByRole("button", { name: /save settings/i }));

		// The stale 120 would have ridden the body here, overwriting the newer server value.
		await waitFor(() =>
			expect(generatedMock.saveFn.mock.calls[0]?.[0]).toEqual({ body: { maxMessageRequestTimeoutSeconds: 900 } }),
		);
	});

	// The other half of the same contract: Reload is the operator asking for the server's values, so it is the one
	// refetch that DOES discard the draft. Seeding once must not turn Reload into a no-op.
	it("replaces the draft with the server's values when the operator clicks Reload", async () => {
		mockSecondLoadDiffers();

		renderPage();
		await screen.findByDisplayValue(/600/);

		const timeout = screen.getByLabelText(/Maximum message request timeout/) as HTMLInputElement;
		const maxProcesses = screen.getByTestId("node-settings-llama-max-processes") as HTMLInputElement;
		fireEvent.change(timeout, { target: { value: "700" } });
		fireEvent.change(maxProcesses, { target: { value: "5" } });

		fireEvent.click(screen.getByRole("button", { name: /reload/i }));

		await waitFor(() => expect(maxProcesses.value).toBe("9"));
		expect(timeout.value).toBe("900 seconds");
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

	it("offers only installed llama.cpp chat models in the keep-warm picker", async () => {
		generatedMock.listLocalModelsOptions.mockReturnValue({
			queryKey: fakeQueryKey("listLocalModels"),
			queryFn: async () => ({
				isAvailable: true,
				items: [
					{
						modelName: "llama-chat",
						provider: "LLaMaCpP",
						isSelected: false,
						kind: "Chat",
						detectedKind: "Chat",
						capabilities: [],
						isReasoningCapable: false,
						isToolCapable: false,
						isOverridden: false,
					},
					{
						modelName: "ollama-chat",
						provider: "ollama",
						isSelected: false,
						kind: "Chat",
						detectedKind: "Chat",
						capabilities: [],
						isReasoningCapable: false,
						isToolCapable: false,
						isOverridden: false,
					},
					{
						modelName: "llama-embedding",
						provider: "llamacpp",
						isSelected: false,
						kind: "Embedding",
						detectedKind: "Embedding",
						capabilities: [],
						isReasoningCapable: false,
						isToolCapable: false,
						isOverridden: false,
					},
				],
			}),
		});

		renderPage();
		const toggle = await screen.findByTestId("node-settings-keep-model-warm-enabled");
		fireEvent.click(toggle);
		const listbox = screen.getByRole("listbox", { name: "Model to keep warm", hidden: true });

		// Mantine keeps Select options mounted in a hidden portal until the dropdown opens; hidden-role queries let this
		// assert the actual option data without coupling the filter test to Popover positioning behavior in jsdom.
		expect(within(listbox).getByRole("option", { name: "llama-chat", hidden: true })).toBeTruthy();
		expect(within(listbox).queryByRole("option", { name: "ollama-chat", hidden: true })).toBeNull();
		expect(within(listbox).queryByRole("option", { name: "llama-embedding", hidden: true })).toBeNull();
	});

	it("preserves and flags a selected keep-warm model that is no longer installed", async () => {
		generatedMock.getNodeSettingsOptions.mockReturnValue({
			queryKey: ["getNodeSettings"],
			queryFn: async () => ({
				...settingsResponse,
				keepModelWarmEnabled: true,
				keepModelWarmModelName: "deleted-model",
				keepModelWarmIntervalSeconds: 120,
				llamaMaxLoadedProcesses: 3,
				llamaIdleTimeToLiveSeconds: 900,
			}),
		});
		generatedMock.listLocalModelsOptions.mockReturnValue({
			queryKey: fakeQueryKey("listLocalModels"),
			queryFn: async () => ({ items: [], isAvailable: true }),
		});

		renderPage();

		await waitFor(() => {
			const listbox = screen.getByRole("listbox", { name: "Model to keep warm", hidden: true });
			expect(within(listbox).getByRole("option", { name: "deleted-model (not installed)", hidden: true })).toBeTruthy();
		});
		expect(await screen.findByText("The selected model deleted-model is no longer installed.")).toBeTruthy();

		fireEvent.click(screen.getByTestId("node-settings-fields-save-button"));
		expect(generatedMock.saveFn).not.toHaveBeenCalled();
	});

	it("flags a keep-warm model whose stored casing does not match the installed model id", async () => {
		generatedMock.getNodeSettingsOptions.mockReturnValue({
			queryKey: ["getNodeSettings"],
			queryFn: async () => ({
				...settingsResponse,
				keepModelWarmEnabled: true,
				keepModelWarmModelName: "MODEL-A",
				keepModelWarmIntervalSeconds: 120,
				llamaMaxLoadedProcesses: 3,
				llamaIdleTimeToLiveSeconds: 900,
			}),
		});
		generatedMock.listLocalModelsOptions.mockReturnValue({
			queryKey: fakeQueryKey("listLocalModels"),
			queryFn: async () => ({
				isAvailable: true,
				items: [
					{
						modelName: "model-a",
						provider: "llamacpp",
						isSelected: false,
						kind: "Chat",
						detectedKind: "Chat",
						capabilities: [],
						isReasoningCapable: false,
						isToolCapable: false,
						isOverridden: false,
					},
				],
			}),
		});

		renderPage();

		expect(await screen.findByText("The selected model MODEL-A is no longer installed.")).toBeTruthy();
		const listbox = screen.getByRole("listbox", { name: "Model to keep warm", hidden: true });
		expect(within(listbox).getByRole("option", { name: "MODEL-A (not installed)", hidden: true })).toBeTruthy();
	});

	// Restart-gated knobs (seeded once at DI composition) must say so on save; live knobs must not.
	it("tells the operator a restart is needed when a restart-gated field changed", async () => {
		// Wait on a value only the RESPONSE can produce: the form renders seed defaults before the query resolves, and the
		// load-sync effect would otherwise land after the edit below and wipe it.
		generatedMock.getNodeSettingsOptions.mockReturnValue({
			queryKey: ["getNodeSettings"],
			queryFn: async () => ({ ...settingsResponse, huggingFaceDefaultQuant: "Q4_K_M" }),
		});
		renderPage();
		await screen.findByDisplayValue("Q4_K_M");

		// huggingFaceDefaultQuant is seeded once into the HF options at composition.
		fireEvent.change(screen.getByTestId("node-settings-hf-default-quant"), { target: { value: "Q5_K_M" } });
		fireEvent.click(screen.getByTestId("node-settings-fields-save-button"));

		await waitFor(() => expect(generatedMock.saveFn).toHaveBeenCalled());
		await waitFor(() =>
			expect(toastMock.success).toHaveBeenCalledWith(
				"Node settings saved. Some of the changed settings only take effect after the node restarts.",
			),
		);
	});

	it("keeps the plain saved notice when only live fields changed", async () => {
		generatedMock.getNodeSettingsOptions.mockReturnValue({
			queryKey: ["getNodeSettings"],
			queryFn: async () => ({ ...settingsResponse, defaultModelName: "loaded-model" }),
		});
		renderPage();
		await screen.findByDisplayValue("loaded-model");

		// enableTools is re-read per send/regenerate — no restart involved.
		const toggle = screen.getByTestId("node-settings-enable-tools");
		fireEvent.click(toggle.querySelector("input[type='checkbox']") ?? toggle);
		fireEvent.click(screen.getByTestId("node-settings-fields-save-button"));

		await waitFor(() =>
			expect(generatedMock.saveFn.mock.calls[0]?.[0]).toEqual({
				body: { enableTools: false, maxMessageRequestTimeoutSeconds: 600 },
			}),
		);
		await waitFor(() =>
			expect(toastMock.success).toHaveBeenCalledWith(
				"Node settings saved. Capability reporting was requested for the worker connection.",
			),
		);
	});

	// One-click recommended-reranker download: response-state handling.
	it("downloads the recommended reranker on a fresh start — shows progress and duplicate-guards the button", async () => {
		renderPage();
		const button = await screen.findByTestId("node-settings-reranker-download-recommended");

		fireEvent.click(button);

		// The generated mutation fires (no body/params), the fresh start marks the model in-flight, and the shared
		// download-progress panel appears keyed on that model while the button is duplicate-guarded (disabled).
		await waitFor(() => expect(generatedMock.downloadRerankerFn).toHaveBeenCalledTimes(1));
		await waitFor(() => expect(screen.getByTestId("model-fit-download-card")).toBeTruthy());
		expect(screen.getByTestId("model-fit-download-row-bge-reranker-v2-m3")).toBeTruthy();
		expect((screen.getByTestId("node-settings-reranker-download-recommended") as HTMLButtonElement).disabled).toBe(true);
		expect(toastMock.info).toHaveBeenCalled();
	});

	it("shows an already-installed notice and no progress panel when the reranker is already installed", async () => {
		generatedMock.downloadRerankerFn.mockResolvedValueOnce({
			modelName: "bge-reranker-v2-m3",
			repoId: "BAAI/bge-reranker-v2-m3",
			quant: "Q4_K_M",
			alreadyInstalled: true,
			alreadyInFlight: false,
		});

		renderPage();
		const button = await screen.findByTestId("node-settings-reranker-download-recommended");

		fireEvent.click(button);

		await waitFor(() => expect(generatedMock.downloadRerankerFn).toHaveBeenCalledTimes(1));
		// Already-installed surfaces an info notice, never marks in-flight, and shows no download-progress panel.
		await waitFor(() => expect(toastMock.info).toHaveBeenCalled());
		expect(screen.queryByTestId("model-fit-download-card")).toBeNull();
		expect((screen.getByTestId("node-settings-reranker-download-recommended") as HTMLButtonElement).disabled).toBe(false);
	});

	// One-click recommended-embedding download: response-state handling. Mirrors the reranker download above — the
	// embedding model is not a node-settings field, so this only exercises the button + shared progress feed.
	it("downloads the recommended embedding model on a fresh start — shows progress and duplicate-guards the button", async () => {
		renderPage();
		const button = await screen.findByTestId("node-settings-embedding-download-recommended");

		fireEvent.click(button);

		await waitFor(() => expect(generatedMock.downloadEmbeddingFn).toHaveBeenCalledTimes(1));
		await waitFor(() => expect(screen.getByTestId("model-fit-download-card")).toBeTruthy());
		expect(screen.getByTestId("model-fit-download-row-nomic-embed-text-v1.5")).toBeTruthy();
		expect((screen.getByTestId("node-settings-embedding-download-recommended") as HTMLButtonElement).disabled).toBe(true);
		expect(toastMock.info).toHaveBeenCalled();
	});

	it("shows an already-installed notice and no progress panel when the embedding model is already installed", async () => {
		generatedMock.downloadEmbeddingFn.mockResolvedValueOnce({
			modelName: "nomic-embed-text-v1.5",
			repoId: "nomic-ai/nomic-embed-text-v1.5-GGUF",
			quant: "Q4_K_M",
			alreadyInstalled: true,
			alreadyInFlight: false,
		});

		renderPage();
		const button = await screen.findByTestId("node-settings-embedding-download-recommended");

		fireEvent.click(button);

		await waitFor(() => expect(generatedMock.downloadEmbeddingFn).toHaveBeenCalledTimes(1));
		await waitFor(() => expect(toastMock.info).toHaveBeenCalled());
		expect(screen.queryByTestId("model-fit-download-card")).toBeNull();
		expect((screen.getByTestId("node-settings-embedding-download-recommended") as HTMLButtonElement).disabled).toBe(false);
	});

	// R2b lives in page state, not in the model: the model-level guard covers buildNodeSettingsRequest, this covers the
	// handler that decides what to hand it.
	it("clears the pending preset when a switch is edited after a preset click", async () => {
		generatedMock.getNodeSettingsOptions.mockReturnValue({
			queryKey: ["getNodeSettings"],
			queryFn: async () => ({
				...settingsResponse,
				externalAccessProfile: "offline",
				autoCheckApplicationUpdates: false,
				autoCheckRuntimeUpdates: false,
				autoProvisionFirstRunModel: false,
			}),
		});
		renderPage();
		await screen.findByDisplayValue("Offline / Manual");

		// Pick Recommended, then turn provisioning back off by hand. Sending the profile too would let the server honour
		// the preset and discard the operator's switch.
		fireEvent.click(screen.getByTestId("node-settings-external-access-profile"));
		// Scoped to this select's own listbox: "Recommended" also appears in the llama.cpp runtime card.
		const profileListbox = screen.getByRole("listbox", { name: "Profile", hidden: true });
		fireEvent.click(within(profileListbox).getByRole("option", { name: "Recommended", hidden: true }));
		const provisioning = screen.getByTestId("node-settings-auto-provision-first-run-model");
		fireEvent.click(provisioning.querySelector("input[type='checkbox']") ?? provisioning);
		fireEvent.click(screen.getByTestId("node-settings-fields-save-button"));

		await waitFor(() =>
			expect(generatedMock.saveFn.mock.calls[0]?.[0]).toEqual({
				body: {
					autoCheckApplicationUpdates: true,
					autoCheckRuntimeUpdates: true,
					maxMessageRequestTimeoutSeconds: 600,
				},
			}),
		);
	});
});
