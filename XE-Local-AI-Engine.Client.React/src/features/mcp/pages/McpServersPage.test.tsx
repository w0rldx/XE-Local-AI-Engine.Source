// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, fireEvent, render, screen, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import type { McpServerRegistration } from "@/features/mcp/models/McpServerModels";
import { useMcpManagementStore } from "@/features/mcp/stores/McpManagementStore";

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

const { hooksMock, confirmMock } = vi.hoisted(() => ({
	hooksMock: {
		useMcpServers: vi.fn(),
		useMcpServerTools: vi.fn(),
		useCreateMcpServer: vi.fn(),
		useUpdateMcpServer: vi.fn(),
		useDeleteMcpServer: vi.fn(),
		useSetMcpServerEnabled: vi.fn(),
	},
	confirmMock: vi.fn(),
}));

vi.mock("@/features/mcp/queries/useMcpServers", () => hooksMock);
vi.mock("@/core/ui/hooks/useConfirm", () => ({
	useConfirm: () => ({ confirm: confirmMock }),
}));

import { McpServersPage } from "@/features/mcp/pages/McpServersPage";

const stdioServer: McpServerRegistration = {
	id: "mcp-1",
	name: "Filesystem tools",
	description: "Local FS",
	transportKind: "Stdio",
	command: "/usr/bin/fs-mcp",
	arguments: ["--stdio"],
	workingDirectory: "/work",
	env: [{ key: "TOKEN", value: "secret" }],
	url: null,
	enabled: false,
	version: 1,
	createdAtUtc: 1000,
	updatedAtUtc: 2000,
};

function makeMutation() {
	return { mutate: vi.fn(), isPending: false, error: null };
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

function renderPage() {
	const queryClient = new QueryClient({
		defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
	});
	return render(
		<MantineProvider>
			<QueryClientProvider client={queryClient}>
				<McpServersPage />
			</QueryClientProvider>
		</MantineProvider>,
	);
}

describe("McpServersPage", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
		useMcpManagementStore.setState({ editorTarget: null });
		hooksMock.useMcpServers.mockReturnValue({ data: [stdioServer], isLoading: false, error: null });
		hooksMock.useMcpServerTools.mockReturnValue({ data: undefined, isLoading: false, error: null });
		hooksMock.useCreateMcpServer.mockReturnValue(makeMutation());
		hooksMock.useUpdateMcpServer.mockReturnValue(makeMutation());
		hooksMock.useDeleteMcpServer.mockReturnValue(makeMutation());
		hooksMock.useSetMcpServerEnabled.mockReturnValue(makeMutation());
	});

	afterEach(() => {
		cleanup();
		vi.clearAllMocks();
	});

	it("renders the list of registered MCP servers", () => {
		renderPage();

		expect(screen.getByTestId("mcp-servers-table")).toBeTruthy();
		const row = screen.getByTestId("mcp-server-row-mcp-1");
		// The server name also appears in the discovered-tools inspector buttons, so scope the assertion to the
		// table row to avoid a multiple-match error.
		expect(within(row).getByText("Filesystem tools")).toBeTruthy();
	});

	it("shows the empty state when there are no servers", () => {
		hooksMock.useMcpServers.mockReturnValue({ data: [], isLoading: false, error: null });

		renderPage();

		expect(screen.getByTestId("mcp-servers-empty")).toBeTruthy();
	});

	it("opens the create editor from the register button", () => {
		renderPage();

		fireEvent.click(screen.getByTestId("mcp-create-button"));

		expect(screen.getByTestId("mcp-editor-card")).toBeTruthy();
		expect(screen.getByTestId("mcp-server-form")).toBeTruthy();
	});

	it("opens the edit editor pre-filled from a row action", () => {
		renderPage();

		fireEvent.click(screen.getByTestId("mcp-server-edit-mcp-1"));

		const nameInput = screen.getByTestId("mcp-form-name") as HTMLInputElement;
		expect(nameInput.value).toBe("Filesystem tools");
		// Stdio transport pre-fills the command field.
		const commandInput = screen.getByTestId("mcp-form-command") as HTMLInputElement;
		expect(commandInput.value).toBe("/usr/bin/fs-mcp");
	});

	it("toggles a server enabled through the row switch", () => {
		const enableMutation = makeMutation();
		hooksMock.useSetMcpServerEnabled.mockReturnValue(enableMutation);

		renderPage();

		fireEvent.click(screen.getByTestId("mcp-server-enabled-mcp-1"));

		expect(enableMutation.mutate).toHaveBeenCalledWith({ id: "mcp-1", enabled: true });
	});

	it("surfaces a load error", () => {
		hooksMock.useMcpServers.mockReturnValue({
			data: undefined,
			isLoading: false,
			error: new Error("boom"),
		});

		renderPage();

		expect(screen.getByTestId("mcp-list-error")).toBeTruthy();
	});
});
