// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, render, screen } from "@testing-library/react";
import type { ReactElement } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { LocalToolsOverview } from "@/features/chat/components/LocalToolsOverview";
import { localToolCatalog } from "@/features/chat/models/LocalToolCatalog";

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

describe("LocalToolsOverview", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
	});

	afterEach(() => {
		cleanup();
	});

	it("renders the overview panel", () => {
		renderWithProviders(<LocalToolsOverview />);

		expect(screen.getByTestId("local-tools-overview")).toBeTruthy();
	});

	it("lists both catalog tools", () => {
		renderWithProviders(<LocalToolsOverview />);

		expect(screen.getByTestId("local-tool-row-GetCurrentTime")).toBeTruthy();
		expect(screen.getByTestId("local-tool-row-Calculate")).toBeTruthy();
	});

	it("shows auto-execute badge for all tools (RequiresApproval=false)", () => {
		renderWithProviders(<LocalToolsOverview />);

		for (const tool of localToolCatalog) {
			const badge = screen.getByTestId(`local-tool-approval-badge-${tool.name}`);
			expect(badge.textContent).toBe("auto-execute");
		}
	});

	it("catalog contains exactly GetCurrentTime and Calculate", () => {
		expect(localToolCatalog).toHaveLength(2);
		expect(localToolCatalog.map((t) => t.name)).toEqual(["GetCurrentTime", "Calculate"]);
	});

	it("all catalog tools have requiresApproval=false", () => {
		for (const tool of localToolCatalog) {
			expect(tool.requiresApproval).toBe(false);
		}
	});

	it("catalog tools carry non-empty descriptions", () => {
		for (const tool of localToolCatalog) {
			expect(tool.description.length).toBeGreaterThan(0);
		}
	});
});
