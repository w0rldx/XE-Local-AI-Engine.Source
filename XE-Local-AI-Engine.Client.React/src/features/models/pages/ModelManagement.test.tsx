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
// (which calls the same factories) refetches the live query — proving the post-override list refetch.
const { queryFns, mutationFns } = vi.hoisted(() => ({
	queryFns: {
		listLocalModels: vi.fn(),
		getLocalModelDetails: vi.fn(),
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
		queryFns.getLocalModelDetails.mockResolvedValue({ modelName: "llama3:8b", maxContextTokens: 8192, template: "{{ .Prompt }}", system: null, license: "fake" });
		mutationFns.selectLocalModel.mockResolvedValue({ selectedModelName: "llama3:8b" });
		mutationFns.pullLocalModel.mockResolvedValue({ modelName: "orca-mini:latest", status: "success", totalBytes: 100, completedBytes: 100 });
		mutationFns.deleteLocalModel.mockResolvedValue({ modelName: "llama3:8b", deleted: true });
		mutationFns.putModelKind.mockResolvedValue({ modelName: "llama3:8b", kind: "Embedding", detectedKind: "Chat", capabilities: ["completion", "tools"], isOverridden: true });
		mutationFns.deleteModelKind.mockResolvedValue({ modelName: "llama3:8b", kind: "Chat", detectedKind: "Chat", capabilities: ["completion", "tools"], isOverridden: false });
		confirmMock.mockResolvedValue(true);
	});

	afterEach(() => {
		cleanup();
	});

	it("renders local models and details", async () => {
		renderWithProviders(<ModelManagement />);

		// "llama3:8b" appears both as the table row button and the details-card title, so match the set, not one node.
		expect((await screen.findAllByText("llama3:8b")).length).toBeGreaterThan(0);
		expect(screen.getByText("1.0 GB")).toBeTruthy();
		expect(screen.getByText("Default")).toBeTruthy();
		expect(await screen.findByText("Context length: 8,192")).toBeTruthy();
	});

	it("selects, pulls, and deletes models through the generated mutations", async () => {
		renderWithProviders(<ModelManagement />);
		await screen.findByLabelText("Set llama3:8b as default");

		fireEvent.click(screen.getByLabelText("Set llama3:8b as default"));
		// TanStack v5 passes (variables, context) — assert the first arg carries the generated body shape.
		await waitFor(() => expect(mutationFns.selectLocalModel.mock.calls[0]?.[0]).toEqual({ body: { modelName: "llama3:8b" } }));

		const pullInput = screen.getAllByTestId("pull-model-name-input").find((element) => element.tagName === "INPUT");
		expect(pullInput).toBeTruthy();
		fireEvent.change(pullInput!, { target: { value: "orca-mini:latest" } });
		const downloadButton = screen.getAllByTestId("download-model-button").find((element) => element.tagName === "BUTTON");
		expect(downloadButton).toBeTruthy();
		fireEvent.click(downloadButton!);
		await waitFor(() => expect(mutationFns.pullLocalModel.mock.calls[0]?.[0]).toEqual({ body: { modelName: "orca-mini:latest" } }));

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
		// Raw Ollama capabilities surface as read-only badges.
		expect(screen.getByText("Tools")).toBeTruthy();
		// A non-overridden model shows no "reset to detected" affordance.
		expect(screen.queryByLabelText("Reset llama3:8b type to detected")).toBeNull();
	});

	it("overrides a model kind through the select and reflects the refetched (overridden) row", async () => {
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
			.mockResolvedValueOnce({ isAvailable: true, selectedModelName: "llama3:8b", configuredDefaultModelName: "llama3:8b", error: null, items: [{ ...overriddenRow, kind: "Chat", isOverridden: false }] })
			.mockResolvedValue({ isAvailable: true, selectedModelName: "llama3:8b", configuredDefaultModelName: "llama3:8b", error: null, items: [overriddenRow] });

		renderWithProviders(<ModelManagement />);

		const initialBadge = await screen.findByTestId("model-kind-badge-llama3:8b");
		expect(initialBadge.textContent).toContain("Chat");

		// Open the override Select and choose Embedding. Mantine's Select associates the aria-label with more than one
		// node, so target the input element explicitly (mirrors the pull-input lookup above).
		const select = screen.getAllByLabelText("Override type for llama3:8b").find((element) => element.tagName === "INPUT");
		expect(select).toBeTruthy();
		fireEvent.click(select!);
		const option = await screen.findByRole("option", { name: "Embedding" });
		fireEvent.click(option);

		await waitFor(() => expect(mutationFns.putModelKind.mock.calls[0]?.[0]).toEqual({ path: { modelName: "llama3:8b" }, body: { kind: "Embedding" } }));
		// The list is refetched after the override so lazy detection refreshes the row.
		await waitFor(() => expect(queryFns.listLocalModels).toHaveBeenCalledTimes(2));
		// The badge reflects the refetched overridden row, and the reset affordance now appears.
		await waitFor(() => expect(screen.getByTestId("model-kind-badge-llama3:8b").textContent).toContain("Embedding"));
		expect(screen.getByLabelText("Reset llama3:8b type to detected")).toBeTruthy();
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

		const resetButton = await screen.findByLabelText("Reset llama3:8b type to detected");
		fireEvent.click(resetButton);

		await waitFor(() => expect(mutationFns.deleteModelKind.mock.calls[0]?.[0]).toEqual({ path: { modelName: "llama3:8b" } }));
	});

	it("shows unavailable Ollama state", async () => {
		queryFns.listLocalModels.mockResolvedValue({ isAvailable: false, selectedModelName: null, configuredDefaultModelName: "llama3:8b", error: "Local model provider is unavailable.", items: [] });

		renderWithProviders(<ModelManagement />);

		expect(await screen.findByText("Local model provider is unavailable.")).toBeTruthy();
		expect(screen.getByText("Ollama offline")).toBeTruthy();
	});

	it("shows license and template in a dialog rather than an inline expander", async () => {
		renderWithProviders(<ModelManagement />);

		// The trigger is a button — no inline "Show full / Show less" expander — and no dialog is open initially.
		const trigger = await screen.findByTestId("model-license-template-button");
		expect(screen.queryByRole("button", { name: /show full/i })).toBeNull();
		expect(screen.queryByRole("dialog")).toBeNull();

		// Clicking opens a dialog containing BOTH the template and the license.
		fireEvent.click(trigger);
		const dialog = await screen.findByRole("dialog");
		expect(within(dialog).getByTestId("model-template-content").textContent).toContain("{{ .Prompt }}");
		expect(within(dialog).getByTestId("model-license-content").textContent).toContain("fake");
	});
});
