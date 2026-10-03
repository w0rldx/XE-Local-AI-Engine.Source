// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { type ReactNode, useState } from "react";
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

vi.mock("@/features/node-settings/queries/useOllamaRuntimeConfigured", () => ({
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
		// The capability probes a feature-switch save invalidates.
		getDevelopmentCapabilityQueryKey: vi.fn(() => ["getDevelopmentCapability"]),
		getDevWorkflowCapabilityQueryKey: vi.fn(() => ["getDevWorkflowCapability"]),
		getGraphWorkflowCapabilityQueryKey: vi.fn(() => ["getGraphWorkflowCapability"]),
		getWorkSessionCapabilityQueryKey: vi.fn(() => ["getWorkSessionCapability"]),
		getTranscriptionRuntimeStatusQueryKey: vi.fn(() => ["getTranscriptionRuntimeStatus"]),
		getDefaultAssistantToolOfferQueryKey: vi.fn(() => fakeQueryKey("getDefaultAssistantToolOffer")),
		getToolCatalogQueryKey: vi.fn(() => fakeQueryKey("getToolCatalog")),
	},
}));

// Centralizes the `_id` discriminator literal (which trips biome's naming-convention rule) in one suppressed spot.
function fakeQueryKey(operationId: string): unknown {
	return [{ _id: operationId }];
}

// The effective tool-capable list only feeds the client mirror of the AgentHome rule; unknown (undefined) reads as satisfied.
vi.mock("@/features/node-settings/queries/useEffectiveToolCapableModels", () => ({
	useEffectiveToolCapableModels: () => undefined,
}));

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
	getDevelopmentCapabilityQueryKey: generatedMock.getDevelopmentCapabilityQueryKey,
	getDevWorkflowCapabilityQueryKey: generatedMock.getDevWorkflowCapabilityQueryKey,
	getGraphWorkflowCapabilityQueryKey: generatedMock.getGraphWorkflowCapabilityQueryKey,
	getWorkSessionCapabilityQueryKey: generatedMock.getWorkSessionCapabilityQueryKey,
	getTranscriptionRuntimeStatusQueryKey: generatedMock.getTranscriptionRuntimeStatusQueryKey,
	getDefaultAssistantToolOfferQueryKey: generatedMock.getDefaultAssistantToolOfferQueryKey,
	getToolCatalogQueryKey: generatedMock.getToolCatalogQueryKey,
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

vi.mock("@/features/node-settings/components/ManagedPythonCard", () => ({
	ManagedPythonCard: () => null,
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
import type { NodeSettingsSectionId } from "@/features/node-settings/models/NodeSettingsSections";
import { useHfTokenStore } from "@/features/node-settings/stores/HfTokenStore";
import { testMantineTheme } from "@/test/MantineTestRender";

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

// The route owns `?section=`; this harness stands in for it so a test can start in a section and follow the page's
// own navigation. Returns the client so a test can drive a BACKGROUND refetch (an invalidation), which is a different
// contract from the operator clicking Reset.
const sectionChanges = vi.fn();

function Harness({ initialSection }: { readonly initialSection: NodeSettingsSectionId }) {
	const [section, setSection] = useState<NodeSettingsSectionId>(initialSection);
	return (
		<NodeSettings
			section={section}
			onSectionChange={(next) => {
				sectionChanges(next);
				setSection(next);
			}}
			updateChannelSelector={<div data-testid="update-channel-slot" />}
		/>
	);
}

function renderPage(section: NodeSettingsSectionId = "chat", cachedSettings?: unknown): QueryClient {
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
			<MantineProvider env="test" theme={testMantineTheme}>
				{children}
			</MantineProvider>
		</QueryClientProvider>
	);

	render(<Harness initialSection={section} />, { wrapper });
	return queryClient;
}

function openSection(section: NodeSettingsSectionId): void {
	fireEvent.click(screen.getByTestId(`node-settings-section-${section}`));
}

function clickSwitch(testId: string): void {
	const toggle = screen.getByTestId(testId);
	fireEvent.click(toggle.querySelector("input[type='checkbox']") ?? toggle);
}

function saveBarStatus(): string {
	return screen.getByTestId("node-settings-save-bar-status").textContent ?? "";
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

		renderPage("runtimes");

		// Through the real page -> NodeSettingsFieldsCard -> NodeSettingsOllamaCard path, so the prop is proven wired.
		expect(screen.queryByTestId("node-settings-ollama-endpoint")).toBeNull();
		expect(screen.getByTestId("node-settings-ollama-disabled")).toBeTruthy();
	});

	it("keeps the Ollama endpoint field while the runtime probe has not answered", () => {
		renderPage("runtimes");

		// FAIL OPEN: `undefined` is a loading or failed probe, and must never take the setting away.
		expect(screen.getByTestId("node-settings-ollama-endpoint")).toBeTruthy();
		expect(screen.queryByTestId("node-settings-ollama-disabled")).toBeNull();
	});

	it("mounts workspace access directly below the inbound MCP key panel", () => {
		renderPage("integrations");

		const keyPanel = screen.getByTestId("mcp-server-key-panel");
		const workspacePanel = screen.getByTestId("mcp-workspace-allowlist-panel");
		expect(keyPanel.compareDocumentPosition(workspacePanel) & Node.DOCUMENT_POSITION_FOLLOWING).not.toBe(0);
		expect(screen.getByTestId("node-settings-integration-links")).toBeTruthy();
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
	// with the server's values. The draft also has to survive moving between sections.
	it("keeps in-progress edits across sections when a background refetch returns different server values", async () => {
		mockSecondLoadDiffers();

		const queryClient = renderPage("runtime");
		await waitFor(() => expect(generatedMock.getNodeSettingsOptions).toHaveBeenCalled());
		const maxProcesses = (await screen.findByTestId("node-settings-llama-max-processes")) as HTMLInputElement;
		await waitFor(() => expect(maxProcesses.value).toBe("3"));
		fireEvent.change(maxProcesses, { target: { value: "5" } });

		openSection("chat");
		const timeout = (await screen.findByLabelText(/Maximum message request timeout/)) as HTMLInputElement;
		fireEvent.change(timeout, { target: { value: "700" } });

		await queryClient.invalidateQueries({ queryKey: ["getNodeSettings"] });

		// The allowed-range description is rendered straight off the query data, so it only reads 7 once the second
		// response has actually landed in React — which is the moment the old effect would have clobbered the draft.
		await waitFor(() => expect(screen.getByText(/Allowed range: 7–3600 seconds\./)).toBeTruthy());
		// The NumberInput renders its suffix inside the value.
		expect(timeout.value).toBe("700 seconds");

		openSection("runtime");
		expect((screen.getByTestId("node-settings-llama-max-processes") as HTMLInputElement).value).toBe("5");
	});

	// Seeding only on the first load was wrong the other way round: mounting against a cached response latched the
	// draft to the CACHE, so the mount refetch's fresher values never landed and a Save wrote the stale ones back.
	it("adopts a fresher server state on mount over a stale cache while the draft is untouched", async () => {
		generatedMock.getNodeSettingsOptions.mockReturnValue({
			queryKey: ["getNodeSettings"],
			queryFn: async () => ({ ...settingsResponse, maxMessageRequestTimeoutSeconds: 900, llamaMaxLoadedProcesses: 9 }),
		});

		renderPage("chat", { ...settingsResponse, maxMessageRequestTimeoutSeconds: 120, llamaMaxLoadedProcesses: 2 });

		const timeout = (await screen.findByLabelText(/Maximum message request timeout/)) as HTMLInputElement;
		await waitFor(() => expect(timeout.value).toBe("900 seconds"));

		clickSwitch("node-settings-enable-tools");
		fireEvent.click(screen.getByTestId("node-settings-save-button"));

		// The stale 120 would have ridden the body here, overwriting the newer server value.
		await waitFor(() =>
			expect(generatedMock.saveFn.mock.calls[0]?.[0]).toEqual({
				body: { enableTools: false, maxMessageRequestTimeoutSeconds: 900 },
			}),
		);
	});

	// The other half of the same contract: Reset is the operator asking for the server's values, so it is the one
	// refetch that DOES discard the draft.
	it("replaces the draft with the server's values when the operator clicks Reset", async () => {
		mockSecondLoadDiffers();

		renderPage("chat");
		await screen.findByDisplayValue(/600/);

		const timeout = screen.getByLabelText(/Maximum message request timeout/) as HTMLInputElement;
		fireEvent.change(timeout, { target: { value: "700" } });

		fireEvent.click(screen.getByTestId("node-settings-reset-button"));

		await waitFor(() => expect(timeout.value).toBe("900 seconds"));
		expect(saveBarStatus()).toBe("No unsaved changes");
		openSection("runtime");
		expect((screen.getByTestId("node-settings-llama-max-processes") as HTMLInputElement).value).toBe("9");
	});

	it("loads settings through the generated query options", async () => {
		renderPage("chat");

		expect(generatedMock.getNodeSettingsOptions).toHaveBeenCalled();
		expect(await screen.findByDisplayValue(/600/)).toBeTruthy();
	});

	it("saves an edited timeout through the generated mutation", async () => {
		renderPage("chat");
		await screen.findByDisplayValue(/600/);

		fireEvent.change(screen.getByLabelText(/Maximum message request timeout/), { target: { value: "610" } });
		fireEvent.click(screen.getByTestId("node-settings-save-button"));

		await waitFor(() => {
			// TanStack passes a second context arg to mutationFn; assert only the request variables.
			expect(generatedMock.saveFn.mock.calls[0]?.[0]).toEqual({
				body: { maxMessageRequestTimeoutSeconds: 610 },
			});
		});
	});

	// The save bar: one save point, counts from the same diff as the save body, Save disabled while nothing changed.
	it("counts unsaved and restart-gated changes in the save bar and disables Save while clean", async () => {
		renderPage("runtime");
		const maxProcesses = (await screen.findByTestId("node-settings-llama-max-processes")) as HTMLInputElement;
		await waitFor(() => expect(generatedMock.getNodeSettingsOptions).toHaveBeenCalled());

		expect(saveBarStatus()).toBe("No unsaved changes");
		expect((screen.getByTestId("node-settings-save-button") as HTMLButtonElement).disabled).toBe(true);
		expect(screen.getByTestId("node-settings-save-bar-status").getAttribute("role")).toBe("status");

		// llamaMaxLoadedProcesses is restart-gated; enableTools is live.
		fireEvent.change(maxProcesses, { target: { value: "5" } });
		openSection("chat");
		clickSwitch("node-settings-enable-tools");

		expect(saveBarStatus()).toBe("2 unsaved changes · 1 need a restart");
		expect((screen.getByTestId("node-settings-save-button") as HTMLButtonElement).disabled).toBe(false);
		// Each section with an edit carries its own count in the nav.
		expect(screen.getByTestId("node-settings-section-runtime").textContent).toContain("1");

		// One Save sends both, wherever they were edited.
		fireEvent.click(screen.getByTestId("node-settings-save-button"));
		await waitFor(() =>
			expect(generatedMock.saveFn.mock.calls[0]?.[0]).toEqual({
				body: { llamaMaxLoadedProcesses: 5, enableTools: false, maxMessageRequestTimeoutSeconds: 600 },
			}),
		);
		// A single save point: the old second Save button is gone.
		expect(screen.queryByTestId("node-settings-fields-save-button")).toBeNull();
	});

	it("badges restart-gated fields in place of the old hint text", async () => {
		renderPage("runtime");

		expect(await screen.findByTestId("node-settings-restart-badge-llamaMaxLoadedProcesses")).toBeTruthy();
		expect(screen.queryByTestId("node-settings-restart-badge-keepModelWarmEnabled")).toBeNull();
	});

	// Section navigation: a landmark with aria-current, driven through the route's search param.
	it("switches sections through the nav, marks the active one and reports it to the route", async () => {
		renderPage("general");

		const nav = screen.getByRole("navigation", { name: "Settings sections" });
		expect(within(nav).getByTestId("node-settings-section-general").getAttribute("aria-current")).toBe("page");
		expect(screen.getByTestId("node-settings-section-content-general")).toBeTruthy();

		openSection("knowledge");

		expect(sectionChanges).toHaveBeenCalledWith("knowledge");
		expect(screen.getByTestId("node-settings-section-knowledge").getAttribute("aria-current")).toBe("page");
		expect(screen.getByTestId("node-settings-section-general").getAttribute("aria-current")).toBeNull();
		expect(await screen.findByTestId("node-settings-web-access-card")).toBeTruthy();
	});

	it("lists only the everyday sections in Simple mode until advanced sections are shown", async () => {
		generatedMock.getNodeSettingsOptions.mockReturnValue({
			queryKey: ["getNodeSettings"],
			queryFn: async () => ({ ...settingsResponse, uiMode: "simple" }),
		});

		renderPage("general");

		const toggle = await screen.findByTestId("node-settings-advanced-sections-toggle");
		for (const everyday of ["general", "chat", "models", "knowledge", "voice", "privacy"]) {
			expect(screen.getByTestId(`node-settings-section-${everyday}`)).toBeTruthy();
		}
		for (const advanced of ["runtime", "runtimes", "integrations", "workspaces", "usage"]) {
			expect(screen.queryByTestId(`node-settings-section-${advanced}`)).toBeNull();
		}

		fireEvent.click(toggle);

		expect(screen.getByTestId("node-settings-section-runtime")).toBeTruthy();
		expect(screen.getByTestId("node-settings-section-usage")).toBeTruthy();
	});

	it("keeps a linked advanced section reachable in Simple mode", async () => {
		generatedMock.getNodeSettingsOptions.mockReturnValue({
			queryKey: ["getNodeSettings"],
			queryFn: async () => ({ ...settingsResponse, uiMode: "simple" }),
		});

		renderPage("usage");

		await screen.findByTestId("node-settings-advanced-sections-toggle");
		expect(screen.getByTestId("node-settings-section-usage").getAttribute("aria-current")).toBe("page");
		expect(screen.getByTestId("node-settings-usage-rates-card")).toBeTruthy();
		expect(screen.queryByTestId("node-settings-section-runtime")).toBeNull();
	});

	// D4: the interface mode and the node voice fields are draft fields now — no instant PUT.
	it("saves the interface mode through the save bar and seeds the shared query the nav reads", async () => {
		// A server that keeps what it was sent, so the post-save refetch agrees with the seeded cache.
		let storedMode = "advanced";
		generatedMock.getNodeSettingsOptions.mockReturnValue({
			queryKey: ["getNodeSettings"],
			queryFn: async () => ({ ...settingsResponse, uiMode: storedMode }),
		});
		generatedMock.saveFn.mockImplementation(async ({ body }: { body: { uiMode?: string } }) => {
			storedMode = body.uiMode ?? storedMode;
			return { ...settingsResponse, uiMode: storedMode } as SaveNodeSettingsResponse;
		});

		const queryClient = renderPage("general");
		await waitFor(() => expect((screen.getByRole("radio", { name: "Advanced" }) as HTMLInputElement).checked).toBe(true));

		fireEvent.click(screen.getByRole("radio", { name: "Simple" }));

		expect(generatedMock.saveFn).not.toHaveBeenCalled();
		// This file's i18n stub renders the `_other` default for every count.
		expect(saveBarStatus()).toBe("1 unsaved changes");

		fireEvent.click(screen.getByTestId("node-settings-save-button"));

		await waitFor(() =>
			expect(generatedMock.saveFn.mock.calls[0]?.[0]).toEqual({
				body: { uiMode: "simple", maxMessageRequestTimeoutSeconds: 600 },
			}),
		);
		await waitFor(() => expect((queryClient.getQueryData(["getNodeSettings"]) as { uiMode?: string }).uiMode).toBe("simple"));
	});

	it("saves the node voice gate through the save bar instead of an instant write", async () => {
		renderPage("voice");

		const gate = (await screen.findByTestId("voice-settings-node-gate-switch")) as HTMLInputElement;
		await waitFor(() => expect(gate.disabled).toBe(false));
		fireEvent.click(gate);

		expect(generatedMock.saveFn).not.toHaveBeenCalled();
		fireEvent.click(screen.getByTestId("node-settings-save-button"));

		await waitFor(() =>
			expect(generatedMock.saveFn.mock.calls[0]?.[0]).toEqual({
				body: { voiceFeatureEnabled: true, maxMessageRequestTimeoutSeconds: 600 },
			}),
		);
	});

	it("groups the per-browser preferences in General and labels them as this browser only", () => {
		renderPage("general");

		const card = screen.getByTestId("node-settings-browser-preferences-card");
		expect(within(card).getByTestId("developer-mode-switch")).toBeTruthy();
		expect(within(card).getByTestId("node-settings-browser-only-badge").textContent).toBe("This browser only");
		expect(screen.getByTestId("node-settings-ui-mode-card")).toBeTruthy();
	});

	it("puts the route-supplied update-channel picker into Privacy & updates", () => {
		renderPage("privacy");

		expect(within(screen.getByTestId("node-settings-external-access-card")).getByTestId("update-channel-slot")).toBeTruthy();
	});

	// Action panels keep their own endpoints and sit in their sections, outside the save bar.
	it("renders the llama.cpp runtime card in Runtimes & builds", async () => {
		renderPage("runtimes");

		// The merged runtime card shows the installed tag + variant on mount (no operator click / version probe).
		expect(await screen.findByTestId("llamacpp-updater-card")).toBeTruthy();
		await waitFor(() => expect(screen.getByTestId("llamacpp-updater-installed").textContent).toContain("b1000"));
	});

	it("ensures the selected llama.cpp variant through the generated mutation", async () => {
		renderPage("runtimes");
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

		renderPage("models");

		const input = (await screen.findByTestId("model-fit-hf-token-input")) as HTMLInputElement;
		// PasswordInput renders a type=password field — the value is masked, never plain text.
		expect(input.type).toBe("password");
		await waitFor(() => expect(screen.getByTestId("model-fit-hf-token-status").textContent).toContain("Token configured"));
	});

	it("saves the HF token draft through the generated mutation", async () => {
		useHfTokenStore.setState({ tokenDraft: "hf_secret" });

		renderPage("models");
		await screen.findByTestId("model-fit-hf-token-card");

		fireEvent.click(screen.getByTestId("model-fit-hf-token-save"));

		// An empty draft clears the token (null body); a non-empty draft is sent verbatim.
		await waitFor(() => expect(generatedMock.setTokenFn.mock.calls[0]?.[0]).toEqual({ body: { token: "hf_secret" } }));
	});

	// Developer-mode gating: the switch lives in General, the gated card in Chat & agents.
	it("hides developer-only fields when developer mode is off and reveals them when on", async () => {
		localStorage.setItem("xe-developer-mode", "false");
		useDeveloperModeStore.setState({ developerMode: false });
		renderPage("chat");
		await screen.findByTestId("node-settings-local-chat-card");

		expect(screen.getByTestId("node-settings-default-model")).toBeTruthy();
		expect(screen.queryByTestId("node-settings-advanced-card")).toBeNull();

		openSection("general");
		clickSwitch("developer-mode-switch");
		openSection("chat");
		await waitFor(() => expect(screen.getByTestId("node-settings-advanced-card")).toBeTruthy());
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

		renderPage("runtime");
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

		renderPage("runtime");

		await waitFor(() => {
			const listbox = screen.getByRole("listbox", { name: "Model to keep warm", hidden: true });
			expect(within(listbox).getByRole("option", { name: "deleted-model (not installed)", hidden: true })).toBeTruthy();
		});
		expect(await screen.findByText("The selected model deleted-model is no longer installed.")).toBeTruthy();

		// An unrelated edit makes the draft saveable; the unavailable model still blocks the save.
		fireEvent.change(screen.getByTestId("node-settings-chat-cache-reuse"), { target: { value: "300" } });
		fireEvent.click(screen.getByTestId("node-settings-save-button"));
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

		renderPage("runtime");

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
		renderPage("models");
		await screen.findByDisplayValue("Q4_K_M");

		// huggingFaceDefaultQuant is seeded once into the HF options at composition.
		fireEvent.change(screen.getByTestId("node-settings-hf-default-quant"), { target: { value: "Q5_K_M" } });
		fireEvent.click(screen.getByTestId("node-settings-save-button"));

		await waitFor(() => expect(generatedMock.saveFn).toHaveBeenCalled());
		await waitFor(() =>
			expect(toastMock.success).toHaveBeenCalledWith(
				"Node settings saved. Some of the changed settings only take effect after the node restarts.",
			),
		);
	});

	it("saves a feature switch from the General Features card and says a restart is needed", async () => {
		generatedMock.getNodeSettingsOptions.mockReturnValue({
			queryKey: ["getNodeSettings"],
			queryFn: async () => ({ ...settingsResponse, schedulerEnabled: true, workSessionsEnabled: true }),
		});
		renderPage("general");
		await waitFor(() =>
			expect((screen.getByTestId("node-settings-feature-work-sessions") as HTMLInputElement).checked).toBe(true),
		);

		fireEvent.click(screen.getByTestId("node-settings-feature-scheduler"));
		fireEvent.click(screen.getByTestId("node-settings-save-button"));

		await waitFor(() =>
			expect(generatedMock.saveFn.mock.calls[0]?.[0]).toEqual({
				body: { schedulerEnabled: false, maxMessageRequestTimeoutSeconds: 600 },
			}),
		);
		await waitFor(() =>
			expect(toastMock.success).toHaveBeenCalledWith(
				"Node settings saved. Some of the changed settings only take effect after the node restarts.",
			),
		);
	});

	it("invalidates the capability probes when a feature switch was saved", async () => {
		const queryClient = renderPage("general");
		const invalidate = vi.spyOn(queryClient, "invalidateQueries");
		await waitFor(() => expect(generatedMock.getNodeSettingsOptions).toHaveBeenCalled());

		fireEvent.click(screen.getByTestId("node-settings-feature-graph-workflows"));
		fireEvent.click(screen.getByTestId("node-settings-save-button"));

		const capabilityKeys = [
			["getDevelopmentCapability"],
			["getDevWorkflowCapability"],
			["getGraphWorkflowCapability"],
			["getWorkSessionCapability"],
			["getTranscriptionRuntimeStatus"],
		];
		await waitFor(() => {
			for (const queryKey of capabilityKeys) {
				expect(invalidate).toHaveBeenCalledWith({ queryKey });
			}
		});
	});

	it("leaves the capability probes alone when no feature switch was saved", async () => {
		const queryClient = renderPage("chat");
		const invalidate = vi.spyOn(queryClient, "invalidateQueries");
		await waitFor(() => expect(generatedMock.getNodeSettingsOptions).toHaveBeenCalled());

		clickSwitch("node-settings-enable-tools");
		fireEvent.click(screen.getByTestId("node-settings-save-button"));

		await waitFor(() => expect(invalidate).toHaveBeenCalledWith({ queryKey: ["getNodeSettings"] }));
		await waitFor(() => expect(invalidate).toHaveBeenCalledTimes(3));
		expect(invalidate).not.toHaveBeenCalledWith({ queryKey: ["getDevelopmentCapability"] });
	});

	it("invalidates every Default Assistant tool offer and the tool catalog when web access was saved", async () => {
		const queryClient = renderPage("knowledge");
		const invalidate = vi.spyOn(queryClient, "invalidateQueries");
		await waitFor(() => expect(generatedMock.getNodeSettingsOptions).toHaveBeenCalled());

		clickSwitch("node-settings-web-access-enabled");
		fireEvent.click(screen.getByTestId("node-settings-save-button"));

		await waitFor(() => {
			expect(invalidate).toHaveBeenCalledWith({ queryKey: fakeQueryKey("getDefaultAssistantToolOffer") });
			expect(invalidate).toHaveBeenCalledWith({ queryKey: fakeQueryKey("getToolCatalog") });
		});
	});

	it("refuses development workflows without work sessions, returns to General and flags the switch", async () => {
		renderPage("general");
		await waitFor(() => expect(generatedMock.getNodeSettingsOptions).toHaveBeenCalled());

		fireEvent.click(screen.getByTestId("node-settings-feature-dev-workflows"));
		openSection("chat");
		fireEvent.click(screen.getByTestId("node-settings-save-button"));

		await waitFor(() => expect(screen.getByTestId("node-settings-features-card")).toBeTruthy());
		expect(generatedMock.saveFn).not.toHaveBeenCalled();
	});

	it("keeps the plain saved notice when only live fields changed", async () => {
		generatedMock.getNodeSettingsOptions.mockReturnValue({
			queryKey: ["getNodeSettings"],
			queryFn: async () => ({ ...settingsResponse, defaultModelName: "loaded-model" }),
		});
		renderPage("chat");
		await screen.findByDisplayValue("loaded-model");

		// enableTools is re-read per send/regenerate — no restart involved.
		clickSwitch("node-settings-enable-tools");
		fireEvent.click(screen.getByTestId("node-settings-save-button"));

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

	it("saves the web-access switch and a cleared SearXNG URL", async () => {
		generatedMock.getNodeSettingsOptions.mockReturnValue({
			queryKey: ["getNodeSettings"],
			queryFn: async () => ({ ...settingsResponse, webAccessEnabled: false, webSearchSearxngUrl: "http://localhost:8888" }),
		});
		renderPage("knowledge");
		await screen.findByDisplayValue("http://localhost:8888");

		const toggle = screen.getByTestId("node-settings-web-access-enabled");
		fireEvent.click(toggle.querySelector("input[type='checkbox']") ?? toggle);
		fireEvent.change(screen.getByTestId("node-settings-web-search-searxng-url"), { target: { value: "" } });
		fireEvent.click(screen.getByTestId("node-settings-save-button"));

		await waitFor(() =>
			expect(generatedMock.saveFn.mock.calls[0]?.[0]).toEqual({
				body: { webAccessEnabled: true, webSearchSearxngUrl: "", maxMessageRequestTimeoutSeconds: 600 },
			}),
		);
	});

	it("refuses to save a relative SearXNG URL, returns to its section and flags the field", async () => {
		renderPage("knowledge");
		await waitFor(() => expect(generatedMock.getNodeSettingsOptions).toHaveBeenCalled());

		fireEvent.change(screen.getByTestId("node-settings-web-search-searxng-url"), { target: { value: "/search" } });
		// Saving from another section still lands the operator on the invalid field.
		openSection("chat");
		fireEvent.click(screen.getByTestId("node-settings-save-button"));

		// This file mocks i18n, so the field's invalid state is asserted here; the bundle copy is asserted in
		// NodeSettingsWebAccessCard.test.tsx.
		await waitFor(() =>
			expect(screen.getByTestId("node-settings-web-search-searxng-url").getAttribute("aria-invalid")).toBe("true"),
		);
		expect(generatedMock.saveFn).not.toHaveBeenCalled();
	});

	// One-click recommended-reranker download: response-state handling.
	it("downloads the recommended reranker on a fresh start — shows progress and duplicate-guards the button", async () => {
		renderPage("knowledge");
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

		renderPage("knowledge");
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
		renderPage("knowledge");
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

		renderPage("knowledge");
		const button = await screen.findByTestId("node-settings-embedding-download-recommended");

		fireEvent.click(button);

		await waitFor(() => expect(generatedMock.downloadEmbeddingFn).toHaveBeenCalledTimes(1));
		await waitFor(() => expect(toastMock.info).toHaveBeenCalled());
		expect(screen.queryByTestId("model-fit-download-card")).toBeNull();
		expect((screen.getByTestId("node-settings-embedding-download-recommended") as HTMLButtonElement).disabled).toBe(false);
	});

	// Clearing the pending preset lives in page state, not in the model: the model-level guard covers
	// buildNodeSettingsRequest, this covers the handler that decides what to hand it.
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
		renderPage("privacy");
		await screen.findByDisplayValue("Offline / Manual");

		// Pick Recommended, then turn provisioning back off by hand. Sending the profile too would let the server honour
		// the preset and discard the operator's switch.
		fireEvent.click(screen.getByTestId("node-settings-external-access-profile"));
		// Scoped to this select's own listbox: "Recommended" also appears in the llama.cpp runtime card.
		const profileListbox = screen.getByRole("listbox", { name: "Profile", hidden: true });
		fireEvent.click(within(profileListbox).getByRole("option", { name: "Recommended", hidden: true }));
		const provisioning = screen.getByTestId("node-settings-auto-provision-first-run-model");
		fireEvent.click(provisioning.querySelector("input[type='checkbox']") ?? provisioning);
		fireEvent.click(screen.getByTestId("node-settings-save-button"));

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

	it("refuses a default knowledge result count above the maximum and flags it in its section", async () => {
		renderPage("knowledge");
		await waitFor(() => expect(generatedMock.getNodeSettingsOptions).toHaveBeenCalled());

		fireEvent.change(await screen.findByTestId("node-settings-knowledge-max-results"), { target: { value: "3" } });
		fireEvent.click(screen.getByTestId("node-settings-save-button"));

		await waitFor(() =>
			expect(screen.getByTestId("node-settings-knowledge-default-results").getAttribute("aria-invalid")).toBe("true"),
		);
		expect(generatedMock.saveFn).not.toHaveBeenCalled();
	});

	it("saves a return to automatic prompt-cache memory as the -1 reset", async () => {
		generatedMock.getNodeSettingsOptions.mockReturnValue({
			queryKey: ["getNodeSettings"],
			queryFn: async () => ({ ...settingsResponse, llamaChatCacheRamMiB: 2048 }),
		});
		renderPage("runtime");

		await screen.findByDisplayValue("2048 MiB");
		fireEvent.click(screen.getByRole("radio", { name: "Automatic" }));

		expect(saveBarStatus()).toBe("1 unsaved changes · 1 need a restart");
		fireEvent.click(screen.getByTestId("node-settings-save-button"));

		await waitFor(() =>
			expect(generatedMock.saveFn.mock.calls[0]?.[0]).toEqual({
				body: { llamaChatCacheRamMiB: -1, maxMessageRequestTimeoutSeconds: 600 },
			}),
		);
	});
});
