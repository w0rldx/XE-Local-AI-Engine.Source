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

import { McpServerToolsPanel } from "@/features/mcp/components/McpServerToolsPanel";

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
		mockTools({ status: "disabled", error: null, tools: [] });

		const { container } = renderWithProviders(<McpServerToolsPanel serverId={null} />);

		// The query hook is still called (hooks must run unconditionally) but the panel renders null.
		expect(container.querySelector('[data-testid="mcp-server-tools-panel"]')).toBeNull();
	});

	it("renders the connecting status as a distinct connecting label, not an error", () => {
		mockTools({ status: "connecting", error: null, tools: [] });

		renderWithProviders(<McpServerToolsPanel serverId="mcp-1" />);

		// The i18n mock returns the raw status as the fallback; the real "connecting…" label is verified by the
		// locale parity check. What matters here is that connecting renders its own label, NOT the error one.
		expect(screen.getByText("connecting")).toBeTruthy();
		expect(screen.queryByText("error")).toBeNull();
		// A connecting server (no error) does not render the red connection-error alert.
		expect(screen.queryByTestId("mcp-server-tools-connection-error")).toBeNull();
	});

	it("renders the connected status with its discovered tools", () => {
		mockTools({
			status: "connected",
			error: null,
			tools: [{ name: "mcp__fs__read", description: "Reads a file.", requiresApproval: true }],
		});

		renderWithProviders(<McpServerToolsPanel serverId="mcp-1" />);

		expect(screen.getByText("connected")).toBeTruthy();
		expect(screen.getByTestId("mcp-discovered-tool-mcp__fs__read")).toBeTruthy();
		// The qualified name is stripped to the bare tool segment for display.
		expect(screen.getByText("read")).toBeTruthy();
	});

	it("renders the error status and the redacted connection error", () => {
		mockTools({ status: "error", error: "redacted reason", tools: [] });

		renderWithProviders(<McpServerToolsPanel serverId="mcp-1" />);

		expect(screen.getByText("error")).toBeTruthy();
		expect(screen.getByTestId("mcp-server-tools-connection-error")).toBeTruthy();
	});

	it("falls back gracefully to the raw label for an unknown status", () => {
		// An unexpected status string must not crash — it renders the raw value (graceful fallback).
		mockTools({ status: "future-state" as McpServerToolsView["status"], error: null, tools: [] });

		renderWithProviders(<McpServerToolsPanel serverId="mcp-1" />);

		expect(screen.getByText("future-state")).toBeTruthy();
	});
});
