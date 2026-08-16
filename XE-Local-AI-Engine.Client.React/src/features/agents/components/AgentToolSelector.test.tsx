// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
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

import { AgentToolSelector } from "@/features/agents/components/AgentToolSelector";

const catalog: ToolCatalogEntry[] = [
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
		name: "mcp__filesystem-tools__read",
		description: "Reads a file via MCP.",
		requiresApproval: true,
		source: { kind: "mcp", serverSlug: "filesystem-tools" },
		category: "Network",
		effectiveRequiresApproval: true,
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

interface HarnessProps {
	selectedToolNames?: string[];
	toolApprovals?: Record<string, boolean>;
	toolCapable?: boolean;
}

function renderSelector(props: HarnessProps = {}) {
	const onToggleTool = vi.fn();
	const onToggleApproval = vi.fn();
	renderWithProviders(
		<AgentToolSelector
			selectedToolNames={props.selectedToolNames ?? []}
			toolApprovals={props.toolApprovals ?? {}}
			toolCapable={props.toolCapable ?? true}
			onToggleTool={onToggleTool}
			onToggleApproval={onToggleApproval}
		/>,
	);
	return { onToggleTool, onToggleApproval };
}

describe("AgentToolSelector", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
		useToolCatalogMock.mockReturnValue({ data: catalog, isLoading: false, error: null });
	});

	afterEach(() => {
		cleanup();
		vi.clearAllMocks();
	});

	it("lists tools from the fetched catalog (built-in + MCP)", () => {
		renderSelector();

		expect(screen.getByTestId("agent-tool-row-GetCurrentTime")).toBeTruthy();
		expect(screen.getByTestId("agent-tool-row-mcp__filesystem-tools__read")).toBeTruthy();
		// MCP tool shows its server source badge.
		expect(screen.getByText("MCP · filesystem-tools")).toBeTruthy();
	});

	it("shows each tool's category badge with the correct approval state", () => {
		renderSelector();

		// ReadLocal built-in: auto-executing (no approval floor).
		const readLocalBadge = screen.getByTestId("tool-category-badge-ReadLocal");
		expect(readLocalBadge.textContent).toContain("read-only");
		expect(readLocalBadge.getAttribute("data-requires-approval")).toBe("false");

		// Network MCP tool: approval-required under node policy.
		const networkBadge = screen.getByTestId("tool-category-badge-Network");
		expect(networkBadge.textContent).toContain("network");
		expect(networkBadge.getAttribute("data-requires-approval")).toBe("true");
	});

	it("badges a since-removed selected tool as Unknown (fail-closed to approval)", () => {
		renderSelector({ selectedToolNames: ["mcp__removed-server__tool"] });

		const unknownBadge = screen.getByTestId("tool-category-badge-Unknown");
		expect(unknownBadge.textContent).toContain("uncategorized");
		expect(unknownBadge.getAttribute("data-requires-approval")).toBe("true");
	});

	it("invokes onToggleTool when a tool checkbox is toggled", () => {
		const { onToggleTool } = renderSelector();

		fireEvent.click(screen.getByTestId("agent-tool-checkbox-GetCurrentTime"));

		expect(onToggleTool).toHaveBeenCalledWith("GetCurrentTime", true);
	});

	it("disables the approval switch until the tool is selected", () => {
		renderSelector({ selectedToolNames: [] });

		const approvalSwitch = screen.getByTestId("agent-tool-approval-GetCurrentTime") as HTMLInputElement;
		expect(approvalSwitch.disabled).toBe(true);
	});

	it("disables all controls and warns when the model is not tool-capable", () => {
		renderSelector({ toolCapable: false });

		expect(screen.getByTestId("agent-tool-capability-warning")).toBeTruthy();
		const checkbox = screen.getByTestId("agent-tool-checkbox-GetCurrentTime") as HTMLInputElement;
		expect(checkbox.disabled).toBe(true);
	});

	it("still renders a selected tool that is no longer in the catalog so it can be deselected", () => {
		useToolCatalogMock.mockReturnValue({ data: catalog, isLoading: false, error: null });

		renderSelector({ selectedToolNames: ["mcp__removed-server__tool"] });

		expect(screen.getByTestId("agent-tool-row-mcp__removed-server__tool")).toBeTruthy();
		const checkbox = screen.getByTestId("agent-tool-checkbox-mcp__removed-server__tool") as HTMLInputElement;
		expect(checkbox.checked).toBe(true);
	});

	it("shows a loading state while the catalog is fetching", () => {
		useToolCatalogMock.mockReturnValue({ data: undefined, isLoading: true, error: null });

		renderSelector();

		expect(screen.getByTestId("agent-tool-catalog-loading")).toBeTruthy();
	});
});
