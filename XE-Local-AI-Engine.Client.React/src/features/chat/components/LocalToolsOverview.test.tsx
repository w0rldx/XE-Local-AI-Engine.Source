// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, render, screen } from "@testing-library/react";
import type { ReactElement } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import type { ToolCatalogEntry } from "@/features/tools/models/ToolCatalogModels";

const { useToolCatalogMock } = vi.hoisted(() => ({
	useToolCatalogMock: vi.fn(),
}));

vi.mock("@/features/tools/queries/useToolCatalog", () => ({
	useToolCatalog: useToolCatalogMock,
}));

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

import { LocalToolsOverview } from "@/features/chat/components/LocalToolsOverview";

const builtinTools: ToolCatalogEntry[] = [
	{
		name: "GetCurrentTime",
		description: "Returns the current time.",
		requiresApproval: false,
		source: { kind: "builtin", serverSlug: null },
		category: "ReadLocal",
		effectiveRequiresApproval: false,
	},
	{
		name: "Calculate",
		description: "Evaluates arithmetic.",
		requiresApproval: false,
		source: { kind: "builtin", serverSlug: null },
		category: "ReadLocal",
		effectiveRequiresApproval: false,
	},
];

const mcpTool: ToolCatalogEntry = {
	name: "mcp__filesystem-tools__read",
	description: "Reads a file via MCP.",
	requiresApproval: true,
	source: { kind: "mcp", serverSlug: "filesystem-tools" },
	category: "Network",
	effectiveRequiresApproval: true,
};

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
		useToolCatalogMock.mockReturnValue({ data: builtinTools, isLoading: false, error: null });
	});

	afterEach(() => {
		cleanup();
		vi.clearAllMocks();
	});

	it("renders the overview panel", () => {
		renderWithProviders(<LocalToolsOverview />);

		expect(screen.getByTestId("local-tools-overview")).toBeTruthy();
	});

	it("lists the built-in catalog tools from the fetched catalog", () => {
		renderWithProviders(<LocalToolsOverview />);

		expect(screen.getByTestId("local-tool-row-GetCurrentTime")).toBeTruthy();
		expect(screen.getByTestId("local-tool-row-Calculate")).toBeTruthy();
	});

	it("shows auto-execute badges for built-in tools", () => {
		renderWithProviders(<LocalToolsOverview />);

		for (const tool of builtinTools) {
			const badge = screen.getByTestId(`local-tool-approval-badge-${tool.name}`);
			expect(badge.textContent).toBe("auto-execute");
		}
	});

	it("renders MCP tools with a server source badge and requires-approval badge", () => {
		useToolCatalogMock.mockReturnValue({
			data: [...builtinTools, mcpTool],
			isLoading: false,
			error: null,
		});

		renderWithProviders(<LocalToolsOverview />);

		expect(screen.getByTestId(`local-tool-row-${mcpTool.name}`)).toBeTruthy();
		const approvalBadge = screen.getByTestId(`local-tool-approval-badge-${mcpTool.name}`);
		expect(approvalBadge.textContent).toBe("requires approval");
		// The MCP tool carries an MCP source badge labelled with its server slug.
		expect(screen.getByText("MCP · filesystem-tools")).toBeTruthy();
	});

	it("shows a loading state while the catalog is fetching", () => {
		useToolCatalogMock.mockReturnValue({ data: undefined, isLoading: true, error: null });

		renderWithProviders(<LocalToolsOverview />);

		expect(screen.getByTestId("local-tools-loading")).toBeTruthy();
	});

	it("shows an empty state when the catalog has no tools", () => {
		useToolCatalogMock.mockReturnValue({ data: [], isLoading: false, error: null });

		renderWithProviders(<LocalToolsOverview />);

		expect(screen.getByTestId("local-tools-empty")).toBeTruthy();
	});

	it("shows an error state when the catalog fails to load", () => {
		useToolCatalogMock.mockReturnValue({ data: undefined, isLoading: false, error: new Error("boom") });

		renderWithProviders(<LocalToolsOverview />);

		expect(screen.getByTestId("local-tools-error")).toBeTruthy();
	});
});
