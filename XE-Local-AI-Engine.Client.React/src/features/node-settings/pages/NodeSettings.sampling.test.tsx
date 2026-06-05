// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import type { ReactElement } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

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
	Object.defineProperty(document, "fonts", {
		writable: true,
		value: { ready: Promise.resolve(), addEventListener: vi.fn(), removeEventListener: vi.fn() },
	});
}

function renderWithProviders(ui: ReactElement) {
	const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
	return render(
		<QueryClientProvider client={qc}>
			<MantineProvider>{ui}</MantineProvider>
		</QueryClientProvider>,
	);
}

// Mock the generated API hooks so NodeSettings renders without a real backend.
vi.mock("@/core/api/generated/@tanstack/react-query.gen", () => ({
	getNodeSettingsOptions: () => ({ queryKey: ["node-settings"], queryFn: () => Promise.resolve(null) }),
	getNodeSettingsQueryKey: () => ["node-settings"],
	saveNodeSettingsMutation: () => ({ mutationFn: vi.fn() }),
}));

vi.mock("@/core/api/ResponseValidation", () => ({
	withResponseValidation: (x: unknown) => x,
}));

describe("NodeSettings developer-mode switch", () => {
	beforeEach(() => {
		localStorage.clear();
		installJsdomEnvironmentMocks();
		vi.resetModules();
	});

	afterEach(() => {
		cleanup();
		localStorage.clear();
	});

	it("renders the developer-mode switch", async () => {
		const { NodeSettings } = await import("@/features/node-settings/pages/NodeSettings");

		renderWithProviders(<NodeSettings />);

		expect(screen.getByTestId("developer-mode-switch")).toBeDefined();
	});

	it("switch starts unchecked when developer mode is off", async () => {
		localStorage.setItem("xe-developer-mode", "false");
		const { NodeSettings } = await import("@/features/node-settings/pages/NodeSettings");

		renderWithProviders(<NodeSettings />);

		const switchEl = screen.getByTestId("developer-mode-switch");
		// Mantine Switch renders a checkbox role input
		const checkbox = switchEl.querySelector("input[type='checkbox']") ?? switchEl;
		expect((checkbox as HTMLInputElement).checked).toBe(false);
	});

	it("switch starts checked when developer mode was persisted on", async () => {
		localStorage.setItem("xe-developer-mode", "true");
		const { NodeSettings } = await import("@/features/node-settings/pages/NodeSettings");

		renderWithProviders(<NodeSettings />);

		const switchEl = screen.getByTestId("developer-mode-switch");
		const checkbox = switchEl.querySelector("input[type='checkbox']") ?? switchEl;
		expect((checkbox as HTMLInputElement).checked).toBe(true);
	});

	it("toggling the switch updates DeveloperModeStore and persists without triggering saveMutation", async () => {
		const { NodeSettings } = await import("@/features/node-settings/pages/NodeSettings");
		const { useDeveloperModeStore } = await import("@/core/dev-tools/stores/DeveloperModeStore");

		renderWithProviders(<NodeSettings />);

		const switchEl = screen.getByTestId("developer-mode-switch");
		const checkbox = switchEl.querySelector("input[type='checkbox']") ?? switchEl;

		fireEvent.click(checkbox);

		// Store state flipped
		expect(useDeveloperModeStore.getState().developerMode).toBe(true);
		// Persisted
		expect(localStorage.getItem("xe-developer-mode")).toBe("true");
		// Save button not in loading / disabled state — toggling dev mode does not trigger the backend mutation
		const saveBtn = screen.queryByText("Save settings");
		if (saveBtn) {
			expect(saveBtn.closest("button")?.hasAttribute("disabled")).toBe(false);
		}
	});
});
