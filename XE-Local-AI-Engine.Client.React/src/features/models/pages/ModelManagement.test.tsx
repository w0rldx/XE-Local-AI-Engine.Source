// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { QueryClient, QueryClientProvider, useQuery } from "@tanstack/react-query";
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import type { ReactElement } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

// Mock the SignalR client the live download-progress hook (useActiveGgufDownloads) opens on mount. The page renders the
// DownloadProgressPanel which mounts that hook; without this the real HubConnectionBuilder throws in jsdom. The
// connection is an inert stub (start/stop resolve, on/off no-op) — these tests assert REST-driven download behavior, not
// live pushes, so no handler needs to be captured here.
vi.mock("@microsoft/signalr", () => ({
	HubConnectionBuilder: vi.fn(function HubConnectionBuilder() {
		const builder = {
			withUrl: vi.fn(() => builder),
			withAutomaticReconnect: vi.fn(() => builder),
			configureLogging: vi.fn(() => builder),
			build: vi.fn(() => ({
				on: vi.fn(),
				off: vi.fn(),
				onreconnected: vi.fn(),
				onreconnecting: vi.fn(),
				onclose: vi.fn(),
				start: vi.fn(() => Promise.resolve()),
				stop: vi.fn(() => Promise.resolve()),
			})),
		};
		return builder;
	}),
	HubConnectionState: { Connected: "Connected", Disconnected: "Disconnected" },
	LogLevel: { Warning: 3 },
}));

// Mock the generated hey-api TanStack layer. The read factories return `{ queryKey, queryFn }` (the queryFn is the
// data source the page renders from); the mutation factories return `{ mutationFn }` the page spreads into
// useMutation. The page wraps every factory result in the real withResponseValidation (not mocked), then layers its
// own onSuccess invalidation. The factory mocks let a test assert the variable shape the page forwarded to the wire.
// Each *QueryKey factory returns a stable single-element key keyed by the operationId so the page's invalidation
// (which calls the same factories) refetches the live query — proving the post-override list refetch. The model-fit
// `getLatestRecommendations` factory is mocked too because the details dialog's Fit tab joins the installed model
// into the latest cached llmfit snapshot.
const { queryFns, mutationFns } = vi.hoisted(() => ({
	queryFns: {
		listLocalModels: vi.fn(),
		getLocalModelDetails: vi.fn(),
		getLatestRecommendations: vi.fn(),
		browseGgufRepositories: vi.fn(),
		inspectGgufRepository: vi.fn(),
		getGgufImportCapability: vi.fn(),
		getGgufImports: vi.fn(),
		getModelCatalogInfo: vi.fn(),
		getHardwareProfile: vi.fn(),
	},
	mutationFns: {
		selectLocalModel: vi.fn(),
		deleteLocalModel: vi.fn(),
		putModelKind: vi.fn(),
		deleteModelKind: vi.fn(),
		startGgufDownload: vi.fn(),
		cancelGgufDownload: vi.fn(),
		previewGgufImport: vi.fn(),
		startGgufImport: vi.fn(),
		cancelGgufImport: vi.fn(),
	},
}));

// Centralizes the `_id` discriminator literal (which trips biome's naming-convention rule) in one suppressed spot.
function fakeQueryKey(operationId: string): unknown {
	return [{ _id: operationId }];
}

vi.mock("@/core/api/generated/@tanstack/react-query.gen", () => ({
	listLocalModelsQueryKey: () => fakeQueryKey("listLocalModels"),
	listLocalModelsOptions: () => ({ queryKey: fakeQueryKey("listLocalModels"), queryFn: queryFns.listLocalModels }),
	getLocalModelDetailsQueryKey: () => fakeQueryKey("getLocalModelDetails"),
	getLocalModelDetailsOptions: () => ({
		queryKey: fakeQueryKey("getLocalModelDetails"),
		queryFn: queryFns.getLocalModelDetails,
	}),
	selectLocalModelMutation: () => ({ mutationFn: mutationFns.selectLocalModel }),
	deleteLocalModelMutation: () => ({ mutationFn: mutationFns.deleteLocalModel }),
	putModelKindMutation: () => ({ mutationFn: mutationFns.putModelKind }),
	deleteModelKindMutation: () => ({ mutationFn: mutationFns.deleteModelKind }),
	// Model-fit factories consumed transitively by the Fit tab (useLatestRecommendations).
	getLatestRecommendationsOptions: () => ({
		queryKey: fakeQueryKey("getLatestRecommendations"),
		queryFn: queryFns.getLatestRecommendations,
	}),
	refreshRecommendationsMutation: () => ({ mutationFn: vi.fn() }),
	// Catalog info: the browse panel lists its tested models before any search.
	getModelCatalogInfoOptions: () => ({
		queryKey: fakeQueryKey("getModelCatalogInfo"),
		queryFn: queryFns.getModelCatalogInfo,
	}),
	// Hardware profile: the tested list observes the shell's profile read to refetch verdicts once the audit exists.
	getHardwareProfileOptions: () => ({
		queryKey: fakeQueryKey("getHardwareProfile"),
		queryFn: queryFns.getHardwareProfile,
	}),
	// GGUF browse + download factories — the GGUF section relocated to this page from the model-fit advisor.
	browseGgufRepositoriesQueryKey: () => fakeQueryKey("browseGgufRepositories"),
	browseGgufRepositoriesOptions: () => ({
		queryKey: fakeQueryKey("browseGgufRepositories"),
		queryFn: queryFns.browseGgufRepositories,
	}),
	inspectGgufRepositoryQueryKey: () => fakeQueryKey("inspectGgufRepository"),
	inspectGgufRepositoryOptions: () => ({
		queryKey: fakeQueryKey("inspectGgufRepository"),
		queryFn: queryFns.inspectGgufRepository,
	}),
	startGgufDownloadMutation: () => ({ mutationFn: mutationFns.startGgufDownload }),
	cancelGgufDownloadMutation: () => ({ mutationFn: mutationFns.cancelGgufDownload }),
	// Active-downloads polling endpoint — returns empty list so no downloads are active in tests by default.
	getGgufDownloadsQueryKey: () => fakeQueryKey("getGgufDownloads"),
	getGgufDownloadsOptions: () => ({
		queryKey: fakeQueryKey("getGgufDownloads"),
		queryFn: async () => ({ items: [] }),
	}),
	getGgufImportCapabilityOptions: () => ({
		queryKey: fakeQueryKey("getGgufImportCapability"),
		queryFn: queryFns.getGgufImportCapability,
	}),
	getGgufImportsQueryKey: () => fakeQueryKey("getGgufImports"),
	getGgufImportsOptions: () => ({ queryKey: fakeQueryKey("getGgufImports"), queryFn: queryFns.getGgufImports }),
	previewGgufImportMutation: () => ({ mutationFn: mutationFns.previewGgufImport }),
	startGgufImportMutation: () => ({ mutationFn: mutationFns.startGgufImport }),
	cancelGgufImportMutation: () => ({ mutationFn: mutationFns.cancelGgufImport }),
}));

const { confirmMock } = vi.hoisted(() => ({ confirmMock: vi.fn() }));
vi.mock("@/core/ui/hooks/useConfirm", () => ({
	useConfirm: () => ({ confirm: confirmMock }),
}));

import { resetSharedHubConnectionsForTest } from "@/core/api/signalr/SharedHubConnection";
import { toast } from "@/core/ui/notifications/Toast";
import "@/i18n";
import { ModelManagement } from "@/features/models/pages/ModelManagement";
import { useGgufBrowseStore } from "@/features/models/stores/GgufBrowseStore";
import { testMantineTheme } from "@/test/MantineTestRender";

const hardwareProfile = {
	totalRamBytes: 34_359_738_368,
	availableRamBytes: 17_179_869_184,
	vramBytes: 8_589_934_592,
	vramKnown: true,
	gpuVendor: "nvidia",
	gpuAccelAvailable: true,
	cpuCores: 16,
	freeDiskBytes: 500_000_000_000,
};

function renderWithProviders(ui: ReactElement) {
	const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
	return render(
		<MantineProvider env="test" theme={testMantineTheme}>
			<QueryClientProvider client={queryClient}>{ui}</QueryClientProvider>
		</MantineProvider>,
	);
}

// Opens the per-model details dialog by clicking the model-name button in the table, then returns the dialog element.
async function openDetailsDialog(modelName: string): Promise<HTMLElement> {
	fireEvent.click(await screen.findByTestId(`model-details-button-${modelName}`));
	return screen.findByRole("dialog");
}

describe("ModelManagement", () => {
	beforeEach(() => {
		vi.clearAllMocks();
		resetSharedHubConnectionsForTest();
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
		// jsdom does not implement scrollIntoView; Mantine's Combobox calls it when the override Select dropdown opens,
		// which would otherwise surface as an unhandled rejection from a deferred timer.
		Element.prototype.scrollIntoView = vi.fn();
		queryFns.listLocalModels.mockResolvedValue({
			isAvailable: true,
			selectedModelName: "llama3:8b",
			configuredDefaultModelName: "llama3:8b",
			error: null,
			items: [
				{
					modelName: "llama3:8b",
					sizeBytes: 1_073_741_824,
					modifiedAtUtc: Date.UTC(2026, 4, 24),
					family: "llama",
					parameterSize: "8B",
					quantizationLevel: "Q4_0",
					isSelected: true,
					kind: "Chat",
					detectedKind: "Chat",
					capabilities: ["completion", "tools"],
					isOverridden: false,
				},
			],
		});
		queryFns.getLocalModelDetails.mockResolvedValue({
			modelName: "llama3:8b",
			maxContextTokens: 8192,
			template: "{{ .Prompt }}",
			system: null,
			license: "fake",
		});
		queryFns.getLatestRecommendations.mockResolvedValue({ hasCache: false, recommendations: [] });
		mutationFns.selectLocalModel.mockResolvedValue({ selectedModelName: "llama3:8b" });
		mutationFns.deleteLocalModel.mockResolvedValue({ modelName: "llama3:8b", deleted: true });
		mutationFns.putModelKind.mockResolvedValue({
			modelName: "llama3:8b",
			kind: "Embedding",
			detectedKind: "Chat",
			capabilities: ["completion", "tools"],
			isOverridden: true,
		});
		mutationFns.deleteModelKind.mockResolvedValue({
			modelName: "llama3:8b",
			kind: "Chat",
			detectedKind: "Chat",
			capabilities: ["completion", "tools"],
			isOverridden: false,
		});
		// GGUF browse + download defaults. The browse query is gated until the operator submits a search term.
		queryFns.browseGgufRepositories.mockResolvedValue({ items: [] });
		queryFns.inspectGgufRepository.mockResolvedValue({ repoId: "", files: [] });
		queryFns.getGgufImportCapability.mockResolvedValue({ available: false });
		queryFns.getGgufImports.mockResolvedValue({ items: [] });
		queryFns.getModelCatalogInfo.mockResolvedValue({
			catalogVersion: "2026.10.2",
			source: "bundled",
			modelCount: 45,
			refreshSourceConfigured: false,
			testedModels: [],
		});
		queryFns.getHardwareProfile.mockResolvedValue(hardwareProfile);
		mutationFns.startGgufDownload.mockResolvedValue({ modelName: "unsloth/llama-3.1-8b-gguf", alreadyInFlight: false });
		mutationFns.cancelGgufDownload.mockResolvedValue({ cancelled: true });
		mutationFns.cancelGgufImport.mockResolvedValue({ cancellationRequested: true });
		// The committed GGUF browse term + the shared in-flight download set survive a remount — reset both so each test
		// starts blank (the in-flight set is now shared, since the advisor hands recommendation-row downloads off here).
		useGgufBrowseStore.setState({ browseQuery: "", inFlightDownloads: [] });
		confirmMock.mockResolvedValue(true);
	});

	afterEach(() => {
		cleanup();
		vi.useRealTimers();
	});

	it("renders local models in the table and shows details in the dialog", async () => {
		renderWithProviders(<ModelManagement />);

		// The table row carries the name, size, and the default badge.
		expect((await screen.findAllByText("llama3:8b")).length).toBeGreaterThan(0);
		expect(screen.getByText("1.0 GB")).toBeTruthy();
		expect(screen.getByText("Default")).toBeTruthy();

		// Details (context length) live in the dialog's Overview tab, not on the page.
		expect(screen.queryByText("Context length: 8,192")).toBeNull();
		const dialog = await openDetailsDialog("llama3:8b");
		expect(await within(dialog).findByText("Context length: 8,192")).toBeTruthy();
	});

	// D10: the list endpoint appends external-provider registrations so the chat picker can offer them, but this page is
	// the model STORE. An external model has no file to delete, no kind override to reset and no store default to hold,
	// so it must never reach the installed table — where every row carries exactly those actions.
	it("keeps external-provider models out of the installed table and its actions", async () => {
		queryFns.listLocalModels.mockResolvedValue({
			isAvailable: true,
			selectedModelName: "llama3:8b",
			configuredDefaultModelName: "llama3:8b",
			error: null,
			items: [
				{
					modelName: "llama3:8b",
					sizeBytes: 1_073_741_824,
					modifiedAtUtc: Date.UTC(2026, 4, 24),
					family: "llama",
					parameterSize: "8B",
					quantizationLevel: "Q4_0",
					isSelected: true,
					kind: "Chat",
					detectedKind: "Chat",
					capabilities: ["completion", "tools"],
					isOverridden: false,
				},
				{
					modelName: "ext:unsloth-box/qwen3-27b",
					provider: "external",
					isSelected: false,
					kind: "Chat",
					detectedKind: "Chat",
					capabilities: ["completion"],
					isOverridden: false,
					externalConnectionId: "unsloth-box",
					externalConnectionName: "Unsloth box",
					declaredLocality: "local",
				},
			],
		});

		renderWithProviders(<ModelManagement />);

		// Wait for the row the list DOES carry, so the absence assertions below are about a rendered table.
		await screen.findByTestId("model-details-button-llama3:8b");

		const table = screen.getByTestId("installed-models-table");
		expect(within(table).queryByText("ext:unsloth-box/qwen3-27b")).toBeNull();
		expect(screen.queryByTestId("model-details-button-ext:unsloth-box/qwen3-27b")).toBeNull();
		expect(screen.queryByLabelText("Delete ext:unsloth-box/qwen3-27b")).toBeNull();
		expect(screen.queryByLabelText("Set ext:unsloth-box/qwen3-27b as default")).toBeNull();
	});

	it("selects and deletes models through the generated mutations", async () => {
		renderWithProviders(<ModelManagement />);
		await screen.findByLabelText("Set llama3:8b as default");

		fireEvent.click(screen.getByLabelText("Set llama3:8b as default"));
		// TanStack v5 passes (variables, context) — assert the first arg carries the generated body shape.
		await waitFor(() => expect(mutationFns.selectLocalModel.mock.calls[0]?.[0]).toEqual({ body: { modelName: "llama3:8b" } }));

		const deleteButton = screen.getAllByLabelText("Delete llama3:8b").find((element) => element.tagName === "BUTTON");
		expect(deleteButton).toBeTruthy();
		fireEvent.click(deleteButton!);
		await waitFor(() => expect(confirmMock).toHaveBeenCalled());
		await waitFor(() => expect(mutationFns.deleteLocalModel.mock.calls[0]?.[0]).toEqual({ path: { modelName: "llama3:8b" } }));
	});

	it("renders the type column with the effective kind badge and capability badges", async () => {
		renderWithProviders(<ModelManagement />);

		const kindBadge = await screen.findByTestId("model-kind-badge-llama3:8b");
		expect(kindBadge.textContent).toContain("Chat");
		// Raw Ollama capabilities surface as read-only badges in the table.
		expect(screen.getByText("Tools")).toBeTruthy();
		// A non-overridden model shows no "reset to detected" affordance, and the table no longer carries the override
		// Select (it moved into the details dialog).
		expect(screen.queryByLabelText("Reset llama3:8b type to detected")).toBeNull();
		expect(screen.queryByLabelText("Override type for llama3:8b")).toBeNull();
	});

	it("overrides a model kind through the dialog Type tab and reflects the refetched (overridden) row", async () => {
		// First list shows the detected Chat kind. The post-override refetch returns the OVERRIDDEN row, so the badge
		// must update from the refetch — not from the mutation response (which the page intentionally does not trust).
		const overriddenRow = {
			modelName: "llama3:8b",
			sizeBytes: 1_073_741_824,
			modifiedAtUtc: Date.UTC(2026, 4, 24),
			family: "llama",
			parameterSize: "8B",
			quantizationLevel: "Q4_0",
			isSelected: true,
			kind: "Embedding",
			detectedKind: "Chat",
			capabilities: ["completion", "tools"],
			isOverridden: true,
		};
		queryFns.listLocalModels
			.mockResolvedValueOnce({
				isAvailable: true,
				selectedModelName: "llama3:8b",
				configuredDefaultModelName: "llama3:8b",
				error: null,
				items: [{ ...overriddenRow, kind: "Chat", isOverridden: false }],
			})
			.mockResolvedValue({
				isAvailable: true,
				selectedModelName: "llama3:8b",
				configuredDefaultModelName: "llama3:8b",
				error: null,
				items: [overriddenRow],
			});

		renderWithProviders(<ModelManagement />);

		const dialog = await openDetailsDialog("llama3:8b");
		fireEvent.click(within(dialog).getByRole("tab", { name: "Type" }));

		// Open the override Select (now inside the dialog Type tab) and choose Embedding. Mantine's Select associates the
		// aria-label with more than one node, so target the input element explicitly.
		const select = within(dialog)
			.getAllByLabelText("Override type for llama3:8b")
			.find((element) => element.tagName === "INPUT");
		expect(select).toBeTruthy();
		fireEvent.click(select!);
		fireEvent.click(await screen.findByRole("option", { name: "Embedding" }));

		await waitFor(() =>
			expect(mutationFns.putModelKind.mock.calls[0]?.[0]).toEqual({
				path: { modelName: "llama3:8b" },
				body: { kind: "Embedding" },
			}),
		);
		// The list is refetched after the override so lazy detection refreshes the row.
		await waitFor(() => expect(queryFns.listLocalModels).toHaveBeenCalledTimes(2));
		// The dialog badge reflects the refetched overridden row, and the reset affordance now appears.
		await waitFor(() => expect(within(dialog).getByTestId("model-kind-badge-llama3:8b").textContent).toContain("Embedding"));
		expect(within(dialog).getByLabelText("Reset llama3:8b type to detected")).toBeTruthy();
	});

	it("shows a reset-to-detected action only for overridden models and resets through the generated mutation", async () => {
		queryFns.listLocalModels.mockResolvedValue({
			isAvailable: true,
			selectedModelName: "llama3:8b",
			configuredDefaultModelName: "llama3:8b",
			error: null,
			items: [
				{
					modelName: "llama3:8b",
					sizeBytes: 1_073_741_824,
					modifiedAtUtc: Date.UTC(2026, 4, 24),
					family: "llama",
					parameterSize: "8B",
					quantizationLevel: "Q4_0",
					isSelected: true,
					// Operator overrode this chat model to Embedding — effective kind differs from detected.
					kind: "Embedding",
					detectedKind: "Chat",
					capabilities: ["completion", "tools"],
					isOverridden: true,
				},
			],
		});

		renderWithProviders(<ModelManagement />);

		// Overridden models keep a quick reset affordance directly in the table row.
		const resetButton = await screen.findByLabelText("Reset llama3:8b type to detected");
		fireEvent.click(resetButton);

		await waitFor(() => expect(mutationFns.deleteModelKind.mock.calls[0]?.[0]).toEqual({ path: { modelName: "llama3:8b" } }));
	});

	it("renders the empty installed list without surfacing a provider-unavailable error", async () => {
		// isAvailable=false no longer means the page is broken: installed GGUF + cloud models still render from the same
		// response even when the legacy Ollama probe reports unavailable, so the page shows the empty-list state rather
		// than a false error banner.
		queryFns.listLocalModels.mockResolvedValue({
			isAvailable: false,
			selectedModelName: null,
			configuredDefaultModelName: "llama3:8b",
			error: "Local model provider is unavailable.",
			items: [],
		});

		renderWithProviders(<ModelManagement />);

		// The empty installed-models state renders, and the provider-unavailable error is NOT shown.
		expect(await screen.findByText("No local models found.")).toBeTruthy();
		expect(screen.queryByText("Local model provider is unavailable.")).toBeNull();
	});

	it("shows license and template in the dialog License tab", async () => {
		renderWithProviders(<ModelManagement />);

		// No dialog is open initially, and there is no inline "Show full / Show less" expander.
		expect(screen.queryByRole("dialog")).toBeNull();
		expect(screen.queryByRole("button", { name: /show full/i })).toBeNull();

		const dialog = await openDetailsDialog("llama3:8b");
		fireEvent.click(within(dialog).getByRole("tab", { name: /license/i }));

		// The License tab carries BOTH the template and the license. Awaited, not read synchronously: the tab is opened
		// the moment the dialog exists, and the details this panel renders arrive from their own request afterwards.
		expect((await within(dialog).findByTestId("model-template-content")).textContent).toContain("{{ .Prompt }}");
		expect(within(dialog).getByTestId("model-license-content").textContent).toContain("fake");
	});

	it("joins the installed model into the latest llmfit snapshot in the Fit tab", async () => {
		queryFns.getLatestRecommendations.mockResolvedValue({
			hasCache: true,
			lastRefreshedAtUtc: Date.UTC(2026, 4, 24),
			recommendations: [
				{
					rank: 1,
					modelName: "llama3:8b",
					providerModelName: "llama3:8b",
					pullModelName: "llama3:8b",
					score: 8.4,
					fitLevel: "Good",
					runMode: "gpu",
					estimatedTokensPerSecond: 42,
					requiredRamMb: 6144,
					requiredVramMb: 4096,
					contextTokens: 8192,
					quantization: "Q4_0",
					isInstalled: true,
				},
			],
		});

		renderWithProviders(<ModelManagement />);

		const dialog = await openDetailsDialog("llama3:8b");
		fireEvent.click(within(dialog).getByRole("tab", { name: "Fit" }));

		const result = await within(dialog).findByTestId("model-fit-result");
		expect(result.textContent).toContain("8.4");
		expect(within(dialog).getByText("Good")).toBeTruthy();
		// The rank badge and the metric labels resolve through i18n (this file initializes it), so a missing key would
		// surface the raw `pages.modelFit....` path here instead of the bundle text.
		expect(result.textContent).toContain("Rank #1");
		expect(result.textContent).toContain("Req. RAM");
		// Panel-scoped, deliberately not the table column's "Quant" abbreviation.
		expect(result.textContent).toContain("Quantization");
		expect(result.textContent).not.toContain("pages.modelFit");
	});

	// GGUF browse + download flow (relocated from the model-fit advisor; it is the model-acquisition path on this page).
	const ggufRepo = {
		repoId: "unsloth/llama-3.1-8b-gguf",
		isGated: false,
		downloads: 1000,
		likes: 50,
		lastModifiedAtUtc: 1_700_000_000_000,
		license: "apache-2.0",
		hasUsableGguf: true,
		isTrustedPublisher: true,
	};

	it("renders the GGUF browse panel on the Model Management page", async () => {
		renderWithProviders(<ModelManagement />);

		expect(await screen.findByTestId("model-fit-browse-card")).toBeTruthy();
	});

	it("commits a GGUF browse search term to the store", async () => {
		renderWithProviders(<ModelManagement />);

		fireEvent.change(await screen.findByTestId("model-fit-browse-input"), { target: { value: "llama 3.1" } });
		fireEvent.click(screen.getByTestId("model-fit-browse-search-button"));

		await waitFor(() => expect(useGgufBrowseStore.getState().browseQuery).toBe("llama 3.1"));
	});

	const testedGranite = {
		id: "granite-4.1-3b",
		displayName: "Granite 4.1 3B",
		publisher: "IBM",
		ggufRepo: "unsloth/granite-4.1-3b-GGUF",
		license: "apache-2.0",
		totalParamsB: 3.4,
		notes: "Tool-capable small model with no thinking mode.",
		testedQuant: "Q4_K_M",
		testedSizeBytes: 2_099_502_400,
		fitVerdict: "Fits",
	};

	const catalogWithTested = (...testedModels: unknown[]) => ({
		catalogVersion: "2026.10.3",
		source: "bundled",
		modelCount: 45,
		refreshSourceConfigured: false,
		testedModels,
	});

	const ggufFile = (quant: string, sizeBytes: number, fitVerdict: string, isRecommended = false) => ({
		fileName: `granite-4.1-3b-${quant}.gguf`,
		quant,
		isDynamic: false,
		isDraft: false,
		sizeBytes,
		qualityTier: "Balanced",
		fitVerdict,
		isRecommended,
	});

	it("shows the tested quant with its size and fit badge, and no badge for an unknown verdict", async () => {
		queryFns.getModelCatalogInfo.mockResolvedValue(
			catalogWithTested(testedGranite, {
				...testedGranite,
				id: "qwen3.8-27b",
				displayName: "Qwen3.8 27B",
				ggufRepo: "unsloth/Qwen3.8-27B-GGUF",
				testedQuant: "UD-Q4_K_M",
				testedSizeBytes: 16_464_440_224,
				fitVerdict: "Unknown",
			}),
		);

		renderWithProviders(<ModelManagement />);

		expect((await screen.findByTestId("model-fit-browse-tested-quant-granite-4.1-3b")).textContent).toBe("Q4_K_M · 2.0 GB");
		expect(screen.getByTestId("model-fit-browse-tested-fit-granite-4.1-3b").textContent).toBe("Fits");
		expect(screen.getByTestId("model-fit-browse-tested-quant-qwen3.8-27b").textContent).toBe("UD-Q4_K_M · 15.3 GB");
		expect(screen.queryByTestId("model-fit-browse-tested-fit-qwen3.8-27b")).toBeNull();
	});

	it("lists the tested models best fitting first", async () => {
		queryFns.getModelCatalogInfo.mockResolvedValue(
			catalogWithTested(
				{ ...testedGranite, id: "wont-fit-70b", totalParamsB: 70, fitVerdict: "WontFit" },
				{ ...testedGranite, id: "tight-27b", totalParamsB: 27, fitVerdict: "Tight" },
				testedGranite,
				{ ...testedGranite, id: "fits-14b", totalParamsB: 14, fitVerdict: "Fits" },
			),
		);

		renderWithProviders(<ModelManagement />);

		const table = await screen.findByTestId("model-fit-browse-tested-table");
		await waitFor(() => expect(within(table).getAllByTestId(/^model-fit-browse-tested-row-/)).toHaveLength(4));
		expect(
			within(table)
				.getAllByTestId(/^model-fit-browse-tested-row-/)
				.map((row) => row.getAttribute("data-testid")),
		).toEqual([
			"model-fit-browse-tested-row-fits-14b",
			"model-fit-browse-tested-row-granite-4.1-3b",
			"model-fit-browse-tested-row-tight-27b",
			"model-fit-browse-tested-row-wont-fit-70b",
		]);
	});

	it("explains a Fits badge as a file-size check against free memory", async () => {
		queryFns.getModelCatalogInfo.mockResolvedValue(catalogWithTested(testedGranite));

		renderWithProviders(<ModelManagement />);

		fireEvent.mouseEnter(await screen.findByTestId("model-fit-browse-tested-fit-granite-4.1-3b"));
		expect(await screen.findByText(/Compares the tested file's size with this machine's free memory\./)).toBeTruthy();
		expect(screen.queryByText(/The tested quant does not fit this machine\./)).toBeNull();
	});

	it("explains a Won't fit badge as the picker offering smaller quants", async () => {
		queryFns.getModelCatalogInfo.mockResolvedValue(catalogWithTested({ ...testedGranite, fitVerdict: "WontFit" }));

		renderWithProviders(<ModelManagement />);

		fireEvent.mouseEnter(await screen.findByTestId("model-fit-browse-tested-fit-granite-4.1-3b"));
		expect(await screen.findByText(/The tested quant does not fit this machine\./)).toBeTruthy();
		expect(screen.queryByText(/Compares the tested file's size/)).toBeNull();
	});

	// A second observer of the profile query: it renders in the same commit that hands the settled profile to the tested
	// list, so once it shows, the list's refetch effect has run.
	function ProfileSettledProbe() {
		const { isSuccess } = useQuery({
			queryKey: fakeQueryKey("getHardwareProfile") as unknown[],
			queryFn: queryFns.getHardwareProfile,
		});
		return isSuccess ? <span data-testid="profile-settled" /> : null;
	}

	// Lands on the page with the catalog answering before the hardware profile, as on a direct first landing; the
	// profile settles a second later (Date is faked so the two dataUpdatedAt stamps cannot share a millisecond).
	async function landWithProfileSettlingAfterCatalog(): Promise<void> {
		vi.useFakeTimers({ toFake: ["Date"] });
		let settleProfile: ((value: typeof hardwareProfile) => void) | undefined;
		queryFns.getHardwareProfile.mockReturnValue(
			new Promise((resolve) => {
				settleProfile = resolve;
			}),
		);

		renderWithProviders(
			<>
				<ModelManagement />
				<ProfileSettledProbe />
			</>,
		);
		await screen.findByTestId("model-fit-browse-tested-quant-granite-4.1-3b");
		vi.setSystemTime(Date.now() + 1000);
		settleProfile?.(hardwareProfile);
		await screen.findByTestId("profile-settled");
	}

	it("refetches the tested list once the hardware profile settles so the fit badge appears without user action", async () => {
		queryFns.getModelCatalogInfo
			.mockResolvedValueOnce(catalogWithTested({ ...testedGranite, fitVerdict: "Unknown" }))
			.mockResolvedValue(catalogWithTested(testedGranite));

		await landWithProfileSettlingAfterCatalog();

		expect((await screen.findByTestId("model-fit-browse-tested-fit-granite-4.1-3b")).textContent).toBe("Fits");
		expect(queryFns.getModelCatalogInfo).toHaveBeenCalledTimes(2);
		expect(queryFns.getHardwareProfile).toHaveBeenCalledTimes(1);
	});

	it("does not refetch the tested list when no verdict is Unknown", async () => {
		queryFns.getModelCatalogInfo.mockResolvedValue(catalogWithTested(testedGranite));

		await landWithProfileSettlingAfterCatalog();

		expect(screen.getByTestId("model-fit-browse-tested-fit-granite-4.1-3b").textContent).toBe("Fits");
		expect(queryFns.getModelCatalogInfo).toHaveBeenCalledTimes(1);
	});

	it("marks a tested model installed when any quant of its repo is installed, keeping Download enabled", async () => {
		queryFns.listLocalModels.mockResolvedValue({
			isAvailable: true,
			items: [
				{
					modelName: "unsloth/Granite-4.1-3b-GGUF:Q8_0",
					provider: "llamacpp",
					isSelected: false,
					kind: "Chat",
					detectedKind: "Chat",
					capabilities: [],
					isOverridden: false,
				},
			],
		});
		queryFns.getModelCatalogInfo.mockResolvedValue(
			catalogWithTested(testedGranite, { ...testedGranite, id: "qwen3.5-4b", ggufRepo: "unsloth/Qwen3.5-4B-GGUF" }),
		);

		renderWithProviders(<ModelManagement />);

		expect((await screen.findByTestId("model-fit-browse-tested-installed-granite-4.1-3b")).textContent).toBe("Installed");
		expect(screen.queryByTestId("model-fit-browse-tested-installed-qwen3.5-4b")).toBeNull();
		expect((screen.getByTestId("model-fit-browse-tested-download-granite-4.1-3b") as HTMLButtonElement).disabled).toBe(false);
	});

	it("brings the tested list back and hides the results when the search is cleared", async () => {
		queryFns.getModelCatalogInfo.mockResolvedValue(catalogWithTested(testedGranite));
		queryFns.browseGgufRepositories.mockResolvedValue({ items: [ggufRepo] });

		renderWithProviders(<ModelManagement />);

		expect(await screen.findByTestId("model-fit-browse-tested-table")).toBeTruthy();
		// No clear control while there is nothing to clear.
		expect(screen.queryByRole("button", { name: "Clear search" })).toBeNull();
		fireEvent.change(screen.getByTestId("model-fit-browse-input"), { target: { value: "llama" } });
		fireEvent.click(screen.getByTestId("model-fit-browse-search-button"));
		expect(await screen.findByTestId("model-fit-browse-table")).toBeTruthy();
		expect(screen.queryByTestId("model-fit-browse-tested-table")).toBeNull();

		fireEvent.click(screen.getByRole("button", { name: "Clear search" }));

		expect(await screen.findByTestId("model-fit-browse-tested-table")).toBeTruthy();
		expect(screen.queryByTestId("model-fit-browse-table")).toBeNull();
		expect((screen.getByTestId("model-fit-browse-input") as HTMLInputElement).value).toBe("");
		expect(useGgufBrowseStore.getState().browseQuery).toBe("");
		expect((screen.getByTestId("model-fit-browse-search-button") as HTMLButtonElement).disabled).toBe(true);
	});

	it("preselects the tested quant when the picker opens from a tested row", async () => {
		queryFns.getModelCatalogInfo.mockResolvedValue(catalogWithTested(testedGranite));
		queryFns.inspectGgufRepository.mockResolvedValue({
			repoId: "unsloth/granite-4.1-3b-GGUF",
			hasProjector: false,
			files: [ggufFile("Q4_K_M", 2_099_502_400, "Fits"), ggufFile("Q8_0", 3_600_000_000, "Fits", true)],
		});

		renderWithProviders(<ModelManagement />);

		fireEvent.click(await screen.findByTestId("model-fit-browse-tested-download-granite-4.1-3b"));
		await waitFor(() => expect((screen.getByLabelText("Q4_K_M") as HTMLInputElement).checked).toBe(true));
		expect((screen.getByLabelText("Q8_0") as HTMLInputElement).checked).toBe(false);
	});

	it("keeps the picker's recommendation when the tested quant won't fit", async () => {
		queryFns.getModelCatalogInfo.mockResolvedValue(catalogWithTested({ ...testedGranite, fitVerdict: "WontFit" }));
		queryFns.inspectGgufRepository.mockResolvedValue({
			repoId: "unsloth/granite-4.1-3b-GGUF",
			hasProjector: false,
			files: [ggufFile("Q2_K", 1_200_000_000, "Tight", true), ggufFile("Q4_K_M", 2_099_502_400, "WontFit")],
		});

		renderWithProviders(<ModelManagement />);

		fireEvent.click(await screen.findByTestId("model-fit-browse-tested-download-granite-4.1-3b"));
		await waitFor(() => expect((screen.getByLabelText("Q2_K") as HTMLInputElement).checked).toBe(true));
		expect((screen.getByLabelText("Q4_K_M") as HTMLInputElement).checked).toBe(false);
	});

	it("lists the tested catalog models before any search and opens the quant picker for one", async () => {
		queryFns.getModelCatalogInfo.mockResolvedValue({
			catalogVersion: "2026.10.2",
			source: "bundled",
			modelCount: 45,
			refreshSourceConfigured: false,
			testedModels: [testedGranite],
		});
		queryFns.inspectGgufRepository.mockResolvedValue({
			repoId: "unsloth/granite-4.1-3b-GGUF",
			files: [{ fileName: "granite-4.1-3b-Q4_K_M.gguf", quant: "Q4_K_M", isDynamic: false, sizeBytes: 2_099_502_400 }],
		});

		renderWithProviders(<ModelManagement />);

		const row = await screen.findByTestId("model-fit-browse-tested-row-granite-4.1-3b");
		expect(row.textContent).toContain("Granite 4.1 3B");
		expect(row.textContent).toContain("3.4B");
		expect(row.textContent).toContain("Tool-capable small model with no thinking mode.");
		fireEvent.click(screen.getByTestId("model-fit-browse-tested-download-granite-4.1-3b"));

		// The same quant picker a browse row opens, inspecting the tested model's repo.
		const dialog = await screen.findByRole("dialog");
		expect(dialog.textContent).toContain("unsloth/granite-4.1-3b-GGUF");
		fireEvent.click(await within(dialog).findByLabelText("Q4_K_M"));
		fireEvent.click(screen.getByTestId("gguf-download-confirm"));
		await waitFor(() =>
			expect(mutationFns.startGgufDownload.mock.calls[0]?.[0]).toEqual({
				body: { repoId: "unsloth/granite-4.1-3b-GGUF", fileName: "granite-4.1-3b-Q4_K_M.gguf", quant: "Q4_K_M" },
			}),
		);
	});

	it("hides the tested catalog models once a search is submitted", async () => {
		queryFns.getModelCatalogInfo.mockResolvedValue({
			catalogVersion: "2026.10.2",
			source: "bundled",
			modelCount: 45,
			refreshSourceConfigured: false,
			testedModels: [testedGranite],
		});

		renderWithProviders(<ModelManagement />);

		expect(await screen.findByTestId("model-fit-browse-tested-table")).toBeTruthy();
		fireEvent.change(screen.getByTestId("model-fit-browse-input"), { target: { value: "llama" } });
		fireEvent.click(screen.getByTestId("model-fit-browse-search-button"));

		await waitFor(() => expect(screen.queryByTestId("model-fit-browse-tested-table")).toBeNull());
	});

	it("shows no tested block and no error when the catalog read fails", async () => {
		queryFns.getModelCatalogInfo.mockRejectedValue(new Error("catalog unavailable"));

		renderWithProviders(<ModelManagement />);

		expect(await screen.findByTestId("model-fit-browse-card")).toBeTruthy();
		await waitFor(() => expect(queryFns.getModelCatalogInfo).toHaveBeenCalled());
		expect(screen.queryByTestId("model-fit-browse-tested-table")).toBeNull();
		expect(screen.queryByTestId("model-fit-browse-error")).toBeNull();
	});

	it("downloads a chosen quant from the browse quant picker", async () => {
		queryFns.browseGgufRepositories.mockResolvedValue({ items: [ggufRepo] });
		queryFns.inspectGgufRepository.mockResolvedValue({
			repoId: "unsloth/llama-3.1-8b-gguf",
			files: [
				{ fileName: "llama-3.1-8b-Q4_K_M.gguf", quant: "Q4_K_M", isDynamic: false, sizeBytes: 5_000_000_000 },
				{ fileName: "llama-3.1-8b-UD-Q4_K_XL.gguf", quant: "UD-Q4_K_XL", isDynamic: true, sizeBytes: 6_000_000_000 },
			],
		});
		useGgufBrowseStore.setState({ browseQuery: "llama" });

		renderWithProviders(<ModelManagement />);

		// Clicking a browse row opens the quant picker rather than downloading the default quant directly.
		fireEvent.click(await screen.findByTestId("model-fit-browse-download-unsloth/llama-3.1-8b-gguf"));

		// Pick the Unsloth Dynamic quant, then confirm — the exact file name is sent so it resolves unambiguously.
		fireEvent.click(await screen.findByLabelText("UD-Q4_K_XL"));
		fireEvent.click(screen.getByTestId("gguf-download-confirm"));

		await waitFor(() =>
			expect(mutationFns.startGgufDownload.mock.calls[0]?.[0]).toEqual({
				body: { repoId: "unsloth/llama-3.1-8b-gguf", fileName: "llama-3.1-8b-UD-Q4_K_XL.gguf", quant: "UD-Q4_K_XL" },
			}),
		);
	});

	// Starting a download of an installed model verifies it first, which can take many seconds: the picker must stay open
	// with its confirm button busy for that whole wait, and close only once the request has settled.
	it.each([
		["succeeds", false],
		["fails", true],
	])("keeps the quant picker open and busy until the start request %s", async (_outcome, fails) => {
		const errorToast = vi.spyOn(toast, "error");
		let settle: () => void = () => undefined;
		mutationFns.startGgufDownload.mockImplementation(
			() =>
				new Promise((resolve, reject) => {
					settle = () =>
						fails
							? reject(new Error("start failed"))
							: resolve({ modelName: "unsloth/llama-3.1-8b-gguf", alreadyInFlight: false });
				}),
		);
		queryFns.browseGgufRepositories.mockResolvedValue({ items: [ggufRepo] });
		queryFns.inspectGgufRepository.mockResolvedValue({
			repoId: "unsloth/llama-3.1-8b-gguf",
			files: [{ fileName: "llama-3.1-8b-Q4_K_M.gguf", quant: "Q4_K_M", isDynamic: false, sizeBytes: 5_000_000_000 }],
		});
		useGgufBrowseStore.setState({ browseQuery: "llama" });

		renderWithProviders(<ModelManagement />);
		fireEvent.click(await screen.findByTestId("model-fit-browse-download-unsloth/llama-3.1-8b-gguf"));
		fireEvent.click(await screen.findByLabelText("Q4_K_M"));
		fireEvent.click(screen.getByTestId("gguf-download-confirm"));

		await waitFor(() => expect(mutationFns.startGgufDownload).toHaveBeenCalledTimes(1));
		const confirm = screen.getByTestId("gguf-download-confirm") as HTMLButtonElement;
		await waitFor(() => expect(confirm.disabled).toBe(true));

		expect(errorToast).not.toHaveBeenCalled();

		settle();
		await waitFor(() => expect(screen.queryByTestId("gguf-download-confirm")).toBeNull());
		if (fails) {
			expect(errorToast).toHaveBeenCalledTimes(1);
			expect(useGgufBrowseStore.getState().inFlightDownloads).not.toContain("unsloth/llama-3.1-8b-gguf");
		} else {
			expect(errorToast).not.toHaveBeenCalled();
			expect(useGgufBrowseStore.getState().inFlightDownloads).toContain("unsloth/llama-3.1-8b-gguf");
		}

		errorToast.mockRestore();
	});

	// The projector choice is the dialog's; what matters on the wire is the body this page actually sends. A repo with no
	// projector must keep today's shape exactly — the key absent, not `false` — so the server default still applies.
	it("omits includeProjector entirely for a repo that ships no vision projector", async () => {
		queryFns.browseGgufRepositories.mockResolvedValue({ items: [ggufRepo] });
		queryFns.inspectGgufRepository.mockResolvedValue({
			repoId: "unsloth/llama-3.1-8b-gguf",
			hasProjector: false,
			projectorSizeBytes: null,
			files: [{ fileName: "llama-3.1-8b-Q4_K_M.gguf", quant: "Q4_K_M", isDynamic: false, sizeBytes: 5_000_000_000 }],
		});
		useGgufBrowseStore.setState({ browseQuery: "llama" });

		renderWithProviders(<ModelManagement />);

		fireEvent.click(await screen.findByTestId("model-fit-browse-download-unsloth/llama-3.1-8b-gguf"));
		expect(await screen.findByTestId("gguf-download-row-Q4_K_M")).toBeTruthy();
		expect(screen.queryByTestId("gguf-download-include-projector")).toBeNull();
		fireEvent.click(screen.getByTestId("gguf-download-confirm"));

		await waitFor(() => expect(mutationFns.startGgufDownload).toHaveBeenCalledTimes(1));
		const body = mutationFns.startGgufDownload.mock.calls[0]?.[0]?.body as Record<string, unknown>;
		// An undefined value would be dropped by JSON.stringify, but assert the round-tripped body so the wire is what
		// is pinned here rather than the in-memory variables object.
		expect(JSON.parse(JSON.stringify(body))).toEqual({
			repoId: "unsloth/llama-3.1-8b-gguf",
			fileName: "llama-3.1-8b-Q4_K_M.gguf",
			quant: "Q4_K_M",
		});
	});

	it("sends includeProjector false when the operator clears the checkbox for a vision repo", async () => {
		queryFns.browseGgufRepositories.mockResolvedValue({ items: [ggufRepo] });
		queryFns.inspectGgufRepository.mockResolvedValue({
			repoId: "unsloth/llama-3.1-8b-gguf",
			hasProjector: true,
			projectorSizeBytes: 1_073_741_824,
			files: [{ fileName: "llama-3.1-8b-Q4_K_M.gguf", quant: "Q4_K_M", isDynamic: false, sizeBytes: 5_000_000_000 }],
		});
		useGgufBrowseStore.setState({ browseQuery: "llama" });

		renderWithProviders(<ModelManagement />);

		fireEvent.click(await screen.findByTestId("model-fit-browse-download-unsloth/llama-3.1-8b-gguf"));
		fireEvent.click(await screen.findByTestId("gguf-download-include-projector"));
		fireEvent.click(screen.getByTestId("gguf-download-confirm"));

		await waitFor(() =>
			expect(mutationFns.startGgufDownload.mock.calls[0]?.[0]).toEqual({
				body: {
					repoId: "unsloth/llama-3.1-8b-gguf",
					fileName: "llama-3.1-8b-Q4_K_M.gguf",
					quant: "Q4_K_M",
					includeProjector: false,
				},
			}),
		);
	});

	it("falls back to the default quant when the picker has no files to offer", async () => {
		queryFns.browseGgufRepositories.mockResolvedValue({ items: [ggufRepo] });
		// Degraded/empty inspection (e.g. HF unreachable → 200 empty list) must not strand the operator.
		queryFns.inspectGgufRepository.mockResolvedValue({ repoId: "unsloth/llama-3.1-8b-gguf", files: [] });
		useGgufBrowseStore.setState({ browseQuery: "llama" });

		renderWithProviders(<ModelManagement />);

		fireEvent.click(await screen.findByTestId("model-fit-browse-download-unsloth/llama-3.1-8b-gguf"));
		fireEvent.click(await screen.findByTestId("gguf-download-default"));

		await waitFor(() =>
			expect(mutationFns.startGgufDownload.mock.calls[0]?.[0]).toEqual({
				body: { repoId: "unsloth/llama-3.1-8b-gguf", fileName: undefined, quant: "Q4_K_M" },
			}),
		);
	});

	it("gates the import action on capability and completes the path-preview-confirm flow", async () => {
		queryFns.getGgufImportCapability.mockResolvedValue({ available: true });
		mutationFns.previewGgufImport.mockResolvedValue({
			modelBaseName: "private-model",
			detectedQuantization: "Q4_K_M",
			canonicalQuantizationChoices: ["Q4_K_M", "Q5_K_M"],
			canonicalModelName: "private-model:Q4_K_M",
			finalFileName: "private-model-Q4_K_M-abc.gguf",
			sizeBytes: 1_048_576,
			sourceDisplayName: "private-model.gguf",
			architecture: "llama",
			ggufVersion: 3,
			warnings: ["Review the chat template."],
			hasSufficientStorage: true,
			previewToken: "opaque-preview",
			expiresAtUtc: "2026-08-14T12:00:00Z",
		});
		mutationFns.startGgufImport.mockResolvedValue({
			operationId: "11111111-1111-1111-1111-111111111111",
			operationKind: "Import",
			modelName: "private-model:Q4_K_M",
		});

		renderWithProviders(<ModelManagement />);
		fireEvent.click(await screen.findByRole("button", { name: "Import model" }));
		const dialog = await screen.findByRole("dialog");
		fireEvent.change(within(dialog).getByPlaceholderText("/path/to/model.gguf"), {
			target: { value: "/private/models/private-model.gguf" },
		});
		fireEvent.click(within(dialog).getByRole("button", { name: "Preview import" }));

		expect(await within(dialog).findByText("private-model.gguf")).toBeTruthy();
		expect(within(dialog).getByText("Review the chat template.")).toBeTruthy();
		fireEvent.click(within(dialog).getByRole("button", { name: "Import model" }));

		await waitFor(() =>
			expect(mutationFns.startGgufImport.mock.calls[0]?.[0]).toEqual({
				body: {
					sourcePath: "/private/models/private-model.gguf",
					previewToken: "opaque-preview",
					modelBaseName: "private-model",
					quantization: "Q4_K_M",
				},
			}),
		);
	});

	it("hides import when capability is unavailable and labels imported provenance from the typed origin", async () => {
		queryFns.listLocalModels.mockResolvedValue({
			isAvailable: true,
			items: [
				{
					modelName: "private-model:Q4_K_M",
					provider: "llamacpp",
					origin: "imported",
					isSelected: false,
					kind: "Chat",
					detectedKind: "Chat",
					capabilities: [],
					isReasoningCapable: false,
					isToolCapable: false,
					isOverridden: false,
				},
			],
		});

		renderWithProviders(<ModelManagement />);
		expect(await screen.findByText("Imported")).toBeTruthy();
		expect(screen.queryByRole("button", { name: "Import model" })).toBeNull();
	});

	it("announces import progress, exposes cancellation, and renders failure text only from the safe error code", async () => {
		// The Failed row below carries a fixed updatedAtUtc; terminal statuses are pruned 24h later against the real
		// clock, so pin only Date (timers stay real for findBy*/waitFor).
		vi.useFakeTimers({ toFake: ["Date"], now: Date.parse("2026-08-14T12:00:00Z") });
		queryFns.getGgufImports.mockResolvedValue({
			items: [
				{
					operationId: "22222222-2222-2222-2222-222222222222",
					operationKind: "Import",
					modelName: "copying:Q4_K_M",
					phase: "Copying",
					completedBytes: 5,
					totalBytes: 10,
					startedAtUtc: "2026-08-14T10:00:00Z",
					updatedAtUtc: "2026-08-14T10:00:01Z",
				},
				{
					operationId: "33333333-3333-3333-3333-333333333333",
					operationKind: "Import",
					modelName: "failed:Q4_K_M",
					phase: "Failed",
					errorCode: "SourceNotFound",
					sanitizedMessage: "/private/models/secret.gguf was not found",
					startedAtUtc: "2026-08-14T10:00:00Z",
					updatedAtUtc: "2026-08-14T10:00:01Z",
				},
			],
		});

		renderWithProviders(<ModelManagement />);
		const region = await screen.findByLabelText("Model import status");
		expect(within(region).getByLabelText("Import progress")).toBeTruthy();
		fireEvent.click(within(region).getByRole("button", { name: "Cancel import" }));
		await waitFor(() =>
			expect(mutationFns.cancelGgufImport.mock.calls[0]?.[0]).toEqual({
				path: { operationId: "22222222-2222-2222-2222-222222222222" },
			}),
		);
		expect(within(region).getByText("The selected GGUF file was not found.")).toBeTruthy();
		expect(region.textContent).not.toContain("/private/models");
	});
});
