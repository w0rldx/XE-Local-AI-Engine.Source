// @vitest-environment jsdom

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { renderHook, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { mcpServersQueryKeys } from "@/features/mcp/queries/McpServersQueryKeys";
import { toolCatalogQueryKeys } from "@/features/tools/queries/ToolCatalogQueryKeys";

const { apiMock } = vi.hoisted(() => ({
	apiMock: {
		createMcpServer: vi.fn(),
		updateMcpServer: vi.fn(),
		deleteMcpServer: vi.fn(),
		setMcpServerEnabled: vi.fn(),
	},
}));

vi.mock("@/features/mcp/api/McpServersApi", () => ({
	createMcpServer: apiMock.createMcpServer,
	updateMcpServer: apiMock.updateMcpServer,
	deleteMcpServer: apiMock.deleteMcpServer,
	setMcpServerEnabled: apiMock.setMcpServerEnabled,
	// listMcpServers / getMcpServerTools are imported by the module but unused in these mutation tests.
	listMcpServers: vi.fn(),
	getMcpServerTools: vi.fn(),
}));

import {
	useCreateMcpServer,
	useDeleteMcpServer,
	useSetMcpServerEnabled,
	useUpdateMcpServer,
} from "@/features/mcp/queries/useMcpServers";

const sampleRequest = {
	name: "Server",
	description: null,
	transportKind: "Stdio" as const,
	command: "/usr/bin/srv",
	arguments: [],
	workingDirectory: null,
	env: {},
	url: null,
};

// Captures the queryKey of every invalidateQueries call so a test can assert which caches a mutation touched.
const invalidatedKeys: unknown[] = [];

function makeWrapper() {
	invalidatedKeys.length = 0;
	const queryClient = new QueryClient({
		defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
	});
	vi.spyOn(queryClient, "invalidateQueries").mockImplementation((filters) => {
		invalidatedKeys.push((filters as { queryKey?: unknown } | undefined)?.queryKey);
		return Promise.resolve();
	});
	function Wrapper({ children }: { children: ReactNode }) {
		return <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>;
	}
	return { Wrapper };
}

describe("useMcpServers mutations", () => {
	beforeEach(() => {
		apiMock.createMcpServer.mockResolvedValue({ id: "mcp-1" });
		apiMock.updateMcpServer.mockResolvedValue({ id: "mcp-1" });
		apiMock.deleteMcpServer.mockResolvedValue(undefined);
		apiMock.setMcpServerEnabled.mockResolvedValue({ id: "mcp-1", enabled: true });
	});

	afterEach(() => {
		vi.clearAllMocks();
	});

	it("create invalidates ONLY the servers list, never the tool catalog (server persists disabled)", async () => {
		const { Wrapper } = makeWrapper();
		const { result } = renderHook(() => useCreateMcpServer(), { wrapper: Wrapper });

		result.current.mutate(sampleRequest);

		await waitFor(() => expect(result.current.isSuccess).toBe(true));

		expect(invalidatedKeys).toContainEqual(mcpServersQueryKeys.all());
		expect(invalidatedKeys).not.toContainEqual(toolCatalogQueryKeys.all());
	});

	it("update invalidates both the servers list and the tool catalog", async () => {
		const { Wrapper } = makeWrapper();
		const { result } = renderHook(() => useUpdateMcpServer(), { wrapper: Wrapper });

		result.current.mutate({ id: "mcp-1", request: sampleRequest });

		await waitFor(() => expect(result.current.isSuccess).toBe(true));

		expect(invalidatedKeys).toContainEqual(mcpServersQueryKeys.all());
		expect(invalidatedKeys).toContainEqual(toolCatalogQueryKeys.all());
	});

	it("delete invalidates both the servers list and the tool catalog", async () => {
		const { Wrapper } = makeWrapper();
		const { result } = renderHook(() => useDeleteMcpServer(), { wrapper: Wrapper });

		result.current.mutate("mcp-1");

		await waitFor(() => expect(result.current.isSuccess).toBe(true));

		expect(invalidatedKeys).toContainEqual(mcpServersQueryKeys.all());
		expect(invalidatedKeys).toContainEqual(toolCatalogQueryKeys.all());
	});

	it("enable toggle invalidates both the servers list and the tool catalog", async () => {
		const { Wrapper } = makeWrapper();
		const { result } = renderHook(() => useSetMcpServerEnabled(), { wrapper: Wrapper });

		result.current.mutate({ id: "mcp-1", enabled: true });

		await waitFor(() => expect(result.current.isSuccess).toBe(true));

		expect(invalidatedKeys).toContainEqual(mcpServersQueryKeys.all());
		expect(invalidatedKeys).toContainEqual(toolCatalogQueryKeys.all());
	});
});
