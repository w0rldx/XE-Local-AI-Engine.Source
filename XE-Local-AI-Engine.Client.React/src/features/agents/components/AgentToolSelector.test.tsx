// @vitest-environment jsdom

import { cleanup, fireEvent, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import type { ToolCatalogEntry } from "@/features/tools/models/ToolCatalogModels";

const { useToolCatalogMock } = vi.hoisted(() => ({
	useToolCatalogMock: vi.fn(),
}));

vi.mock("@/features/tools/queries/useToolCatalog", () => ({
	useToolCatalog: useToolCatalogMock,
}));

import { AgentToolSelector } from "@/features/agents/components/AgentToolSelector";
import { renderWithProviders } from "@/test/RenderWithProviders";

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

interface HarnessProps {
	selectedToolNames?: string[];
	toolApprovals?: Record<string, boolean>;
	toolCapable?: boolean;
	isDefaultAssistant?: boolean;
	webAccessEnabled?: boolean | null;
	defaultOfferToolNames?: readonly string[];
	defaultOfferError?: boolean;
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
			isDefaultAssistant={props.isDefaultAssistant}
			webAccessEnabled={props.webAccessEnabled}
			defaultOfferToolNames={props.defaultOfferToolNames}
			defaultOfferError={props.defaultOfferError}
		/>,
		// The web-access hint is a router link; a router mounts asynchronously, so those tests await with findBy*.
		{ withRouter: props.webAccessEnabled === false || props.webAccessEnabled === null },
	);
	return { onToggleTool, onToggleApproval };
}

describe("AgentToolSelector", () => {
	beforeEach(() => {
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
	const webSearch: ToolCatalogEntry = {
		name: "web_search",
		description: "Searches the web.",
		requiresApproval: true,
		source: { kind: "builtin", serverSlug: null },
		category: "Network",
		effectiveRequiresApproval: true,
		sessionScopeEligible: false,
	};
	const runPython: ToolCatalogEntry = {
		name: "run_python",
		description: "Runs Python.",
		requiresApproval: true,
		source: { kind: "builtin", serverSlug: null },
		category: "WriteExecute",
		effectiveRequiresApproval: true,
		sessionScopeEligible: false,
	};

	it("checks and locks exactly the server's Default Assistant offer, whatever the catalog holds", () => {
		useToolCatalogMock.mockReturnValue({ data: [...catalog, webSearch, runPython], isLoading: false, error: null });

		renderSelector({
			isDefaultAssistant: true,
			selectedToolNames: [],
			webAccessEnabled: true,
			defaultOfferToolNames: ["GetCurrentTime", "ask_user"],
		});

		expect(screen.getByTestId("agent-tool-default-offer-note").textContent).toContain(
			"The default assistant uses every tool available to its model.",
		);
		const checkbox = (name: string) => screen.getByTestId<HTMLInputElement>(`agent-tool-checkbox-${name}`);
		expect(checkbox("GetCurrentTime").checked).toBe(true);
		// ask_user is not in the mocked catalog: the server listed it, so it is still shown, checked.
		expect(checkbox("ask_user").checked).toBe(true);
		for (const name of ["mcp__filesystem-tools__read", "web_search", "run_python"]) {
			expect(checkbox(name).checked).toBe(false);
		}
		for (const name of ["GetCurrentTime", "ask_user", "web_search"]) {
			expect(checkbox(name).disabled).toBe(true);
			expect(screen.getByTestId<HTMLInputElement>(`agent-tool-approval-${name}`).disabled).toBe(true);
		}
	});

	it("shows a loading state instead of guessing while the Default Assistant's offer loads", () => {
		renderSelector({ isDefaultAssistant: true, defaultOfferToolNames: undefined });

		expect(screen.getByTestId("agent-tool-catalog-loading")).toBeTruthy();
		expect(screen.queryByTestId("agent-tool-row-GetCurrentTime")).toBeNull();
	});

	it("shows an error instead of guessing when the Default Assistant's offer fails to load", () => {
		renderSelector({ isDefaultAssistant: true, defaultOfferError: true });

		expect(screen.getByTestId("agent-tool-default-offer-error").textContent).toContain(
			"Could not load the default assistant's tools.",
		);
		expect(screen.queryByTestId("agent-tool-row-GetCurrentTime")).toBeNull();
	});

	it("does not show the default-offer note for an ordinary agent", () => {
		renderSelector();

		expect(screen.queryByTestId("agent-tool-default-offer-note")).toBeNull();
	});

	// null is a fresh node's never-saved switch, which the server reads as off.
	it.each([false, null])("links a web tool to Node Settings while web access is %s", async (webAccessEnabled) => {
		useToolCatalogMock.mockReturnValue({ data: [...catalog, webSearch], isLoading: false, error: null });

		renderSelector({ isDefaultAssistant: true, webAccessEnabled, defaultOfferToolNames: ["GetCurrentTime"] });

		const hint = await screen.findByTestId("agent-tool-web-access-hint-web_search");
		expect(hint.textContent).toContain("Requires web access in Node Settings");
		expect(hint.getAttribute("href")).toBe("/node-settings?section=knowledge");
		expect(screen.getByTestId<HTMLInputElement>("agent-tool-checkbox-web_search").checked).toBe(false);
		// Only the web tools carry the hint.
		expect(screen.queryByTestId("agent-tool-web-access-hint-GetCurrentTime")).toBeNull();
	});

	it("shows no web-access hint while web access is on or still loading", () => {
		useToolCatalogMock.mockReturnValue({ data: [...catalog, webSearch], isLoading: false, error: null });

		renderSelector({ webAccessEnabled: true });
		expect(screen.queryByTestId("agent-tool-web-access-hint-web_search")).toBeNull();
		cleanup();

		renderSelector({ webAccessEnabled: undefined });
		expect(screen.queryByTestId("agent-tool-web-access-hint-web_search")).toBeNull();
	});

	it("labels a web tool's approval as a consent before sending plus a result review", () => {
		useToolCatalogMock.mockReturnValue({ data: [...catalog, webSearch], isLoading: false, error: null });

		renderSelector({ webAccessEnabled: true, selectedToolNames: ["web_search", "mcp__filesystem-tools__read"] });

		expect(screen.getByTestId("agent-tool-row-web_search").textContent).toContain("Asks before sending, reviews the result");
		expect(screen.getByTestId("agent-tool-row-web_search").textContent).not.toContain("requires approval");
		expect(screen.getByTestId("agent-tool-row-mcp__filesystem-tools__read").textContent).toContain("requires approval");
	});

	it("shows ask_user checked and locked for an ordinary agent, with a note", () => {
		const askUser: ToolCatalogEntry = {
			name: "ask_user",
			description: "Asks the operator.",
			requiresApproval: true,
			source: { kind: "builtin", serverSlug: null },
			category: "ReadLocal",
			effectiveRequiresApproval: true,
			sessionScopeEligible: false,
		};
		useToolCatalogMock.mockReturnValue({ data: [...catalog, askUser], isLoading: false, error: null });

		renderSelector({ selectedToolNames: [] });

		const checkbox = screen.getByTestId<HTMLInputElement>("agent-tool-checkbox-ask_user");
		expect(checkbox.checked).toBe(true);
		expect(checkbox.disabled).toBe(true);
		expect(screen.getByTestId("agent-tool-always-offered-note").textContent).toContain("Always available");
		expect(screen.getByTestId<HTMLInputElement>("agent-tool-checkbox-GetCurrentTime").disabled).toBe(false);
	});
});
