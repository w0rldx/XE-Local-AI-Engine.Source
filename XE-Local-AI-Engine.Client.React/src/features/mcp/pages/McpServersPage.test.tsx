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

// The editor dialog wires the navigation guard (useUnsavedChangesGuard → TanStack useBlocker). The page tests
// render without a real Router, so stub useBlocker to an idle (never-blocked) state — navigation blocking is the
// hook's own concern and is covered by its dedicated test.
vi.mock("@tanstack/react-router", () => ({
	useBlocker: () => ({ status: "idle", proceed: undefined, reset: undefined }),
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

	it("opens the create editor as a dialog with Save/Cancel from the register button", async () => {
		renderPage();

		// No editor dialog until the register button is clicked.
		expect(screen.queryByRole("dialog")).toBeNull();

		fireEvent.click(screen.getByTestId("mcp-create-button"));

		// The Mantine Modal mounts through an open transition, so await its appearance.
		const dialog = await screen.findByRole("dialog");
		expect(within(dialog).getByTestId("mcp-editor-card")).toBeTruthy();
		expect(within(dialog).getByTestId("mcp-server-form")).toBeTruthy();
		// Save/Cancel live in the dialog footer and are always present regardless of body length.
		expect(within(dialog).getByTestId("mcp-form-submit")).toBeTruthy();
		expect(within(dialog).getByTestId("mcp-form-cancel")).toBeTruthy();
	});

	it("opens the edit editor pre-filled from a row action", async () => {
		renderPage();

		fireEvent.click(screen.getByTestId("mcp-server-edit-mcp-1"));

		const dialog = await screen.findByRole("dialog");
		const nameInput = within(dialog).getByTestId("mcp-form-name") as HTMLInputElement;
		expect(nameInput.value).toBe("Filesystem tools");
		// Stdio transport pre-fills the command field.
		const commandInput = within(dialog).getByTestId("mcp-form-command") as HTMLInputElement;
		expect(commandInput.value).toBe("/usr/bin/fs-mcp");
	});

	it("submits the create form through the footer Save button", async () => {
		const createMutation = makeMutation();
		hooksMock.useCreateMcpServer.mockReturnValue(createMutation);

		renderPage();

		fireEvent.click(screen.getByTestId("mcp-create-button"));
		const dialog = await screen.findByRole("dialog");

		// A minimal valid stdio registration: name + command.
		fireEvent.change(within(dialog).getByTestId("mcp-form-name"), { target: { value: "FS" } });
		fireEvent.change(within(dialog).getByTestId("mcp-form-command"), { target: { value: "/bin/fs" } });
		fireEvent.click(within(dialog).getByTestId("mcp-form-submit"));

		expect(createMutation.mutate).toHaveBeenCalledTimes(1);
	});

	it("resets the editor target on unmount so it does not reopen on remount (stuck-editor fix)", () => {
		// Open the editor, then unmount the page while it is still open.
		useMcpManagementStore.setState({ editorTarget: { mode: "create" } });
		const { unmount } = renderPage();
		expect(screen.getByRole("dialog")).toBeTruthy();

		unmount();

		// The module-singleton store must have been cleared by the page's unmount effect.
		expect(useMcpManagementStore.getState().editorTarget).toBeNull();

		// Remounting shows the list, not a reopened editor.
		renderPage();
		expect(screen.queryByRole("dialog")).toBeNull();
		expect(screen.getByTestId("mcp-servers-table")).toBeTruthy();
	});

	it("toggles a server enabled through the row switch", () => {
		const enableMutation = makeMutation();
		hooksMock.useSetMcpServerEnabled.mockReturnValue(enableMutation);

		renderPage();

		fireEvent.click(screen.getByTestId("mcp-server-enabled-mcp-1"));

		expect(enableMutation.mutate).toHaveBeenCalledWith({ id: "mcp-1", enabled: true }, { onError: expect.any(Function) });
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
