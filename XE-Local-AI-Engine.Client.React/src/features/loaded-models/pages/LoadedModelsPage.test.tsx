// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("react-i18next", () => ({
	useTranslation: () => ({
		t: (_key: string, defaultValue?: string, options?: Record<string, unknown>) => {
			let text = defaultValue ?? _key;
			if (options) {
				for (const [name, value] of Object.entries(options)) {
					text = text.replace(`{{${name}}}`, String(value));
				}
			}
			return text;
		},
	}),
}));

const { hooksMock, confirmMock, toastMock } = vi.hoisted(() => ({
	hooksMock: {
		useLoadedModels: vi.fn(),
		useEjectModel: vi.fn(),
	},
	confirmMock: vi.fn(),
	toastMock: { success: vi.fn(), error: vi.fn() },
}));

vi.mock("@/features/loaded-models/queries/useLoadedModels", () => hooksMock);
vi.mock("@/core/ui/hooks/useConfirm", () => ({ useConfirm: () => ({ confirm: confirmMock }) }));
vi.mock("@/core/ui/notifications/Toast", () => ({ toast: toastMock }));

import { LoadedModelsPage } from "@/features/loaded-models/pages/LoadedModelsPage";

function makeQuery<T>(data: T, overrides: Record<string, unknown> = {}) {
	return { data, isLoading: false, error: null, ...overrides };
}

function makeEjectMutation(overrides: Record<string, unknown> = {}) {
	return { mutate: vi.fn(), isPending: false, variables: undefined, ...overrides };
}

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

function renderPage() {
	const queryClient = new QueryClient({
		defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
	});
	return render(
		<MantineProvider>
			<QueryClientProvider client={queryClient}>
				<LoadedModelsPage />
			</QueryClientProvider>
		</MantineProvider>,
	);
}

const availableSnapshot = {
	isAvailable: true,
	error: null,
	models: [
		{ modelName: "llama3.1:8b", sizeBytes: 8_589_934_592, sizeVramBytes: 4_294_967_296, expiresAtUtc: null },
		{ modelName: "qwen2.5:3b", sizeBytes: 3_221_225_472, sizeVramBytes: null, expiresAtUtc: null },
	],
};

describe("LoadedModelsPage", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
		hooksMock.useLoadedModels.mockReturnValue(makeQuery(availableSnapshot));
		hooksMock.useEjectModel.mockReturnValue(makeEjectMutation());
		confirmMock.mockResolvedValue(true);
	});

	afterEach(() => {
		cleanup();
		vi.clearAllMocks();
	});

	it("renders a row per loaded model with an eject button", () => {
		renderPage();

		expect(screen.getByTestId("loaded-models-table")).toBeTruthy();
		expect(screen.getByTestId("loaded-models-row-llama3.1:8b")).toBeTruthy();
		expect(screen.getByTestId("loaded-models-eject-llama3.1:8b")).toBeTruthy();
		expect(screen.getByTestId("loaded-models-row-qwen2.5:3b")).toBeTruthy();
	});

	it("shows the empty state when the runtime is available but holds no models", () => {
		hooksMock.useLoadedModels.mockReturnValue(makeQuery({ isAvailable: true, error: null, models: [] }));

		renderPage();

		expect(screen.getByTestId("loaded-models-empty")).toBeTruthy();
		expect(screen.queryByTestId("loaded-models-table")).toBeNull();
	});

	it("shows the unavailable state (and its sanitized reason) when the runtime is unreachable", () => {
		hooksMock.useLoadedModels.mockReturnValue(
			makeQuery({ isAvailable: false, error: "Provider unreachable", models: [] }),
		);

		renderPage();

		const unavailable = screen.getByTestId("loaded-models-unavailable");
		expect(unavailable.textContent).toContain("Provider unreachable");
		expect(screen.queryByTestId("loaded-models-table")).toBeNull();
	});

	it("surfaces a load error via the error alert", () => {
		hooksMock.useLoadedModels.mockReturnValue(makeQuery(undefined, { error: new Error("boom") }));

		renderPage();

		expect(screen.getByTestId("loaded-models-error")).toBeTruthy();
	});

	it("ejects a model after the confirm dialog is accepted", async () => {
		const ejectMutation = makeEjectMutation();
		hooksMock.useEjectModel.mockReturnValue(ejectMutation);

		renderPage();

		fireEvent.click(screen.getByTestId("loaded-models-eject-llama3.1:8b"));

		await waitFor(() => expect(ejectMutation.mutate).toHaveBeenCalled());
		expect(ejectMutation.mutate.mock.calls[0]?.[0]).toBe("llama3.1:8b");
	});

	it("does not eject when the confirm dialog is cancelled", async () => {
		const ejectMutation = makeEjectMutation();
		hooksMock.useEjectModel.mockReturnValue(ejectMutation);
		confirmMock.mockResolvedValue(false);

		renderPage();

		fireEvent.click(screen.getByTestId("loaded-models-eject-llama3.1:8b"));

		await waitFor(() => expect(confirmMock).toHaveBeenCalled());
		expect(ejectMutation.mutate).not.toHaveBeenCalled();
	});
});
