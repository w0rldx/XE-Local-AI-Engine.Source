// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import type { ReactElement } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

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
	},
	mutationFns: {
		selectLocalModel: vi.fn(),
		pullLocalModel: vi.fn(),
		deleteLocalModel: vi.fn(),
		putModelKind: vi.fn(),
		deleteModelKind: vi.fn(),
	},
}));

// Centralizes the `_id` discriminator literal (which trips biome's naming-convention rule) in one suppressed spot.
function fakeQueryKey(operationId: string): unknown {
	// biome-ignore lint/style/useNamingConvention: `_id` is the generated hey-api query-key discriminator field.
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
	pullLocalModelMutation: () => ({ mutationFn: mutationFns.pullLocalModel }),
	deleteLocalModelMutation: () => ({ mutationFn: mutationFns.deleteLocalModel }),
	putModelKindMutation: () => ({ mutationFn: mutationFns.putModelKind }),
	deleteModelKindMutation: () => ({ mutationFn: mutationFns.deleteModelKind }),
	// Model-fit factories consumed transitively by the Fit tab (useLatestRecommendations).
	getLatestRecommendationsOptions: () => ({
		queryKey: fakeQueryKey("getLatestRecommendations"),
		queryFn: queryFns.getLatestRecommendations,
	}),
	listApprovedImagesOptions: () => ({ queryKey: fakeQueryKey("listApprovedImages"), queryFn: vi.fn() }),
	refreshRecommendationsMutation: () => ({ mutationFn: vi.fn() }),
}));

const { confirmMock } = vi.hoisted(() => ({ confirmMock: vi.fn() }));
vi.mock("@/core/ui/hooks/useConfirm", () => ({
	useConfirm: () => ({ confirm: confirmMock }),
}));

import { ModelManagement } from "@/features/models/pages/ModelManagement";

function renderWithProviders(ui: ReactElement) {
	const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
	return render(
		<MantineProvider>
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
		mutationFns.pullLocalModel.mockResolvedValue({
			modelName: "orca-mini:latest",
			status: "success",
			totalBytes: 100,
			completedBytes: 100,
		});
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
		confirmMock.mockResolvedValue(true);
	});

	afterEach(() => {
		cleanup();
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

	it("selects, pulls, and deletes models through the generated mutations", async () => {
		renderWithProviders(<ModelManagement />);
		await screen.findByLabelText("Set llama3:8b as default");

		fireEvent.click(screen.getByLabelText("Set llama3:8b as default"));
		// TanStack v5 passes (variables, context) — assert the first arg carries the generated body shape.
		await waitFor(() => expect(mutationFns.selectLocalModel.mock.calls[0]?.[0]).toEqual({ body: { modelName: "llama3:8b" } }));

		// Pull now lives in a dialog opened from the header button.
		fireEvent.click(screen.getByTestId("open-pull-dialog-button"));
		const pullInput = (await screen.findAllByTestId("pull-model-name-input")).find((element) => element.tagName === "INPUT");
		expect(pullInput).toBeTruthy();
		fireEvent.change(pullInput!, { target: { value: "orca-mini:latest" } });
		const downloadButton = screen.getAllByTestId("download-model-button").find((element) => element.tagName === "BUTTON");
		expect(downloadButton).toBeTruthy();
		fireEvent.click(downloadButton!);
		await waitFor(() =>
			expect(mutationFns.pullLocalModel.mock.calls[0]?.[0]).toEqual({ body: { modelName: "orca-mini:latest" } }),
		);

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

	it("shows unavailable Ollama state", async () => {
		queryFns.listLocalModels.mockResolvedValue({
			isAvailable: false,
			selectedModelName: null,
			configuredDefaultModelName: "llama3:8b",
			error: "Local model provider is unavailable.",
			items: [],
		});

		renderWithProviders(<ModelManagement />);

		expect(await screen.findByText("Local model provider is unavailable.")).toBeTruthy();
		expect(screen.getByText("Ollama offline")).toBeTruthy();
	});

	it("shows license and template in the dialog License tab", async () => {
		renderWithProviders(<ModelManagement />);

		// No dialog is open initially, and there is no inline "Show full / Show less" expander.
		expect(screen.queryByRole("dialog")).toBeNull();
		expect(screen.queryByRole("button", { name: /show full/i })).toBeNull();

		const dialog = await openDetailsDialog("llama3:8b");
		fireEvent.click(within(dialog).getByRole("tab", { name: /license/i }));

		// The License tab carries BOTH the template and the license.
		expect(within(dialog).getByTestId("model-template-content").textContent).toContain("{{ .Prompt }}");
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
	});

	it("opens the pull dialog from the header without an inline pull form on the page", async () => {
		renderWithProviders(<ModelManagement />);
		await screen.findByTestId("open-pull-dialog-button");

		// The pull form is not rendered on the page until the dialog is opened.
		expect(screen.queryByTestId("pull-model-name-input")).toBeNull();

		fireEvent.click(screen.getByTestId("open-pull-dialog-button"));
		const dialog = await screen.findByRole("dialog");
		expect(within(dialog).getByTestId("pull-model-name-input")).toBeTruthy();
		expect(within(dialog).getByTestId("download-model-button")).toBeTruthy();
	});
});
