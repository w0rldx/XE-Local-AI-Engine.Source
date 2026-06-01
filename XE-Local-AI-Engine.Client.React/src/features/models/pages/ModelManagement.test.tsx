// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import type { ReactElement } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

const { apiMock, confirmMock } = vi.hoisted(() => ({
	apiMock: {
		deleteLocalModel: vi.fn(),
		getLocalModelDetails: vi.fn(),
		listLocalModels: vi.fn(),
		pullLocalModel: vi.fn(),
		selectLocalModel: vi.fn(),
	},
	confirmMock: vi.fn(),
}));

vi.mock("@/features/models/api/LocalModelsApi", () => apiMock);
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
		apiMock.listLocalModels.mockResolvedValue({
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
				},
			],
		});
		apiMock.getLocalModelDetails.mockResolvedValue({ modelName: "llama3:8b", maxContextTokens: 8192, template: "{{ .Prompt }}", system: null, license: "fake" });
		apiMock.selectLocalModel.mockResolvedValue({ selectedModelName: "llama3:8b" });
		apiMock.pullLocalModel.mockResolvedValue({ modelName: "orca-mini:latest", status: "success", totalBytes: 100, completedBytes: 100 });
		apiMock.deleteLocalModel.mockResolvedValue({ modelName: "llama3:8b", deleted: true });
		confirmMock.mockResolvedValue(true);
	});

	afterEach(() => {
		cleanup();
	});

	it("renders local models and details", async () => {
		renderWithProviders(<ModelManagement />);

		expect(await screen.findByText("llama3:8b")).toBeTruthy();
		expect(screen.getByText("1.0 GB")).toBeTruthy();
		expect(screen.getByText("Default")).toBeTruthy();
		expect(await screen.findByText("Context length: 8,192")).toBeTruthy();
	});

	it("selects, pulls, and deletes models through mutations", async () => {
		renderWithProviders(<ModelManagement />);
		await screen.findByLabelText("Set llama3:8b as default");

		fireEvent.click(screen.getByLabelText("Set llama3:8b as default"));
		await waitFor(() => expect(apiMock.selectLocalModel).toHaveBeenCalledWith({ modelName: "llama3:8b" }));

		const pullInput = screen.getAllByTestId("pull-model-name-input").find((element) => element.tagName === "INPUT");
		expect(pullInput).toBeTruthy();
		fireEvent.change(pullInput!, { target: { value: "orca-mini:latest" } });
		const downloadButton = screen.getAllByTestId("download-model-button").find((element) => element.tagName === "BUTTON");
		expect(downloadButton).toBeTruthy();
		fireEvent.click(downloadButton!);
		await waitFor(() => expect(apiMock.pullLocalModel).toHaveBeenCalledWith({ modelName: "orca-mini:latest" }));

		const deleteButton = screen.getAllByLabelText("Delete llama3:8b").find((element) => element.tagName === "BUTTON");
		expect(deleteButton).toBeTruthy();
		fireEvent.click(deleteButton!);
		await waitFor(() => expect(confirmMock).toHaveBeenCalled());
		await waitFor(() => expect(apiMock.deleteLocalModel).toHaveBeenCalledWith("llama3:8b"));
	});

	it("shows unavailable Ollama state", async () => {
		apiMock.listLocalModels.mockResolvedValue({ isAvailable: false, selectedModelName: null, configuredDefaultModelName: "llama3:8b", error: "Local model provider is unavailable.", items: [] });

		renderWithProviders(<ModelManagement />);

		expect(await screen.findByText("Local model provider is unavailable.")).toBeTruthy();
		expect(screen.getByText("Ollama offline")).toBeTruthy();
	});

	it("collapses the license text by default and expands inline on toggle", async () => {
		renderWithProviders(<ModelManagement />);

		// Toggle is present and starts collapsed
		const toggle = await screen.findByRole("button", { name: /show full/i });
		expect(toggle.getAttribute("aria-expanded")).toBe("false");

		// No dialog rendered initially
		expect(screen.queryByRole("dialog")).toBeNull();

		// Click expands inline — toggle switches to "Show less"
		fireEvent.click(toggle);
		expect(toggle.getAttribute("aria-expanded")).toBe("true");
		expect(screen.getByRole("button", { name: /show less/i })).toBeTruthy();

		// No dialog opened
		expect(screen.queryByRole("dialog")).toBeNull();
	});
});
