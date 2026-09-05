// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

const { confirmMock, hooksMock, toastMock } = vi.hoisted(() => ({
	confirmMock: vi.fn(),
	hooksMock: {
		query: {
			data: [] as Array<{ id: string; alias: string; mode: "read-only" }>,
			isLoading: false,
			isFetching: false,
			error: null as unknown,
			refetch: vi.fn(),
		},
		create: { mutate: vi.fn(), isPending: false },
		remove: { mutate: vi.fn(), isPending: false },
	},
	toastMock: { success: vi.fn(), error: vi.fn() },
}));

vi.mock("react-i18next", () => ({
	useTranslation: () => ({
		t: (key: string, fallback?: string, variables?: Record<string, unknown>) => {
			const text = fallback ?? key;
			return Object.entries(variables ?? {}).reduce(
				(result, [name, value]) => result.replace(new RegExp(`{{${name}}}`, "g"), String(value)),
				text,
			);
		},
	}),
}));

vi.mock("@/core/ui/hooks/useConfirm", () => ({ useConfirm: () => ({ confirm: confirmMock }) }));
vi.mock("@/core/ui/notifications/Toast", () => ({ toast: toastMock }));
vi.mock("@/features/mcp/queries/useMcpWorkspaces", () => ({
	useMcpWorkspaces: () => hooksMock.query,
	useCreateMcpWorkspace: () => hooksMock.create,
	useDeleteMcpWorkspace: () => hooksMock.remove,
}));

import { McpWorkspaceAllowlistPanel } from "@/features/node-settings/components/McpWorkspaceAllowlistPanel";

function renderPanel(): void {
	render(
		<MantineProvider>
			<McpWorkspaceAllowlistPanel />
		</MantineProvider>,
	);
}

describe("McpWorkspaceAllowlistPanel", () => {
	// Cleared BEFORE each test, not after: the global `afterEach(cleanup)` in `src/test/Cleanup.ts` runs after this
	// file's own hooks (Vitest stacks them), so clearing here would drop the unmount's calls into the next test.
	// `restoreMocks` covers the `vi.spyOn` console spies below but NOT these `vi.fn()` doubles: in Vitest 4
	// `vi.restoreAllMocks()` only touches spies, so a `vi.fn()` call history still has to be cleared by hand.
	beforeEach(() => {
		vi.clearAllMocks();
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
		hooksMock.query.data = [];
		hooksMock.query.isLoading = false;
		hooksMock.query.isFetching = false;
		hooksMock.query.error = null;
		hooksMock.create.isPending = false;
		hooksMock.remove.isPending = false;
		confirmMock.mockResolvedValue(true);
	});

	it("shows loading, load-error retry, empty, and pending states", () => {
		hooksMock.query.isLoading = true;
		hooksMock.create.isPending = true;
		const { rerender } = render(
			<MantineProvider>
				<McpWorkspaceAllowlistPanel />
			</MantineProvider>,
		);
		expect(screen.getByText("Loading workspace access…")).toBeTruthy();
		expect(screen.getByText("Updating workspace access…")).toBeTruthy();

		hooksMock.query.isLoading = false;
		hooksMock.create.isPending = false;
		hooksMock.query.error = new Error("offline");
		rerender(
			<MantineProvider>
				<McpWorkspaceAllowlistPanel />
			</MantineProvider>,
		);
		fireEvent.click(screen.getByRole("button", { name: "Retry" }));
		expect(hooksMock.query.refetch).toHaveBeenCalledTimes(1);

		hooksMock.query.error = null;
		rerender(
			<MantineProvider>
				<McpWorkspaceAllowlistPanel />
			</MantineProvider>,
		);
		expect(screen.getByText("No folders are available to delegated MCP agents.")).toBeTruthy();
	});

	it("erases the trusted path before mutation and never exposes it through rendered messages, logs, or toasts", () => {
		const secretPath = "/TOP_SECRET_PATH_97/project";
		// Left to the suite-wide `restoreMocks`, which undoes every `vi.spyOn` before the next test starts — a manual
		// restore here would be undone by the first `expect` that fails and returns early.
		const consoleSpies = [
			vi.spyOn(console, "log"),
			vi.spyOn(console, "info"),
			vi.spyOn(console, "warn"),
			vi.spyOn(console, "error"),
		];
		let valueAtMutation = "not-called";
		hooksMock.create.mutate.mockImplementation((variables, callbacks) => {
			valueAtMutation = (screen.getByTestId("mcp-workspace-path") as HTMLInputElement).value;
			expect(variables).toEqual({ body: { alias: "Repository", hostPath: secretPath } });
			callbacks.onError(new Error(secretPath));
		});
		renderPanel();

		expect(screen.getByLabelText(/Alias/)).toBe(screen.getByTestId("mcp-workspace-alias"));
		expect(screen.getByLabelText(/Trusted host path/)).toBe(screen.getByTestId("mcp-workspace-path"));
		fireEvent.change(screen.getByTestId("mcp-workspace-alias"), { target: { value: " Repository " } });
		fireEvent.change(screen.getByTestId("mcp-workspace-path"), { target: { value: ` ${secretPath} ` } });
		fireEvent.submit(screen.getByRole("form", { name: "Add workspace access" }));

		expect(valueAtMutation).toBe("");
		expect((screen.getByTestId("mcp-workspace-path") as HTMLInputElement).value).toBe("");
		expect(document.body.textContent).not.toContain(secretPath);
		expect(toastMock.error).toHaveBeenCalledWith("Could not add workspace access. Check the values and try again.");
		expect(JSON.stringify(toastMock.error.mock.calls)).not.toContain(secretPath);
		expect(JSON.stringify(confirmMock.mock.calls)).not.toContain(secretPath);
		expect(consoleSpies.flatMap((spy) => spy.mock.calls).join(" ")).not.toContain(secretPath);
	});

	it("lists only alias, opaque ID, and read-only access without a host path", () => {
		hooksMock.query.data = [{ id: "ws_opaque_42", alias: "Repository", mode: "read-only" }];
		renderPanel();

		const table = screen.getByTestId("mcp-workspaces-table");
		expect(table.textContent).toContain("Repository");
		expect(table.textContent).toContain("ws_opaque_42");
		expect(table.textContent).toContain("Read only");
		expect(table.textContent).not.toContain("/");
	});

	it("confirms revocation with the alias only and deletes by opaque ID when accepted", async () => {
		hooksMock.query.data = [{ id: "ws_opaque_42", alias: "Repository", mode: "read-only" }];
		renderPanel();

		fireEvent.click(screen.getByRole("button", { name: "Revoke access to Repository" }));

		await waitFor(() => expect(confirmMock).toHaveBeenCalledTimes(1));
		expect(confirmMock).toHaveBeenCalledWith({
			title: "Revoke workspace access",
			description: "Revoke read-only access to 'Repository'? New delegated work will no longer be able to use it.",
			confirmationText: "Revoke",
			cancellationText: "Cancel",
		});
		expect(hooksMock.remove.mutate).toHaveBeenCalledWith(
			{ path: { workspaceId: "ws_opaque_42" } },
			expect.objectContaining({ onSuccess: expect.any(Function), onError: expect.any(Function) }),
		);
	});

	it("does not revoke when confirmation is cancelled and uses a path-free generic error", async () => {
		hooksMock.query.data = [{ id: "ws_opaque_42", alias: "Repository", mode: "read-only" }];
		confirmMock.mockResolvedValueOnce(false);
		renderPanel();

		fireEvent.click(screen.getByRole("button", { name: "Revoke access to Repository" }));
		await waitFor(() => expect(confirmMock).toHaveBeenCalledTimes(1));
		expect(hooksMock.remove.mutate).not.toHaveBeenCalled();

		confirmMock.mockResolvedValueOnce(true);
		fireEvent.click(screen.getByRole("button", { name: "Revoke access to Repository" }));
		await waitFor(() => expect(hooksMock.remove.mutate).toHaveBeenCalledTimes(1));
		const callbacks = hooksMock.remove.mutate.mock.calls[0]?.[1];
		callbacks.onError(new Error("/TOP_SECRET_PATH_97/project"));
		expect(toastMock.error).toHaveBeenCalledWith("Could not revoke workspace access.");
	});
});
