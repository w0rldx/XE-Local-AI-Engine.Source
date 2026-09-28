// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, render, screen } from "@testing-library/react";
import type { ReactElement } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import type { McpServerToolsView } from "@/features/mcp/models/McpServerToolsModels";

const { useMcpServerToolsMock } = vi.hoisted(() => ({
	useMcpServerToolsMock: vi.fn(),
}));

vi.mock("@/features/mcp/queries/useMcpServers", () => ({
	useMcpServerTools: useMcpServerToolsMock,
}));

import { McpServerToolsPanel } from "@/features/mcp/components/McpServerToolsPanel";
import { testMantineTheme } from "@/test/MantineTestRender";

function renderWithProviders(ui: ReactElement) {
	return render(
		<MantineProvider env="test" theme={testMantineTheme}>
			{ui}
		</MantineProvider>,
	);
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

function mockTools(view: McpServerToolsView): void {
	useMcpServerToolsMock.mockReturnValue({ data: view, isLoading: false, error: null });
}

describe("McpServerToolsPanel", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
	});

	afterEach(() => {
		cleanup();
		vi.clearAllMocks();
	});

	it("renders nothing when no server is selected", () => {
		mockTools({ status: "disabled", error: null, failureReason: null, tools: [] });

		const { container } = renderWithProviders(<McpServerToolsPanel serverId={null} />);

		// The query hook is still called (hooks must run unconditionally) but the panel renders null.
		expect(container.querySelector('[data-testid="mcp-server-tools-panel"]')).toBeNull();
	});

	it("renders the connecting status as a distinct connecting label, not an error", () => {
		mockTools({ status: "connecting", error: null, failureReason: null, tools: [] });

		renderWithProviders(<McpServerToolsPanel serverId="mcp-1" />);

		// Connecting renders its own label, NOT the error one.
		expect(screen.getByText("connecting…")).toBeTruthy();
		expect(screen.queryByText("error")).toBeNull();
		// A connecting server (no error) does not render the red connection-error alert.
		expect(screen.queryByTestId("mcp-server-tools-connection-error")).toBeNull();
	});

	it("renders the connected status with its discovered tools", () => {
		mockTools({
			status: "connected",
			error: null,
			failureReason: null,
			tools: [{ name: "mcp__fs__read", description: "Reads a file.", requiresApproval: true }],
		});

		renderWithProviders(<McpServerToolsPanel serverId="mcp-1" />);

		expect(screen.getByText("connected")).toBeTruthy();
		expect(screen.getByTestId("mcp-discovered-tool-mcp__fs__read")).toBeTruthy();
		// The qualified name is stripped to the bare tool segment for display.
		expect(screen.getByText("read")).toBeTruthy();
	});

	it("renders the error status and the redacted connection error when no reason is reported", () => {
		mockTools({ status: "error", error: "redacted reason", failureReason: null, tools: [] });

		renderWithProviders(<McpServerToolsPanel serverId="mcp-1" />);

		expect(screen.getByText("error")).toBeTruthy();
		expect(screen.getByTestId("mcp-server-tools-connection-error").textContent).toContain("redacted reason");
	});

	it("words a missing server command by its reason instead of the server's generic text", () => {
		mockTools({
			status: "error",
			error: "The MCP server's command was not found or could not be started.",
			failureReason: "ServerNotFound",
			tools: [],
		});

		renderWithProviders(<McpServerToolsPanel serverId="mcp-1" />);

		expect(screen.getByTestId("mcp-server-tools-connection-error").textContent).toBe(
			"The server's command was not found or could not be started. Check the command and that it is installed on this machine.",
		);
	});

	it("shows a sandbox refusal with the engine's remedy beneath it, distinct from a missing command", () => {
		mockTools({
			status: "error",
			error:
				"The sandbox refused to start the MCP server: its command or working directory overlaps a protected location, or the sandbox boundary could not be established. Point it at the directory holding the server's own files.",
			failureReason: "SandboxRefused",
			tools: [],
		});

		renderWithProviders(<McpServerToolsPanel serverId="mcp-1" />);

		const alert = screen.getByTestId("mcp-server-tools-connection-error");
		expect(alert.textContent).toContain("The sandbox refused to start this server.");
		expect(alert.textContent).toContain("Point it at the directory holding the server's own files.");
		expect(alert.textContent).not.toContain("was not found");
	});

	it("shows the engine's remedy when this node cannot sandbox at all", () => {
		mockTools({
			status: "error",
			error:
				"This node cannot isolate the MCP server from the host filesystem. Install bubblewrap (bwrap) with user-namespace support, or move the server to the Privileged host tier.",
			failureReason: "SandboxUnavailable",
			tools: [],
		});

		renderWithProviders(<McpServerToolsPanel serverId="mcp-1" />);

		const alert = screen.getByTestId("mcp-server-tools-connection-error");
		expect(alert.textContent).toContain("This node cannot run a server in the sandbox.");
		expect(alert.textContent).toContain("Install bubblewrap (bwrap) with user-namespace support");
	});

	it("falls back gracefully to the raw label for an unknown status", () => {
		// An unexpected status string must not crash — it renders the raw value (graceful fallback).
		mockTools({ status: "future-state" as McpServerToolsView["status"], error: null, failureReason: null, tools: [] });

		renderWithProviders(<McpServerToolsPanel serverId="mcp-1" />);

		expect(screen.getByText("future-state")).toBeTruthy();
	});
});
