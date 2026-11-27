// @vitest-environment jsdom

import { cleanup, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { type ToolCatalogEntry, toToolDisplayName } from "@/core/tools/models/ToolCatalogModels";

const { useToolCatalogMock } = vi.hoisted(() => ({
	useToolCatalogMock: vi.fn(),
}));

vi.mock("@/core/tools/queries/useToolCatalog", () => ({
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

import { LocalToolsOverview } from "@/features/tools/components/LocalToolsOverview";
import { installJsdomEnvironmentMocks, renderWithMantine } from "@/test/MantineTestRender";

const builtinTools: ToolCatalogEntry[] = [
	{
		name: "GetCurrentTime",
		description: "Returns the current time.",
		requiresApproval: false,
		source: { kind: "builtin", serverSlug: null },
		category: "ReadLocal",
		effectiveRequiresApproval: false,
		sessionScopeEligible: false,
		isAvailableOnNode: true,
	},
	{
		name: "Calculate",
		description: "Evaluates arithmetic.",
		requiresApproval: false,
		source: { kind: "builtin", serverSlug: null },
		category: "ReadLocal",
		effectiveRequiresApproval: false,
		sessionScopeEligible: false,
		isAvailableOnNode: true,
	},
];

const mcpTool: ToolCatalogEntry = {
	name: "mcp__filesystem-tools__read",
	description: "Reads a file via MCP.",
	requiresApproval: true,
	source: { kind: "mcp", serverSlug: "filesystem-tools" },
	category: "Network",
	effectiveRequiresApproval: true,
	sessionScopeEligible: false,
	isAvailableOnNode: true,
};

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
		renderWithMantine(<LocalToolsOverview />);

		expect(screen.getByTestId("local-tools-overview")).toBeTruthy();
	});

	it("lists the built-in catalog tools from the fetched catalog", () => {
		renderWithMantine(<LocalToolsOverview />);

		expect(screen.getByTestId("local-tool-row-GetCurrentTime")).toBeTruthy();
		expect(screen.getByTestId("local-tool-row-Calculate")).toBeTruthy();
	});

	it("shows auto-execute badges for built-in tools", () => {
		renderWithMantine(<LocalToolsOverview />);

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

		renderWithMantine(<LocalToolsOverview />);

		expect(screen.getByTestId(`local-tool-row-${mcpTool.name}`)).toBeTruthy();
		const approvalBadge = screen.getByTestId(`local-tool-approval-badge-${mcpTool.name}`);
		expect(approvalBadge.textContent).toBe("requires approval");
		// The MCP tool carries an MCP source badge labeled with its server slug.
		expect(screen.getByText("MCP · filesystem-tools")).toBeTruthy();
	});

	// Regression guard for two live 390px squeezes: first the approval badge got clipped, then the name was cut to an
	// ellipsis nobody could expand. The name now wraps in full (minWidth: 0 plus an anywhere-break, never a truncate)
	// and the badges hold their width, dropping to the next line when the row is too narrow.
	it("keeps a long tool name whole and the badges next to it intact", () => {
		const longNameTool: ToolCatalogEntry = {
			...mcpTool,
			name: "mcp__filesystem-tools__read_a_very_long_file_path_from_the_workspace_root",
		};
		useToolCatalogMock.mockReturnValue({ data: [longNameTool], isLoading: false, error: null });

		renderWithMantine(<LocalToolsOverview />);

		const name = screen.getByTestId(`local-tool-name-${longNameTool.name}`);
		expect(name.style.minWidth).toBe("0px");
		expect(name.getAttribute("data-truncate")).toBeNull();
		expect(name.style.overflowWrap).toBe("anywhere");
		expect(name.textContent).toBe(toToolDisplayName(longNameTool.name));

		const approvalBadge = screen.getByTestId(`local-tool-approval-badge-${longNameTool.name}`);
		expect(approvalBadge.style.flexShrink).toBe("0");
		expect(approvalBadge.textContent).toBe("requires approval");

		// The source badge it sits beside already refuses to shrink; both badges keeping their width is the contract.
		expect(screen.getByTestId("tool-source-badge-mcp").style.flexShrink).toBe("0");
	});

	it("shows a loading state while the catalog is fetching", () => {
		useToolCatalogMock.mockReturnValue({ data: undefined, isLoading: true, error: null });

		renderWithMantine(<LocalToolsOverview />);

		expect(screen.getByTestId("local-tools-loading")).toBeTruthy();
	});

	it("shows an empty state when the catalog has no tools", () => {
		useToolCatalogMock.mockReturnValue({ data: [], isLoading: false, error: null });

		renderWithMantine(<LocalToolsOverview />);

		expect(screen.getByTestId("local-tools-empty")).toBeTruthy();
	});

	// The panel renders the node's installed catalog, while the per-turn offer is narrowed by model tool capability,
	// the ToolCapableModels allow-list and the agent's AllowedToolNames. The copy has to say so rather than imply the
	// listed tools are all callable right now.
	it("labels the list as what is installed on the node, not what a turn can call", () => {
		renderWithMantine(<LocalToolsOverview />);

		expect(screen.getByText("Tools installed on this node")).toBeTruthy();
		expect(screen.getByText("2 installed")).toBeTruthy();
	});

	it("states that the usable subset depends on the model and the agent", () => {
		renderWithMantine(<LocalToolsOverview />);

		const notice = screen.getByTestId("local-tools-scope-notice").textContent ?? "";
		expect(notice).toContain("full node catalog");
		expect(notice).toContain("selected model");
		expect(notice).toContain("agent");
	});

	it("shows an error state when the catalog fails to load", () => {
		useToolCatalogMock.mockReturnValue({ data: undefined, isLoading: false, error: new Error("boom") });

		renderWithMantine(<LocalToolsOverview />);

		expect(screen.getByTestId("local-tools-error")).toBeTruthy();
	});
});
