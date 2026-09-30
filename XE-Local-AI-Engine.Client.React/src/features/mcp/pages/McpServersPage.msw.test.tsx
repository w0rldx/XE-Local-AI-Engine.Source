// @vitest-environment jsdom

// The page against the REAL hooks and generated client, with MSW answering the wire: the status column, the reconnect
// action and the secret round-trip are all claims about what goes over HTTP, which a mocked hooks module cannot make.

import { cleanup, fireEvent, screen, waitFor, within } from "@testing-library/react";
import { http, HttpResponse } from "msw";
import { afterEach, beforeEach, describe, expect, it } from "vitest";

import { ConfirmProvider } from "@/core/ui/components/ConfirmProvider/ConfirmProvider";
import { maskedEnvValue } from "@/features/mcp/models/McpServerModels";
import { McpServersPage } from "@/features/mcp/pages/McpServersPage";
import { useMcpManagementStore } from "@/features/mcp/stores/McpManagementStore";
import { localApiPath } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { renderWithProviders } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

setupMswServer();

const DISABLED_ID = "11111111-0000-4000-8000-000000000001";
const HTTP_ID = "11111111-0000-4000-8000-000000000002";
const CRASHED_ID = "11111111-0000-4000-8000-000000000003";

function serverDto(id: string, overrides: Record<string, unknown>) {
	return {
		id,
		name: id,
		description: null,
		transportKind: "Stdio",
		command: "/usr/bin/srv",
		arguments: [],
		workingDirectory: null,
		env: {},
		url: null,
		trustTier: "Sandboxed",
		headers: {},
		sessionScope: "Shared",
		enabled: true,
		version: 1,
		createdAtUtc: 1000,
		updatedAtUtc: 2000,
		...overrides,
	};
}

const servers = [
	serverDto(DISABLED_ID, { name: "Idle", enabled: false }),
	serverDto(HTTP_ID, {
		name: "Ticket desk",
		transportKind: "Http",
		command: null,
		url: "http://127.0.0.1:18912/mcp",
		headers: { Authorization: maskedEnvValue },
	}),
	serverDto(CRASHED_ID, { name: "Codegraph" }),
];

const toolsById: Record<string, object> = {
	[HTTP_ID]: { status: "connected", error: null, failureReason: null, tools: [] },
	[CRASHED_ID]: {
		status: "error",
		error: "The MCP server process exited. Exit code 1.\nstderr: boom",
		failureReason: "ServerExited",
		tools: [],
	},
};

interface SaveBody {
	readonly headers?: unknown;
	readonly env?: unknown;
	readonly sessionScope?: unknown;
}

interface Recorded {
	toolsRequests: string[];
	enabledPatches: { id: string; enabled: unknown }[];
	puts: { id: string; body: SaveBody }[];
}

function installRoutes(): Recorded {
	const recorded: Recorded = { toolsRequests: [], enabledPatches: [], puts: [] };
	server.use(
		http.get(localApiPath("mcp/servers"), () => HttpResponse.json({ items: servers })),
		http.get<{ id: string }>(localApiPath("mcp/servers/:id/tools"), ({ params }) => {
			const id = String(params.id);
			recorded.toolsRequests.push(id);
			return HttpResponse.json(toolsById[id] ?? {});
		}),
		http.patch<{ id: string }>(localApiPath("mcp/servers/:id/enabled"), async ({ params, request }) => {
			const id = String(params.id);
			const body = (await request.json()) as { enabled: unknown };
			recorded.enabledPatches.push({ id, enabled: body.enabled });
			return HttpResponse.json(servers.find((entry) => entry.id === id));
		}),
		http.put<{ id: string }>(localApiPath("mcp/servers/:id"), async ({ params, request }) => {
			const id = String(params.id);
			recorded.puts.push({ id, body: (await request.json()) as SaveBody });
			return HttpResponse.json(servers.find((entry) => entry.id === id));
		}),
	);
	return recorded;
}

function renderPage() {
	return renderWithProviders(
		<ConfirmProvider>
			<McpServersPage />
		</ConfirmProvider>,
		{ withRouter: true },
	);
}

describe("McpServersPage over the wire", () => {
	beforeEach(() => {
		useMcpManagementStore.setState({ editorTarget: null });
	});

	afterEach(() => {
		cleanup();
	});

	it("shows each row's live status and never asks the node about a disabled server", async () => {
		const recorded = installRoutes();
		renderPage();

		expect((await screen.findByTestId(`mcp-server-status-${HTTP_ID}`)).textContent).toBe("connected");
		expect((await screen.findByTestId(`mcp-server-status-${CRASHED_ID}`)).textContent).toBe("error");
		expect(screen.getByTestId(`mcp-server-status-reason-${CRASHED_ID}`).textContent).toBe("The MCP server process exited.");
		expect(screen.getByTestId(`mcp-server-status-${DISABLED_ID}`).textContent).toBe("disabled");
		expect(recorded.toolsRequests).not.toContain(DISABLED_ID);
		// No reconnect for a server that is not enabled: there is no session to re-open.
		expect(screen.queryByTestId(`mcp-server-reconnect-${DISABLED_ID}`)).toBeNull();
	});

	it("reconnects a server with one enable request, and re-reads its status", async () => {
		const recorded = installRoutes();
		renderPage();
		await screen.findByTestId(`mcp-server-status-${CRASHED_ID}`);
		const readsBefore = recorded.toolsRequests.filter((id) => id === CRASHED_ID).length;

		fireEvent.click(screen.getByTestId(`mcp-server-reconnect-${CRASHED_ID}`));

		await waitFor(() => expect(recorded.enabledPatches).toEqual([{ id: CRASHED_ID, enabled: true }]));
		await waitFor(() => expect(recorded.toolsRequests.filter((id) => id === CRASHED_ID).length).toBeGreaterThan(readsBefore));
	});

	it("edits an HTTP server with headers, no env or trust tier, and sends an untouched secret back masked", async () => {
		const recorded = installRoutes();
		renderPage();

		fireEvent.click(await screen.findByTestId(`mcp-server-edit-${HTTP_ID}`));
		const form = await screen.findByTestId("mcp-server-form");

		expect(within(form).queryByTestId("mcp-form-env")).toBeNull();
		expect(within(form).queryByTestId("mcp-form-trust-tier")).toBeNull();
		expect(within(form).getByTestId("mcp-form-url").getAttribute("placeholder")).toBe("http://127.0.0.1:PORT/mcp");
		expect(within(form).getByText("Loopback only (127.0.0.1, localhost, ::1).")).toBeTruthy();

		const storedValue = within(form).getByTestId("mcp-form-headers-value-0");
		expect(storedValue.getAttribute("type")).toBe("password");
		expect((storedValue as HTMLInputElement).value).toBe("");
		expect(storedValue.getAttribute("placeholder")).toBe("unchanged — enter a new value to replace");

		fireEvent.click(within(form).getByTestId("mcp-form-headers-add"));
		fireEvent.change(within(form).getByTestId("mcp-form-headers-key-1"), { target: { value: "X-Api-Key" } });
		fireEvent.change(within(form).getByTestId("mcp-form-headers-value-1"), { target: { value: "k-123" } });
		fireEvent.click(screen.getByTestId("mcp-form-submit"));

		await waitFor(() => expect(recorded.puts).toHaveLength(1));
		expect(recorded.puts[0]?.body.headers).toEqual({ Authorization: maskedEnvValue, "X-Api-Key": "k-123" });
		expect(recorded.puts[0]?.body.env).toEqual({});
		expect(recorded.puts[0]?.body.sessionScope).toBe("Shared");
	});

	it("shows env and trust tier but no headers for a stdio server", async () => {
		installRoutes();
		renderPage();

		fireEvent.click(await screen.findByTestId(`mcp-server-edit-${CRASHED_ID}`));
		const form = await screen.findByTestId("mcp-server-form");

		expect(within(form).getByTestId("mcp-form-env")).toBeTruthy();
		expect(within(form).getByTestId("mcp-form-trust-tier")).toBeTruthy();
		expect(within(form).queryByTestId("mcp-form-headers")).toBeNull();
		expect(within(form).getByTestId("mcp-form-session-scope")).toBeTruthy();
	});
});
