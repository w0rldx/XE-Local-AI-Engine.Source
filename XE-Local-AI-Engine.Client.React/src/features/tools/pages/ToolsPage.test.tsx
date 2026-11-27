// @vitest-environment jsdom

import { cleanup, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import type { ToolCatalogEntry } from "@/core/tools/models/ToolCatalogModels";
import { ToolsPage } from "@/features/tools/pages/ToolsPage";
import { installJsdomEnvironmentMocks, renderWithMantine } from "@/test/MantineTestRender";

const { useToolCatalogMock } = vi.hoisted(() => ({
	useToolCatalogMock: vi.fn(),
}));

vi.mock("@/core/tools/queries/useToolCatalog", () => ({
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
		renderWithMantine(<ToolsPage />);

		expect(screen.getByTestId("tools-page")).toBeTruthy();
	});

	it("renders the LocalToolsOverview panel inside the page", () => {
		renderWithMantine(<ToolsPage />);

		expect(screen.getByTestId("local-tools-overview")).toBeTruthy();
	});

	it("lists the fetched catalog tools on the page", () => {
		renderWithMantine(<ToolsPage />);

		for (const tool of catalogTools) {
			expect(screen.getByTestId(`local-tool-row-${tool.name}`)).toBeTruthy();
		}
	});

	it("shows auto-execute badges for all built-in tools", () => {
		renderWithMantine(<ToolsPage />);

		for (const tool of catalogTools) {
			const badge = screen.getByTestId(`local-tool-approval-badge-${tool.name}`);
			expect(badge.textContent).toBe("auto-execute");
		}
	});
});
