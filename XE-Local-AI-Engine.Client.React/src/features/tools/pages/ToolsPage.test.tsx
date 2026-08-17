// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, render, screen } from "@testing-library/react";
import type { ReactElement } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import type { ToolCatalogEntry } from "@/features/tools/models/ToolCatalogModels";
import { ToolsPage } from "@/features/tools/pages/ToolsPage";

const { useToolCatalogMock } = vi.hoisted(() => ({
	useToolCatalogMock: vi.fn(),
}));

vi.mock("@/features/tools/queries/useToolCatalog", () => ({
	useToolCatalog: useToolCatalogMock,
}));

const catalogTools: ToolCatalogEntry[] = [
	{
		name: "GetCurrentTime",
		description: "Returns the current time.",
		requiresApproval: false,
		source: { kind: "builtin", serverSlug: null },
		category: "ReadLocal",
		effectiveRequiresApproval: false,
		sessionScopeEligible: false,
	},
	{
		name: "Calculate",
		description: "Evaluates arithmetic.",
		requiresApproval: false,
		source: { kind: "builtin", serverSlug: null },
		category: "ReadLocal",
		effectiveRequiresApproval: false,
		sessionScopeEligible: false,
	},
];

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
		useToolCatalogMock.mockReturnValue({ data: catalogTools, isLoading: false, error: null });
	});

	afterEach(() => {
		cleanup();
		vi.clearAllMocks();
	});

	it("renders the tools page container", () => {
		renderWithProviders(<ToolsPage />);

		expect(screen.getByTestId("tools-page")).toBeTruthy();
	});

	it("renders the LocalToolsOverview panel inside the page", () => {
		renderWithProviders(<ToolsPage />);

		expect(screen.getByTestId("local-tools-overview")).toBeTruthy();
	});

	it("lists the fetched catalog tools on the page", () => {
		renderWithProviders(<ToolsPage />);

		for (const tool of catalogTools) {
			expect(screen.getByTestId(`local-tool-row-${tool.name}`)).toBeTruthy();
		}
	});

	it("shows auto-execute badges for all built-in tools", () => {
		renderWithProviders(<ToolsPage />);

		for (const tool of catalogTools) {
			const badge = screen.getByTestId(`local-tool-approval-badge-${tool.name}`);
			expect(badge.textContent).toBe("auto-execute");
		}
	});
});
