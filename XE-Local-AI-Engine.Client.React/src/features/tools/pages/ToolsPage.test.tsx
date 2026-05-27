// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, render, screen } from "@testing-library/react";
import type { ReactElement } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { localToolCatalog } from "@/features/chat/models/LocalToolCatalog";
import { ToolsPage } from "@/features/tools/pages/ToolsPage";

function renderWithProviders(ui: ReactElement) {
	return render(<MantineProvider>{ui}</MantineProvider>);
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
	Object.defineProperty(document, "fonts", {
		writable: true,
		value: { ready: Promise.resolve(), addEventListener: vi.fn(), removeEventListener: vi.fn() },
	});
}

describe("ToolsPage", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
	});

	afterEach(() => {
		cleanup();
	});

	it("renders the tools page container", () => {
		renderWithProviders(<ToolsPage />);

		expect(screen.getByTestId("tools-page")).toBeTruthy();
	});

	it("renders the LocalToolsOverview panel inside the page", () => {
		renderWithProviders(<ToolsPage />);

		expect(screen.getByTestId("local-tools-overview")).toBeTruthy();
	});

	it("lists both catalog tools on the page", () => {
		renderWithProviders(<ToolsPage />);

		for (const tool of localToolCatalog) {
			expect(screen.getByTestId(`local-tool-row-${tool.name}`)).toBeTruthy();
		}
	});

	it("shows auto-execute badges for all tools", () => {
		renderWithProviders(<ToolsPage />);

		for (const tool of localToolCatalog) {
			const badge = screen.getByTestId(`local-tool-approval-badge-${tool.name}`);
			expect(badge.textContent).toBe("auto-execute");
		}
	});
});
